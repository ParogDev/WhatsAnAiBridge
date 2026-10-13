using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// UI catalogue: while observing, every top-level IngameUi panel that opens gets a bounded content snapshot a few frames
/// later (it fills after it opens) and again after each mouse click while it is open (a dialog moves to its next page),
/// so agents learn the interfaces the user meets - NPC dialogues and their options, travel menus, choice and reward
/// panels - and can later point at an option with guide.highlight. One entry per distinct interface, keyed by a stable
/// fingerprint (mapped HUD property + title text, never the index or address); each option carries a ready-to-use
/// highlight target. Read-only, never input. Persisted to &lt;BridgeDirectory&gt;\observe\ui-catalogue.json.
///   observe.ui_catalogue {query?, key?, options?: bool}
/// Cost: a capture walks at most UcMaxVisit visible elements (~13 us and ~5 KB each, measured live), sliced to UcSliceUs
/// per frame, one capture at a time; it runs only on an opening and after clicks. The reply's cost has the last and worst
/// capture (total us / bytes, frames it spread over) and the worst single frame.
/// </summary>
public partial class WhatsAnAiBridge
{
    internal sealed class UiCatalogueFile
    {
        [JsonProperty("version")] public int Version = 1;
        [JsonProperty("entries")] public List<UiCatalogueEntry> Entries = [];
        [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
    }

    /// <summary>One distinct interface (an NPC's dialogue, a travel menu, a choice panel).</summary>
    internal sealed class UiCatalogueEntry
    {
        /// <summary>Fingerprint: "&lt;mapped HUD property or panel&gt;|&lt;title&gt;".</summary>
        [JsonProperty("key")] public string Key = "";
        /// <summary>IngameUi property that returns this panel, when the HUD maps it (e.g. NpcDialog).</summary>
        [JsonProperty("mapped")] public string? Mapped;
        /// <summary>The panel's first text (breadth-first): what guide.highlight's panel / within match on.</summary>
        [JsonProperty("title")] public string? Title;
        [JsonProperty("npc", NullValueHandling = NullValueHandling.Ignore)] public string? Npc;
        /// <summary>How to recognise it: its first texts.</summary>
        [JsonProperty("recognise")] public List<string> Recognise = [];
        [JsonProperty("areas")] public List<string> Areas = [];
        [JsonProperty("firstSeen")] public DateTime FirstSeen;
        [JsonProperty("lastSeen")] public DateTime LastSeen;
        /// <summary>Openings seen.</summary>
        [JsonProperty("opened")] public int Opened;
        /// <summary>Distinct text sets seen (pages of a dialogue).</summary>
        [JsonProperty("views")] public int Views;
        [JsonProperty("viewHashes")] public List<int> ViewHashes = [];
        /// <summary>Panel rect at the last capture: x, y, w, h.</summary>
        [JsonProperty("rect")] public float[]? Rect;
        [JsonProperty("options")] public List<UiCatalogueOption> Options = [];
        [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
    }

    /// <summary>A visible text element in the interface (an option, a label, a title).</summary>
    internal sealed class UiCatalogueOption
    {
        [JsonProperty("text")] public string Text = "";
        /// <summary>Child indexes from the panel (top-level IngameUi child) to the element.</summary>
        [JsonProperty("child")] public int[] Child = [];
        [JsonProperty("rect")] public float[]? Rect;
        /// <summary>A hint, PoE2 only (always false on PoE1): the element or its direct parent reacts to hover (ElementFlags.DetectsHover; the panel root never counts).
        /// False does not mean unclickable (dialogue buttons often lack the flag).</summary>
        [JsonProperty("clickable")] public bool Clickable;
        /// <summary>Its text appears once in the view it was seen in, so a text target hits only it.</summary>
        [JsonProperty("unique")] public bool Unique;
        [JsonProperty("lastSeen")] public DateTime LastSeen;
        [JsonProperty("seen")] public int Seen;
        /// <summary>Ready-to-use guide.highlight / highlight target (add label, tier, durationSec as needed).</summary>
        [JsonProperty("highlight")] public UiHighlightRecipe Highlight = new();
        [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
    }

