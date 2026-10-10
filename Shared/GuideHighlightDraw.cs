using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// Draws the in-game highlights kept by GuideHighlight.cs: frames around the items, UI elements or screen areas an
/// agent points at, on the ImGui foreground draw list (over the game UI, no window, no input). The interior of every
/// box stays clear so the item art is never covered.
/// Tiers: primary = corner brackets in the accent, the only thing that pulses (slow, low amplitude); secondary = a
/// thin accent frame, calm; context = a thin neutral frame to orient the eye. Sequences: a numbered badge on the
/// box's top-left corner; the current step keeps its tier at full strength, earlier steps shrink to a muted frame
/// with a green check, later steps show quietly at under half strength, and a dotted hint runs from the current
/// step to the next. Entry: corners snap in from outside over 0.3 s and the active primary step gets one expanding
/// ring; a step becoming done pops its check. Labels sit outside the box in a small pill, placed where they cover
/// no other highlight; the title sits above the active box once.
/// Asked targets (ask): instead of the label pill, a question strip sits outside the box - an accent "?" badge, the
/// question, and Yes / No / Not sure pills. Only the pills are an ImGui window (no background, no decoration), so
/// those clicks stop at the overlay and everything else stays click-through. A click records the verdict
/// (HighlightAnswer); the strip becomes a result pill for 2.2 s (popping check / cross / dash), then the label pill
/// returns and a small check / cross / dash mark stays on the box's top-right corner.
/// Nothing lingers (the box carries the rule, this file only draws it):
///   - EndTicks: a box with a lifetime fades out - frame, badge, ring, cue, label or question, verdict mark, the
///     title anchored on it, the hint to or from it - over its last HlFadeSec, the corners drifting outward as they go;
///     a box past its end is not drawn or placed at all. UTC is read once per frame.
///   - Covered (over an open NPC dialogue / large / fullscreen panel it is not part of): nothing of it is drawn, it
///     reserves no placement space, the title anchors on another box or is skipped, no hint runs to or from it. Its
///     frame alone fades out over 0.22 s instead of popping, and it snaps in again when uncovered.
///   - Docked (an unanswered ask about something outside such a panel): the box keeps its frame (if not covered) but
///     the question strip is replaced by a small pill docked at a screen edge, flush with it: the "?" badge and the
///     question clipped to 150 px in dim text, no pulse. Every layer's docked asks stack in one column (equal widths,
///     4 px apart) at the first of right-middle / left-middle / right-upper / left-upper / right-lower / left-lower
///     that covers no open big panel and no box, else the least covered. Hovering a pill slides it out over 0.18 s
///     into the full question and the Yes / No / Not sure pills (interactive once mostly open, the same ask window as
///     the strip), and it slides back 0.35 s after the mouse leaves. When the panel closes the strip returns.
/// Game-agnostic: ImGui only; nothing here touches ExileCore* directly. Box coordinates come from
/// Element.GetClientRect(), which the HUD draws 1:1 on the ImGui display (Graphics.DrawFrame does the same).
/// </summary>
public partial class WhatsAnAiBridge
{
    private const double HlSnapSec = 0.32, HlRingSec = 0.6, HlCheckSec = 0.3, HlPulseSec = 2.6;
    private const double HlPressSec = 1.4, HlPressDelay = 0.7;   // mouse cue: one press per cycle, the first after the entry ring
    private const float HlBadgeR = 9f, HlSnapPx = 10f, HlMouseW = 14f, HlMouseH = 20f, HlMousePad = 5f;
    private const double HlVerdictShowSec = 2.2, HlVerdictPopSec = 0.3, HlVerdictFadeSec = 0.4;
    private const double HlHideSec = 0.22;                                  // a covered box's frame fades out this long
    private const double HlDockOpenSec = 0.18, HlDockGraceSec = 0.35;       // docked pill: slide-out time, hold after the mouse leaves
    private const float HlDockTextW = 150f, HlDockGap = 4f, HlDockPad = 8f, HlAskGap = 6f;
    private const long HlFadeTicks = (long)(HlFadeSec * TimeSpan.TicksPerSecond);

    private static readonly string[] HlAskIds = ["##hl_yes", "##hl_no", "##hl_skip"];

    /// <summary>Overlay-local state of one layer: when each target's boxes appeared, for the entry animations.</summary>
    private sealed class HighlightUiState
    {
        public int SeenRev = -1;
        public int? SeenCurrent;
        public string? SeenTitle;
        public readonly Dictionary<int, double> ArrivedAt = new();           // target index -> ImGui time its boxes (re)appeared
        public readonly HashSet<int> Present = new();
        public readonly HashSet<int> Labelled = new();                       // targets that got their label this frame
        public readonly List<int> Gone = new();
        public readonly Dictionary<int, int> Counts = new();                 // drawn boxes per target (an item can match several)
        public readonly Dictionary<int, double> AnsweredAt = new();          // target index -> ImGui time its answer was first drawn
        public readonly Dictionary<int, double> CoveredAt = new();           // target index -> ImGui time it became covered (its frame fades)
        public readonly Dictionary<int, double> DockHoverAt = new();         // target index -> ImGui time its docked pill was last hovered
        public readonly Dictionary<int, float> DockOpen = new();             // target index -> 0 (at rest) .. 1 (slid out)
        public bool Drawn;                                                   // its layer was drawn this frame
    }

    private readonly Dictionary<string, HighlightUiState> _hlUis = new();   // layer owner -> its overlay state
    private readonly List<(Vector2 min, Vector2 max)> _hlPlaced = new();    // label rects placed this frame, every layer's
    private readonly List<HighlightBox> _hlAll = new();                     // every layer's drawn boxes this frame (placement avoids them all)
    private readonly List<(HighlightLayerView layer, int box)> _hlDocked = new();   // this frame's docked asks, every layer's, in drawing order
    private readonly List<string> _hlUiGone = new();
    private readonly Dictionary<int, string> _hlAskWin = new();             // slot * 40 + target index -> window id
    private readonly Dictionary<string, (string text, float small, float width)> _hlDockClip = new();   // ask -> its clipped form at a font size
    private string? _hlLastError;

    partial void DrawHighlightsImpl(List<HighlightLayerView> layers)
    {
        try
        {
            DrawHighlightsBody(layers);
        }
        catch (Exception ex)
        {
            if (_hlLastError != ex.Message)
            {
                _hlLastError = ex.Message;
                LogError($"[GuideHighlight] {ex}");
            }
        }
    }

    // ── Frame ────────────────────────────────────────────────────────

