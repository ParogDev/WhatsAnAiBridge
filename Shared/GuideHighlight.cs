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
///   guide.highlight {targets: [{item | path | rect:[x,y,w,h], label?, tier?: primary|secondary|context, order?}],
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
        public float[]? Rect;         // fixed screen rect x, y, w, h
        public string? Label;
        public string Tier = "primary";
        public int? Order;
    }

    /// <summary>One drawable box (an item target can resolve to several).</summary>
    internal readonly record struct HighlightBox(float X, float Y, float W, float H, string? Label, string Tier, int? Order, int TargetIndex);

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
                var ht = new HighlightTarget
                {
                    Item = t["item"]?.ToString(), Path = t["path"]?.ToString(), Label = Clip(t["label"]?.ToString(), 40),
                    Tier = t["tier"]?.ToString() is "secondary" or "context" ? t["tier"]!.ToString() : "primary",
                    Order = t["order"]?.Type == JTokenType.Integer ? t["order"]!.Value<int>() : null,
                };
                if (t["rect"] is JArray r && r.Count == 4) ht.Rect = r.Select(v => v.Value<float>()).ToArray();
                if (ht.Item == null && ht.Path == null && ht.Rect == null)
                    return Err("bad_target", "Each target needs item (name), path (UI element) or rect [x,y,w,h].");
                targets.Add(ht);
            }
            _hl.Targets = targets;
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
                    ["index"] = i, ["item"] = t.Item, ["path"] = t.Path, ["rect"] = t.Rect == null ? null : new JArray(t.Rect),
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
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            int before = boxes.Count;
            try
            {
                if (t.Rect != null) boxes.Add(new(t.Rect[0], t.Rect[1], t.Rect[2], t.Rect[3], t.Label, t.Tier, t.Order, i));
                else if (t.Path != null)
                {
                    if (new ExpressionWalker(GameController).Resolve(t.Path, out _) is UiElement e && e.IsVisible)
                    {
                        var r = e.GetClientRect();
                        if (r.Width > 0 && r.Height > 0) boxes.Add(new(r.X, r.Y, r.Width, r.Height, t.Label, t.Tier, t.Order, i));
                    }
                }
                else if (t.Item != null)
                    foreach (var r in FindItemRects(t.Item))
                        boxes.Add(new(r.x, r.y, r.w, r.h, t.Label, t.Tier, t.Order, i));
            }
            catch { }
            if (boxes.Count == before) missing.Add(i);
        }
        lock (_hlLock) { _hl.Boxes = boxes; _hl.Missing = missing; }
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
        if (boxes.Count == 0) return;
        DrawHighlightsImpl(boxes, title, current, since, rev);
    }
}