    internal sealed class UiHighlightRecipe
    {
        [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)] public string? Text;
        [JsonProperty("within", NullValueHandling = NullValueHandling.Ignore)] public string? Within;
        [JsonProperty("panel", NullValueHandling = NullValueHandling.Ignore)] public string? Panel;
        [JsonProperty("child", NullValueHandling = NullValueHandling.Ignore)] public int[]? Child;
    }

    private sealed class UcOpen
    {
        public UiElement E = null!;
        public string? Mapped;
        public DateTime OpenedAt;
        public DateTime? DueAt;
        public int Captures;
        public int LastHash;
        public UcWalk? Walk;   // the capture in progress (sliced over frames)
        public RectangleF? Rect;   // panel rect at the last capture start (click hit-test)
    }

    private const int UcMaxVisit = 600, UcMaxDepth = 14, UcMaxTexts = 80, UcMaxEntries = 400, UcMaxOptions = 120, UcMaxCaptures = 40;
    /// <summary>Walk budget per frame, us: ~13 us per element, so ~40 elements a frame.</summary>
    private const double UcSliceUs = 500;
    /// <summary>Child visibility checks per capture: bounds a panel with thousands of hidden children.</summary>
    private const int UcMaxChildChecks = 3000;
    /// <summary>An element with more children than this is not opened (counted in cost.lastSkippedBig).</summary>
    private const int UcMaxChildren = 300;
    private readonly Dictionary<long, UcOpen> _ucOpen = new();
    private UiCatalogueFile? _uc;
    private volatile bool _ucDirty;   // set by merges (main thread), cleared and restored by the writer
    private bool _ucMouseWasDown, _ucWarmed;
    private int _ucWarmTries;
    private double _ucWarmUs;
    private double _ucLastUs, _ucMaxUs, _ucMaxFrameUs, _ucFirstFrameUs;
    private long _ucLastBytes, _ucMaxBytes;
    private int _ucLastVisited, _ucLastFrames, _ucLastSkippedBig, _ucCaptures;
    private string? _ucLastError;

    private string UcPath => Path.Combine(ObsDir, "ui-catalogue.json");

    private UiCatalogueFile Uc()
    {
        if (_uc != null) return _uc;
        UiCatalogueFile loaded;
        try { loaded = File.Exists(UcPath) ? JsonConvert.DeserializeObject<UiCatalogueFile>(File.ReadAllText(UcPath)) ?? new() : new(); }
        catch (Exception ex)
        {
            // Never let the next write replace a catalogue that failed to load: keep it aside, start a new one.
            string? kept = null;
            try { kept = UcPath + $".bad-{DateTime.UtcNow:yyyyMMddHHmmss}"; File.Move(UcPath, kept); } catch { kept = null; }
            _ucLastError = $"catalogue load failed ({UcPath}): {ex.Message}; " + (kept != null ? $"kept as {kept}, starting a new one" : "could not move it aside: writes are off");
            if (kept == null) _ucNoWrite = true;
            loaded = new();
        }
        // A hand-edited or partial file may hold nulls: never let them reach a merge.
        loaded.Entries = (loaded.Entries ?? []).Where(x => x != null && !string.IsNullOrEmpty(x.Key)).ToList();
        foreach (var x in loaded.Entries)
        {
            x.Recognise ??= []; x.Areas ??= []; x.ViewHashes ??= [];
            x.Options = (x.Options ?? []).Where(op => op != null && op.Text != null).ToList();
            foreach (var op in x.Options) { op.Child ??= []; op.Highlight ??= new(); }
        }
        lock (_ucLock) _uc ??= loaded;
        return _uc;
    }

    private System.Threading.Tasks.Task? _ucLoading;

