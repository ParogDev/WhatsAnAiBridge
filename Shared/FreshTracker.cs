using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Overlay accuracy probe for the render-fidelity work (scaffolding research/render-fidelity.md). Every frame, at the end
/// of the bridge's Render (as late as a plugin can draw), it projects the nearest players twice:
///   hud   = what plugins draw: the HUD's camera snapshot and its cached Render position (Camera.Snapshot, Render.Pos);
///   fresh = the same camera matrix and position read from the game right now, bypassing the page cache.
/// The pixel distance between the two is how far the HUD's drawing is from where the game currently has the target,
/// split into the camera's share and the position's share. With draw=true it also marks both on screen (fresh: cyan
/// square, HUD: magenta square; pure colours for tools/fidelity) for screenshots. path= tracks other entities.
/// Offsets are not hard-coded: at start the matrix and the position are located in fresh bytes by matching the HUD's own
/// values (camera +0x100 and Render +0x138 on PoE2 in 2026-10). If a match is lost the start fails naming that link.
/// Read-only (game memory reads like any HUD read; nothing patched). tracker.start {durationMs?, entities?, draw?, path?, entityId?, delayMs?, delays?: [ms, up to 3]} -> {id};
/// tracker.result {id}; tracker.stop.
/// </summary>
public partial class WhatsAnAiBridge
{
    private sealed class TrackerJob
    {
        public string Id = "";
        public DateTime Until;
        public bool Draw;
        public double DelayMs;   // cyan marker shows the fresh state from this long ago (game image latency compensation)
        public double[] ExtraDelays = [];   // up to 3 more markers (yellow, green, blue) at these delays: one pan calibrates them all
        public readonly List<(long t, Matrix4x4 m, Vector3[] pos)> History = new();
        public int CamOffset, PosOffset;
        public long CamAddress;
        public List<(Entity e, Render r, string name)> Targets = new();
        public List<double> Total = new(), CameraPart = new(), PositionPart = new();
        public int Frames, FramesCameraStale, FramesPositionStale, SecondCopyMatches, Reads, ReadFailures;
        public double MathCheckPx;
        public JObject? Result;
    }

    private TrackerJob? _tracker;
    private readonly Dictionary<string, JObject> _trackerResults = new();

    private string? ProcessTrackerMethod(string method, JToken? p) => method switch
    {
        "tracker.start" => SafeMemory(() => TrackerStart(p)),
        "tracker.result" => SafeMemory(() =>
        {
            var id = p?["id"]?.ToString() ?? "";
            if (_tracker is { } t && t.Id == id) return new JObject { ["id"] = id, ["status"] = "running", ["frames"] = t.Frames };
            lock (_trackerResults) return _trackerResults.TryGetValue(id, out var r) ? r : Err("unknown_id", "No such tracker run.");
        }),
        "tracker.stop" => SafeMemory(() => { if (_tracker is { } t) t.Until = DateTime.MinValue; return new JObject { ["ok"] = true }; }),
        _ => null,
    };