    /// <summary>
    /// Every session's layer together: frames and badges of all layers first, then the badges and verdict marks of all
    /// layers are reserved, then the docked asks of all layers in one column, then each layer's title, cues and labels,
    /// placed so they cover no box of any layer. Each layer keeps its own sequence (current step), entry animations and
    /// title; with two or more layers the title pill says whose it is. Covered boxes and boxes past their end take no
    /// part in any of it.
    /// </summary>
    private void DrawHighlightsBody(List<HighlightLayerView> layers)
    {
        var now = ImGui.GetTime();
        var utc = DateTime.UtcNow.Ticks;
        var dl = ImGui.GetForegroundDrawList();
        var disp = ImGui.GetIO().DisplaySize;
        var th = PanelTheme.Current();
        var pulse = (float)(0.5 + 0.5 * Math.Sin(now * Math.PI * 2 / HlPulseSec));
        _hlAll.Clear();
        foreach (var l in layers) foreach (var b in l.Boxes) if (HlShown(b, utc)) _hlAll.Add(b);
        foreach (var u in _hlUis.Values) u.Drawn = false;

        // Pass 1: frames, badges and the hint to the next step, per layer.
        foreach (var l in layers)
        {
            var u = HlUiFor(l.Owner);
            u.Drawn = true;
            ObserveHighlights(u, l.Boxes, l.Title, l.Current, l.Since, l.Rev, now, utc);
            HlDrawFrames(dl, u, l.Boxes, l.Current, now, utc, pulse, disp, th);
        }

        // Pass 2: reserve every layer's badges and verdict marks, so no label of any layer covers them.
        _hlPlaced.Clear();
        var r = new Vector2(HlBadgeR + 1.5f, HlBadgeR + 1.5f);
        foreach (var b in _hlAll)
        {
            var (min, max) = HlRect(b);
            if (b.Order != null) _hlPlaced.Add((min - r, min + r));
            if (b.Answer != null) { var c = new Vector2(max.X, min.Y); _hlPlaced.Add((c - r, c + r)); }   // the verdict mark on the top-right corner
        }

        // Pass 3: the docked asks of every layer, one column at a screen edge (placed before the labels so they avoid it).
        _hlDocked.Clear();
        foreach (var l in layers)
        {
            var last = -1;
            for (var i = 0; i < l.Boxes.Count; i++)
            {
                var b = l.Boxes[i];
                if (b.TargetIndex == last) continue;
                last = b.TargetIndex;
                if (b.Docked && b.Ask != null && b.Answer == null && HlLife(b.EndTicks, utc) > 0f) _hlDocked.Add((l, i));
            }
        }
        if (_hlDocked.Count > 0) HlDrawDock(dl, now, utc, disp, th);

        // Pass 4: per layer, its title once, then per target the action cue and the label (or question) outside its first box.
        foreach (var l in layers)
            HlDrawLabels(dl, HlUiFor(l.Owner), l, layers.Count > 1, now, utc, disp, th);

        // A layer that left (cleared, expired) forgets its animations; it enters fresh if it comes back.
        _hlUiGone.Clear();
        foreach (var (k, u) in _hlUis) if (!u.Drawn) _hlUiGone.Add(k);
        foreach (var k in _hlUiGone) _hlUis.Remove(k);
    }

    private HighlightUiState HlUiFor(string owner)
    {
        if (!_hlUis.TryGetValue(owner, out var u)) _hlUis[owner] = u = new HighlightUiState();
        return u;
    }

    /// <summary>How much of a box is left: 1 until its last HlFadeSec, then down to 0 at EndTicks (0 = no end).</summary>
    private static float HlLife(long endTicks, long utc)
    {
        if (endTicks == 0) return 1f;
        var left = endTicks - utc;
        return left <= 0 ? 0f : left >= HlFadeTicks ? 1f : (float)left / HlFadeTicks;
    }

    /// <summary>A box that takes part in this frame: not covered by a big panel and not past its end.</summary>
    private static bool HlShown(in HighlightBox b, long utc) => !b.Covered && HlLife(b.EndTicks, utc) > 0f;

    /// <summary>The sequence as drawn: the first shown box of the current step, of the next step, and whether there is one.</summary>
    private static (bool hasSeq, int curIdx, int nextIdx) HlSequence(List<HighlightBox> boxes, int? current, long utc)
    {
        var hasSeq = false;
        int curIdx = -1, nextIdx = -1, nextOrder = int.MaxValue;
        for (var i = 0; i < boxes.Count; i++)
        {
            if (boxes[i].Order is not int o) continue;
            hasSeq = true;
            if (!HlShown(boxes[i], utc)) continue;
            if (current is int c)
            {
                if (o == c && curIdx < 0) curIdx = i;
                if (o > c && o < nextOrder) { nextOrder = o; nextIdx = i; }
            }
        }
        return (hasSeq, curIdx, nextIdx);
    }

    private void HlDrawFrames(ImDrawListPtr dl, HighlightUiState u, List<HighlightBox> boxes, int? current, double now, long utc, float pulse, Vector2 disp, PanelTheme th)
    {
        var (_, curIdx, nextIdx) = HlSequence(boxes, current, utc);
        u.Counts.Clear();
        foreach (var b in boxes) if (HlShown(b, utc)) u.Counts[b.TargetIndex] = u.Counts.GetValueOrDefault(b.TargetIndex) + 1;

        for (var i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            var (min, max) = HlRect(b);
            if (max.X < 0 || max.Y < 0 || min.X > disp.X || min.Y > disp.Y) continue;
            var state = HlStateOf(b, current);
            var life = HlLife(b.EndTicks, utc);
            if (life <= 0f) continue;
            if (b.Covered)
            {
                // A box the open panel is not about: its frame alone fades out instead of popping; nothing else of it.
                if (!u.CoveredAt.TryGetValue(b.TargetIndex, out var coveredAt)) u.CoveredAt[b.TargetIndex] = coveredAt = now;
                var k = (now - coveredAt) / HlHideSec;
                if (k < 1) HlFrame(dl, min, max, b.Tier, state, (float)(1 - k) * life, 0f);
                continue;
            }
            u.CoveredAt.Remove(b.TargetIndex);
            var arrived = u.ArrivedAt.GetValueOrDefault(b.TargetIndex, -1e9);
            var snap = HlEase((now - arrived) / HlSnapSec) * life;   // the fade drifts the corners outward with the alpha
            var active = state == 0;
            HlFrame(dl, min, max, b.Tier, state, snap, active ? pulse : 0f);
            if (active && b.Tier == "primary")
            {
                // One expanding ring on arrival: the eye lands here, then the slow pulse takes over.
                var k = (now - arrived) / HlRingSec;
                if (k < 1)
                {
                    var grow = 3f + 14f * HlEase(k);
                    dl.AddRect(min - new Vector2(grow, grow), max + new Vector2(grow, grow), U(ToneAccent, 0.55f * (float)(1 - k) * life), 5f, ImDrawFlags.None, 1.5f);
                }
            }
            if (b.Order is int order)
                HlBadge(dl, new Vector2(min.X, min.Y), order, state, state == 2 ? HlEase((now - arrived) / HlCheckSec) : 1f, snap, th);
        }

        // The hint from the current step to the next, dotted and quiet; it fades in with the current step and out with either box.
        if (curIdx >= 0 && nextIdx >= 0)
        {
            var (aMin, aMax) = HlRect(boxes[curIdx]);
            var (bMin, bMax) = HlRect(boxes[nextIdx]);
            var snap = HlEase((now - u.ArrivedAt.GetValueOrDefault(boxes[curIdx].TargetIndex, -1e9)) / HlSnapSec);
            var life = MathF.Min(HlLife(boxes[curIdx].EndTicks, utc), HlLife(boxes[nextIdx].EndTicks, utc));
            HlDots(dl, aMin, aMax, bMin, bMax, U(ToneNeutral, 0.6f * snap * life));
        }
    }

