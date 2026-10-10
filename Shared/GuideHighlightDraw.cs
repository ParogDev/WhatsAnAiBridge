using System;
using System.Collections.Generic;
using System.Linq;
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
/// Game-agnostic: ImGui only; nothing here touches ExileCore* directly. Box coordinates come from
/// Element.GetClientRect(), which the HUD draws 1:1 on the ImGui display (Graphics.DrawFrame does the same).
/// </summary>
public partial class WhatsAnAiBridge
{
    private const double HlSnapSec = 0.32, HlRingSec = 0.6, HlCheckSec = 0.3, HlPulseSec = 2.6;
    private const double HlPressSec = 1.4, HlPressDelay = 0.7;   // mouse cue: one press per cycle, the first after the entry ring
    private const float HlBadgeR = 9f, HlSnapPx = 10f, HlMouseW = 14f, HlMouseH = 20f, HlMousePad = 5f;
    private const double HlVerdictShowSec = 2.2, HlVerdictPopSec = 0.3, HlVerdictFadeSec = 0.4;
    private const int HlAskWindows = 40;   // one window id per target index (guide.highlight takes at most 40 targets)
    private static readonly string[] HlAskWin = Enumerable.Range(0, HlAskWindows).Select(i => "##hl_ask_" + i).ToArray();
    private static readonly string[] HlAskIds = ["##hl_yes", "##hl_no", "##hl_skip"];

    /// <summary>Overlay-local state: when each target's boxes appeared, for the entry animations.</summary>
    private sealed class HighlightUiState
    {
        public int SeenRev = -1;
        public int? SeenCurrent;
        public string? SeenTitle;
        public readonly Dictionary<int, double> ArrivedAt = new();           // target index -> ImGui time its boxes (re)appeared
        public readonly HashSet<int> Present = new();
        public readonly HashSet<int> Labelled = new();                       // targets that got their label this frame
        public readonly List<int> Gone = new();
        public readonly Dictionary<int, int> Counts = new();                 // boxes per target (an item can match several)
        public readonly List<(Vector2 min, Vector2 max)> Placed = new();     // label rects placed this frame
        public readonly Dictionary<int, double> AnsweredAt = new();          // target index -> ImGui time its answer was first drawn
        public string? LastError;
    }

    private readonly HighlightUiState _hlUi = new();

    partial void DrawHighlightsImpl(List<HighlightBox> boxes, string? title, int? current, DateTime since, int rev)
    {
        try
        {
            DrawHighlightsBody(boxes, title, current, since, rev);
        }
        catch (Exception ex)
        {
            if (_hlUi.LastError != ex.Message)
            {
                _hlUi.LastError = ex.Message;
                LogError($"[GuideHighlight] {ex}");
            }
        }
    }

    // ── Frame ────────────────────────────────────────────────────────

