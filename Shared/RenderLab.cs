using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Render Lab: experimental world renderers for the render-fidelity research (scaffolding research/render-fidelity.md),
/// to compare against HealthBars and Radar during movement. Off by default; lab.set turns parts on.
/// The data side lives here; RenderLabDraw.cs draws a LabFrame (RenderLabTypes.cs).
/// - Fresh, time-aligned view: the camera matrix and the player's position are read fresh at draw time (bypassing the
///   page cache, offsets calibrated by matching the HUD's values like tracker.start), kept as a short history, and
///   drawn as of DelayMs ago (default 5 ms: the game image's own latency measured with tools/fidelity).
/// - Walls: rays cast from the player across the walkability grid (RawPathfindingData, 0 = blocked), re-cast only when
///   the player changes grid cell; consecutive hits on the same wall form a run. Projected every frame.
/// - Path: A* to a target (waypoint, area transition, or an entity path substring) on a worker thread when the player
///   changes cell (at most every 150 ms), simplified by line of sight, smoothed (Chaikin, heights too), and trimmed to
///   start at the player's live position so re-plans never draw backwards from the feet.
/// lab.set {walls?, path?, target?, delayMs?}, lab.state.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const float GridToWorld = 250f / 23f;   // world units per grid cell (Radar: TileToWorldConversion / TileToGridConversion)

    private sealed class LabState
    {
        public bool Walls, Path;
        public string Target = "waypoint";
        public double DelayMs = 5;
        // area cache
        public int[][]? Grid; public float[][]? Height; public long AreaKey;
        // fresh view calibration
        public long CamAddress; public int CamOffset = -1, PosOffset = -1; public long PlayerRender;
        public readonly List<(long t, Matrix4x4 m, Vector3 pos)> History = new();
        // walls (world space), re-cast per grid cell
        public (int x, int y) RayCell = (int.MinValue, 0);
        public readonly List<List<(Vector3 ground, float dist)>> WallRuns = new();
        // path (world space)
        public volatile Vector3[]? PathWorld; public string TargetLabel = ""; public long PathChangedAt;
        public (int x, int y) PathCell = (int.MinValue, 0); public long PathPlannedAt; public Task? Planning;
        public string? LastError;
        public readonly LabFrame Frame = new();
    }

    private readonly LabState _lab = new();

    partial void DrawRenderLabImpl(LabFrame f);

    private string? ProcessLabMethod(string method, JToken? p) => method switch
    {
        "lab.set" => SafeMemory(() =>
        {
            if (p?["walls"] is { } w) _lab.Walls = w.Value<bool>();
            if (p?["path"] is { } pa) _lab.Path = pa.Value<bool>();
            if (p?["target"]?.Value<string>() is { Length: > 0 } t) { _lab.Target = t; _lab.PathWorld = null; _lab.PathCell = (int.MinValue, 0); }
            if (p?["delayMs"] is { } d) _lab.DelayMs = Math.Clamp(d.Value<double>(), 0, 100);
            return LabStateJson();
        }),
        "lab.state" => SafeMemory(LabStateJson),
        "lab.compare_paths" => SafeMemory(() => ComparePaths(p)),
        _ => null,
    };

    private JObject LabStateJson() => new()
    {
        ["walls"] = _lab.Walls, ["path"] = _lab.Path, ["target"] = _lab.Target, ["delayMs"] = _lab.DelayMs,
        ["wallRuns"] = _lab.WallRuns.Count, ["pathPoints"] = _lab.PathWorld?.Length ?? 0, ["targetLabel"] = _lab.TargetLabel,
        ["calibrated"] = _lab.CamOffset >= 0 && _lab.PosOffset >= 0, ["error"] = _lab.LastError,
    };

    /// <summary>Called from Render, after the other drawers.</summary>
    private void RenderLabFrame()
    {
        if (!_lab.Walls && !_lab.Path) return;
        if (!GameController.InGame || GameController.Player == null) return;
        try
        {
            if (!LabEnsureArea() || !LabSample(out var m, out var player, out var half)) return;
            var f = _lab.Frame;
            f.Walls.Clear(); f.Path = null;
            f.ShowWalls = _lab.Walls; f.ShowPath = _lab.Path;
            f.Time = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            f.PlayerScreen = Project(m, half, player);
            f.PxPerWorld = Vector2.Distance(f.PlayerScreen, Project(m, half, player + new Vector3(100, 0, 0))) / 100f;
            var cell = ((int)(player.X / GridToWorld), (int)(player.Y / GridToWorld));
            if (_lab.Walls) { LabCastWalls(cell, player); LabProjectWalls(f, m, half, player); }
            if (_lab.Path) { LabPlanPath(cell); LabProjectPath(f, m, half, player); }
            DrawRenderLabImpl(f);
            _lab.LastError = null;
        }
        catch (Exception ex)
        {
            if (_lab.LastError != ex.Message) { _lab.LastError = ex.Message; LogError($"[RenderLab] {ex}"); }
        }
    }

    private bool LabEnsureArea()
    {
        var data = GameController.IngameState.Data;
        var key = GameController.Area.CurrentArea?.Hash ?? 0;
        if (_lab.Grid != null && _lab.AreaKey == key) return true;
        _lab.Grid = data.RawPathfindingData; _lab.Height = data.RawTerrainHeightData; _lab.AreaKey = key;
        _lab.RayCell = (int.MinValue, 0); _lab.PathCell = (int.MinValue, 0); _lab.PathWorld = null; _lab.CamOffset = -1;
        if (_lab.Grid is not { Length: > 0 }) { _lab.LastError = "IngameData.RawPathfindingData is empty (not loaded for this area yet?)"; _lab.Grid = null; return false; }
        return true;
    }

    /// <summary>Fresh camera + player position, time-aligned (DelayMs ago). Calibrates offsets on first use.</summary>
    private bool LabSample(out Matrix4x4 m, out Vector3 player, out Vector2 half)
    {
        var cam = GameController.IngameState.Camera;
        var snap = cam.Snapshot;
        half = snap.HalfSize; m = snap.Matrix;
        var r = GameController.Player.GetComponent<Render>();
        player = r != null ? RenderPosNum(r) : default;
        if (r == null) return false;
        var mem = GameController.Memory;
        if (_lab.CamOffset < 0 || _lab.CamAddress != cam.Address || _lab.PlayerRender != r.Address)
        {
            byte[] cb, rb;
            using (mem.DisableCaching()) { cb = mem.ReadBytes(cam.Address, 0x400); rb = mem.ReadBytes(r.Address, 0x400); }
            _lab.CamOffset = FindFloats(cb, MatrixFloats(snap.Matrix), 1e-3f);
            _lab.PosOffset = FindFloats(rb, [player.X, player.Y, player.Z], 0.5f);
            _lab.CamAddress = cam.Address; _lab.PlayerRender = r.Address; _lab.History.Clear();
            if (_lab.CamOffset < 0 || _lab.PosOffset < 0)
            {
                _lab.LastError = _lab.CamOffset < 0 ? "camera matrix not found in Camera's first 0x400 bytes" : "player position not found in its Render's first 0x400 bytes";
                return true;   // fall back to the HUD's cached view (not time-aligned)
            }
        }
        if (_lab.CamOffset < 0 || _lab.PosOffset < 0) return true;
        byte[] mb, pb;
        using (mem.DisableCaching()) { mb = mem.ReadBytes(_lab.CamAddress + _lab.CamOffset, 64); pb = mem.ReadBytes(r.Address + _lab.PosOffset, 12); }
        if (mb is not { Length: 64 } || pb is not { Length: 12 }) return true;
        var now = Stopwatch.GetTimestamp();
        _lab.History.Add((now, MemoryMarshal.Read<Matrix4x4>(mb), new Vector3(BitConverter.ToSingle(pb, 0), BitConverter.ToSingle(pb, 4), BitConverter.ToSingle(pb, 8))));
        while (_lab.History.Count > 2 && _lab.History[0].t < now - Stopwatch.Frequency / 4) _lab.History.RemoveAt(0);
        var t = now - (long)(_lab.DelayMs * Stopwatch.Frequency / 1000);
        (m, player) = LabStateAt(t);
        return true;
    }

    private (Matrix4x4 m, Vector3 pos) LabStateAt(long t)
    {
        var h = _lab.History;
        if (t <= h[0].t) return (h[0].m, h[0].pos);
        for (var k = h.Count - 1; k > 0; k--)
        {
            if (h[k - 1].t > t) continue;
            var f = h[k].t == h[k - 1].t ? 1f : (float)(t - h[k - 1].t) / (h[k].t - h[k - 1].t);
            return (Matrix4x4.Lerp(h[k - 1].m, h[k].m, f), Vector3.Lerp(h[k - 1].pos, h[k].pos, f));
        }
        return (h[^1].m, h[^1].pos);
    }

    private bool Walkable(int x, int y) => _lab.Grid is { } g && y >= 0 && y < g.Length && x >= 0 && x < g[y].Length && g[y][x] != 0;

    private float GroundZ(float gx, float gy)
    {
        var h = _lab.Height;
        int x = (int)gx, y = (int)gy;
        return h != null && y >= 0 && y < h.Length && x >= 0 && x < h[y].Length ? h[y][x] : 0;
    }

    // ── Walls ──
    private const int LabRays = 360, LabRayCells = 70;

    private void LabCastWalls((int x, int y) cell, Vector3 player)
    {
        if (cell == _lab.RayCell) return;
        _lab.RayCell = cell;
        _lab.WallRuns.Clear();
        float px = player.X / GridToWorld, py = player.Y / GridToWorld;
        List<(Vector3, float)>? run = null;
        Vector2? last = null;
        for (var i = 0; i <= LabRays; i++)
        {
            var a = (i % LabRays) * MathF.Tau / LabRays;
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            Vector2? hit = null;
            for (var s = 0.5f; s < LabRayCells; s += 0.5f)
            {
                float gx = px + dir.X * s, gy = py + dir.Y * s;
                if (Walkable((int)gx, (int)gy)) continue;
                // Thin obstacles (NPCs, props: a few blocked cells with open ground right behind) are not walls: look through them.
                var thin = false;
                for (var k = 1f; k <= 4f && !thin; k += 0.5f) thin = Walkable((int)(gx + dir.X * k), (int)(gy + dir.Y * k));
                if (thin) continue;
                // Refine to sub-cell precision: binary search the walkable/blocked boundary within this half-cell step.
                float lo = s - 0.5f, hi = s;
                for (var k = 0; k < 6; k++)
                {
                    var mid = (lo + hi) / 2;
                    if (Walkable((int)(px + dir.X * mid), (int)(py + dir.Y * mid))) lo = mid; else hi = mid;
                }
                hit = new Vector2(px + dir.X * lo, py + dir.Y * lo);   // the wall's foot
                break;
            }
            // A run continues while consecutive hits stay close (same wall); a miss or a jump starts a new one.
            // Same wall when the step between neighbouring hits is close to the arc between the rays at that distance.
            if (hit is { } h && last is { } l && run != null &&
                Vector2.Distance(h, l) < 0.8f + 2.5f * (MathF.Tau / LabRays) * Vector2.Distance(h, new Vector2(px, py)))
                run.Add((new Vector3(h.X * GridToWorld, h.Y * GridToWorld, GroundZ(h.X, h.Y)), Vector2.Distance(h, new Vector2(px, py)) * GridToWorld));
            else
            {
                if (run is { Count: >= 2 }) _lab.WallRuns.Add(run);
                run = hit is { } h2 ? [(new Vector3(h2.X * GridToWorld, h2.Y * GridToWorld, GroundZ(h2.X, h2.Y)), Vector2.Distance(h2, new Vector2(px, py)) * GridToWorld)] : null;
            }
            last = hit;
        }
        if (run is { Count: >= 2 }) _lab.WallRuns.Add(run);
        // Grid steps show as zigzags: smooth each run (two passes of a 3-point average, ends kept), heights re-read.
        foreach (var r in _lab.WallRuns)
            for (var pass = 0; pass < 2; pass++)
            {
                var src = r.ToArray();
                for (var i = 1; i < src.Length - 1; i++)
                {
                    var a = (src[i - 1].ground + src[i].ground * 2 + src[i + 1].ground) / 4;
                    r[i] = (a with { Z = GroundZ(a.X / GridToWorld, a.Y / GridToWorld) }, src[i].dist);
                }
            }
    }

    private void LabProjectWalls(LabFrame f, Matrix4x4 m, Vector2 half, Vector3 player)
    {
        const float wallHeight = 60f;   // world units drawn as the wall face (up is -Z)
        foreach (var run in _lab.WallRuns)
        {
            var pts = new Vector2[run.Count]; var dist = new float[run.Count]; var hgt = new float[run.Count];
            for (var i = 0; i < run.Count; i++)
            {
                var (g, _) = run[i];
                pts[i] = Project(m, half, g);
                hgt[i] = Vector2.Distance(pts[i], Project(m, half, g with { Z = g.Z - wallHeight }));
                dist[i] = Vector2.Distance(new Vector2(g.X, g.Y), new Vector2(player.X, player.Y));
            }
            f.Walls.Add(new LabWallRun(pts, dist, hgt));
        }
    }

    // ── Path ──
    private void LabPlanPath((int x, int y) cell)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lab.Planning is { IsCompleted: false }) return;
        if (cell == _lab.PathCell && _lab.PathWorld != null) return;
        if (now - _lab.PathPlannedAt < Stopwatch.Frequency * 150 / 1000) return;
        var target = LabFindTarget(out var label);
        if (target == null) { _lab.TargetLabel = $"no target '{_lab.Target}' loaded"; _lab.PathWorld = null; return; }
        _lab.PathCell = cell; _lab.PathPlannedAt = now;
        var grid = _lab.Grid!; var goal = ((int)(target.Value.X / GridToWorld), (int)(target.Value.Y / GridToWorld));
        _lab.Planning = Task.Run(() =>
        {
            var cells = AStar(grid, cell, goal, 200_000);
            if (cells == null) { _lab.TargetLabel = label + " (unreachable)"; return; }
            var simple = StringPull(cells);
            var world = simple.Select(c => new Vector3(c.x * GridToWorld + GridToWorld / 2, c.y * GridToWorld + GridToWorld / 2, GroundZ(c.x, c.y))).ToList();
            for (var k = 0; k < 3; k++) world = Chaikin(world);
            _lab.PathWorld = world.ToArray(); _lab.TargetLabel = label; _lab.PathChangedAt = Stopwatch.GetTimestamp();
        });
    }

    private Vector3? LabFindTarget(out string label)
    {
        var t = _lab.Target;
        var es = GameController.Entities.Where(e => e.IsValid);
        es = t switch
        {
            "waypoint" => es.Where(e => e.Path?.Contains("Waypoint", StringComparison.OrdinalIgnoreCase) == true),
            "transition" => es.Where(e => e.Type == EntityType.AreaTransition),
            _ => es.Where(e => e.Path?.Contains(t, StringComparison.OrdinalIgnoreCase) == true),
        };
        var best = es.OrderBy(e => e.DistancePlayer).FirstOrDefault();
        label = best == null ? "" : t == "transition" ? $"Area transition: {best.RenderName}" : best.RenderName is { Length: > 0 } n ? n : t;
        var r = best?.GetComponent<Render>();
        return r == null ? null : RenderPosNum(r);
    }

    private void LabProjectPath(LabFrame f, Matrix4x4 m, Vector2 half, Vector3 player)
    {
        var w = _lab.PathWorld;
        if (w is not { Length: >= 2 }) return;
        // Start at the player's live position: drop everything before the closest point on the path.
        int seg = 0; float bestD = float.MaxValue; Vector3 start = w[0];
        for (var i = 0; i < w.Length - 1; i++)
        {
            var c = ClosestOnSegment(w[i], w[i + 1], player);
            var d = Vector2.DistanceSquared(new Vector2(c.X, c.Y), new Vector2(player.X, player.Y));
            if (d < bestD) { bestD = d; seg = i; start = c; }
            if (i > 40 && d > bestD * 4) break;   // well past the nearby part
        }
        var pts = new List<Vector2>(); var along = new List<float>();
        float acc = 0; var prev = player with { Z = start.Z };
        void Add(Vector3 p)
        {
            acc += Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(prev.X, prev.Y));
            // resample long world segments so the drawer gets a point every ~12 world units
            var steps = Math.Max(1, (int)(Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(prev.X, prev.Y)) / 12f));
            for (var s = 1; s <= steps; s++)
            {
                var q = Vector3.Lerp(prev, p, s / (float)steps);
                pts.Add(Project(m, half, q)); along.Add(acc - Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(q.X, q.Y)));
            }
            prev = p;
        }
        pts.Add(Project(m, half, prev)); along.Add(0);
        Add(start);
        for (var i = seg + 1; i < w.Length; i++) Add(w[i]);
        f.Path = new LabPath(pts.ToArray(), along.ToArray(), acc, _lab.TargetLabel,
            (float)((Stopwatch.GetTimestamp() - _lab.PathChangedAt) / (double)Stopwatch.Frequency));
    }

    private static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = new Vector2(b.X - a.X, b.Y - a.Y); var len2 = ab.LengthSquared();
        var t = len2 < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(new Vector2(p.X - a.X, p.Y - a.Y), ab) / len2, 0, 1);
        return Vector3.Lerp(a, b, t);
    }

    /// <summary>A* over walkable cells, 8-connected, no corner cutting. Null when unreachable within maxExpand.</summary>
    private static List<(int x, int y)>? AStar(int[][] g, (int x, int y) s, (int x, int y) goal, int maxExpand)
    {
        bool W(int x, int y) => y >= 0 && y < g.Length && x >= 0 && x < g[y].Length && g[y][x] != 0;
        if (!W(s.x, s.y)) s = Nearest(g, s, W); if (!W(goal.x, goal.y)) goal = Nearest(g, goal, W);
        var open = new PriorityQueue<(int x, int y), float>();
        var gScore = new Dictionary<(int, int), float> { [s] = 0 };
        var from = new Dictionary<(int, int), (int, int)>();
        float H((int x, int y) a) { int dx = Math.Abs(a.x - goal.x), dy = Math.Abs(a.y - goal.y); return Math.Max(dx, dy) + 0.4142f * Math.Min(dx, dy); }
        open.Enqueue(s, H(s));
        var expanded = 0;
        while (open.TryDequeue(out var c, out _))
        {
            if (c == goal)
            {
                var path = new List<(int x, int y)> { c };
                while (from.TryGetValue(c, out var p)) { c = p; path.Add(c); }
                path.Reverse();
                return path;
            }
            if (++expanded > maxExpand) return null;
            var gc = gScore[c];
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = c.x + dx, ny = c.y + dy;
                if (!W(nx, ny) || (dx != 0 && dy != 0 && (!W(c.x + dx, c.y) || !W(c.x, c.y + dy)))) continue;
                var ng = gc + (dx != 0 && dy != 0 ? 1.4142f : 1f);
                if (gScore.TryGetValue((nx, ny), out var old) && old <= ng) continue;
                gScore[(nx, ny)] = ng; from[(nx, ny)] = c;
                open.Enqueue((nx, ny), ng + H((nx, ny)));
            }
        }
        return null;
    }

    private static (int x, int y) Nearest(int[][] g, (int x, int y) c, Func<int, int, bool> w)
    {
        for (var r = 1; r < 30; r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
            if (w(c.x + dx, c.y + dy)) return (c.x + dx, c.y + dy);
        return c;
    }

    /// <summary>Keep only the cells needed to see the next one (line of sight over walkable cells).</summary>
    private List<(int x, int y)> StringPull(List<(int x, int y)> cells)
    {
        var outp = new List<(int x, int y)> { cells[0] };
        var i = 0;
        while (i < cells.Count - 1)
        {
            var j = cells.Count - 1;
            while (j > i + 1 && !LineWalkable(cells[i], cells[j])) j--;
            outp.Add(cells[j]); i = j;
        }
        return outp;
    }

    private bool LineWalkable((int x, int y) a, (int x, int y) b)
    {
        var n = Math.Max(Math.Abs(b.x - a.x), Math.Abs(b.y - a.y)) * 2;
        for (var k = 0; k <= n; k++)
        {
            var t = n == 0 ? 0 : k / (float)n;
            float x = a.x + (b.x - a.x) * t + 0.5f, y = a.y + (b.y - a.y) * t + 0.5f;
            // a margin of one cell so the line doesn't hug corners
            if (!Walkable((int)x, (int)y) || !Walkable((int)(x + 0.6f), (int)y) || !Walkable((int)(x - 0.6f), (int)y) ||
                !Walkable((int)x, (int)(y + 0.6f)) || !Walkable((int)x, (int)(y - 0.6f))) return false;
        }
        return true;
    }

    private static List<Vector3> Chaikin(List<Vector3> p)
    {
        if (p.Count < 3) return p;
        var o = new List<Vector3>(p.Count * 2) { p[0] };
        for (var i = 0; i < p.Count - 1; i++)
        {
            o.Add(Vector3.Lerp(p[i], p[i + 1], 0.25f));
            o.Add(Vector3.Lerp(p[i], p[i + 1], 0.75f));
        }
        o.Add(p[^1]);
        return o;
    }
}