    private void HlDrawLabels(ImDrawListPtr dl, HighlightUiState u, HighlightLayerView l, bool multi, double now, long utc, Vector2 disp, PanelTheme th)
    {
        var boxes = l.Boxes;
        var current = l.Current;
        var all = _hlAll;
        var (hasSeq, curIdx, _) = HlSequence(boxes, current, utc);
        if (l.Title != null)
        {
            // The title needs a shown box to sit on: the current step's, else the first active primary, else none at all.
            var anchor = curIdx >= 0 ? curIdx : HlAnchor(boxes, current, utc);
            if (anchor >= 0)
            {
                var (min, max) = HlRect(boxes[anchor]);
                string? sub = null;
                if (hasSeq)
                {
                    var orders = new SortedSet<int>();
                    foreach (var b in boxes) if (b.Order is int o) orders.Add(o);
                    if (current is int c) { var pos = 0; foreach (var o in orders) { pos++; if (o == c) break; } sub = $"step {pos} of {orders.Count}"; }
                    else sub = "done";
                }
                // Several agents point at once: the title says whose targets these are.
                if (multi && l.Who != null) sub = sub != null ? $"{sub} - {l.Who}" : l.Who;
                var vis = HlEase((now - u.ArrivedAt.GetValueOrDefault(boxes[anchor].TargetIndex, -1e9)) / HlSnapSec) * HlLife(boxes[anchor].EndTicks, utc);
                HlTitle(dl, l.Title, sub, min, max, all, disp, th, vis);
            }
        }
        u.Labelled.Clear();
        for (var i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            if (!HlShown(b, utc)) continue;
            if (!u.Labelled.Add(b.TargetIndex)) continue;
            var (min, max) = HlRect(b);
            if (max.X < 0 || max.Y < 0 || min.X > disp.X || min.Y > disp.Y) continue;
            var state = HlStateOf(b, current);
            var arrived = u.ArrivedAt.GetValueOrDefault(b.TargetIndex, -1e9);
            var snap = HlEase((now - arrived) / HlSnapSec) * HlLife(b.EndTicks, utc);
            // Which mouse button to press: animated on the current step, a quiet static glyph on an upcoming one.
            if (b.Action is "click" or "rightclick" && state != 2)
            {
                var size = new Vector2(HlMouseW + HlMousePad * 2, HlMouseH + HlMousePad * 2);
                var (pmin, pmax) = HlPlace(size, min, max, all, disp, prefer: 2);
                HlMouse(dl, pmin + new Vector2(HlMousePad, HlMousePad), b.Action == "rightclick", state == 0 ? snap : 0.4f * snap,
                    state == 0 ? now - arrived : -1, th);
                _hlPlaced.Add((pmin, pmax));
            }
            if (b.Ask != null)
            {
                // The question carries the words; a docked one was drawn at the screen edge already.
                if (b.Answer == null) { if (!b.Docked) HlAskStrip(dl, b, l.Owner, l.Slot, min, max, all, disp, th, snap); continue; }
                if (!u.AnsweredAt.TryGetValue(b.TargetIndex, out var answeredAt)) u.AnsweredAt[b.TargetIndex] = answeredAt = now;
                var age = now - answeredAt;
                HlVerdictMark(dl, new Vector2(max.X, min.Y), b.Answer, HlEase(age / HlVerdictPopSec) * snap, th);
                if (age < HlVerdictShowSec) { HlVerdictPill(dl, b.Answer, age, min, max, all, disp, th, snap); continue; }
            }
            if (b.Label == null) continue;
            var count = u.Counts.GetValueOrDefault(b.TargetIndex);
            var text = count > 1 ? $"{b.Label} x{count}" : b.Label;
            HlLabel(dl, text, b.Tier, state, min, max, all, disp, th, snap);
        }
    }

    /// <summary>Notice a new highlight or an advance, so the right boxes snap in; forget targets that left the screen
    /// (or went under a big panel: they snap in again when they come back).</summary>
    private static void ObserveHighlights(HighlightUiState u, List<HighlightBox> boxes, string? title, int? current, DateTime since, int rev, double now, long utc)
    {
        var fresh = true;
        if (rev != u.SeenRev)
        {
            var first = u.SeenRev < 0;
            // An advance keeps the title and moves current: only the step just finished and the new one snap again.
            // Anything else is a new highlight: everything enters.
            var advance = !first && title == u.SeenTitle && current != u.SeenCurrent;
            if (advance)
            {
                foreach (var b in boxes)
                    if (b.Order != null && (b.Order == current || b.Order == u.SeenCurrent)) u.ArrivedAt.Remove(b.TargetIndex);
            }
            else { u.ArrivedAt.Clear(); u.AnsweredAt.Clear(); u.CoveredAt.Clear(); u.DockHoverAt.Clear(); u.DockOpen.Clear(); }
            // A highlight set long before this frame (the overlay was not drawing) appears without the entry animation.
            fresh = !first || (DateTime.UtcNow - since).TotalSeconds < 2;
            u.SeenRev = rev;
            u.SeenCurrent = current;
            u.SeenTitle = title;
        }
        u.Present.Clear();
        foreach (var b in boxes)
        {
            if (!HlShown(b, utc)) continue;
            u.Present.Add(b.TargetIndex);
            if (!u.ArrivedAt.ContainsKey(b.TargetIndex)) u.ArrivedAt[b.TargetIndex] = fresh ? now : -1e9;
        }
        // A target whose boxes left the screen (stash tab switched) snaps in again when it comes back.
        u.Gone.Clear();
        foreach (var k in u.ArrivedAt.Keys) if (!u.Present.Contains(k)) u.Gone.Add(k);
        foreach (var k in u.Gone) u.ArrivedAt.Remove(k);
    }

    // ── Geometry and state ───────────────────────────────────────────

    private static (Vector2 min, Vector2 max) HlRect(in HighlightBox b) =>
        (new Vector2(MathF.Round(b.X), MathF.Round(b.Y)), new Vector2(MathF.Round(b.X + b.W), MathF.Round(b.Y + b.H)));

    /// <summary>0 = active (the current step, or not part of a sequence), 1 = upcoming, 2 = done.</summary>
    private static int HlStateOf(in HighlightBox b, int? current)
    {
        if (b.Order is not int o) return 0;
        if (current is not int c) return 2;    // a sequence past its last step: all done
        return o == c ? 0 : o < c ? 2 : 1;
    }

    /// <summary>The box the title belongs to when no step is current: the first shown active primary, else the first
    /// shown box; -1 when every box is covered or gone (then there is no title).</summary>
    private static int HlAnchor(List<HighlightBox> boxes, int? current, long utc)
    {
        var first = -1;
        for (var i = 0; i < boxes.Count; i++)
        {
            if (!HlShown(boxes[i], utc)) continue;
            if (first < 0) first = i;
            if (boxes[i].Tier == "primary" && HlStateOf(boxes[i], current) == 0) return i;
        }
        return first;
    }

