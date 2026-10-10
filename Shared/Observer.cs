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
///   observe.events {since?, kinds?, limit?}     kinds: layer | layer.noisy | ui | area | level | entity | hud | agent
/// Runs from Render while on and in game: panels at 10 Hz (2 Hz until the visibility bit is calibrated), area and level
/// every 500 ms, entities every 2 s, layers at their own rates, HUD hiccups every frame (ObserveHud.cs).
/// </summary>
public partial class WhatsAnAiBridge
{
    private sealed class ObserveState
    {
        public bool Enabled;
        public DateTime? Since;
        /// <summary>Layer specs (ObserveLayers.cs); null = the defaults.</summary>
        public List<LayerSpec>? Layers;
    }

    private readonly object _obsLock = new();
    private ObserveState? _obs;
    private readonly List<JObject> _obsEvents = new();
    private long _obsSeq;
    private DateTime _obsLastTick = DateTime.MinValue, _obsLastEntities = DateTime.MinValue, _obsLastPanels = DateTime.MinValue;
    private Dictionary<long, bool>? _obsVisible;              // top-level panel address -> visible
    private Dictionary<long, (int index, UiElement e)>? _obsKids;   // top-level panels by address, listed once a second
    private DateTime _obsKidsAt;
    private readonly HashSet<string> _obsUnmappedSeen = new();   // by first text (stable when indexes shift), else "#index"
    private readonly Dictionary<long, string> _obsPanelNames = new();   // address -> property name seen when it opened
    private readonly HashSet<string> _obsEntityTypes = new();
    private readonly HashSet<string> _obsSeenPaths = new();   // entity paths already examined (string work only once each)
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
        // Continue from the journal: the sequence (an agent waiting with since=<seq> across a HUD restart must not miss
        // events) and what was already seen (entity kinds, unmapped panels), so a restart doesn't report them as new.
        try
        {
            // 16 MB in all, newest last: the rotated journal (ObsFlush rotates at 128 MB) fills what the current one lacks,
            // since right after a rotation the current one is near empty.
            const long budget = 16 * 1024 * 1024;
            var current = Path.Combine(ObsDir, "journal.jsonl");
            var currentBytes = File.Exists(current) ? new FileInfo(current).Length : 0;
            foreach (var (j, bytes) in new[] { (Path.Combine(ObsDir, "journal.1.jsonl"), budget - currentBytes), (current, budget) })
            {
                if (bytes <= 0 || !File.Exists(j)) continue;
                foreach (var line in JournalTail(j, bytes))
                {
                    if (line.Length == 0) continue;
                    JObject e;
                    try { e = JObject.Parse(line); } catch { continue; }
                    _obsSeq = Math.Max(_obsSeq, e["seq"]?.Value<long>() ?? 0);
                    if (e["kind"]?.ToString() == "entity" && e["type"]?.ToString() is { } t) _obsEntityTypes.Add(t);
                    if (e["firstSeen"]?.Value<bool>() == true && e["index"] != null) _obsUnmappedSeen.Add(PanelKey(e["texts"] as JArray, e["index"]!.Value<int>()));
                }
            }
        }
        catch { }
        return _obs;
    }

    /// <summary>
    /// The journal's last maxBytes as lines (the first, partial line skipped). Layer events make the journal grow by
    /// MBs per hour; reading it all at every start would grow with it. The sequence is monotonic, so the tail has the max.
    /// </summary>
    private static IEnumerable<string> JournalTail(string path, long maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var skipFirst = fs.Length > maxBytes;
        if (skipFirst) fs.Seek(-maxBytes, SeekOrigin.End);
        using var rd = new StreamReader(fs);
        if (skipFirst) rd.ReadLine();
        for (string? line; (line = rd.ReadLine()) != null;) yield return line;
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
        "observe.layers" => SafeMemory(LayersList),
        "observe.layer_set" => SafeMemory(() => LayerSet(p)),
        "observe.layer_remove" => SafeMemory(() => LayerRemove(p)),
        "observe.layer_map" => SafeMemory(() => LayerMap(p)),
        "observe.server_map" => SafeMemory(() => LayerMap(new JObject((p as JObject)?.Properties().ToArray() ?? []) { ["layer"] = "server" })),
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
                ["layers"] = new JArray(_layers.Select(LayerStatus)),
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
            // One clock and frame number for every layer, so events from different layers can be lined up.
            e["t"] = Math.Round(ObsClock.Elapsed.TotalMilliseconds, 1);
            e["frame"] = ImGuiNET.ImGui.GetFrameCount();
            _obsEvents.Add(e);
            if (_obsEvents.Count > ObsRing) _obsEvents.RemoveRange(0, _obsEvents.Count - ObsRing);
            _obsPending.Add(e.ToString(Formatting.None));
        }
    }

    private static readonly System.Diagnostics.Stopwatch ObsClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<string> _obsPending = new();
    private DateTime _obsSizeCheckedAt = DateTime.MinValue;
    private const long JournalMaxBytes = 128L * 1024 * 1024;

    /// <summary>Writes the events emitted since the last flush to the journal (once per observer tick, not per event).</summary>
    private void ObsFlush()
    {
        string[] lines;
        lock (_obsLock) { if (_obsPending.Count == 0) return; lines = _obsPending.ToArray(); _obsPending.Clear(); }
        try
        {
            Directory.CreateDirectory(ObsDir);
            var journal = Path.Combine(ObsDir, "journal.jsonl");
            // One generation is kept: at 128 MB the journal becomes journal.1.jsonl (replacing the older one).
            if ((DateTime.UtcNow - _obsSizeCheckedAt).TotalSeconds >= 60)
            {
                _obsSizeCheckedAt = DateTime.UtcNow;
                if (File.Exists(journal) && new FileInfo(journal).Length > JournalMaxBytes)
                    File.Move(journal, Path.Combine(ObsDir, "journal.1.jsonl"), overwrite: true);
            }
            File.AppendAllLines(journal, lines);
        }
        catch { }
    }

    // ── Tick (main thread, from Render) ──────────────────────────────

    private void ObserveTick()
    {
        if (!Obs().Enabled || !GameController.InGame) { _obsFrameAt = 0; return; }
        var now = DateTime.UtcNow;
        ObserveHudFrame();
        try { ObserveLayers(now); } catch (Exception ex) { LogError($"[Observe] layers: {ex.Message}"); }
        // Panels at 10 Hz once the visibility bit is calibrated (8 bytes per panel), so a panel lines up with 10 Hz
        // server changes; 2 Hz while it falls back to IsVisibleLocal (~3.6 KB allocated per panel).
        var panelsDue = (now - _obsLastPanels).TotalMilliseconds >= (_obsFlagOff >= 0 ? 100 : 500);
        var slowDue = (now - _obsLastTick).TotalMilliseconds >= 500;
        if (!panelsDue && !slowDue) return;
        try
        {
            if (slowDue) { _obsLastTick = now; ObserveArea(); }
            _obsLastPanels = now;
            ObservePanels();
            if ((now - _obsLastEntities).TotalSeconds >= 2) { _obsLastEntities = now; ObserveEntities(); }
        }
        catch (Exception ex) { LogError($"[Observe] {ex.Message}"); }
        if (slowDue) ObsFlush();
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

    // IsVisibleLocal on a UI element costs ~3.6 KB of allocation the first time each frame (the HUD caches the whole
    // element struct), ~450 KB per check for IngameUi's ~120 children. The flag is one bit of the element's memory:
    // find which (word offset, bit) matches IsVisibleLocal on every child once, then read 8 bytes per child.
    private int _obsFlagOff = -1, _obsFlagBit = -1;   // -1 = not calibrated yet; _obsFlagOff -2 = no unique match (fall back)

    private bool ObsVisible(UiElement e)
    {
        if (_obsFlagOff >= 0)
        {
            return (RawRead<ulong>(e.Address + _obsFlagOff) >> _obsFlagBit & 1) != 0;
        }
        return e.IsVisibleLocal;
    }

    private void ObsCalibrateFlag(List<UiElement> kids)
    {
        var vis = kids.Select(k => k.IsVisibleLocal).ToArray();
        if (vis.Count(v => v) < 2 || vis.Count(v => !v) < 2) return;   // need both kinds to tell the bit apart
        var mem = kids.Select(k => GameController.Memory.ReadBytes(k.Address, 0x400)).ToArray();
        if (mem.Any(m => m is not { Length: 0x400 })) return;
        var found = new List<(int off, int bit)>();
        for (var off = 0; off + 8 <= 0x400; off += 8)
            for (var bit = 0; bit < 64; bit++)
            {
                var ok = true;
                for (var i = 0; i < kids.Count && ok; i++) ok = ((BitConverter.ToUInt64(mem[i], off) >> bit & 1) != 0) == vis[i];
                if (ok) found.Add((off, bit));
            }
        if (found.Count == 1) (_obsFlagOff, _obsFlagBit) = found[0];
        else _obsFlagOff = -2;   // none or ambiguous: keep the HUD property (correct, just allocating)
        LogMessage(found.Count == 1 ? $"[Observe] visibility flag at +0x{_obsFlagOff:X} bit {_obsFlagBit}" : $"[Observe] visibility flag: {found.Count} candidates, using IsVisibleLocal");
    }

    private void ObservePanels()
    {
        var ui = GameController.IngameState?.IngameUi;
        if (ui == null) return;
        // The top-level children (~120 wrapper objects, ~13 KB) are listed once a second, not on every 10 Hz scan:
        // panels are created at area load and then only shown and hidden.
        var at = DateTime.UtcNow;
        if (_obsKids == null || _obsVisible == null || (at - _obsKidsAt).TotalSeconds >= 1)
        {
            _obsKidsAt = at;
            var kids = ui.Children;
            _obsKids = new Dictionary<long, (int index, UiElement e)>();
            for (int i = 0; i < kids.Count; i++)
                if (kids[i] is { Address: not 0 } k) _obsKids[k.Address] = (i, k);
        }
        var now = _obsKids;
        if (_obsFlagOff == -1) ObsCalibrateFlag(now.Values.Select(v => v.e).ToList());
        if (_obsVisible == null)
        {
            _obsVisible = now.ToDictionary(kv => kv.Key, kv => ObsVisible(kv.Value.e));
            return;
        }
        Dictionary<long, string>? names = null;
        foreach (var (addr, (index, e)) in now)
        {
            var vis = ObsVisible(e);
            var known = _obsVisible.TryGetValue(addr, out var was);
            if (known && was == vis) continue;
            _obsVisible[addr] = vis;
            if (!known && !vis) continue;   // a new panel that is hidden is not an event
            names ??= MappedPanels(ui);
            // Some properties only return their panel while it is open: keep the name seen at opening for the close.
            var mapped = names.TryGetValue(addr, out var n) ? n : null;
            if (vis && mapped != null) _obsPanelNames[addr] = mapped;
            else if (!vis && _obsPanelNames.TryGetValue(addr, out var openedAs)) mapped = openedAs;
            var ev = new JObject
            {
                ["kind"] = "ui", ["index"] = index, ["address"] = $"0x{addr:X}", ["visible"] = vis, ["mapped"] = mapped,
                ["children"] = e.ChildCount,
            };
            if (vis && mapped == null)
            {
                var texts = new JArray(PanelTexts(e, 4, 6));
                ev["texts"] = texts;
                ev["firstSeen"] = _obsUnmappedSeen.Add(PanelKey(texts, index));
                var bytes = GameController.Memory.ReadBytes(addr, 0x200);
                if (bytes != null) ev["snapshot"] = Convert.ToBase64String(bytes);
            }
            ObsEmit(ev);
        }
    }

    /// <summary>Identity of an unmapped panel: its first text if it has one (top-level indexes shift between openings).</summary>
    private static string PanelKey(JArray? texts, int index) => texts?.FirstOrDefault()?.ToString() is { Length: > 0 } t ? t : "#" + index;

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
        if (_obsSeenPaths.Count > 50_000) _obsSeenPaths.Clear();
        foreach (var e in GameController.EntityListWrapper.ValidEntitiesByType.SelectMany(kv => kv.Value))
        {
            var path = e.Path;
            if (string.IsNullOrEmpty(path) || !_obsSeenPaths.Add(path)) continue;   // most paths were seen before: skip the string work
            var parts = path.Split('/');
            var key = string.Join("/", parts.Take(Math.Min(4, parts.Length)));
            if (_obsEntityTypes.Count >= 3000 || !_obsEntityTypes.Add(key)) continue;
            ObsEmit(new JObject { ["kind"] = "entity", ["type"] = key, ["example"] = path, ["entityType"] = e.Type.ToString(), ["area"] = _obsArea });
        }
    }
}