    /// <summary>JIT-compiles the capture path off the frame (pool thread): its first run in a frame cost 6-8 ms of
    /// compiling, measured live. No game UI is touched here (the HUD's element caches belong to the render thread):
    /// UcStep / UcFinish are prepared, and UcMerge runs once on synthetic texts into a scratch catalogue, which also
    /// instantiates its LINQ and tuple generics.</summary>
    private void UcPrepare()
    {
        try
        {
            foreach (var name in new[] { nameof(UcStep), nameof(UcFinish), nameof(UiDetectsHover) })
                if (typeof(WhatsAnAiBridge).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public) is { } m)
                    System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle);
            // The HUD's element members the walk calls (compiled lazily too) and the walk's generic shapes, with no game read.
            foreach (var m in typeof(UiElement).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                if (m.Name is "get_Text" or "get_Children" or "GetClientRect" or "get_IsVisibleLocal" or "HasFlag" or "get_Address"
                    && !m.ContainsGenericParameters)
                    System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle);
            var o = new UcOpen { E = null!, OpenedAt = DateTime.UtcNow };
            UcMerge(new UiCatalogueFile(), null, o, [("warm", [0], [0f, 0f, 1f, 1f], false), ("up", [1], null, true), ("up", [2], null, false)]);
        }
        catch (Exception ex) { _ucLastError = $"warm-up (pool thread) failed: {ex.GetType().Name}: {ex.Message}"; }
    }
    private bool UcGameInFront()
    {
        try { return GameController.Window.IsForeground(); } catch { return false; }
    }

    private readonly object _ucLock = new();   // the catalogue: merges (main thread), the writer and queries

    /// <summary>From ObservePanels: a top-level panel became visible / hidden.</summary>
    private void UcPanelChanged(long addr, UiElement e, bool visible, string? mapped)
    {
        if (!visible) { _ucOpen.Remove(addr); return; }
        _ucOpen[addr] = new UcOpen { E = e, Mapped = mapped, OpenedAt = DateTime.UtcNow, DueAt = DateTime.UtcNow.AddMilliseconds(300) };
    }