    private static float HlEase(double t) => t <= 0 ? 0f : t >= 1 ? 1f : 1f - (float)Math.Pow(1 - t, 3);

    // ── Frames ───────────────────────────────────────────────────────

    /// <summary>The frame of one box, 1 px outside its rect so the interior stays clear. snap 0..1 slides the corners in.</summary>
    private static void HlFrame(ImDrawListPtr dl, Vector2 min, Vector2 max, string tier, int state, float snap, float pulse)
    {
        if (state == 2)
        {
            dl.AddRect(min - Vector2.One, max + Vector2.One, U(ToneNeutral, 0.4f * snap), 3f, ImDrawFlags.None, 1f);
            return;
        }
        var strength = state == 1 ? 0.45f : 1f;
        var off = 1f + (1f - snap) * HlSnapPx;
        var o = new Vector2(off, off);
        switch (tier)
        {
            case "primary":
            {
                var len = Math.Clamp(MathF.Min(max.X - min.X, max.Y - min.Y) * 0.3f, 8f, 18f);
                if (state == 0)
                    dl.AddRect(min - new Vector2(4, 4), max + new Vector2(4, 4), U(ToneAccent, (0.07f + 0.13f * pulse) * snap), 5f, ImDrawFlags.None, 1f);
                HlBrackets(dl, min - o, max + o, len, U(ToneAccent, (0.78f + 0.22f * pulse) * strength * snap), 2f);
                break;
            }
            case "secondary":
                dl.AddRect(min - o * 0.5f - new Vector2(0.5f, 0.5f), max + o * 0.5f + new Vector2(0.5f, 0.5f),
                    U(ToneAccent, 0.6f * strength * snap), 3f, ImDrawFlags.None, 1.5f);
                break;
            default:   // context: a thin neutral outline, enough to find the area, not enough to look at
                dl.AddRect(min - Vector2.One, max + Vector2.One, U(ToneNeutral, 0.5f * strength * snap), 5f, ImDrawFlags.None, 1f);
                break;
        }
    }

    /// <summary>Four L-shaped corner brackets of <paramref name="len"/> px.</summary>
    private static void HlBrackets(ImDrawListPtr dl, Vector2 min, Vector2 max, float len, uint col, float thick)
    {
        dl.PathLineTo(new Vector2(min.X, min.Y + len)); dl.PathLineTo(min); dl.PathLineTo(new Vector2(min.X + len, min.Y));
        dl.PathStroke(col, ImDrawFlags.None, thick);
        dl.PathLineTo(new Vector2(max.X - len, min.Y)); dl.PathLineTo(new Vector2(max.X, min.Y)); dl.PathLineTo(new Vector2(max.X, min.Y + len));
        dl.PathStroke(col, ImDrawFlags.None, thick);
        dl.PathLineTo(new Vector2(max.X, max.Y - len)); dl.PathLineTo(max); dl.PathLineTo(new Vector2(max.X - len, max.Y));
        dl.PathStroke(col, ImDrawFlags.None, thick);
        dl.PathLineTo(new Vector2(min.X + len, max.Y)); dl.PathLineTo(new Vector2(min.X, max.Y)); dl.PathLineTo(new Vector2(min.X, max.Y - len));
        dl.PathStroke(col, ImDrawFlags.None, thick);
    }

    /// <summary>
    /// 18 px step badge on the box's top-left corner: filled accent with the number for the current step, a dark
    /// disc with a neutral ring for an upcoming one, a green disc with a check (pop 0..1 scales it in) for a done one.
    /// </summary>
    private static void HlBadge(ImDrawListPtr dl, Vector2 c, int n, int state, float pop, float snap, PanelTheme th)
    {
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var font = ImGui.GetFont();
        var ink = new Vector4(0.05f, 0.08f, 0.06f, 1f);
        var text = n.ToString();
        var tw = ImGui.CalcTextSize(text).X * (small / f);
        var tp = new Vector2(c.X - tw * 0.5f, c.Y - small * 0.5f);
        // A dark rim under every badge so it reads on bright item art.
        dl.AddCircleFilled(c, HlBadgeR + 1.5f, U(ink, 0.55f * snap), 24);
        switch (state)
        {
            case 0:
                dl.AddCircleFilled(c, HlBadgeR, U(ToneAccent, snap), 24);
                dl.AddText(font, small, tp, U(ink, snap), text);
                dl.AddText(font, small, tp + new Vector2(0.6f, 0), U(ink, snap), text);   // faux bold
                break;
            case 1:
                dl.AddCircleFilled(c, HlBadgeR, U(th.Card, 0.9f * snap), 24);
                dl.AddCircle(c, HlBadgeR, U(ToneNeutral, 0.85f * snap), 24, 1.3f);
                dl.AddText(font, small, tp, U(th.TextDim, snap), text);
                break;
            default:
            {
                dl.AddCircleFilled(c, HlBadgeR, U(ToneOk, 0.8f * snap), 24);
                var s = 0.4f + 0.6f * pop;
                var ck = U(ink, snap);
                dl.AddLine(new Vector2(c.X - 3.6f * s, c.Y + 0.2f * s), new Vector2(c.X - 1f * s, c.Y + 2.8f * s), ck, 1.8f);
                dl.AddLine(new Vector2(c.X - 1f * s, c.Y + 2.8f * s), new Vector2(c.X + 3.8f * s, c.Y - 2.8f * s), ck, 1.8f);
                break;
            }
        }
    }

    /// <summary>
    /// 14 x 20 px mouse: a rounded body with two buttons, the one to press filled with the accent. While
    /// <paramref name="age"/> (seconds since the step became current) is non-negative the button presses once per
    /// 1.4 s cycle: it dips 1 px and brightens, and a small ring leaves its top edge. Negative age: static.
    /// </summary>
    private static void HlMouse(ImDrawListPtr dl, Vector2 pos, bool right, float alpha, double age, PanelTheme th)
    {
        if (alpha <= 0.01f) return;
        var min = pos;
        var max = pos + new Vector2(HlMouseW, HlMouseH);
        var split = min.Y + HlMouseH * 0.42f;
        var midX = min.X + HlMouseW * 0.5f;
        var press = 0f;
        var phase = -1.0;
        if (age >= HlPressDelay)
        {
            phase = (age - HlPressDelay) % HlPressSec / HlPressSec;
            if (phase < 0.22) press = MathF.Sin((float)(Math.PI * phase / 0.22));
        }
        // Body: card fill so it reads on any art, light outline, button split lines.
        dl.AddRectFilled(min, max, U(th.Card, 0.92f * alpha), 6f);
        dl.AddRect(min, max, U(th.Text, 0.8f * alpha), 6f, ImDrawFlags.None, 1.2f);
        dl.AddLine(new Vector2(min.X + 1, split), new Vector2(max.X - 1, split), U(th.Text, 0.55f * alpha), 1f);
        dl.AddLine(new Vector2(midX, min.Y + 1), new Vector2(midX, split), U(th.Text, 0.55f * alpha), 1f);
        // The button to press, dipping while pressed.
        var bx0 = right ? midX + 1f : min.X + 1.5f;
        var bx1 = right ? max.X - 1.5f : midX - 1f;
        var dip = press * 1f;
        dl.AddRectFilled(new Vector2(bx0, min.Y + 1.5f + dip), new Vector2(bx1, split - 1f), U(ToneAccent, (0.7f + 0.3f * press) * alpha), 4.5f,
            right ? ImDrawFlags.RoundCornersTopRight : ImDrawFlags.RoundCornersTopLeft);
        // Click ring: leaves the pressed button's top edge and fades over the first 40% of the cycle.
        if (phase >= 0 && phase < 0.4)
        {
            var k = (float)(phase / 0.4);
            dl.AddCircle(new Vector2((bx0 + bx1) * 0.5f, min.Y + 1.5f), 2.5f + 6.5f * HlEase(k), U(ToneAccent, 0.6f * (1 - k) * alpha), 16, 1.2f);
        }
    }

