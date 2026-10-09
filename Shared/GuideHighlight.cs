using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// In-game highlights: an agent points at what the user should look at or click - items in the inventory or the visible
/// stash tab (by name), any UI element (walker path) or a screen area - with an emphasis tier and an optional order
/// (a sequence: "1 then 2 then 3"). Targets are re-resolved every 100 ms so they follow the UI; GuideHighlightDraw.cs
/// draws them. Read-only: nothing is clicked.
///   guide.highlight {targets: [{item | path | panel+child | rect:[x,y,w,h], label?, tier?: primary|secondary|context, order?}],
///                    title?, current?, durationSec?, clear?}
///   guide.highlight_advance {}      the next step of a sequence becomes current
///   guide.highlight_state {}        targets with their resolved rects (what the user sees)
/// </summary>
public partial class WhatsAnAiBridge
{
    internal sealed class HighlightTarget
    {
        public string? Item;          // name / base / path substring, matched in the player inventory and the visible stash tab
        public string? Path;          // walker path to a UI element
        public string? Panel;         // text inside a top-level IngameUi panel (e.g. "Stash Tab Settings"): stable when child indexes shift
        public int[]? Child;          // child indexes inside that panel, e.g. [0,1,7,1,11,1]
        public string? Text;          // exact label of a visible element (stash tab, button, row): every visible match
        public string? Within;        // optional: limit the text search to the panel containing this text
        public string? Action;        // optional cue drawn on the target: click | rightclick
        public string? UntilCond;     // optional: step done when checked | unchecked (checkbox, PoE2 +0x60A) | gone (target disappears)
        public string? ClipTo;        // optional walker path to a container: only parts of the target inside its rect count (e.g. the visible part of a scrolling tab row)
        public string[]? Rel;         // optional navigation from the matched element: "^" = parent, "n" = child n (e.g. a label's checkbox: ["^","1"])
        public float[]? Rect;         // fixed screen rect x, y, w, h
        public string? Label;
        public string Tier = "primary";
        public int? Order;
    }

    /// <summary>One drawable box (an item target can resolve to several).</summary>
    internal readonly record struct HighlightBox(float X, float Y, float W, float H, string? Label, string Tier, int? Order, int TargetIndex, string? Action = null);

    internal sealed class HighlightState
    {
        public List<HighlightTarget> Targets = new();
        public string? Title;
        public int? Current;              // order of the current step (sequences)
        public DateTime Since = DateTime.MinValue;
        public DateTime? Until;
        public int Rev;
        public List<HighlightBox> Boxes = new();
        public List<int> Missing = new();  // target indexes that resolved to nothing
        public bool Auto = true;            // sequences follow the user: a later step appearing, a met condition or the current target leaving moves on
        public Dictionary<int, bool> WasFound = new();   // order -> found at the last resolution
    }

    private readonly object _hlLock = new();
    private readonly HighlightState _hl = new();
    private DateTime _hlLastResolve = DateTime.MinValue;

    private string? ProcessHighlightMethod(string method, JToken? p) => method switch
    {
        "guide.highlight" => SafeMemory(() => HighlightSet(p)),
        "guide.highlight_advance" => SafeMemory(HighlightAdvance),
        "guide.highlight_state" => SafeMemory(HighlightStateJson),
        _ => null,
    };

    internal JObject HighlightSet(JToken? p)
    {
        lock (_hlLock)
        {
            if (p?["clear"]?.Value<bool>() == true || p?["targets"] is not JArray arr)
            {
                _hl.Targets.Clear(); _hl.Boxes.Clear(); _hl.Missing.Clear(); _hl.Title = null; _hl.Current = null; _hl.Until = null;
                _hl.Rev++;
                return new JObject { ["ok"] = true, ["cleared"] = true };
            }
            var targets = new List<HighlightTarget>();
            foreach (var t in arr.OfType<JObject>().Take(40))
            {
                if (ParseTarget(t) is not { } ht)
                    return Err("bad_target", "Each target needs item (name), text (element label), path (UI element), panel (+ child) or rect [x,y,w,h].");
                targets.Add(ht);
            }
            _hl.Targets = targets;
            _hl.Auto = p?["auto"]?.Value<bool>() != false;
            _hl.WasFound.Clear();
            _hl.Title = Clip(p?["title"]?.ToString(), 80);
            var orders = targets.Where(t => t.Order != null).Select(t => t.Order!.Value).OrderBy(o => o).ToList();
            _hl.Current = p?["current"]?.Type == JTokenType.Integer ? p["current"]!.Value<int>() : orders.Count > 0 ? orders[0] : null;
            _hl.Since = DateTime.UtcNow;
            var dur = p?["durationSec"]?.Value<double>();
            _hl.Until = dur is > 0 ? DateTime.UtcNow.AddSeconds(Math.Min(dur.Value, 3600)) : null;
            _hl.Rev++;
            _hlLastResolve = DateTime.MinValue;
        }
        ResolveHighlights(force: true);
        return HighlightStateJson();
    }

