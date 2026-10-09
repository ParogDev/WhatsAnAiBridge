using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// lab.compare_paths {target?, stepWorld?, replanMs?, speed?}: an offline A/B of two ways to draw a path, on this area's
/// real walkability grid, by simulating the player walking the route (no screen, no input).
///   radar: what Radar does - grid A* from the player's integer cell, drawn as one segment per cell (cell corner =
///          cell * GridToWorld) starting at the live position, re-planned only when the cell changes and at most every
///          replanMs (default 100) - so between re-plans the drawn path starts from a stale plan.
///   lab:   what the Render Lab does - the same A*, line-of-sight simplified, Chaikin-smoothed, and trimmed to the closest
///          point to the live position every frame.
/// Metrics per approach, over the steps:
///   backwardsStarts: steps whose first drawn segment points more than 90 degrees from the walking direction;
///   turnDegPer100: total absolute turning over the first 400 world units of the drawn path, per 100 units (jaggedness);
///   jumpPx-like: mean distance between this step's and the previous step's drawn path, sampled at arc lengths
///   50..300 world units (how much the line moves on its own while walking). World units; ~0.7 px each on screen.
/// </summary>
public partial class WhatsAnAiBridge
{
    private JObject ComparePaths(JToken? p)
    {
        if (!LabEnsureArea()) return Err("no_grid", _lab.LastError ?? "no pathfinding grid");
        if (p?["target"]?.Value<string>() is { Length: > 0 } t) _lab.Target = t;
        var step = Math.Clamp(p?["stepWorld"]?.Value<float>() ?? 20f, 5f, 100f);
        var replanMs = Math.Clamp(p?["replanMs"]?.Value<float>() ?? 100f, 0f, 1000f);
        var speed = Math.Clamp(p?["speed"]?.Value<float>() ?? 450f, 50f, 3000f);   // world units/s (a walk)
        var target = LabFindTarget(out var label);
        if (target == null) return Err("no_target", $"No '{_lab.Target}' entity loaded in this area.");
        var playerR = GameController.Player.GetComponent<Render>();
        if (playerR == null) return Err("no_player", "No player Render component.");
        var start = RenderPosNum(playerR);
        var grid = _lab.Grid!;
        (int, int) Cell(Vector3 v) => ((int)(v.X / GridToWorld), (int)(v.Y / GridToWorld));
        var goal = Cell(target.Value);

        // The route the simulated player walks: the smoothed lab path (a plausible human line).
        var routeCells = AStar(grid, Cell(start), goal, 400_000);
        if (routeCells == null) return Err("unreachable", $"{label} is not reachable on the grid.");
        var route = LabWorld(StringPull(routeCells));
        var walk = Resample(route, step);
        if (walk.Count < 4) return Err("too_short", $"The route to {label} is only {walk.Count} steps long.");
        var stepMs = step / speed * 1000f;

        var radarPrev = new List<Vector3>(); var labPrev = new List<Vector3>();
        var radarPlan = new List<Vector3>(); (int, int) radarPlanCell = (int.MinValue, 0); float sincePlan = float.MaxValue;
        var radar = new List<(bool back, float turn, float jump)>(); var lab = new List<(bool back, float turn, float jump)>();
        var labPlan = route; (int, int) labPlanCell = Cell(walk[0]);
        for (var k = 1; k < walk.Count - 2; k++)
        {
            var pos = walk[k]; var dir = Vector2.Normalize(Flat(walk[k + 1] - walk[k - 1]));
            var cell = Cell(pos);
            // Radar: re-plan when the cell changed and replanMs has passed; drawn = live pos + plan cells (corners).
            sincePlan += stepMs;
            if (cell != radarPlanCell && sincePlan >= replanMs && AStar(grid, cell, goal, 400_000) is { } rc)
            {
                radarPlan = rc.Select(c => new Vector3(c.x * GridToWorld, c.y * GridToWorld, GroundZ(c.x, c.y))).ToList();
                radarPlanCell = cell; sincePlan = 0;
            }
            var radarDrawn = new List<Vector3> { pos }; radarDrawn.AddRange(radarPlan);
            // Lab: re-plan on cell change (150 ms like RenderLab), drawn from the closest point to the live position.
            if (cell != labPlanCell && AStar(grid, cell, goal, 400_000) is { } lc) { labPlan = LabWorld(StringPull(lc)); labPlanCell = cell; }
            var labDrawn = TrimTo(labPlan, pos);

            radar.Add(Metrics(radarDrawn, radarPrev, dir)); lab.Add(Metrics(labDrawn, labPrev, dir));
            radarPrev = radarDrawn; labPrev = labDrawn;
        }
        JObject Sum(List<(bool back, float turn, float jump)> m) => new()
        {
            ["steps"] = m.Count,
            ["backwardsStarts"] = m.Count(x => x.back),
            ["turnDegPer100"] = Math.Round(m.Average(x => x.turn), 1),
            ["jumpWorldMean"] = Math.Round(m.Skip(1).Average(x => x.jump), 1),
            ["jumpWorldP95"] = Math.Round(m.Skip(1).Select(x => x.jump).OrderBy(x => x).ElementAt((int)((m.Count - 1) * 0.95)), 1),
        };
        return new JObject
        {
            ["target"] = label, ["routeWorld"] = Math.Round(Length(route)), ["stepWorld"] = step, ["replanMs"] = replanMs, ["speed"] = speed,
            ["radar"] = Sum(radar), ["lab"] = Sum(lab),
            ["note"] = "World units (~0.7 px each at default zoom). Radar-style = per-cell A* path from the integer cell, re-planned at most every replanMs; lab = simplified + smoothed + trimmed to the live position.",
        };
    }