    /// <summary>Dotted hint from the edge of box a to the edge of box b, with a small chevron at b. Skipped when they touch.</summary>
    private static void HlDots(ImDrawListPtr dl, Vector2 aMin, Vector2 aMax, Vector2 bMin, Vector2 bMax, uint col)
    {
        var ca = (aMin + aMax) * 0.5f;
        var cb = (bMin + bMax) * 0.5f;
        var d = cb - ca;
        var dist = d.Length();
        if (dist < 1f) return;
        d /= dist;
        var s = ca + d * (HlExit((aMax - aMin) * 0.5f, d) + 8f);
        var e = cb - d * (HlExit((bMax - bMin) * 0.5f, d) + 8f);
        var len = (e - s).Length();
        if (len < 24f) return;
        const float gap = 9f;
        var n = (int)(len / gap);
        var start = (len - n * gap) * 0.5f;
        for (var i = 0; i <= n; i++) dl.AddCircleFilled(s + d * (start + i * gap), 1.2f, col, 8);
        var nrm = new Vector2(-d.Y, d.X);
        var tip = e + d * 3f;
        dl.AddLine(tip, tip - d * 5f + nrm * 4f, col, 1.2f);
        dl.AddLine(tip, tip - d * 5f - nrm * 4f, col, 1.2f);
    }

    /// <summary>Distance from a rect's centre to its edge along <paramref name="dir"/> (unit), given the half extents.</summary>
    private static float HlExit(Vector2 half, Vector2 dir)
    {
        var tx = MathF.Abs(dir.X) > 1e-4f ? half.X / MathF.Abs(dir.X) : float.MaxValue;
        var ty = MathF.Abs(dir.Y) > 1e-4f ? half.Y / MathF.Abs(dir.Y) : float.MaxValue;
        return MathF.Min(tx, ty);
    }

    // ── Text ─────────────────────────────────────────────────────────

    /// <summary>The label pill of one box, outside it. Tone follows the tier; done and upcoming steps read dim.</summary>
    private void HlLabel(ImDrawListPtr dl, string text, string tier, int state, Vector2 min, Vector2 max,
        List<HighlightBox> boxes, Vector2 disp, PanelTheme th, float snap)
    {
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var font = ImGui.GetFont();
        var tw = ImGui.CalcTextSize(text).X * (small / f);
        var size = new Vector2(tw + 12, small + 6);
        var (pmin, pmax) = HlPlace(size, min, max, boxes, disp);
        var tone = state != 0 ? th.TextDim : tier switch { "primary" => ToneAccent, "secondary" => ToneAccent, _ => th.TextDim };
        var border = state == 0 && tier != "context" ? ToneAccent : th.Border;
        dl.AddRectFilled(pmin, pmax, U(th.Card, 0.94f * snap), size.Y * 0.5f);
        dl.AddRect(pmin, pmax, U(border, 0.55f * snap), size.Y * 0.5f);
        dl.AddText(font, small, new Vector2(pmin.X + 6, pmin.Y + 3), U(tone, (state == 0 ? 1f : 0.85f) * snap), text);
        _hlPlaced.Add((pmin, pmax));
    }

    // ── Verdicts ─────────────────────────────────────────────────────

    /// <summary>
    /// The question strip of an asked target, outside its box (below first): an accent "?" badge, the question, and
    /// Yes / No / Not sure pills. The pills alone are an ImGui window (no background, no decoration, no saved
    /// settings), so the overlay takes exactly those clicks and nothing else; the visuals stay on the foreground list.
    /// </summary>
    private void HlAskStrip(ImDrawListPtr dl, in HighlightBox b, string owner, int slot, Vector2 min, Vector2 max, List<HighlightBox> boxes, Vector2 disp, PanelTheme th, float snap)
    {
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var font = ImGui.GetFont();
        var k = small / f;
        const float pad = 8f;
        var pillH = small + 6f;
        var askW = ImGui.CalcTextSize(b.Ask).X * k;
        var ctrlW = HlAskControlsWidth(k);
        var size = new Vector2(pad + HlBadgeR * 2 + HlAskGap + askW + 12f + ctrlW + pad, pillH + 8f);
        var (pmin, pmax) = HlPlace(size, min, max, boxes, disp);
        var cy = (pmin.Y + pmax.Y) * 0.5f;

        // The strip: card fill, accent hairline (the question is the current thing to look at).
        dl.AddRectFilled(pmin, pmax, U(th.Card, 0.95f * snap), 6f);
        dl.AddRect(pmin, pmax, U(ToneAccent, 0.55f * snap), 6f);
        var x = pmin.X + pad;
        HlQuestionBadge(dl, font, small, new Vector2(x + HlBadgeR, cy), snap);
        x += HlBadgeR * 2 + HlAskGap;
        dl.AddText(font, small, new Vector2(x, cy - small * 0.5f), U(th.Text, snap), b.Ask);
        x += askW + 12f;
        var answer = HlAskControls(dl, font, small, x, cy, pillH, slot, b.TargetIndex, true, snap, th);
        _hlPlaced.Add((pmin, pmax));
        if (answer != null) HighlightAnswer(owner, b.TargetIndex, answer);
    }

    /// <summary>The width of the Yes / No / Not sure pills with their gaps, at text scale <paramref name="k"/>.</summary>
    private static float HlAskControlsWidth(float k)
    {
        HlAskPillWidths(k, out var yesW, out var noW, out var skipW);
        return yesW + HlAskGap + noW + HlAskGap + skipW;
    }

    private static void HlAskPillWidths(float k, out float yesW, out float noW, out float skipW)
    {
        const float pillPad = 8f, glyphW = 10f;
        yesW = glyphW + 4f + ImGui.CalcTextSize("Yes").X * k + pillPad * 2;
        noW = glyphW + 4f + ImGui.CalcTextSize("No").X * k + pillPad * 2;
        skipW = ImGui.CalcTextSize("Not sure").X * k + pillPad * 2 - 2f;
    }