    private void DrawHighlightsBody(List<HighlightBox> boxes, string? title, int? current, DateTime since, int rev)
    {
        var now = ImGui.GetTime();
        var u = _hlUi;
        ObserveHighlights(boxes, title, current, since, rev, now);

        var dl = ImGui.GetForegroundDrawList();
        var disp = ImGui.GetIO().DisplaySize;
        var th = PanelTheme.Current();
        var pulse = (float)(0.5 + 0.5 * Math.Sin(now * Math.PI * 2 / HlPulseSec));

        // The sequence as drawn: the first box of the current step, of the next step, and how far along it is.
        var hasSeq = false;
        int curIdx = -1, nextIdx = -1, nextOrder = int.MaxValue;
        for (var i = 0; i < boxes.Count; i++)
        {
            if (boxes[i].Order is not int o) continue;
            hasSeq = true;
            if (current is int c)
            {
                if (o == c && curIdx < 0) curIdx = i;
                if (o > c && o < nextOrder) { nextOrder = o; nextIdx = i; }
            }
        }
        u.Counts.Clear();
        foreach (var b in boxes) u.Counts[b.TargetIndex] = u.Counts.GetValueOrDefault(b.TargetIndex) + 1;

        // Pass 1: frames and badges.
        for (var i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            var (min, max) = HlRect(b);
            if (max.X < 0 || max.Y < 0 || min.X > disp.X || min.Y > disp.Y) continue;
            var state = HlStateOf(b, current);
            var arrived = u.ArrivedAt.GetValueOrDefault(b.TargetIndex, -1e9);
            var snap = HlEase((now - arrived) / HlSnapSec);
            var active = state == 0;
            HlFrame(dl, min, max, b.Tier, state, snap, active ? pulse : 0f);
            if (active && b.Tier == "primary")
            {
                // One expanding ring on arrival: the eye lands here, then the slow pulse takes over.
                var k = (now - arrived) / HlRingSec;
                if (k < 1)
                {
                    var grow = 3f + 14f * HlEase(k);
                    dl.AddRect(min - new Vector2(grow, grow), max + new Vector2(grow, grow), U(ToneAccent, 0.55f * (float)(1 - k)), 5f, ImDrawFlags.None, 1.5f);
                }
            }
            if (b.Order is int order)
                HlBadge(dl, new Vector2(min.X, min.Y), order, state, state == 2 ? HlEase((now - arrived) / HlCheckSec) : 1f, snap, th);
        }

        // Pass 2: the hint from the current step to the next, dotted and quiet; it fades in with the current step.
        if (curIdx >= 0 && nextIdx >= 0)
        {
            var (aMin, aMax) = HlRect(boxes[curIdx]);
            var (bMin, bMax) = HlRect(boxes[nextIdx]);
            var snap = HlEase((now - u.ArrivedAt.GetValueOrDefault(boxes[curIdx].TargetIndex, -1e9)) / HlSnapSec);
            HlDots(dl, aMin, aMax, bMin, bMax, U(ToneNeutral, 0.6f * snap));
        }

        // Pass 3: the title once, above the box the user should look at; then, per target, the action cue and the
        // label outside its first box, where they cover no other highlight, badge or pill. Badges are reserved first.
        u.Placed.Clear();
        foreach (var b in boxes)
        {
            var r = new Vector2(HlBadgeR + 1.5f, HlBadgeR + 1.5f);
            if (b.Order != null)
            {
                var (min, _) = HlRect(b);
                u.Placed.Add((min - r, min + r));
            }
            if (b.Answer != null)   // the verdict mark on the top-right corner
            {
                var (min, max) = HlRect(b);
                var c = new Vector2(max.X, min.Y);
                u.Placed.Add((c - r, c + r));
            }
        }
        if (title != null)
        {
            var anchor = curIdx >= 0 ? curIdx : HlAnchor(boxes, current);
            var (min, max) = HlRect(boxes[anchor]);
            string? sub = null;
            if (hasSeq)
            {
                var orders = new SortedSet<int>();
                foreach (var b in boxes) if (b.Order is int o) orders.Add(o);
                if (current is int c) { var pos = 0; foreach (var o in orders) { pos++; if (o == c) break; } sub = $"step {pos} of {orders.Count}"; }
                else sub = "done";
            }
            HlTitle(dl, title, sub, min, max, boxes, disp, th, HlEase((now - u.ArrivedAt.GetValueOrDefault(boxes[anchor].TargetIndex, -1e9)) / HlSnapSec));
        }
        u.Labelled.Clear();
        for (var i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            if (!u.Labelled.Add(b.TargetIndex)) continue;
            var (min, max) = HlRect(b);
            if (max.X < 0 || max.Y < 0 || min.X > disp.X || min.Y > disp.Y) continue;
            var state = HlStateOf(b, current);
            var arrived = u.ArrivedAt.GetValueOrDefault(b.TargetIndex, -1e9);
            var snap = HlEase((now - arrived) / HlSnapSec);
            // Which mouse button to press: animated on the current step, a quiet static glyph on an upcoming one.
            if (b.Action is "click" or "rightclick" && state != 2)
            {
                var size = new Vector2(HlMouseW + HlMousePad * 2, HlMouseH + HlMousePad * 2);
                var (pmin, pmax) = HlPlace(size, min, max, boxes, disp, prefer: 2);
                HlMouse(dl, pmin + new Vector2(HlMousePad, HlMousePad), b.Action == "rightclick", state == 0 ? snap : 0.4f * snap,
                    state == 0 ? now - arrived : -1, th);
                u.Placed.Add((pmin, pmax));
            }
            if (b.Ask != null)
            {
                if (b.Answer == null) { HlAskStrip(dl, b, min, max, boxes, disp, th, snap); continue; }   // the question carries the words
                if (!u.AnsweredAt.TryGetValue(b.TargetIndex, out var answeredAt)) u.AnsweredAt[b.TargetIndex] = answeredAt = now;
                var age = now - answeredAt;
                HlVerdictMark(dl, new Vector2(max.X, min.Y), b.Answer, HlEase(age / HlVerdictPopSec) * snap, th);
                if (age < HlVerdictShowSec) { HlVerdictPill(dl, b.Answer, age, min, max, boxes, disp, th, snap); continue; }
            }
            if (b.Label == null) continue;
            var count = u.Counts.GetValueOrDefault(b.TargetIndex);
            var text = count > 1 ? $"{b.Label} x{count}" : b.Label;
            HlLabel(dl, text, b.Tier, state, min, max, boxes, disp, th, snap);
        }
    }

