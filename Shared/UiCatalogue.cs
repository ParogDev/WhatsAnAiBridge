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
/// Cost: a capture walks at most UcMaxVisit visible elements (~13 us and ~5 KB each, measured live); it runs only on an
/// opening and after clicks, never per frame. The last and worst capture cost are in the reply.
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
        /// <summary>The element or one of its two parents reacts to hover (ElementFlags.DetectsHover): likely clickable.</summary>
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
    }

    private const int UcMaxVisit = 600, UcMaxDepth = 14, UcMaxTexts = 80, UcMaxEntries = 400, UcMaxOptions = 120, UcMaxCaptures = 40;
    private readonly Dictionary<long, UcOpen> _ucOpen = new();
    private UiCatalogueFile? _uc;
    private bool _ucDirty, _ucMouseWasDown;
    private double _ucLastUs, _ucMaxUs;
    private long _ucLastBytes, _ucMaxBytes;
    private int _ucLastVisited, _ucCaptures;
    private string? _ucLastError;

    private string UcPath => Path.Combine(ObsDir, "ui-catalogue.json");

    private UiCatalogueFile Uc()
    {
        if (_uc != null) return _uc;
        try { _uc = File.Exists(UcPath) ? JsonConvert.DeserializeObject<UiCatalogueFile>(File.ReadAllText(UcPath)) ?? new() : new(); }
        catch (Exception ex) { _ucLastError = $"catalogue load failed ({UcPath}): {ex.Message}"; _uc = new(); }
        return _uc;
    }

    /// <summary>From ObservePanels: a top-level panel became visible / hidden.</summary>
    private void UcPanelChanged(long addr, UiElement e, bool visible, string? mapped)
    {
        if (!visible) { _ucOpen.Remove(addr); return; }
        _ucOpen[addr] = new UcOpen { E = e, Mapped = mapped, OpenedAt = DateTime.UtcNow, DueAt = DateTime.UtcNow.AddMilliseconds(300) };
    }

    /// <summary>From ObserveTick, every frame while observing: due captures, and a capture after each click.</summary>
    private void UcTick(DateTime now)
    {
        if (_ucOpen.Count == 0) { _ucMouseWasDown = false; return; }
        bool down = false;
        try { down = Input.IsKeyDown(0x01); } catch { }   // VK_LBUTTON: reading key state, never sending
        var released = _ucMouseWasDown && !down;
        _ucMouseWasDown = down;
        foreach (var (addr, o) in _ucOpen.ToList())
        {
            if (released && o.Captures < UcMaxCaptures) o.DueAt ??= now.AddMilliseconds(350);
            if (o.DueAt is not { } due || now < due) continue;
            o.DueAt = o.Captures == 0 ? now.AddMilliseconds(1200) : null;   // a second look once it has settled
            o.Captures++;
            try { UcCapture(addr, o); }
            catch (Exception ex) { _ucLastError = $"capture of panel 0x{addr:X} ({o.Mapped ?? "unmapped"}) failed: {ex.Message}"; }
        }
    }

    private void UcCapture(long addr, UcOpen o)
    {
        var sw = Stopwatch.StartNew();
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var root = o.E;
        if (!root.IsVisibleLocal) { _ucOpen.Remove(addr); return; }
        // Breadth-first over visible children, as guide.highlight's text search walks them, so a recipe finds the same element.
        var found = new List<(string text, int[] path, UiElement e, bool hover)>();
        var queue = new Queue<(UiElement e, int[] path, int hoverUp)>();
        queue.Enqueue((root, [], 99));
        var visited = 0;
        while (queue.Count > 0 && visited < UcMaxVisit && found.Count < UcMaxTexts)
        {
            var (e, path, hoverUp) = queue.Dequeue();
            visited++;
            var hovers = UiDetectsHover(e);
            var hoverDist = hovers ? 0 : hoverUp + 1;   // distance to the nearest hover-detecting ancestor
            string? t = null;
            try { t = e.Text; } catch { }
            t = t?.Trim();
            if (!string.IsNullOrEmpty(t) && t.Length <= 120) found.Add((t, path, e, hoverDist <= 2));
            if (path.Length >= UcMaxDepth) continue;
            IList<UiElement>? cs = null;
            try { cs = e.Children; } catch { }
            if (cs == null) continue;
            for (var i = 0; i < cs.Count; i++)
                if (cs[i] is { Address: not 0 } c && c.IsVisibleLocal) queue.Enqueue((c, [.. path, i], hoverDist));
        }
        var hash = 17;
        foreach (var f in found) hash = unchecked(hash * 31 + f.text.GetHashCode());
        _ucLastVisited = visited;
        if (found.Count > 0 && hash != o.LastHash)
        {
            o.LastHash = hash;
            lock (_obsLock) UcMerge(root, o, found);
        }
        _ucLastUs = sw.Elapsed.TotalMilliseconds * 1000;
        _ucLastBytes = GC.GetAllocatedBytesForCurrentThread() - a0;
        _ucMaxUs = Math.Max(_ucMaxUs, _ucLastUs);
        _ucMaxBytes = Math.Max(_ucMaxBytes, _ucLastBytes);
        _ucCaptures++;
    }

    private void UcMerge(UiElement root, UcOpen o, List<(string text, int[] path, UiElement e, bool hover)> found)
    {
        // The title is the first text guide.highlight's panel lookup sees (PanelTexts, the same walk), so panel:title finds it.
        var first = PanelTexts(root, 6, 12);
        var title = first.Count > 0 ? first[0].Trim() : null;
        if (title == null) return;   // no text: nothing to recognise it by or point at
        string? npc = null;
        if (o.Mapped == "NpcDialog") try { npc = GameController.IngameState.IngameUi.NpcDialog?.NpcName?.Trim(); } catch { }
        // An NPC dialogue is one panel for every NPC: its identity is the NPC.
        var key = o.Mapped == "NpcDialog" && !string.IsNullOrEmpty(npc) ? $"NpcDialog|{npc}" : $"{o.Mapped ?? "panel"}|{title}";
        var now = DateTime.UtcNow;
        var cat = Uc();
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
        entry.Recognise = first.Take(6).ToList();
        entry.LastSeen = now;
        if (_obsArea != null && !entry.Areas.Contains(_obsArea)) { entry.Areas.Add(_obsArea); if (entry.Areas.Count > 12) entry.Areas.RemoveAt(0); }
        if (!entry.ViewHashes.Contains(o.LastHash)) { entry.ViewHashes.Add(o.LastHash); entry.Views++; if (entry.ViewHashes.Count > 50) entry.ViewHashes.RemoveAt(0); }
        try { var r = root.GetClientRect(); entry.Rect = [r.X, r.Y, r.Width, r.Height]; } catch { }
        // Mapped panels: within by walker path (exact); unmapped: within by the title text.
        var within = o.Mapped != null ? $"GameController.IngameState.IngameUi.{o.Mapped}" : title;
        var counts = found.GroupBy(f => f.text, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var (text, path, e, hover) in found)
        {
            var unique = counts[text] == 1;
            var opt = entry.Options.FirstOrDefault(x => x.Text == text && (unique || x.Child.SequenceEqual(path)));
            if (opt == null)
            {
                if (entry.Options.Count >= UcMaxOptions) entry.Options.Remove(entry.Options.OrderBy(x => x.LastSeen).First());
                opt = new UiCatalogueOption { Text = text };
                entry.Options.Add(opt);
            }
            opt.Child = path;
            opt.Clickable = hover;
            opt.Unique = unique;
            opt.LastSeen = now;
            opt.Seen++;
            try { var r = e.GetClientRect(); opt.Rect = [r.X, r.Y, r.Width, r.Height]; } catch { }
            opt.Highlight = unique
                ? new UiHighlightRecipe { Text = text, Within = within }
                : new UiHighlightRecipe { Panel = title, Child = path };
        }
        _ucDirty = true;
    }

    /// <summary>From ObsFlush: writes the catalogue when it changed (at most every 500 ms).</summary>
    private void UcFlush()
    {
        if (!_ucDirty || _uc == null) return;
        _ucDirty = false;
        try
        {
            Directory.CreateDirectory(ObsDir);
            var tmp = UcPath + ".tmp";
            string json;
            lock (_obsLock) json = JsonConvert.SerializeObject(_uc, Formatting.Indented);
            File.WriteAllText(tmp, json);
            File.Move(tmp, UcPath, overwrite: true);
        }
        catch (Exception ex) { _ucLastError = $"catalogue write failed ({UcPath}): {ex.Message}"; }
    }

    /// <summary>observe.ui_catalogue {query?, key?, options?}: the catalogued interfaces; query matches key, title, NPC,
    /// area or an option's text; options=false leaves the option lists out.</summary>
    private JObject UiCatalogueQuery(JToken? p)
    {
        var query = p?["query"]?.ToString();
        var key = p?["key"]?.ToString();
        var withOptions = p?["options"]?.Value<bool>() ?? true;
        List<UiCatalogueEntry> list;
        lock (_obsLock)
        {
            var all = Uc().Entries;
            list = all.Where(x => (key == null || string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrEmpty(query) || Has(x.Key) || Has(x.Title) || Has(x.Npc) || x.Areas.Any(Has) || x.Options.Any(o => Has(o.Text))))
                .OrderByDescending(x => x.LastSeen).ToList();
        }
        var entries = new JArray(list.Select(x =>
        {
            var j = JObject.FromObject(x);
            j.Remove("viewHashes");
            if (!withOptions) { j.Remove("options"); j["optionCount"] = x.Options.Count; }
            return j;
        }));
        var r = new JObject
        {
            ["ok"] = true, ["observing"] = Obs().Enabled, ["file"] = UcPath, ["total"] = Uc().Entries.Count, ["entries"] = entries,
            ["cost"] = new JObject
            {
                ["captures"] = _ucCaptures, ["lastUs"] = Math.Round(_ucLastUs), ["maxUs"] = Math.Round(_ucMaxUs),
                ["lastBytes"] = _ucLastBytes, ["maxBytes"] = _ucMaxBytes, ["lastVisited"] = _ucLastVisited,
            },
            ["openNow"] = _ucOpen.Count,
        };
        if (_ucLastError != null) r["lastError"] = _ucLastError;
        if (list.Count == 0)
            r["why"] = !Obs().Enabled ? "observe is off: nothing is catalogued until observe.start"
                : key != null ? $"no catalogued interface with key '{key}' ({Uc().Entries.Count} catalogued)"
                : !string.IsNullOrEmpty(query) ? $"no catalogued interface matches '{query}' ({Uc().Entries.Count} catalogued)"
                : "nothing catalogued yet: interfaces are added when a panel opens while observing";
        return r;

        bool Has(string? s) => s != null && s.Contains(query!, StringComparison.OrdinalIgnoreCase);
    }
}