    /// <summary>
    /// The Yes / No / Not sure pills from <paramref name="x"/> (left edge) at <paramref name="cy"/>. While
    /// <paramref name="interactive"/>, one window over the pills only: hit-testing is the window's, the drawing is ours.
    /// Returns the answer clicked this frame, or null.
    /// </summary>
    private string? HlAskControls(ImDrawListPtr dl, ImFontPtr font, float small, float x, float cy, float pillH, int slot, int target, bool interactive, float alpha, PanelTheme th)
    {
        var k = small / ImGui.GetFontSize();
        HlAskPillWidths(k, out var yesW, out var noW, out var skipW);
        var ctrlW = yesW + HlAskGap + noW + HlAskGap + skipW;
        string? answer = null;
        var hovYes = false; var hovNo = false; var hovSkip = false;
        if (interactive)
        {
            var wmin = new Vector2(x - 3f, cy - pillH * 0.5f - 3f);
            var wsize = new Vector2(ctrlW + 6f, pillH + 6f);
            ImGui.SetNextWindowPos(wmin, ImGuiCond.Always);
            ImGui.SetNextWindowSize(wsize, ImGuiCond.Always);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings;
            if (ImGui.Begin(HlAskWindow(slot, target), flags))
            {
                var py = cy - pillH * 0.5f;
                ImGui.SetCursorScreenPos(new Vector2(x, py));
                ImGui.InvisibleButton(HlAskIds[0], new Vector2(yesW, pillH));
                hovYes = ImGui.IsItemHovered();
                if (ImGui.IsItemClicked()) answer = "yes";
                ImGui.SetCursorScreenPos(new Vector2(x + yesW + HlAskGap, py));
                ImGui.InvisibleButton(HlAskIds[1], new Vector2(noW, pillH));
                hovNo = ImGui.IsItemHovered();
                if (ImGui.IsItemClicked()) answer = "no";
                ImGui.SetCursorScreenPos(new Vector2(x + yesW + HlAskGap + noW + HlAskGap, py));
                ImGui.InvisibleButton(HlAskIds[2], new Vector2(skipW, pillH));
                hovSkip = ImGui.IsItemHovered();
                if (ImGui.IsItemClicked()) answer = "skip";
            }
            ImGui.End();
            ImGui.PopStyleVar(3);
        }
        HlAnswerPill(dl, font, small, new Vector2(x, cy), yesW, pillH, "Yes", ToneOk, 0, hovYes, alpha, th, 1f);
        x += yesW + HlAskGap;
        HlAnswerPill(dl, font, small, new Vector2(x, cy), noW, pillH, "No", ToneBad, 1, hovNo, alpha, th, 1f);
        x += noW + HlAskGap;
        HlAnswerPill(dl, font, small, new Vector2(x, cy), skipW, pillH, "Not sure", th.TextDim, 2, hovSkip, alpha, th, 1f);
        return answer;
    }

    /// <summary>One window id per layer slot and target index (guide.highlight takes at most 40 targets), built once.</summary>
    private string HlAskWindow(int slot, int target)
    {
        var k = slot * 40 + target;
        if (!_hlAskWin.TryGetValue(k, out var id)) _hlAskWin[k] = id = $"##hl_ask_{slot}_{target}";
        return id;
    }

    /// <summary>The "?" badge: the step badge's filled accent disc with a question mark, so "asking" reads like "now".</summary>
    private static void HlQuestionBadge(ImDrawListPtr dl, ImFontPtr font, float small, Vector2 c, float alpha)
    {
        var ink = new Vector4(0.05f, 0.08f, 0.06f, 1f);
        dl.AddCircleFilled(c, HlBadgeR + 1.5f, U(ink, 0.55f * alpha), 24);
        dl.AddCircleFilled(c, HlBadgeR, U(ToneAccent, alpha), 24);
        var f = ImGui.GetFontSize();
        var tw = ImGui.CalcTextSize("?").X * (small / f);
        var tp = new Vector2(c.X - tw * 0.5f, c.Y - small * 0.5f);
        dl.AddText(font, small, tp, U(ink, alpha), "?");
        dl.AddText(font, small, tp + new Vector2(0.6f, 0), U(ink, alpha), "?");
    }

    /// <summary>
    /// One answer pill at <paramref name="pos"/> (left edge, vertical centre): glyph (0 check, 1 cross, 2 none) and
    /// text in the tone; a tinted fill when hovered so the control answers the mouse before the click.
    /// </summary>
    private static void HlAnswerPill(ImDrawListPtr dl, ImFontPtr font, float small, Vector2 pos, float w, float h, string text, Vector4 tone,
        int glyph, bool hovered, float alpha, PanelTheme th, float strength)
    {
        var pmin = new Vector2(pos.X, pos.Y - h * 0.5f);
        var pmax = pmin + new Vector2(w, h);
        var dim = glyph == 2;
        dl.AddRectFilled(pmin, pmax, dim ? U(th.Tile, (hovered ? 1f : 0.7f) * alpha) : U(tone, (hovered ? 0.34f : 0.16f) * alpha), h * 0.5f);
        dl.AddRect(pmin, pmax, dim ? U(th.Border, (hovered ? 0.9f : 0.6f) * alpha) : U(tone, (hovered ? 1f : 0.75f) * alpha), h * 0.5f, ImDrawFlags.None, hovered ? 1.4f : 1f);
        var x = pmin.X + 8f;
        var textTone = hovered && !dim ? Vector4.Lerp(tone, Vector4.One, 0.35f) : tone;
        if (glyph == 0) { HlCheckGlyph(dl, new Vector2(x + 5f, pos.Y), strength, U(textTone, alpha)); x += 14f; }
        else if (glyph == 1) { HlCrossGlyph(dl, new Vector2(x + 5f, pos.Y), strength, U(textTone, alpha)); x += 14f; }
        dl.AddText(font, small, new Vector2(x - (dim ? 1f : 0f), pos.Y - small * 0.5f), U(textTone, alpha), text);
    }

    /// <summary>The result pill after a click, where the strip was: glyph pops in, the pill fades out at the end.</summary>
    private void HlVerdictPill(ImDrawListPtr dl, string answer, double age, Vector2 min, Vector2 max, List<HighlightBox> boxes, Vector2 disp, PanelTheme th, float snap)
    {
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var font = ImGui.GetFont();
        var k = small / f;
        var (text, tone, glyph) = answer switch { "yes" => ("Yes", ToneOk, 0), "no" => ("No", ToneBad, 1), _ => ("Not sure", th.TextDim, 2) };
        var w = (glyph == 2 ? 0f : 14f) + ImGui.CalcTextSize(text).X * k + 16f;
        var h = small + 6f;
        var size = new Vector2(w + 8f, h + 8f);
        var (pmin, pmax) = HlPlace(size, min, max, boxes, disp);
        var fade = age > HlVerdictShowSec - HlVerdictFadeSec ? (float)((HlVerdictShowSec - age) / HlVerdictFadeSec) : 1f;
        var alpha = Math.Clamp(fade, 0f, 1f) * snap;
        dl.AddRectFilled(pmin, pmax, U(th.Card, 0.95f * alpha), 6f);
        dl.AddRect(pmin, pmax, U(tone, 0.55f * alpha), 6f);
        HlAnswerPill(dl, font, small, new Vector2(pmin.X + 4f, (pmin.Y + pmax.Y) * 0.5f), w, h, text, tone, glyph, false, alpha, th, 0.4f + 0.6f * HlEase(age / HlVerdictPopSec));
        _hlPlaced.Add((pmin, pmax));
    }

