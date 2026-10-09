using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// Render Lab "bars": a comparison marker at the exact anchor HealthBars uses for each nearby monster and player
/// (Entity.Pos, raised by 2 x Render.Bounds.Z unless "Place Bar Relative To Ground Level", plus its Z offset; read from
/// HealthBars' live settings by reflection), projected from fresh, time-aligned data like the rest of the lab. Drawn
/// as a thin gold bracket (two end ticks and a centre tick, 60 px wide) so the user can see, side by side while
/// moving, whether HealthBars' bar stays centred on where the entity really is. Measurement aid, not a replacement bar.
/// lab.set {bars: true}.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int LabBarMax = 30;
    private readonly Dictionary<long, (long t0, Vector3 p0, long t1, Vector3 p1)> _labBarHist = new();
    private (bool groundLevel, float zOffset, long readAt) _hbAnchor = (false, 0, 0);

    private void LabDrawBars(Matrix4x4 m, Vector2 half)
    {
        if (_lab.PosOffset < 0) return;
        var now = Stopwatch.GetTimestamp();
        if (now - _hbAnchor.readAt > Stopwatch.Frequency) _hbAnchor = (HealthBarsSetting<bool>("PlaceBarRelativeToGroundLevel"), HealthBarsSetting<float>("GlobalZOffset"), now);
        var t = now - (long)(_lab.DelayMs * Stopwatch.Frequency / 1000);
        var dl = ImGui.GetBackgroundDrawList();
        var gold = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.82f, 0.35f, 0.95f));
        var ink = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.55f));
        var mem = GameController.Memory;
        var seen = new HashSet<long>();
        var list = GameController.EntityListWrapper.ValidEntitiesByType;
        var es = (list.TryGetValue(EntityType.Monster, out var mons) ? mons.Where(e => e.IsAlive && e.IsHostile) : [])
            .Concat(list.TryGetValue(EntityType.Player, out var pls) ? pls : [])
            .Where(e => e.DistancePlayer < 120).OrderBy(e => e.DistancePlayer).Take(LabBarMax);
        foreach (var e in es)
        {
            var r = e.GetComponent<Render>();
            if (r == null) continue;
            byte[] b;
            using (mem.DisableCaching()) b = mem.ReadBytes(r.Address + _lab.PosOffset, 12);
            if (b is not { Length: 12 }) continue;
            var p = new Vector3(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8));
            seen.Add(e.Id);
            var h = _labBarHist.TryGetValue(e.Id, out var old) ? (old.t1, old.p1, now, p) : (now, p, now, p);
            _labBarHist[e.Id] = h;
            // Time-aligned: the position as of t, between the last two fresh samples (clamped).
            var f = h.Item3 == h.Item1 ? 1f : Math.Clamp((float)(t - h.Item1) / (h.Item3 - h.Item1), 0f, 1f);
            var pos = Vector3.Lerp(h.Item2, h.Item4, f);
            if (!_hbAnchor.groundLevel) pos.Z -= 2 * RenderBoundsZ(r);
            pos.Z += _hbAnchor.zOffset;
            var s = Project(m, half, pos);
            if (s.X < -50 || s.Y < -50 || s.X > half.X * 2 + 50 || s.Y > half.Y * 2 + 50) continue;
            foreach (var (c, w) in new[] { (ink, 3f), (gold, 1.4f) })
            {
                dl.AddLine(s + new Vector2(-30, -5), s + new Vector2(-30, 5), c, w);
                dl.AddLine(s + new Vector2(30, -5), s + new Vector2(30, 5), c, w);
                dl.AddLine(s + new Vector2(-30, 5), s + new Vector2(30, 5), c, w * 0.7f);
                dl.AddLine(s + new Vector2(0, 2), s + new Vector2(0, 8), c, w);
            }
        }
        foreach (var gone in _labBarHist.Keys.Where(k => !seen.Contains(k)).ToList()) _labBarHist.Remove(gone);
    }

    /// <summary>A HealthBars setting's current value (ToggleNode/RangeNode .Value), or default when HealthBars or the setting is missing.</summary>
    private static T HealthBarsSetting<T>(string name)
    {
        try
        {
            var wrapper = Core.Current?.pluginManager?.Plugins.FirstOrDefault(w => w.Name == "HealthBars");
            if (wrapper == null) return default!;
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var plugin = wrapper.GetType().GetProperty("Plugin", any)?.GetValue(wrapper) ?? wrapper.GetType().GetField("_plugin", any)?.GetValue(wrapper);
            var settings = plugin?.GetType().GetProperty("Settings", any)?.GetValue(plugin);
            var node = settings?.GetType().GetProperty(name, any)?.GetValue(settings);
            var v = node?.GetType().GetProperty("Value")?.GetValue(node);
            return v is T tv ? tv : v is IConvertible c ? (T)c.ToType(typeof(T), null) : default!;
        }
        catch { return default!; }
    }
}