    /// <summary>From ObserveTick, every frame while observing: due captures, and a capture after each click.</summary>
    private void UcTick(DateTime now)
    {
        if (!_ucWarmed)
        {
            // First observer tick of this HUD load (the HUD is just starting: its own startup hitches dwarf this): one
            // short real walk of IngameUi on this thread JIT-compiles the HUD element calls the walk makes. Measured live,
            // leaving that to the first capture cost one 6.5-8.5 ms frame while playing; pool-thread warm-ups that touch
            // no UI only got part of it, and a walk off the render thread races the HUD's element caches.
            // Retried on the next ticks while IngameUi is not there yet or the walk throws (3 tries, then the first
            // capture pays the JIT). Per bridge load: a reload is a new instance and warms again.
            _ucLoading ??= System.Threading.Tasks.Task.Run(() => { UcPrepare(); Uc(); });
            var sw0 = Stopwatch.StartNew();
            _ucWarmTries++;
            try
            {
                if (GameController?.IngameState?.IngameUi is UiElement ui)
                {
                    var wo = new UcOpen { E = ui, OpenedAt = DateTime.UtcNow, Walk = new UcWalk() };
                    wo.Walk.Queue.Enqueue((ui, [], false));
                    UcStep(wo, sw0, 3000);
                    _ucWarmed = true;
                }
                else _ucLastError = "warm-up: GameController.IngameState.IngameUi is null";
            }
            catch (Exception ex) { _ucLastError = $"warm-up walk failed: {ex.GetType().Name}: {ex.Message}"; }
            if (_ucWarmTries >= 3) _ucWarmed = true;
            _ucWarmUs = Math.Max(_ucWarmUs, sw0.Elapsed.TotalMilliseconds * 1000);
            return;
        }
        if (_ucOpen.Count == 0) { _ucMouseWasDown = false; return; }
        // The catalogue file is read (and Newtonsoft warmed up) on a pool thread, never in a frame.
        // The capture code is JIT-compiled there too: its first run in a frame cost ~7 ms of compiling, measured live.
        if (_uc == null) { _ucLoading ??= System.Threading.Tasks.Task.Run(() => { UcPrepare(); Uc(); }); return; }
        bool down = false;
        try { down = Input.IsKeyDown(0x01); } catch { }   // VK_LBUTTON: reading key state, never sending
        // Only clicks in the game count (a click in another window over the game is not the user using this panel).
        var released = _ucMouseWasDown && !down && UcGameInFront();
        _ucMouseWasDown = down;
        var mouse = released ? ImGuiNET.ImGui.GetIO().MousePos : default;
        // One walk at a time, sliced: at most UcSliceUs of it per frame (a big panel spreads over a few frames), so a
        // capture never makes a frame spike.
        long addr = 0, dueAddr = 0;
        UcOpen? o = null, due = null;
        foreach (var (a, x) in _ucOpen)
        {
            // A click inside the panel (its options may live a little outside its rect: 40 px slack) may move it to its
            // next page; clicks elsewhere (combat) never trigger a walk.
            if (released && x.Captures < UcMaxCaptures && x.Walk == null && x.Rect is { } r
                && mouse.X >= r.X - 40 && mouse.X <= r.Right + 40 && mouse.Y >= r.Y - 40 && mouse.Y <= r.Bottom + 40)
                x.DueAt ??= now.AddMilliseconds(350);
            if (x.Walk != null) (addr, o) = (a, x);
            else if (due == null && x.DueAt is { } d && now >= d) (dueAddr, due) = (a, x);
        }
        if (o == null && due != null)
        {
            (addr, o) = (dueAddr, due);
            o.DueAt = o.Captures == 0 ? now.AddMilliseconds(1200) : null;   // a second look once it has settled
            o.Captures++;
            // Not visible right now (fading in, rebuilt): the observer reports only changes, so look again shortly
            // rather than dropping it; its close removes it.
            if (!o.E.IsVisibleLocal) { if (o.Captures < UcMaxCaptures) o.DueAt = now.AddMilliseconds(500); return; }
            try { o.Rect = o.E.GetClientRect(); } catch { }
            o.Walk = new UcWalk();
            o.Walk.Queue.Enqueue((o.E, [], false));
        }
        if (o?.Walk is not { } w) return;
        var sw = Stopwatch.StartNew();
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var done = false;
        try { done = UcStep(o, sw) && UcFinish(o); }
        catch (Exception ex)
        {
            o.Walk = null;
            _ucLastError = $"capture of panel 0x{addr:X} ({o.Mapped ?? "unmapped"}) failed: {ex.Message}";
        }
        var us = sw.Elapsed.TotalMilliseconds * 1000;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - a0;
        if (_ucCaptures == 0) _ucFirstFrameUs = Math.Max(_ucFirstFrameUs, us);   // the first capture after a load pays JIT: reported apart
        else _ucMaxFrameUs = Math.Max(_ucMaxFrameUs, us);
        w.Us += us; w.Bytes += bytes; w.Frames++;
        if (done)
        {
            (_ucLastUs, _ucLastBytes, _ucLastVisited, _ucLastFrames, _ucLastSkippedBig) = (w.Us, w.Bytes, w.Visited, w.Frames, w.SkippedBig);
            _ucMaxUs = Math.Max(_ucMaxUs, w.Us);
            _ucMaxBytes = Math.Max(_ucMaxBytes, w.Bytes);
            _ucCaptures++;
        }
    }