    /// <summary>The lasting mark on the box's top-right corner: a badge-sized disc in the answer's tone with its glyph.</summary>
    private static void HlVerdictMark(ImDrawListPtr dl, Vector2 c, string answer, float pop, PanelTheme th)
    {
        var ink = new Vector4(0.05f, 0.08f, 0.06f, 1f);
        var (tone, glyph) = answer switch { "yes" => (ToneOk, 0), "no" => (ToneBad, 1), _ => (ToneNeutral, 2) };
        var s = 0.4f + 0.6f * pop;
        dl.AddCircleFilled(c, (HlBadgeR + 1.5f) * s, U(ink, 0.55f * pop), 24);
        dl.AddCircleFilled(c, HlBadgeR * s, U(tone, 0.85f * pop), 24);
        var g = U(ink, pop);
        if (glyph == 0) HlCheckGlyph(dl, c, s, g);
        else if (glyph == 1) HlCrossGlyph(dl, c, s, g);
        else dl.AddLine(new Vector2(c.X - 3.5f * s, c.Y), new Vector2(c.X + 3.5f * s, c.Y), g, 1.8f);
    }

    private static void HlCheckGlyph(ImDrawListPtr dl, Vector2 c, float s, uint col)
    {
        dl.AddLine(new Vector2(c.X - 3.6f * s, c.Y + 0.2f * s), new Vector2(c.X - 1f * s, c.Y + 2.8f * s), col, 1.8f);
        dl.AddLine(new Vector2(c.X - 1f * s, c.Y + 2.8f * s), new Vector2(c.X + 3.8f * s, c.Y - 2.8f * s), col, 1.8f);
    }

    private static void HlCrossGlyph(ImDrawListPtr dl, Vector2 c, float s, uint col)
    {
        dl.AddLine(new Vector2(c.X - 3.2f * s, c.Y - 3.2f * s), new Vector2(c.X + 3.2f * s, c.Y + 3.2f * s), col, 1.8f);
        dl.AddLine(new Vector2(c.X - 3.2f * s, c.Y + 3.2f * s), new Vector2(c.X + 3.2f * s, c.Y - 3.2f * s), col, 1.8f);
    }

    // ── Docked asks ──────────────────────────────────────────────────

    /// <summary>
    /// Every layer's docked asks as one column of small pills flush with a screen edge: at rest the "?" badge and the
    /// question clipped to HlDockTextW in dim text (one width for the column, so it reads as a tidy stack); hovered,
    /// the pill slides out into the full question and the Yes / No / Not sure pills, interactive once mostly open,
    /// and slides back HlDockGraceSec after the mouse leaves. The column goes where it covers no open big panel and no
    /// box (HlDockSpot). Each pill fades with its box's life. Hovering is a hit-test on io.MousePos: no window, so the
    /// resting pill stays click-through.
    /// </summary>
    private void HlDrawDock(ImDrawListPtr dl, double now, long utc, Vector2 disp, PanelTheme th)
    {
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var font = ImGui.GetFont();
        var k = small / f;
        var pillH = MathF.Max(small + 10f, HlBadgeR * 2f + 6f);
        var n = _hlDocked.Count;

        // One rest width for the column: the widest clipped question (capped), plus badge and padding.
        var textW = 0f;
        for (var i = 0; i < n; i++)
        {
            var (l, bi) = _hlDocked[i];
            var w = HlDockClipped(l.Boxes[bi].Ask!, small).width;
            if (w > textW) textW = w;
        }
        var restW = HlDockPad + HlBadgeR * 2f + HlAskGap + textW + HlDockPad;
        var total = n * pillH + (n - 1) * HlDockGap;
        var (right, y0) = HlDockSpot(restW, total, disp);
        var io = ImGui.GetIO();
        var mp = io.MousePos;
        var step = io.DeltaTime > 0 ? io.DeltaTime / (float)HlDockOpenSec : 1f;
        var ctrlW = HlAskControlsWidth(k);
        var strip = small + 6f;   // the answer pills' height, as in the strip

        for (var i = 0; i < n; i++)
        {
            var (l, bi) = _hlDocked[i];
            var b = l.Boxes[bi];
            var u = HlUiFor(l.Owner);
            var life = HlLife(b.EndTicks, utc);
            var y = y0 + i * (pillH + HlDockGap);

            // Slide state: towards 1 while hovered within the grace, else back to 0.
            var open = u.DockOpen.GetValueOrDefault(b.TargetIndex);
            var want = u.DockHoverAt.TryGetValue(b.TargetIndex, out var hAt) && now - hAt < HlDockGraceSec ? 1f : 0f;
            open = want > open ? MathF.Min(want, open + step) : MathF.Max(want, open - step);
            u.DockOpen[b.TargetIndex] = open;
            var ease = HlEase(open);

            var askW = ImGui.CalcTextSize(b.Ask).X * k;
            var fullW = HlDockPad + HlBadgeR * 2f + HlAskGap + askW + 12f + ctrlW + HlDockPad;
            var w = restW + (MathF.Max(fullW, restW) - restW) * ease;
            var pmin = right ? new Vector2(disp.X - w, y) : new Vector2(0f, y);
            var pmax = pmin + new Vector2(w, pillH);
            if (mp.X >= pmin.X && mp.X < pmax.X && mp.Y >= pmin.Y && mp.Y < pmax.Y) u.DockHoverAt[b.TargetIndex] = now;

            // The pill: card fill, accent hairline, rounded away from the edge only (the edge side runs 2 px off screen).
            var edgeMin = right ? pmin : pmin - new Vector2(2f, 0f);
            var edgeMax = right ? pmax + new Vector2(2f, 0f) : pmax;
            var corners = right ? ImDrawFlags.RoundCornersLeft : ImDrawFlags.RoundCornersRight;
            dl.AddRectFilled(edgeMin, edgeMax, U(th.Card, (0.9f + 0.06f * ease) * life), 6f, corners);
            dl.AddRect(edgeMin, edgeMax, U(ToneAccent, (0.35f + 0.25f * ease) * life), 6f, corners, 1f);
            dl.PushClipRect(pmin, pmax, true);
            var cy = (pmin.Y + pmax.Y) * 0.5f;
            var x = pmin.X + HlDockPad;
            HlQuestionBadge(dl, font, small, new Vector2(x + HlBadgeR, cy), (0.8f + 0.2f * ease) * life);
            x += HlBadgeR * 2f + HlAskGap;
            string? answer = null;
            if (open <= 0.001f)
            {
                dl.AddText(font, small, new Vector2(x, cy - small * 0.5f), U(th.TextDim, 0.95f * life), HlDockClipped(b.Ask!, small).text);
            }
            else
            {
                dl.AddText(font, small, new Vector2(x, cy - small * 0.5f), U(th.Text, life), b.Ask);
                x += askW + 12f;
                answer = HlAskControls(dl, font, small, x, cy, strip, l.Slot, b.TargetIndex, open > 0.6f, life, th);
            }
            dl.PopClipRect();
            _hlPlaced.Add((pmin, pmax));
            if (answer != null) HighlightAnswer(l.Owner, b.TargetIndex, answer);
        }
    }

