using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Passive observation: while the user plays, note what happens (read-only, never input) so an agent can map things
/// later without asking: top-level UI panels opening/closing (with the HUD property that maps them, or none), area and
/// level changes, new kinds of entities. Unmapped panels get a raw byte snapshot and their first texts at the moment
/// they open, since they may be closed when the agent looks. Events go to an in-memory ring (sequence numbers) and
/// &lt;BridgeDirectory&gt;\observe\journal.jsonl; the on/off state survives HUD restarts (observe\state.json).
///   observe.start {} / observe.stop {} / observe.status {}
///   observe.events {since?, kinds?, limit?}     kinds: ui | area | level | entity
/// Runs from Render every 500 ms while on and in game (a UI scan costs ~1-3 ms; entities every 2 s).
/// </summary>
public partial class WhatsAnAiBridge
{
    private sealed class ObserveState
    {
        public bool Enabled;
        public DateTime? Since;
    }

    private readonly object _obsLock = new();
    private ObserveState? _obs;
    private readonly List<JObject> _obsEvents = new();
    private long _obsSeq;
    private DateTime _obsLastTick = DateTime.MinValue, _obsLastEntities = DateTime.MinValue;
    private Dictionary<long, bool>? _obsVisible;              // top-level panel address -> visible
    private readonly HashSet<int> _obsUnmappedSeen = new();      // by child index: addresses change on every area change
    private readonly HashSet<string> _obsEntityTypes = new();
    private string? _obsArea;
    private int _obsLevel = -1;
    private const int ObsRing = 1000;

    private string ObsDir => Path.Combine(_bridgeDir, "observe");

    private ObserveState Obs()
    {
        if (_obs != null) return _obs;
        try
        {
            var f = Path.Combine(ObsDir, "state.json");
            _obs = File.Exists(f) ? JsonConvert.DeserializeObject<ObserveState>(File.ReadAllText(f)) ?? new() : new();
        }
        catch { _obs = new(); }
        // Continue the sequence from the journal: an agent waiting with since=<seq> across a HUD restart must not miss events.
        try
        {
            var j = Path.Combine(ObsDir, "journal.jsonl");
            if (File.Exists(j) && File.ReadLines(j).LastOrDefault(l => l.Length > 0) is { } last)
                _obsSeq = JObject.Parse(last)["seq"]?.Value<long>() ?? 0;
        }
        catch { }
        return _obs;
    }

    private void SaveObs()
    {
        try
        {
            Directory.CreateDirectory(ObsDir);
            File.WriteAllText(Path.Combine(ObsDir, "state.json"), JsonConvert.SerializeObject(Obs()));
        }
        catch (Exception ex) { LogError($"[Observe] state save failed: {ex.Message}"); }
    }

    private string? ProcessObserveMethod(string method, JToken? p) => method switch
    {
        "observe.start" => SafeMemory(() => ObserveSet(true)),
        "observe.stop" => SafeMemory(() => ObserveSet(false)),
        "observe.status" => SafeMemory(ObserveStatus),
        "observe.events" => SafeMemory(() => ObserveEvents(p)),
        _ => null,
    };

    private JObject ObserveSet(bool on)
    {
        lock (_obsLock)
        {
            var s = Obs();
            if (s.Enabled != on)
            {
                s.Enabled = on;
                s.Since = on ? DateTime.UtcNow : null;
                _obsVisible = null;   // re-baseline: what is open now is not an event
                SaveObs();
            }
        }
        GuideLog(new JObject { ["text"] = on ? "Claude is observing (read-only): panels, areas, levels" : "Claude stopped observing", ["kind"] = "agent" });
        return ObserveStatus();
    }

    private JObject ObserveStatus()
    {
        lock (_obsLock)
        {
            var s = Obs();
            return new JObject
            {
                ["ok"] = true, ["enabled"] = s.Enabled, ["since"] = s.Since?.ToString("O"), ["seq"] = _obsSeq,
                ["counts"] = JObject.FromObject(_obsEvents.GroupBy(e => e["kind"]!.ToString()).ToDictionary(g => g.Key, g => g.Count())),
                ["unmappedPanelsSeen"] = _obsUnmappedSeen.Count, ["entityTypesSeen"] = _obsEntityTypes.Count,
                ["journal"] = Path.Combine(ObsDir, "journal.jsonl"),
            };
        }
    }

    private JObject ObserveEvents(JToken? p)
    {
        var since = p?["since"]?.Value<long>() ?? 0;
        var kinds = (p?["kinds"] as JArray)?.Select(k => k.ToString()).ToHashSet();
        var limit = Math.Clamp(p?["limit"]?.Value<int>() ?? 100, 1, 500);
        lock (_obsLock)
        {
            var list = _obsEvents.Where(e => e["seq"]!.Value<long>() > since && (kinds == null || kinds.Contains(e["kind"]!.ToString()))).Take(limit).ToList();
            return new JObject { ["ok"] = true, ["enabled"] = Obs().Enabled, ["seq"] = _obsSeq, ["events"] = new JArray(list) };
        }
    }