    private sealed class UcWalk
    {
        public readonly Queue<(UiElement e, int[] path, bool parentHovers)> Queue = new();
        public readonly List<(string text, int[] path, float[]? rect, bool hover)> Found = [];
        public int Visited, Frames, KidIdx, KidChecks, SkippedBig;
        public IList<UiElement>? Kids;   // children of the last element dequeued, checked a few per budget test
        public int[] KidsPath = [];
        public bool KidsHover;
        public double Us;
        public long Bytes;
    }

    /// <summary>Walks until the slice budget is spent; true when the walk is complete. Breadth-first over visible
    /// children, as guide.highlight's text search walks them, so a recipe finds the same element.</summary>
    private bool UcStep(UcOpen o, Stopwatch sw, double budgetUs = UcSliceUs)
    {
        var w = o.Walk!;
        while (true)
        {
            if (sw.Elapsed.TotalMilliseconds * 1000 > budgetUs) return false;
            // Children are checked a few at a time (a panel can hold thousands, each visibility check reads the element):
            // measured live, enumerating one big panel's children in one go took 8 ms.
            if (w.Kids != null)
            {
                for (var n = 0; n < 8 && w.KidIdx < w.Kids.Count; n++, w.KidIdx++, w.KidChecks++)
                    if (w.Kids[w.KidIdx] is { Address: not 0 } c && c.IsVisibleLocal) w.Queue.Enqueue((c, [.. w.KidsPath, w.KidIdx], w.KidsHover));
                if (w.KidIdx < w.Kids.Count && w.KidChecks < UcMaxChildChecks) continue;
                w.Kids = null;
            }
            if (w.Queue.Count == 0 || w.Visited >= UcMaxVisit || w.Found.Count >= UcMaxTexts) break;
            var (e, path, parentHovers) = w.Queue.Dequeue();
            w.Visited++;
            // The panel root and big containers detect hover too: only the element itself or its direct parent count.
            var hovers = path.Length > 0 && UiDetectsHover(e);
            string? t = null;
            try { t = e.Text; } catch { }
            t = t?.Trim();
            if (!string.IsNullOrEmpty(t) && t.Length <= 120) { float[]? r = null; try { var cr = e.GetClientRect(); r = [cr.X, cr.Y, cr.Width, cr.Height]; } catch { } w.Found.Add((t, path, r, hovers || parentHovers)); }
            if (path.Length >= UcMaxDepth) continue;
            IList<UiElement>? cs = null;
            // Listing children builds one wrapper per child in one call: an element with hundreds (world labels, item
            // grids' backing lists) is a container, not an option list, and is not opened.
            try { if (e.ChildCount is > 0 and <= UcMaxChildren) cs = e.Children; else if (e.ChildCount > UcMaxChildren) w.SkippedBig++; } catch { }
            if (cs is { Count: > 0 } && w.KidChecks < UcMaxChildChecks) (w.Kids, w.KidsPath, w.KidIdx, w.KidsHover) = (cs, path, 0, hovers);
        }
        return true;
    }

    private bool UcFinish(UcOpen o)
    {
        var w = o.Walk!;
        // FNV-1a over the texts: stable across processes (string.GetHashCode is randomized per run), since view hashes persist.
        var hash = unchecked((int)2166136261);
        foreach (var f in w.Found)
        {
            foreach (var ch in f.text) hash = unchecked((hash ^ ch) * 16777619);
            hash = unchecked((hash ^ '\n') * 16777619);
        }
        if (w.Found.Count > 0 && hash != o.LastHash && o.E.IsVisibleLocal)
        {
            // The writer may hold the lock while it serializes: never wait in a frame, merge on a later one.
            if (!System.Threading.Monitor.TryEnter(_ucLock)) return false;
            var previous = o.LastHash;
            o.LastHash = hash;   // UcMerge records it as this view
            try { UcMerge(Uc(), o.E, o, w.Found); }
            catch { o.LastHash = previous; throw; }
            finally { System.Threading.Monitor.Exit(_ucLock); }
        }
        o.Walk = null;
        return true;
    }