    /// <summary>
    /// Where the dock column of <paramref name="w"/> x <paramref name="h"/> goes: the first of right-middle, left-middle,
    /// right-upper, left-upper, right-lower, left-lower that covers no open big panel (_hlPanels) and no drawn box
    /// (_hlAll); else the least panel overlap, then the least box overlap. Returns the edge and the column's top.
    /// </summary>
    private (bool right, float y) HlDockSpot(float w, float h, Vector2 disp)
    {
        Span<float> centres = stackalloc float[3];
        centres[0] = 0.5f; centres[1] = 0.3f; centres[2] = 0.72f;
        var bestRight = true;
        var bestY = 0f;
        var bestPanel = float.MaxValue;
        var bestBox = float.MaxValue;
        for (var ci = 0; ci < centres.Length; ci++)
        {
            for (var side = 0; side < 2; side++)
            {
                var right = side == 0;
                var y = Math.Clamp(disp.Y * centres[ci] - h * 0.5f, 8f, MathF.Max(8f, disp.Y - h - 8f));
                var x = right ? disp.X - w : 0f;
                var panel = 0f;
                foreach (var p in _hlPanels) panel += HlOverlap(x, y, w, h, p.x, p.y, p.w, p.h);
                var box = 0f;
                foreach (var b in _hlAll) box += HlOverlap(x, y, w, h, b.X, b.Y, b.W, b.H);
                if (panel < bestPanel || (panel == bestPanel && box < bestBox))
                {
                    bestPanel = panel; bestBox = box; bestRight = right; bestY = y;
                    if (panel == 0f && box == 0f) return (right, y);
                }
            }
        }
        return (bestRight, bestY);
    }

    private static float HlOverlap(float ax, float ay, float aw, float ah, float bx, float by, float bw, float bh)
    {
        var w = MathF.Min(ax + aw, bx + bw) - MathF.Max(ax, bx);
        var h = MathF.Min(ay + ah, by + bh) - MathF.Max(ay, by);
        return w > 0 && h > 0 ? w * h : 0f;
    }

    /// <summary>The question clipped to HlDockTextW with "..", and its width, built once per ask string and font size.</summary>
    private (string text, float width) HlDockClipped(string ask, float small)
    {
        if (_hlDockClip.TryGetValue(ask, out var c) && c.small == small) return (c.text, c.width);
        if (_hlDockClip.Count > 64) _hlDockClip.Clear();
        var k = small / ImGui.GetFontSize();
        var text = ask;
        var width = ImGui.CalcTextSize(ask).X * k;
        if (width > HlDockTextW)
        {
            var dots = ImGui.CalcTextSize("..").X * k;
            var n = ask.Length;
            while (n > 1 && ImGui.CalcTextSize(ask.Substring(0, n)).X * k + dots > HlDockTextW) n--;
            text = ask.Substring(0, n).TrimEnd() + "..";
            width = ImGui.CalcTextSize(text).X * k;
        }
        _hlDockClip[ask] = (text, small, width);
        return (text, width);
    }

    // ── Title and placement ──────────────────────────────────────────

    /// <summary>The title once, above the active box: accent dot, title, and the step position of a sequence.</summary>
    private void HlTitle(ImDrawListPtr dl, string title, string? sub, Vector2 min, Vector2 max,
        List<HighlightBox> boxes, Vector2 disp, PanelTheme th, float snap)
    {
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var font = ImGui.GetFont();
        var tw = ImGui.CalcTextSize(title).X;
        var sw = sub != null ? ImGui.CalcTextSize(sub).X * (small / f) + 8 : 0;
        var size = new Vector2(10 + 6 + 6 + tw + sw + 10, f + 8);
        var (pmin, pmax) = HlPlace(size, min, max, boxes, disp, prefer: 1);
        var cy = (pmin.Y + pmax.Y) * 0.5f;
        dl.AddRectFilled(pmin, pmax, U(th.Card, 0.94f * snap), 5f);
        dl.AddRect(pmin, pmax, U(ToneAccent, 0.5f * snap), 5f);
        dl.AddCircleFilled(new Vector2(pmin.X + 10, cy), 3f, U(ToneAccent, snap), 12);
        var x = pmin.X + 10 + 6 + 6;
        dl.AddText(new Vector2(x, cy - f * 0.5f), U(th.Text, snap), title);
        if (sub != null) dl.AddText(font, small, new Vector2(x + tw + 8, cy - small * 0.5f), U(th.TextDim, snap), sub);
        _hlPlaced.Add((pmin, pmax));
    }

    /// <summary>
    /// Where a pill of <paramref name="size"/> goes around a box: the first of below / above / right / left that is on
    /// screen and covers no highlight box, badge or pill placed earlier this frame; else the preferred spot.
    /// <paramref name="prefer"/>: 0 = below first (labels), 1 = above first (title), 2 = right first (the mouse cue).
    /// </summary>
    private (Vector2 min, Vector2 max) HlPlace(Vector2 size, Vector2 min, Vector2 max, List<HighlightBox> boxes, Vector2 disp, int prefer = 0)
    {
        const float gap = 5f;
        var cx = (min.X + max.X) * 0.5f;
        var cy = (min.Y + max.Y) * 0.5f;
        var below = new Vector2(cx - size.X * 0.5f, max.Y + gap);
        var above = new Vector2(cx - size.X * 0.5f, min.Y - gap - size.Y);
        var right = new Vector2(max.X + gap, cy - size.Y * 0.5f);
        var left = new Vector2(min.X - gap - size.X, cy - size.Y * 0.5f);
        Span<Vector2> cands = stackalloc Vector2[4];
        switch (prefer)
        {
            case 1: cands[0] = above; cands[1] = below; cands[2] = right; cands[3] = left; break;
            case 2: cands[0] = right; cands[1] = left; cands[2] = above; cands[3] = below; break;
            default: cands[0] = below; cands[1] = above; cands[2] = right; cands[3] = left; break;
        }
        var fallback = default(Vector2);
        for (var i = 0; i < cands.Length; i++)
        {
            var p = HlClamp(cands[i], size, disp);
            if (i == 0) fallback = p;
            if (!HlCovers(p, p + size, boxes)) return (p, p + size);
        }
        return (fallback, fallback + size);
    }

    private static Vector2 HlClamp(Vector2 p, Vector2 size, Vector2 disp) =>
        new(Math.Clamp(p.X, 2f, MathF.Max(2f, disp.X - size.X - 2f)), Math.Clamp(p.Y, 2f, MathF.Max(2f, disp.Y - size.Y - 2f)));

    private bool HlCovers(Vector2 pmin, Vector2 pmax, List<HighlightBox> boxes)
    {
        foreach (var b in boxes)
            if (pmax.X > b.X - 2 && pmin.X < b.X + b.W + 2 && pmax.Y > b.Y - 2 && pmin.Y < b.Y + b.H + 2) return true;
        foreach (var (lmin, lmax) in _hlPlaced)
            if (pmax.X > lmin.X - 2 && pmin.X < lmax.X + 2 && pmax.Y > lmin.Y - 2 && pmin.Y < lmax.Y + 2) return true;
        return false;
    }
}