    private JObject HighlightAdvance()
    {
        lock (_hlLock)
        {
            var orders = _hl.Targets.Where(t => t.Order != null).Select(t => t.Order!.Value).Distinct().OrderBy(o => o).ToList();
            if (orders.Count == 0) return Err("no_sequence", "The highlight has no ordered targets.");
            var next = orders.FirstOrDefault(o => o > (_hl.Current ?? int.MinValue), int.MaxValue);
            _hl.Current = next == int.MaxValue ? null : next;   // past the last step: all done
            _hl.Since = DateTime.UtcNow;
            _hl.Rev++;
        }
        return HighlightStateJson();
    }

    private JObject HighlightStateJson()
    {
        lock (_hlLock)
        {
            return new JObject
            {
                ["ok"] = true, ["rev"] = _hl.Rev, ["title"] = _hl.Title, ["current"] = _hl.Current,
                ["until"] = _hl.Until?.ToString("O"),
                ["targets"] = new JArray(_hl.Targets.Select((t, i) => new JObject
                {
                    ["index"] = i, ["item"] = t.Item, ["path"] = t.Path, ["text"] = t.Text, ["within"] = t.Within, ["action"] = t.Action, ["panel"] = t.Panel, ["child"] = t.Child == null ? null : new JArray(t.Child), ["rect"] = t.Rect == null ? null : new JArray(t.Rect),
                    ["label"] = t.Label, ["tier"] = t.Tier, ["order"] = t.Order,
                    ["found"] = _hl.Boxes.Count(b => b.TargetIndex == i),
                })),
                ["boxes"] = new JArray(_hl.Boxes.Select(b => new JObject { ["target"] = b.TargetIndex, ["rect"] = new JArray(b.X, b.Y, b.W, b.H) })),
                ["note"] = _hl.Missing.Count > 0 ? $"{_hl.Missing.Count} target(s) not on screen right now (panel closed, item not visible); they appear when visible." : null,
            };
        }
    }

    /// <summary>A consistent copy for drawing (taken once per frame).</summary>
    internal (List<HighlightBox> boxes, string? title, int? current, DateTime since, int rev) HighlightSnapshot()
    {
        lock (_hlLock) return (_hl.Boxes.ToList(), _hl.Title, _hl.Current, _hl.Since, _hl.Rev);
    }