    private void ObsEmit(JObject e)
    {
        lock (_obsLock)
        {
            e["seq"] = ++_obsSeq;
            e["at"] = DateTime.UtcNow.ToString("O");
            _obsEvents.Add(e);
            if (_obsEvents.Count > ObsRing) _obsEvents.RemoveRange(0, _obsEvents.Count - ObsRing);
        }
        try
        {
            Directory.CreateDirectory(ObsDir);
            File.AppendAllText(Path.Combine(ObsDir, "journal.jsonl"), e.ToString(Formatting.None) + "\n");
        }
        catch { }
    }

    // ── Tick (main thread, from Render) ──────────────────────────────

    private void ObserveTick()
    {
        if (!Obs().Enabled || !GameController.InGame) return;
        var now = DateTime.UtcNow;
        if ((now - _obsLastTick).TotalMilliseconds < 500) return;
        _obsLastTick = now;
        try
        {
            ObserveArea();
            ObservePanels();
            if ((now - _obsLastEntities).TotalSeconds >= 2) { _obsLastEntities = now; ObserveEntities(); }
        }
        catch (Exception ex) { LogError($"[Observe] {ex.Message}"); }
    }

    private void ObserveArea()
    {
        var area = GameController.Area?.CurrentArea?.Name;
        if (area != null && area != _obsArea)
        {
            if (_obsArea != null) ObsEmit(new JObject { ["kind"] = "area", ["from"] = _obsArea, ["to"] = area });
            _obsArea = area;
            _obsVisible = null;   // the UI tree is rebuilt on area change
        }
        var level = GameController.Player?.GetComponent<Player>()?.Level ?? -1;
        if (level > 0 && level != _obsLevel)
        {
            if (_obsLevel > 0) ObsEmit(new JObject { ["kind"] = "level", ["from"] = _obsLevel, ["to"] = level, ["area"] = area });
            _obsLevel = level;
        }
    }

    private void ObservePanels()
    {
        var ui = GameController.IngameState?.IngameUi;
        if (ui == null) return;
        var kids = ui.Children;
        var now = new Dictionary<long, (int index, UiElement e)>();
        for (int i = 0; i < kids.Count; i++)
            if (kids[i] is { Address: not 0 } e) now[e.Address] = (i, e);
        if (_obsVisible == null)
        {
            _obsVisible = now.ToDictionary(kv => kv.Key, kv => kv.Value.e.IsVisibleLocal);
            return;
        }
        Dictionary<long, string>? names = null;
        foreach (var (addr, (index, e)) in now)
        {
            var vis = e.IsVisibleLocal;
            var known = _obsVisible.TryGetValue(addr, out var was);
            if (known && was == vis) continue;
            _obsVisible[addr] = vis;
            if (!known && !vis) continue;   // a new panel that is hidden is not an event
            names ??= MappedPanels(ui);
            var mapped = names.TryGetValue(addr, out var n) ? n : null;
            var ev = new JObject
            {
                ["kind"] = "ui", ["index"] = index, ["address"] = $"0x{addr:X}", ["visible"] = vis, ["mapped"] = mapped,
                ["children"] = e.ChildCount,
            };
            if (vis && mapped == null)
            {
                ev["texts"] = new JArray(PanelTexts(e, 4, 6));
                ev["firstSeen"] = _obsUnmappedSeen.Add(index);
                var bytes = GameController.Memory.ReadBytes(addr, 0x200);
                if (bytes != null) ev["snapshot"] = Convert.ToBase64String(bytes);
            }
            ObsEmit(ev);
        }
    }

    /// <summary>Address -> IngameUIElements property name, for the panels the HUD maps (taken only when something changed).</summary>
    private static Dictionary<long, string> MappedPanels(object ui)
    {
        var d = new Dictionary<long, string>();
        foreach (var p in ui.GetType().GetProperties())
        {
            if (!typeof(UiElement).IsAssignableFrom(p.PropertyType) || p.GetIndexParameters().Length > 0) continue;
            try { if (p.GetValue(ui) is UiElement el && el.Address != 0) d.TryAdd(el.Address, p.Name); } catch { }
        }
        return d;
    }

    /// <summary>The first few non-empty texts in a panel's subtree (breadth-first, bounded): what the panel says it is.</summary>
    private static List<string> PanelTexts(UiElement root, int depth, int max)
    {
        var texts = new List<string>();
        var level = new List<UiElement> { root };
        for (int d = 0; d <= depth && level.Count > 0 && texts.Count < max; d++)
        {
            var next = new List<UiElement>();
            foreach (var e in level.Take(60))
            {
                string? t = null;
                try { t = e.Text; } catch { }
                if (!string.IsNullOrWhiteSpace(t) && t.Length < 80 && !texts.Contains(t)) texts.Add(t);
                if (texts.Count >= max) break;
                try { next.AddRange(e.Children.Where(c => c != null)); } catch { }
            }
            level = next;
        }
        return texts;
    }

    private void ObserveEntities()
    {
        foreach (var e in GameController.EntityListWrapper.ValidEntitiesByType.SelectMany(kv => kv.Value))
        {
            var path = e.Path;
            if (string.IsNullOrEmpty(path)) continue;
            var parts = path.Split('/');
            var key = string.Join("/", parts.Take(Math.Min(4, parts.Length)));
            if (_obsEntityTypes.Count >= 3000 || !_obsEntityTypes.Add(key)) continue;
            ObsEmit(new JObject { ["kind"] = "entity", ["type"] = key, ["example"] = path, ["entityType"] = e.Type.ToString(), ["area"] = _obsArea });
        }
    }
}