    /// <summary>Notice a new highlight or an advance, so the right boxes snap in; forget targets that left the screen.</summary>
    private void ObserveHighlights(List<HighlightBox> boxes, string? title, int? current, DateTime since, int rev, double now)
    {
        var u = _hlUi;
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
            else { u.ArrivedAt.Clear(); u.AnsweredAt.Clear(); }
            // A highlight set long before this frame (the overlay was not drawing) appears without the entry animation.
            fresh = !first || (DateTime.UtcNow - since).TotalSeconds < 2;
            u.SeenRev = rev;
            u.SeenCurrent = current;
            u.SeenTitle = title;
        }
        u.Present.Clear();
        foreach (var b in boxes)
        {
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

    /// <summary>The box the title belongs to when no step is current: the first active primary, else the first box.</summary>
    private static int HlAnchor(List<HighlightBox> boxes, int? current)
    {
        for (var i = 0; i < boxes.Count; i++)
            if (boxes[i].Tier == "primary" && HlStateOf(boxes[i], current) == 0) return i;
        return 0;
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
        _hlUi.Placed.Add((pmin, pmax));
    }

    // ── Verdicts ─────────────────────────────────────────────────────

    /// <summary>
    /// The question strip of an asked target, outside its box (below first): an accent "?" badge, the question, and
    /// Yes / No / Not sure pills. The pills alone are an ImGui window (no background, no decoration, no saved
    /// settings), so the overlay takes exactly those clicks and nothing else; the visuals stay on the foreground list.
    /// </summary>
    private void HlAskStrip(ImDrawListPtr dl, in HighlightBox b, Vector2 min, Vector2 max, List<HighlightBox> boxes, Vector2 disp, PanelTheme th, float snap)
    {
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var font = ImGui.GetFont();
        var k = small / f;
        const float pad = 8f, gap = 6f, pillPad = 8f, glyphW = 10f;
        var pillH = small + 6f;
        var askW = ImGui.CalcTextSize(b.Ask).X * k;
        var yesW = glyphW + 4f + ImGui.CalcTextSize("Yes").X * k + pillPad * 2;
        var noW = glyphW + 4f + ImGui.CalcTextSize("No").X * k + pillPad * 2;
        var skipW = ImGui.CalcTextSize("Not sure").X * k + pillPad * 2 - 2f;
        var ctrlW = yesW + gap + noW + gap + skipW;
        var size = new Vector2(pad + HlBadgeR * 2 + gap + askW + 12f + ctrlW + pad, pillH + 8f);
        var (pmin, pmax) = HlPlace(size, min, max, boxes, disp);
        var cy = (pmin.Y + pmax.Y) * 0.5f;

        // The strip: card fill, accent hairline (the question is the current thing to look at).
        dl.AddRectFilled(pmin, pmax, U(th.Card, 0.95f * snap), 6f);
        dl.AddRect(pmin, pmax, U(ToneAccent, 0.55f * snap), 6f);
        var x = pmin.X + pad;
        HlQuestionBadge(dl, font, small, new Vector2(x + HlBadgeR, cy), snap);
        x += HlBadgeR * 2 + gap;
        dl.AddText(font, small, new Vector2(x, cy - small * 0.5f), U(th.Text, snap), b.Ask);
        x += askW + 12f;

        // The controls: one window over the pills only. Hit-testing is the window's; the drawing is ours.
        var wmin = new Vector2(x - 3f, cy - pillH * 0.5f - 3f);
        var wsize = new Vector2(ctrlW + 6f, pillH + 6f);
        string? answer = null;
        var hovYes = false; var hovNo = false; var hovSkip = false;
        ImGui.SetNextWindowPos(wmin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(wsize, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings;
        if (ImGui.Begin(HlAskWin[Math.Clamp(b.TargetIndex, 0, HlAskWindows - 1)], flags))
        {
            var py = cy - pillH * 0.5f;
            ImGui.SetCursorScreenPos(new Vector2(x, py));
            ImGui.InvisibleButton(HlAskIds[0], new Vector2(yesW, pillH));
            hovYes = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) answer = "yes";
            ImGui.SetCursorScreenPos(new Vector2(x + yesW + gap, py));
            ImGui.InvisibleButton(HlAskIds[1], new Vector2(noW, pillH));
            hovNo = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) answer = "no";
            ImGui.SetCursorScreenPos(new Vector2(x + yesW + gap + noW + gap, py));
            ImGui.InvisibleButton(HlAskIds[2], new Vector2(skipW, pillH));
            hovSkip = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) answer = "skip";
        }
        ImGui.End();
        ImGui.PopStyleVar(3);

        HlAnswerPill(dl, font, small, new Vector2(x, cy), yesW, pillH, "Yes", ToneOk, 0, hovYes, snap, th, 1f);
        x += yesW + gap;
        HlAnswerPill(dl, font, small, new Vector2(x, cy), noW, pillH, "No", ToneBad, 1, hovNo, snap, th, 1f);
        x += noW + gap;
        HlAnswerPill(dl, font, small, new Vector2(x, cy), skipW, pillH, "Not sure", th.TextDim, 2, hovSkip, snap, th, 1f);
        _hlUi.Placed.Add((pmin, pmax));
        if (answer != null) HighlightAnswer(b.TargetIndex, answer);
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
        _hlUi.Placed.Add((pmin, pmax));
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
        _hlUi.Placed.Add((pmin, pmax));
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
        foreach (var (lmin, lmax) in _hlUi.Placed)
            if (pmax.X > lmin.X - 2 && pmin.X < lmax.X + 2 && pmax.Y > lmin.Y - 2 && pmin.Y < lmax.Y + 2) return true;
        return false;
    }
}