    private List<Vector3> LabWorld(List<(int x, int y)> cells)
    {
        var w = cells.Select(c => new Vector3(c.x * GridToWorld + GridToWorld / 2, c.y * GridToWorld + GridToWorld / 2, GroundZ(c.x, c.y))).ToList();
        for (var k = 0; k < 3; k++) w = Chaikin(w);
        return w;
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Y);

    private static float Length(List<Vector3> p) { float s = 0; for (var i = 1; i < p.Count; i++) s += Vector2.Distance(Flat(p[i - 1]), Flat(p[i])); return s; }

    /// <summary>Points every 'step' world units along a polyline.</summary>
    private static List<Vector3> Resample(List<Vector3> p, float step)
    {
        var o = new List<Vector3> { p[0] }; float carry = 0;
        for (var i = 1; i < p.Count; i++)
        {
            var a = p[i - 1]; var b = p[i]; var len = Vector2.Distance(Flat(a), Flat(b));
            var d = step - carry;
            while (d <= len) { o.Add(Vector3.Lerp(a, b, d / len)); d += step; }
            carry = len - (d - step);
        }
        return o;
    }

    private static List<Vector3> TrimTo(List<Vector3> w, Vector3 pos)
    {
        int seg = 0; float best = float.MaxValue; var at = w[0];
        for (var i = 0; i < w.Count - 1; i++)
        {
            var c = ClosestOnSegment(w[i], w[i + 1], pos);
            var d = Vector2.DistanceSquared(Flat(c), Flat(pos));
            if (d < best) { best = d; seg = i; at = c; }
        }
        var o = new List<Vector3> { pos, at };
        o.AddRange(w.Skip(seg + 1));
        return o;
    }

    private static Vector3 AtArc(List<Vector3> p, float s)
    {
        for (var i = 1; i < p.Count; i++)
        {
            var len = Vector2.Distance(Flat(p[i - 1]), Flat(p[i]));
            if (s <= len) return len < 1e-4f ? p[i] : Vector3.Lerp(p[i - 1], p[i], s / len);
            s -= len;
        }
        return p[^1];
    }

    private static (bool back, float turn, float jump) Metrics(List<Vector3> drawn, List<Vector3> prev, Vector2 walkDir)
    {
        // First segment that is at least 10 units long: does it point backwards?
        var first = Vector2.Zero;
        for (var i = 1; i < drawn.Count && first == Vector2.Zero; i++)
            if (Vector2.Distance(Flat(drawn[i]), Flat(drawn[0])) >= 10) first = Vector2.Normalize(Flat(drawn[i] - drawn[0]));
        var back = first != Vector2.Zero && Vector2.Dot(first, walkDir) < 0;
        // Turning over the first 400 units, per 100.
        float turn = 0, arc = 0; Vector2? lastDir = null;
        for (var i = 1; i < drawn.Count && arc < 400; i++)
        {
            var seg = Flat(drawn[i] - drawn[i - 1]); var len = seg.Length(); if (len < 1e-3f) continue;
            var d = seg / len;
            if (lastDir is { } ld) turn += MathF.Acos(Math.Clamp(Vector2.Dot(ld, d), -1f, 1f)) * 180f / MathF.PI;
            lastDir = d; arc += len;
        }
        var turnPer100 = arc > 0 ? turn / Math.Max(arc, 1) * 100 : 0;
        // Jump: how far this frame's line (arc 50..300) is from the previous frame's line, point to nearest segment
        // (a stable path scores 0 even though the player moved along it).
        float jump = 0; var n = 0;
        if (prev.Count >= 2)
            for (var s = 50f; s <= 300f; s += 25f)
            {
                var q = AtArc(drawn, s); var best = float.MaxValue;
                for (var i = 1; i < prev.Count; i++) best = Math.Min(best, Vector2.Distance(Flat(ClosestOnSegment(prev[i - 1], prev[i], q)), Flat(q)));
                jump += best; n++;
            }
        return (back, turnPer100, n > 0 ? jump / n : 0);
    }
}