    private JObject TrackerStart(JToken? p)
    {
        if (_tracker != null) return Err("busy", "A tracker run is in progress.");
        if (!GameController.InGame) return Err("not_in_game", "Not in game.");
        var duration = Math.Clamp(p?["durationMs"]?.Value<int>() ?? 5000, 500, 60_000);
        var max = Math.Clamp(p?["entities"]?.Value<int>() ?? 8, 1, 32);
        var cam = GameController.IngameState.Camera;
        var snap = cam.Snapshot;
        var m = GameController.Memory;

        // Calibrate: where the matrix and the position are in fresh bytes (first offset whose bytes equal the HUD's value).
        byte[] camBytes;
        using (m.DisableCaching()) camBytes = m.ReadBytes(cam.Address, 0x400);
        var camOff = FindFloats(camBytes, MatrixFloats(snap.Matrix), 1e-3f);
        if (camOff < 0) return Err("calibration_failed", $"Camera matrix not found in the first 0x400 bytes of Camera @0x{cam.Address:X} (does Camera.Snapshot.Matrix still come from the camera struct?)");
        // Players by default (they move); path= tracks any entity whose metadata path contains it (static anchors).
        var pathFilter = p?["path"]?.Value<string>();
        var entityId = p?["entityId"]?.Value<long?>();
        IEnumerable<Entity> players = entityId is { } eid ? GameController.Entities.Where(e => e.Id == eid)
            : string.IsNullOrWhiteSpace(pathFilter)
            ? GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Player, out var list) ? list : []
            : GameController.Entities.Where(e => e.Path?.Contains(pathFilter, StringComparison.OrdinalIgnoreCase) == true);
        var job = new TrackerJob { Id = Guid.NewGuid().ToString("N")[..10], Until = DateTime.UtcNow.AddMilliseconds(duration), Draw = p?["draw"]?.Value<bool>() ?? false, DelayMs = Math.Clamp(p?["delayMs"]?.Value<double>() ?? 0, 0, 100), ExtraDelays = (p?["delays"] as JArray)?.Select(x => Math.Clamp(x.Value<double>(), 0, 100)).Take(3).ToArray() ?? [], CamOffset = camOff, CamAddress = cam.Address, PosOffset = -1 };
        foreach (var e in players.OrderBy(e => e.DistancePlayer).Take(max))
        {
            var r = e.GetComponent<Render>();
            if (r == null || r.Address == 0) continue;
            if (job.PosOffset < 0)
            {
                byte[] rb;
                using (m.DisableCaching()) rb = m.ReadBytes(r.Address, 0x400);
                var pos = RenderPosNum(r);
                job.PosOffset = FindFloats(rb, [pos.X, pos.Y, pos.Z], 0.5f);
                if (job.PosOffset < 0) return Err("calibration_failed", $"Render position {pos} not found in the first 0x400 bytes of {e.RenderName}'s Render @0x{r.Address:X}");
            }
            job.Targets.Add((e, r, e.RenderName ?? e.Path ?? "?"));
        }
        if (job.Targets.Count == 0) return Err("no_targets", string.IsNullOrWhiteSpace(pathFilter) ? "No players nearby to track (the tracker follows the nearest players, including yours)." : $"No entity whose path contains '{pathFilter}' is loaded.");
        _tracker = job;
        return new JObject
        {
            ["id"] = job.Id, ["status"] = "running", ["durationMs"] = duration,
            ["calibration"] = new JObject { ["cameraMatrixOffset"] = $"0x{camOff:X}", ["renderPositionOffset"] = $"0x{job.PosOffset:X}" },
            ["targets"] = new JArray(job.Targets.Select(t => t.name)),
        };
    }

    /// <summary>Runs at the end of Render: one sample per frame while a tracker run is active.</summary>
    private void TrackerFrame()
    {
        MotionTick();
        var job = _tracker;
        if (job == null) return;
        if (DateTime.UtcNow > job.Until || !GameController.InGame) { FinishTracker(job); return; }
        try
        {
            var snap = GameController.IngameState.Camera.Snapshot;
            var m = GameController.Memory;
            byte[] camBytes;
            var fresh = new List<(Vector3 pos, bool ok)>(job.Targets.Count);
            using (m.DisableCaching())
            {
                camBytes = m.ReadBytes(job.CamAddress + job.CamOffset, 0x80);
                foreach (var (_, r, _) in job.Targets)
                {
                    var b = m.ReadBytes(r.Address + job.PosOffset, 12);
                    job.Reads++;
                    fresh.Add(b is { Length: 12 } ? (new Vector3(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8)), true) : (default, false));
                }
            }
            if (camBytes is not { Length: 0x80 }) { job.ReadFailures++; return; }
            var freshM = MemoryMarshal.Read<Matrix4x4>(camBytes);
            var second = MemoryMarshal.Read<Matrix4x4>(camBytes.AsSpan(0x40));
            job.Frames++;
            if (freshM != snap.Matrix) job.FramesCameraStale++;
            if (second == snap.Matrix && freshM != snap.Matrix) job.SecondCopyMatches++;
            var nowT = System.Diagnostics.Stopwatch.GetTimestamp();
            job.History.Add((nowT, freshM, fresh.Select(f => f.pos).ToArray()));
            while (job.History.Count > 2 && job.History[0].t < nowT - System.Diagnostics.Stopwatch.Frequency / 4) job.History.RemoveAt(0);
            var (delayedM, delayedPos) = job.DelayMs > 0 ? StateAt(job.History, nowT - (long)(job.DelayMs * System.Diagnostics.Stopwatch.Frequency / 1000)) : (freshM, null);
            var half = snap.HalfSize;
            var dl = job.Draw ? ImGui.GetForegroundDrawList() : default;
            var anyPosStale = false;
            for (var i = 0; i < job.Targets.Count; i++)
            {
                var (e, r, _) = job.Targets[i];
                if (!fresh[i].ok || !e.IsValid) { job.ReadFailures++; continue; }
                var cachedPos = RenderPosNum(r);
                var hud = snap.WorldToScreen(cachedPos);
                if (job.Frames == 1) job.MathCheckPx = Math.Max(job.MathCheckPx, Vector2.Distance(hud, Project(snap.Matrix, half, cachedPos)));
                var now = Project(freshM, half, fresh[i].pos);
                if (!InView(hud, half) && !InView(now, half)) continue;
                if (fresh[i].pos != cachedPos) anyPosStale = true;
                job.Total.Add(Vector2.Distance(hud, now));
                job.CameraPart.Add(Vector2.Distance(hud, Project(freshM, half, cachedPos)));
                job.PositionPart.Add(Vector2.Distance(hud, snap.WorldToScreen(fresh[i].pos)));
                if (job.Draw)
                {
                    // Pure colours a screenshot tool can find: magenta = the HUD's projection 6 px above the point, cyan = fresh 6 px
                    // below (apart, so neither hides the other; tools/fidelity measures drift relative to the first frame).
                    dl.AddRectFilled(hud - new Vector2(2, 8), hud + new Vector2(3, -3), ImGui.GetColorU32(new Vector4(1f, 0f, 1f, 1f)));
                    var shown = delayedPos == null ? now : Project(delayedM, half, delayedPos[i]);
                    dl.AddRectFilled(shown - new Vector2(2, -4), shown + new Vector2(3, 9), ImGui.GetColorU32(new Vector4(0f, 1f, 1f, 1f)));
                    // Extra delays: yellow 8 px left, green 8 px right, blue 14 px below (constant offsets: drift is measured
                    // relative to the first frame).
                    for (var x = 0; x < job.ExtraDelays.Length; x++)
                    {
                        var (xm, xp) = StateAt(job.History, nowT - (long)(job.ExtraDelays[x] * System.Diagnostics.Stopwatch.Frequency / 1000));
                        var q = Project(xm, half, xp[i]) + (x switch { 0 => new Vector2(-8, 0), 1 => new Vector2(8, 0), _ => new Vector2(0, 14) });
                        var col = x switch { 0 => new Vector4(1f, 1f, 0f, 1f), 1 => new Vector4(0f, 1f, 0f, 1f), _ => new Vector4(0f, 0f, 1f, 1f) };
                        dl.AddRectFilled(q - new Vector2(2, 2), q + new Vector2(3, 3), ImGui.GetColorU32(col));
                    }
                }
            }
            if (anyPosStale) job.FramesPositionStale++;
        }
        catch { job.ReadFailures++; }
    }

    private void FinishTracker(TrackerJob job)
    {
        _tracker = null;
        static JObject Px(List<double> xs)
        {
            if (xs.Count == 0) return new JObject { ["n"] = 0 };
            var a = xs.OrderBy(x => x).ToArray();
            return new JObject { ["n"] = a.Length, ["avg"] = Math.Round(a.Average(), 2), ["p50"] = Math.Round(a[a.Length / 2], 2), ["p95"] = Math.Round(a[(int)(a.Length * 0.95)], 2), ["max"] = Math.Round(a[^1], 2), ["over1px"] = a.Count(x => x > 1) };
        }
        var r = new JObject
        {
            ["id"] = job.Id, ["status"] = "done", ["frames"] = job.Frames, ["targets"] = new JArray(job.Targets.Select(t => t.name)),
            ["calibration"] = new JObject { ["cameraMatrixOffset"] = $"0x{job.CamOffset:X}", ["renderPositionOffset"] = $"0x{job.PosOffset:X}" },
            // Self-check: our projection of the HUD's own inputs must land where the HUD's does (else the numbers are ours).
            ["projectionSelfCheckPx"] = Math.Round(job.MathCheckPx, 4),
            ["errorPx"] = Px(job.Total), ["cameraPartPx"] = Px(job.CameraPart), ["positionPartPx"] = Px(job.PositionPart),
            ["framesCameraStale"] = job.FramesCameraStale, ["framesPositionStale"] = job.FramesPositionStale,
            // The camera struct holds a second copy of the matrix 0x40 later: does the HUD's (stale) one equal it?
            ["staleCameraEqualsSecondCopy"] = job.SecondCopyMatches,
            ["reads"] = job.Reads, ["readFailures"] = job.ReadFailures,
        };
        lock (_trackerResults)
        {
            if (_trackerResults.Count >= 10) _trackerResults.Remove(_trackerResults.Keys.First());
            _trackerResults[job.Id] = r;
        }
    }

    /// <summary>The fresh state at time t, linearly interpolated between the two samples around it (clamped to the oldest).</summary>
    private static (Matrix4x4 m, Vector3[] pos) StateAt(List<(long t, Matrix4x4 m, Vector3[] pos)> h, long t)
    {
        if (t <= h[0].t) return (h[0].m, h[0].pos);
        for (var k = h.Count - 1; k > 0; k--)
        {
            if (h[k - 1].t > t) continue;
            var (a, b) = (h[k - 1], h[k]);
            var f = b.t == a.t ? 1f : (float)(t - a.t) / (b.t - a.t);
            return (Matrix4x4.Lerp(a.m, b.m, f), a.pos.Zip(b.pos, (x, y) => Vector3.Lerp(x, y, f)).ToArray());
        }
        return (h[^1].m, h[^1].pos);
    }

    private static Vector2 Project(Matrix4x4 m, Vector2 half, Vector3 v)
    {
        var c = Vector4.Transform(new Vector4(v, 1), m);
        c /= c.W;
        return new Vector2((c.X + 1f) * half.X, (1f - c.Y) * half.Y);
    }

    private static bool InView(Vector2 s, Vector2 half) => s.X >= 0 && s.Y >= 0 && s.X <= half.X * 2 && s.Y <= half.Y * 2;

    private static float[] MatrixFloats(Matrix4x4 m) => [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    private static int FindFloats(byte[] buf, float[] want, float tol)
    {
        for (var off = 0; off + want.Length * 4 <= buf.Length; off += 4)
        {
            var ok = true;
            for (var i = 0; i < want.Length && ok; i++) ok = Math.Abs(BitConverter.ToSingle(buf, off + i * 4) - want[i]) <= tol;
            if (ok) return off;
        }
        return -1;
    }
}