    private void UcMerge(UiCatalogueFile cat, UiElement? root, UcOpen o, List<(string text, int[] path, float[]? rect, bool hover)> found)
    {
        // The title is the first visible text breadth-first: among the first texts guide.highlight's panel lookup reads
        // (PanelTexts, breadth-first too), so panel:title finds it. Not PanelTexts itself: it walks hidden children as well
        // (up to ~360 elements, milliseconds on big panels) in one frame.
        var first = found.Select(f => f.text).Distinct().Take(6).ToList();
        var title = first.Count > 0 ? first[0] : null;
        if (title == null) return;   // no text: nothing to recognise it by or point at
        string? npc = null;
        if (o.Mapped == "NpcDialog") try { npc = GameController.IngameState.IngameUi.NpcDialog?.NpcName?.Trim(); } catch { }
        // An NPC dialogue is one panel for every NPC: its identity is the NPC.
        var key = o.Mapped == "NpcDialog" && !string.IsNullOrEmpty(npc) ? $"NpcDialog|{npc}" : $"{o.Mapped ?? "panel"}|{title}";
        var now = DateTime.UtcNow;
        var entry = cat.Entries.FirstOrDefault(x => x.Key == key);
        if (entry == null)
        {
            if (cat.Entries.Count >= UcMaxEntries) cat.Entries.Remove(cat.Entries.OrderBy(x => x.LastSeen).First());
            entry = new UiCatalogueEntry { Key = key, FirstSeen = now };
            cat.Entries.Add(entry);
        }
        if (entry.LastSeen < o.OpenedAt) entry.Opened++;   // first merge of this opening
        entry.Mapped = o.Mapped;
        entry.Title = title;
        entry.Npc = npc ?? entry.Npc;
        entry.Recognise = first;
        entry.LastSeen = now;
        if (_obsArea != null && !entry.Areas.Contains(_obsArea)) { entry.Areas.Add(_obsArea); if (entry.Areas.Count > 12) entry.Areas.RemoveAt(0); }
        if (!entry.ViewHashes.Contains(o.LastHash)) { entry.ViewHashes.Add(o.LastHash); entry.Views++; if (entry.ViewHashes.Count > 50) entry.ViewHashes.RemoveAt(0); }
        if (root != null) try { var r = root.GetClientRect(); entry.Rect = [r.X, r.Y, r.Width, r.Height]; } catch { }
        // Mapped panels: within by walker path (exact); unmapped: within by the title text.
        var within = o.Mapped != null ? $"GameController.IngameState.IngameUi.{o.Mapped}" : title;
        var counts = found.GroupBy(f => f.text, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        // A text repeated in this view makes any option stored with it as unique ambiguous: a text target would hit
        // several elements now, so those fall back to panel + child too.
        foreach (var op in entry.Options)
            if (op.Unique && counts.TryGetValue(op.Text, out var n) && n > 1)
            {
                op.Unique = false;
                op.Highlight = new UiHighlightRecipe { Panel = title, Child = op.Child };
            }
        foreach (var (text, path, rect, hover) in found)
        {
            var unique = counts[text] == 1;
            // Same element: same text and path, or (both unique) the same text wherever it moved.
            var opt = entry.Options.FirstOrDefault(x => x.Text == text && x.Child.SequenceEqual(path))
                      ?? (unique ? entry.Options.FirstOrDefault(x => x.Text == text && x.Unique) : null);
            if (opt == null)
            {
                if (entry.Options.Count >= UcMaxOptions) entry.Options.Remove(entry.Options.OrderBy(x => x.LastSeen).First());
                opt = new UiCatalogueOption { Text = text };
                entry.Options.Add(opt);
            }
            opt.Child = path;
            opt.Clickable = hover;
            opt.Unique = unique && (opt.Seen == 0 || opt.Unique);   // repeated in any view it was seen in: panel+child from then on
            opt.LastSeen = now;
            opt.Seen++;
            opt.Rect = rect;
            opt.Highlight = opt.Unique
                ? new UiHighlightRecipe { Text = text, Within = within }
                : new UiHighlightRecipe { Panel = title, Child = path };
        }
        if (cat == _uc) _ucDirty = true;
    }

    /// <summary>From ObsFlush: writes the catalogue when it changed (at most every 500 ms).</summary>
    private void UcFlush()
    {
        if (!_ucDirty || _uc == null || _ucWriting || _ucNoWrite) return;
        _ucDirty = false;
        _ucWriting = true;
        // Serialized and written on a pool thread (400 entries are a few hundred KB of JSON: not in a frame).
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(ObsDir);
                var tmp = UcPath + ".tmp";
                string json;
                lock (_ucLock) json = JsonConvert.SerializeObject(_uc, Formatting.Indented);
                File.WriteAllText(tmp, json);
                File.Move(tmp, UcPath, overwrite: true);
            }
            catch (Exception ex) { _ucDirty = true; _ucLastError = $"catalogue write failed ({UcPath}): {ex.Message}"; }
            finally { _ucWriting = false; }
        });
    }

    private volatile bool _ucWriting, _ucNoWrite;

    /// <summary>observe.ui_catalogue {query?, key?, options?}: the catalogued interfaces; query matches key, title, NPC,
    /// area or an option's text; options=false leaves the option lists out.</summary>
    private JObject UiCatalogueQuery(JToken? p)
    {
        var query = p?["query"]?.ToString();
        var key = p?["key"]?.ToString();
        var withOptions = p?["options"]?.Value<bool>() ?? true;
        List<UiCatalogueEntry> list;
        JArray entries;
        int total;
        lock (_ucLock)
        {
            var all = Uc().Entries;
            total = all.Count;
            list = all.Where(x => (key == null || string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrEmpty(query) || Has(x.Key) || Has(x.Title) || Has(x.Npc) || x.Areas.Any(Has) || x.Options.Any(o => Has(o.Text))))
                .OrderByDescending(x => x.LastSeen).ToList();
            entries = new JArray(list.Select(x =>
            {
                var j = JObject.FromObject(x);
                j.Remove("viewHashes");
                if (!withOptions) { j.Remove("options"); j["optionCount"] = x.Options.Count; }
                return j;
            }));
        }
        var r = new JObject
        {
            ["ok"] = true, ["observing"] = Obs().Enabled, ["file"] = UcPath, ["total"] = total, ["entries"] = entries,
            ["cost"] = new JObject
            {
                ["captures"] = _ucCaptures, ["lastUs"] = Math.Round(_ucLastUs), ["maxUs"] = Math.Round(_ucMaxUs),
                ["lastBytes"] = _ucLastBytes, ["maxBytes"] = _ucMaxBytes, ["lastVisited"] = _ucLastVisited,
                ["lastFrames"] = _ucLastFrames, ["lastSkippedBig"] = _ucLastSkippedBig, ["maxFrameUs"] = Math.Round(_ucMaxFrameUs), ["firstCaptureFrameUs"] = Math.Round(_ucFirstFrameUs), ["warmUpUs"] = Math.Round(_ucWarmUs), ["sliceUs"] = UcSliceUs,
            },
            ["openNow"] = _ucOpen.Count,
        };
        if (_ucLastError != null) r["lastError"] = _ucLastError;
        if (list.Count == 0)
            r["why"] = !Obs().Enabled ? "observe is off: nothing is catalogued until observe.start"
                : key != null ? $"no catalogued interface with key '{key}' ({total} catalogued)"
                : !string.IsNullOrEmpty(query) ? $"no catalogued interface matches '{query}' ({total} catalogued)"
                : "nothing catalogued yet: interfaces are added when a panel opens while observing";
        return r;

        bool Has(string? s) => s != null && s.Contains(query!, StringComparison.OrdinalIgnoreCase);
    }
}