    /// <summary>Main thread, from Render: re-resolve targets to screen rects every 100 ms (they follow the UI).</summary>
    private void ResolveHighlights(bool force = false)
    {
        List<HighlightTarget> targets;
        lock (_hlLock)
        {
            if (_hl.Until is { } u && DateTime.UtcNow > u) { _hl.Targets.Clear(); _hl.Boxes.Clear(); _hl.Until = null; _hl.Rev++; }
            if (_hl.Targets.Count == 0) return;
            if (!force && (DateTime.UtcNow - _hlLastResolve).TotalMilliseconds < 100) return;
            _hlLastResolve = DateTime.UtcNow;
            targets = _hl.Targets.ToList();
        }
        var boxes = new List<HighlightBox>();
        var missing = new List<int>();
        var conditionMet = new Dictionary<int, bool>();   // target index -> its 'until' holds now
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            int before = boxes.Count;
            try
            {
                var found = ResolveTarget(t);
                foreach (var (_, x, y, w, h) in found) boxes.Add(new(x, y, w, h, t.Label, t.Tier, t.Order, i, t.Action));
                // Checkbox state (PoE2: byte at +0x60A of the checkbox element, see finding ui.poe2.checkbox-checked).
                if (t.UntilCond is "checked" or "unchecked" && found.FirstOrDefault(f => f.e != null).e is { } cb)
                    conditionMet[i] = IsChecked(cb) == (t.UntilCond == "checked");
            }
            catch { }
            if (boxes.Count == before) missing.Add(i);
        }
        lock (_hlLock)
        {
            _hl.Boxes = boxes;
            _hl.Missing = missing;
            if (_hl.Auto) AutoAdvanceLocked(targets, boxes, conditionMet);
        }
    }

    /// <summary>A string field, or null when missing, JSON null or empty (empty must never mean "match anything").</summary>
    private static string? S(JToken? v) => v == null || v.Type == JTokenType.Null || string.IsNullOrEmpty(v.ToString()) ? null : v.ToString();

    /// <summary>A target from JSON (highlights and flows share the shape); null when it names nothing to find.</summary>
    internal static HighlightTarget? ParseTarget(JObject t)
    {
        var ht = new HighlightTarget
        {
            Item = S(t["item"]), Path = S(t["path"]), Panel = S(t["panel"]), Text = S(t["text"]), Within = S(t["within"]),
            Action = t["action"]?.ToString() is "click" or "rightclick" ? t["action"]!.ToString() : null,
            UntilCond = t["until"]?.ToString() is "checked" or "unchecked" or "gone" ? t["until"]!.ToString() : null,
            ClipTo = S(t["clipTo"]),
            Rel = t["rel"] is JArray rel ? rel.Select(v => v.ToString()).ToArray() : null,
            Child = t["child"] is JArray ch ? ch.Select(v => v.Value<int>()).ToArray() : t["child"]?.ToString() is { Length: > 0 } cs ? cs.Split('.', ',').Select(int.Parse).ToArray() : null,
            Label = Clip(t["label"]?.ToString(), 40),
            Tier = t["tier"]?.ToString() is "secondary" or "context" ? t["tier"]!.ToString() : "primary",
            Order = t["order"]?.Type == JTokenType.Integer ? t["order"]!.Value<int>() : null,
        };
        if (t["rect"] is JArray r && r.Count == 4) ht.Rect = r.Select(v => v.Value<float>()).ToArray();
        return ht.Item == null && ht.Path == null && ht.Rect == null && ht.Panel == null && ht.Text == null ? null : ht;
    }

    /// <summary>Follow "^" (parent) / "n" (child n) from an element; null when a step doesn't exist.</summary>
    private static UiElement? Navigate(UiElement e, string[] rel)
    {
        UiElement? cur = e;
        foreach (var s in rel)
        {
            if (cur == null) return null;
            if (s == "^") cur = cur.Parent;
            else if (int.TryParse(s, out var i)) { var cs = cur.Children; cur = i >= 0 && i < cs.Count ? cs[i] : null; }
            else return null;
        }
        return cur;
    }

    /// <summary>A target's visible boxes now (element when it is a UI element), clipped to its clipTo container if any.</summary>
    /// <summary>Why the last ResolveTarget / condition came up empty or false: the FIRST link that failed, by name (main thread only).
    /// Offsets and UI trees change with patches: a failure must name its broken link, not surface ten calls later.</summary>
    internal string? Why;

    internal List<(UiElement? e, float x, float y, float w, float h)> ResolveTarget(HighlightTarget t)
    {
        var list = new List<(UiElement?, float, float, float, float)>();
        Why = null;
        if (t.Rect != null) list.Add((null, t.Rect[0], t.Rect[1], t.Rect[2], t.Rect[3]));
        else if (t.Item != null) foreach (var r in FindItemRects(t.Item)) list.Add((null, r.x, r.y, r.w, r.h));
        else
        {
            List<UiElement> elements;
            if (t.Text != null) elements = FindByText(t.Text, t.Within);
            else if (t.Panel != null) elements = PanelChild(t.Panel, t.Child) is { } pe ? [pe] : [];
            else
            {
                var obj = new ExpressionWalker(GameController).Resolve(t.Path!, out var err);
                elements = obj is UiElement we ? [we] : [];
                if (err != null) Why = $"path {t.Path}: {err}";
                else if (obj == null) Why = $"path {t.Path}: resolved to null";
                else if (obj is not UiElement) Why = $"path {t.Path}: is a {obj.GetType().Name}, not a UI element";
            }
            if (t.Rel != null && elements.Count > 0)
            {
                var moved = elements.Select(e => Navigate(e, t.Rel)).Where(e => e != null).Select(e => e!).ToList();
                if (moved.Count == 0) Why = $"rel [{string.Join(",", t.Rel)}] from {elements.Count} match(es): a step does not exist (UI tree changed?)";
                elements = moved;
            }
            if (elements.Count > 0 && elements.All(e => !e.IsVisible)) Why ??= $"{elements.Count} match(es), none visible";
            if (t.Rel != null) elements = elements.Select(e => Navigate(e, t.Rel)).Where(e => e != null).Select(e => e!).ToList();
            foreach (var e in elements)
            {
                if (!e.IsVisible) continue;
                var r = e.GetClientRect();
                if (r.Width > 0 && r.Height > 0) list.Add((e, r.X, r.Y, r.Width, r.Height));
            }
        }
        if (t.ClipTo != null)
        {
            // Keep only boxes whose centre lies inside the container (a tab scrolled out of the row is not "in view").
            if (new ExpressionWalker(GameController).Resolve(t.ClipTo, out _) is UiElement c && c.IsVisible)
            {
                var cr = c.GetClientRect();
                list.RemoveAll(b => b.Item2 + b.Item4 / 2 < cr.X || b.Item2 + b.Item4 / 2 > cr.X + cr.Width
                                    || b.Item3 + b.Item5 / 2 < cr.Y || b.Item3 + b.Item5 / 2 > cr.Y + cr.Height);
            }
            else { list.Clear(); Why = $"clipTo {t.ClipTo}: container not found or not visible"; }
            if (list.Count == 0) Why ??= "outside the clipTo container (e.g. scrolled out of view)";
        }
        if (list.Count == 0) Why ??= t.Item != null ? $"no visible item matches '{t.Item}' (inventory / visible stash tab)" : "not found";
        return list;
    }

    /// <summary>PoE2 checkbox state: byte at +0x60A of the checkbox element (finding ui.poe2.checkbox-checked).</summary>
    internal bool IsChecked(UiElement checkbox) => GameController.Memory.Read<byte>(checkbox.Address + 0x60A) == 1;

    /// <summary>
    /// Sequences follow what the user does: a later step whose target appears (a dialog opened) becomes current; the
    /// current step is done when its 'until' holds (checked / unchecked) or, for 'gone' or no condition, when its target
    /// disappears (the dialog closed after Confirm). Past the last step: all done.
    /// </summary>
    private void AutoAdvanceLocked(List<HighlightTarget> targets, List<HighlightBox> boxes, Dictionary<int, bool> conditionMet)
    {
        var orders = targets.Where(t => t.Order != null).Select(t => t.Order!.Value).Distinct().OrderBy(o => o).ToList();
        if (orders.Count == 0) return;
        var found = orders.ToDictionary(o => o, o => boxes.Any(b => b.Order == o));
        var first = _hl.WasFound.Count == 0;
        int? next = null;
        if (!first && _hl.Current is { } cur)
        {
            // 1. A later step appeared.
            foreach (var o in orders.Where(o => o > cur))
                if (found[o] && !_hl.WasFound.GetValueOrDefault(o)) { next = o; break; }
            // 2. The current step's condition holds, or its target left.
            if (next == null)
            {
                var curTargets = targets.Select((t, i) => (t, i)).Where(x => x.t.Order == cur).ToList();
                var met = curTargets.Any(x => conditionMet.GetValueOrDefault(x.i));
                var gone = _hl.WasFound.GetValueOrDefault(cur) && !found[cur] && curTargets.All(x => x.t.UntilCond is null or "gone");
                if (met || gone) next = orders.FirstOrDefault(o => o > cur, int.MinValue) is var n && n != int.MinValue ? n : -1;
            }
            if (next != null)
            {
                _hl.Current = next == -1 ? null : next;   // -1: past the last step, all done
                _hl.Since = DateTime.UtcNow;
                _hl.Rev++;
            }
        }
        _hl.WasFound = found;
    }

    /// <summary>Visible elements whose text is exactly <paramref name="text"/> (case-insensitive), in the panel containing
    /// <paramref name="within"/> or in every visible top-level panel. Bounded walk.</summary>
    private List<UiElement> FindByText(string text, string? within)
    {
        var roots = new List<UiElement>();
        if (within != null)
        {
            // within: a walker path (starts with GameController) or text inside a top-level panel
            if (within.StartsWith("GameController", StringComparison.Ordinal)) { if (new ExpressionWalker(GameController).Resolve(within, out _) is UiElement w && w.IsVisible) roots.Add(w); }
            else if (PanelChild(within, null) is { } p) roots.Add(p);
        }
        else if (GameController.IngameState?.IngameUi?.Children is { } kids)
            roots.AddRange(kids.Where(k => k != null && k.Address != 0 && k.IsVisibleLocal));
        if (roots.Count == 0)
        {
            Why = within == null ? "no visible top-level panel" : within.StartsWith("GameController", StringComparison.Ordinal)
                ? $"within {within}: not found or not visible" : $"within: no open panel contains the text '{within}'";
            return [];
        }
        var hits = new List<UiElement>();
        var queue = new Queue<(UiElement e, int d)>(roots.Select(r => (r, 0)));
        int visited = 0;
        while (queue.Count > 0 && visited++ < 6000 && hits.Count < 10)
        {
            var (e, d) = queue.Dequeue();
            string? t = null;
            try { t = e.Text; } catch { }
            if (t != null && string.Equals(t.Trim(), text, StringComparison.OrdinalIgnoreCase)) hits.Add(e);
            if (d >= 16) continue;
            try { foreach (var c in e.Children) if (c != null && c.IsVisibleLocal) queue.Enqueue((c, d + 1)); } catch { }
        }
        if (hits.Count == 0) Why = $"text '{text}' not found in {(within ?? "the open panels")} ({visited} visible elements searched)";
        return hits;
    }

    // Panel lookups by text: address -> texts, refreshed when the panel's address changes (cheap: only visible top-level panels).
    private readonly Dictionary<long, List<string>> _hlPanelTexts = new();

    /// <summary>The visible top-level IngameUi panel whose texts contain <paramref name="text"/>, then its child at <paramref name="child"/>.</summary>
    private UiElement? PanelChild(string text, int[]? child)
    {
        var kids = GameController.IngameState?.IngameUi?.Children;
        if (kids == null) { Why = "IngameUi.Children unavailable"; return null; }
        string? brokenAt = null;
        foreach (var p in kids)
        {
            if (p == null || p.Address == 0 || !p.IsVisibleLocal) continue;
            if (!_hlPanelTexts.TryGetValue(p.Address, out var texts))
            {
                if (_hlPanelTexts.Count > 200) _hlPanelTexts.Clear();
                _hlPanelTexts[p.Address] = texts = PanelTexts(p, 6, 12);
            }
            if (!texts.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase))) continue;
            UiElement? e = p;
            var walked = new List<int>();
            foreach (var i in child ?? [])
            {
                var cs = e.Children;
                if (i < 0 || i >= cs.Count || cs[i] == null)
                {
                    brokenAt = $"panel '{text}' is open, but child [{string.Join(",", walked.Append(i))}] does not exist " +
                               $"(element at [{string.Join(",", walked)}] has {cs.Count} children): its layout changed";
                    e = null;
                    break;
                }
                walked.Add(i);
                e = cs[i];
            }
            if (e != null) return e;
        }
        Why = brokenAt ?? $"panel '{text}' is not open";
        return null;
    }

    /// <summary>Visible items whose base name, unique name or metadata path contains the query (player inventory, visible stash tab).</summary>
    private IEnumerable<(float x, float y, float w, float h)> FindItemRects(string query)
    {
        // Typed access (no reflection: NormalInventoryItem declares Item more than once, so GetProperty("Item") throws).
        var ui = GameController.IngameState.IngameUi;
        var found = new List<(float, float, float, float)>();
        try
        {
            var inv = ui.InventoryPanel[InventoryIndex.PlayerInventory];
            if (inv?.IsVisible == true && inv.VisibleInventoryItems is { } items)
                foreach (var it in items) AddIfMatch(it?.Item, it?.GetClientRect());
        }
        catch { }
        try
        {
            var st = ui.StashElement;
            if (st?.IsVisible == true && st.VisibleStash?.VisibleInventoryItems is { } items)
                foreach (var it in items) AddIfMatch(it?.Item, it?.GetClientRect());
        }
        catch { }
        return found;

        void AddIfMatch(Entity? entity, RectangleF? rect)
        {
            if (entity == null || rect is not { } r || r.Width <= 0 || r.Height <= 0) return;
            var name = entity.GetComponent<Base>()?.Name ?? "";
            var unique = "";
            try { unique = entity.GetComponent<Mods>()?.UniqueName ?? ""; } catch { }
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase) || unique.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (entity.Path?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                found.Add((r.X, r.Y, r.Width, r.Height));
        }
    }

    /// <summary>Drawn by GuideHighlightDraw.cs (Fable). Placeholder until then: plain frames.</summary>
    partial void DrawHighlightsImpl(List<HighlightBox> boxes, string? title, int? current, DateTime since, int rev);

    private void DrawHighlights()
    {
        ResolveHighlights();
        var (boxes, title, current, since, rev) = HighlightSnapshot();
        // Attention (GuidePanel.cs, decided earlier this frame): in combat the overlay stays off unless a flow is
        // running and no hostile is near.
        if (boxes.Count == 0 || !_guideAttention.HighlightsAllowed) return;
        DrawHighlightsImpl(boxes, title, current, since, rev);
    }
}
