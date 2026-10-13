using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// The work strip (research\f8-agent-ux.md 3.2-3.5): one line per active F8 press in the stack under the floor card and the
/// restart card, 480 x (f + 10). Who-dot (the handler's hue once claimed, neutral before), "F8 #12", the request in tracked
/// caps, the track (five phase dots joined by hairlines: done filled in the phase tone, current filled accent with a ring
/// that breathes over 2.6 s, upcoming a neutral ring), the stage word in the phase tone, one tail (the NEEDS YOU pill and
/// where to look, "live fix", a wait, "nobody listening yet", else the item's text) and the elapsed time on the right. At
/// most two lines; a third folds into "+N more" on the second line's tail. Hover = the item's whole story (GuideHover: the
/// stage history, the current text, the PR or where to watch it).
/// Flashes: arrival (accent wash 0.9 s), each stage change (the moved dot pops 0.35 s), Done (the track fills left to right
/// over 0.5 s), Failed (a red wash). Terminal rows hold (Done 6 s, Failed 10 s, Dropped 4 s from the item's FinalAt), then
/// leave. Attention (3.3) is decided in GuideAttentionFor: the strip ghosts with the card, hides in combat (the dot gets an
/// accent ring while an F8 item runs), collapses into the dot after 2 min without a stage change (any change brings it
/// back; a row waiting on the player never hides), and moves with the stack (GuideAvoid). Pops missed while hidden are
/// shown collapsed into one when the strip returns (the flags are consumed at draw time, not at observe time).
/// Reads WorkSnapshot() (Work.cs: immutable rows, rebuilt by the tick only on change). No per-frame allocation: the caps,
/// clipped tails and elapsed texts are cached per row (elapsed once a second), the two strip rows live in a fixed array.
/// Same rules as the rest of the panel: ASCII only, glyphs on the draw list, the toast family's fixed colours.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int WorkStripLines = 2;
    private const double WorkQuietSec = 120, WorkPopSec = 0.35, WorkFillSec = 0.5, WorkBreatheSec = 2.6;
    private const double WorkHoldDoneSec = 6, WorkHoldFailedSec = 10, WorkHoldDroppedSec = 4;
    private const float WorkDotR = 3.5f, WorkDotPitch = 14f, WorkRequestMaxW = 118f, WorkNameW = 110f, WorkRequestW = 150f;
    private const string WorkNeedsYouLabel = "NEEDS YOU", WorkMoreSuffix = " more";

    /// <summary>Per-row drawer state: the animation clocks (consumed when the row is drawn) and the text caches.</summary>
    private sealed class WorkRowUi
    {
        public string Stage = "";
        public bool PendingWash, PendingPop, PendingFill, PendingFail;   // set by ObserveWork, started when the row is next drawn
        public double WashAt = -1e9, PopAt = -1e9, FillAt = -1e9;
        public bool FailWash;                                              // the wash runs in ToneBad (Failed)
        public string? RequestSrc; public string RequestCaps = "";
        public float RequestSize = -1; public string RequestOut = "";      // RequestCaps clipped to WorkRequestMaxW at RequestSize
        public long ElapsedSecond = -1; public string ElapsedText = "";
        public string? TailSrc; public float TailW = -1; public string TailOut = "";
        public bool Seen;
    }

    /// <summary>The strip's and the board's panel-local state.</summary>
    private sealed class WorkUiState
    {
        public long SeenRev = -1;
        public bool First = true;
        public readonly Dictionary<string, WorkRowUi> Rows = new(StringComparer.Ordinal);
        public readonly List<string> Sweep = new();
        public double ChangedAt = -1e9;            // ImGui time of the last stage change or arrival (the 2 min quiet clock)
        public readonly WorkRowView?[] Strip = new WorkRowView?[WorkStripLines];
        public int StripCount, StripMore;
        public int MoreN; public string MoreText = "";
        // The board (GuidePanelBoard.cs): pinned by a click on the sessions chip, unpinned by the x, Esc, a second click, or
        // 30 min without any agent.
        public bool Pinned;
        public double NoAgentSince = -1e9;
    }

    private readonly WorkUiState _workUi = new();

    private static Vector4 WorkToneColor(WorkTone t) => t switch
    {
        WorkTone.Accent => ToneAccent, WorkTone.Ok => ToneOk, WorkTone.Warn => ToneWarn, WorkTone.Bad => ToneBad, _ => ToneNeutral,
    };

    /// <summary>How long a terminal row stays on the strip after its FinalAt (3.4).</summary>
    private static double WorkHoldSec(string stage) => stage switch { "Done" => WorkHoldDoneSec, "Failed" => WorkHoldFailedSec, _ => WorkHoldDroppedSec };

    private static bool WorkHeld(WorkRowView r, DateTime utc) =>
        r.Final && r.FinalAt is { } fa && (utc - fa).TotalSeconds < WorkHoldSec(r.Stage);

    // ── Observe ──────────────────────────────────────────────────────

    /// <summary>
    /// Once per frame, before attention: notice what changed since the last view (arrivals, stage changes, terminal states)
    /// for the flashes and the quiet clock, and pick the strip's rows (active F8 rows, needs-you first, then the terminal
    /// rows still in their hold). Row state is touched only when the view's rev moved; the strip pick runs every frame
    /// without allocating (the hold is a clock).
    /// </summary>
    private void ObserveWork(WorkView work, double now, DateTime utc)
    {
        var u = _workUi;
        if (work.Rev != u.SeenRev)
        {
            u.SeenRev = work.Rev;
            foreach (var r in u.Rows.Values) r.Seen = false;
            foreach (var r in work.Rows) WorkObserveRow(u, r, now);
            foreach (var r in work.Recent) WorkObserveRow(u, r, now);
            u.Sweep.Clear();
            foreach (var (k, r) in u.Rows) if (!r.Seen) u.Sweep.Add(k);
            foreach (var k in u.Sweep) u.Rows.Remove(k);
            if (u.First && work.F8Rows.Length > 0) u.ChangedAt = now;   // items restored at load: the strip shows for its 2 min before it collapses
            u.First = false;
        }
        u.StripCount = 0; u.StripMore = 0;
        foreach (var r in work.F8Rows) WorkStripPlace(u, r);
        foreach (var r in work.Recent) if (r.Kind == "F8" && WorkHeld(r, utc)) WorkStripPlace(u, r);
        for (var i = u.StripCount; i < WorkStripLines; i++) u.Strip[i] = null;
    }

    private static void WorkStripPlace(WorkUiState u, WorkRowView r)
    {
        if (u.StripCount < WorkStripLines) u.Strip[u.StripCount++] = r;
        else u.StripMore++;
    }

    private static void WorkObserveRow(WorkUiState u, WorkRowView r, double now)
    {
        if (!u.Rows.TryGetValue(r.Key, out var ui))
        {
            ui = new WorkRowUi { Stage = r.Stage };
            u.Rows[r.Key] = ui;
            if (!u.First) { ui.PendingWash = true; u.ChangedAt = now; }
            if (r.Final && !u.First) { ui.PendingFill = r.Stage == "Done"; ui.PendingFail = r.Stage == "Failed"; }
        }
        else if (ui.Stage != r.Stage)
        {
            ui.Stage = r.Stage;
            ui.PendingPop = true;
            if (r.Stage == "Done") ui.PendingFill = true;
            else if (r.Stage == "Failed") ui.PendingFail = true;
            u.ChangedAt = now;
        }
        ui.Seen = true;
    }

    /// <summary>The strip has rows to show (active, or a terminal row in its hold).</summary>
    private bool WorkStripHasRows => _workUi.StripCount > 0;

    /// <summary>No stage change for 2 min: the strip collapses into the dot (3.3), unless a row waits on the player.</summary>
    private bool WorkQuiet(double now) => now - _workUi.ChangedAt > WorkQuietSec;

    private bool WorkStripNeedsYou()
    {
        var u = _workUi;
        for (var i = 0; i < u.StripCount; i++) if (u.Strip[i]!.NeedsYou) return true;
        return false;
    }

    // ── The strip ────────────────────────────────────────────────────

    /// <summary>The strip's rows in the stack (DrawGuideBody): one line each, then a 5 px gap.</summary>
    private void DrawWorkStrip(in GuideAttention at, double now, DateTime utc)
    {
        var u = _workUi;
        var dl = ImGui.GetWindowDrawList();
        var f = ImGui.GetFontSize();
        var h = f + 10f;
        var w = GuideWidth - GuideEdge * 2;
        for (var i = 0; i < u.StripCount; i++)
        {
            var r = u.Strip[i]!;
            var p = ImGui.GetCursorScreenPos();
            var min = new Vector2(p.X + GuideEdge, p.Y);
            var more = i == WorkStripLines - 1 ? u.StripMore : 0;
            DrawWorkRow(dl, min, w, h, r, WorkRowUiOf(r), more, now, utc, true, 1f, WorkRowIds[i]);
            ImGui.SetCursorScreenPos(p);
            ImGui.Dummy(new Vector2(GuideWidth, h + (i < u.StripCount - 1 ? 3f : GuideEdge)));
        }
    }

    private WorkRowUi WorkRowUiOf(WorkRowView r)
    {
        var u = _workUi;
        if (!u.Rows.TryGetValue(r.Key, out var ui)) { ui = new WorkRowUi { Stage = r.Stage, Seen = true }; u.Rows[r.Key] = ui; }
        return ui;
    }

    /// <summary>
    /// One strip row (also the board's F8 rows, GuidePanelBoard.cs, with <paramref name="shell"/> false: no ink, no stripe,
    /// no hover of its own). The pending flashes are started here, so changes missed while the strip was hidden (combat,
    /// the quiet dot) show as one pop when it returns.
    /// </summary>
    private void DrawWorkRow(ImDrawListPtr dl, Vector2 min, float w, float h, WorkRowView r, WorkRowUi ui, int more, double now, DateTime utc,
        bool shell, float alpha, string id)
    {
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var k = small / f;
        var max = min + new Vector2(w, h);
        var cy = min.Y + h * 0.5f;
        var tone = WorkToneColor(r.Tone);
        if (ui.PendingWash) { ui.PendingWash = false; ui.WashAt = now; ui.FailWash = false; }
        if (ui.PendingFail) { ui.PendingFail = false; ui.WashAt = now; ui.FailWash = true; }
        if (ui.PendingPop) { ui.PendingPop = false; ui.PopAt = now; }
        if (ui.PendingFill) { ui.PendingFill = false; ui.FillAt = now; }

        if (shell)
        {
            dl.AddRectFilled(min, max, U(GuideToastInk, 0.94f * alpha), 0f);
            var washT = (now - ui.WashAt) / GuideFlashSec;
            if (washT < 1) dl.AddRectFilled(min, max, U(ui.FailWash ? ToneBad : ToneAccent, 0.35f * (float)(1 - washT) * alpha), 0f);
            dl.AddRect(min, max, U(GuideToastLine, alpha), 0f);
            dl.AddRectFilled(min, new Vector2(min.X + GuideToastStripe, max.Y), U(tone, alpha), 0f);
            // Hover: the whole story under the stack (title, the stage history, the text, the PR or where to watch it).
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton(id, new Vector2(w, h));
            if (ImGui.IsItemHovered()) GuideHover(r.HoverTitle, r.HoverBody, r.HoverNote, r.HoverFoot, tone);
        }

        // Left to right: who-dot, label, the request in tracked caps, the track, the stage word.
        var x = min.X + (shell ? GuideToastStripe : 0f) + GuideToastPadX;
        if (r.HasOwner) DrawWhoDot(dl, new Vector2(x + GuideDotR, cy), r.Hue, alpha);
        else DrawNeutralDot(dl, new Vector2(x + GuideDotR, cy), alpha);
        x += WhoDotW;
        dl.AddText(font, small, new Vector2(x, cy - small * 0.5f), U(GuideToastText, 0.9f * alpha), r.Label);
        x += ImGui.CalcTextSize(r.Label).X * k + 8f;
        if (!GuideSame(ui.RequestSrc, r.Request)) { ui.RequestSrc = r.Request; ui.RequestCaps = GuideCaps(r.Request); ui.RequestSize = -1; }
        if (ui.RequestSize != small) { ui.RequestSize = small; ui.RequestOut = GuideClipTracked(ui.RequestCaps, font, small, GuideToastMsgTrack, WorkRequestMaxW); }
        var req = ui.RequestOut;
        GuideTrackedText(dl, font, small, new Vector2(x, cy - small * 0.5f), U(GuideToastMuted, alpha), req, GuideToastMsgTrack);
        x += GuideTrackedWidth(font, small, req, GuideToastMsgTrack) + 14f;
        DrawWorkTrack(dl, new Vector2(x + WorkDotR, cy), r, ui, tone, now, alpha);
        x += WorkDotR * 2 + WorkDotPitch * 4 + 12f;
        var wcol = U(tone, alpha);
        GuideTrackedText(dl, font, small, new Vector2(x, cy - small * 0.5f), wcol, r.StageWord, GuideToastTitleTrack);
        GuideTrackedText(dl, font, small, new Vector2(x + 0.6f, cy - small * 0.5f), wcol, r.StageWord, GuideToastTitleTrack);
        x += GuideTrackedWidth(font, small, r.StageWord, GuideToastTitleTrack) + 10f;

        // Right to left: elapsed since the press (warn when stalled), "+N more" on the last line, then the tail in the room left.
        var rx = max.X - GuideToastPadX;
        var sec = utc.Ticks / TimeSpan.TicksPerSecond;
        if (ui.ElapsedSecond != sec) { ui.ElapsedSecond = sec; ui.ElapsedText = GuideElapsed(utc - r.StartedAt); }
        var ew = ImGui.CalcTextSize(ui.ElapsedText).X * k;
        dl.AddText(font, small, new Vector2(rx - ew, cy - small * 0.5f), U(r.Stalled && !r.Final ? ToneWarn : GuideToastMuted, alpha), ui.ElapsedText);
        rx -= ew + 10f;
        if (more > 0)
        {
            var u = _workUi;
            if (u.MoreN != more) { u.MoreN = more; u.MoreText = "+" + more + WorkMoreSuffix; }
            var mw = ImGui.CalcTextSize(u.MoreText).X * k;
            dl.AddText(font, small, new Vector2(rx - mw, cy - small * 0.5f), U(GuideToastMuted, 0.85f * alpha), u.MoreText);
            rx -= mw + 10f;
        }
        DrawWorkTail(dl, font, f, x, rx, cy, r, ui, alpha);
    }

    /// <summary>The one tail (3.2): the NEEDS YOU pill + where to look, "live fix", a wait, "nobody listening yet", or the text, clipped.</summary>
    private static void DrawWorkTail(ImDrawListPtr dl, ImFontPtr font, float f, float x, float rx, float cy, WorkRowView r, WorkRowUi ui, float alpha)
    {
        if (r.TailKind == WorkTailKind.None || r.Tail == null || rx - x < 24f) return;
        var small = f * 0.85f;
        var k = small / f;
        if (r.TailKind == WorkTailKind.NeedsYou)
        {
            var tiny = f * 0.75f;
            var pw = GuideTrackedWidth(font, tiny, WorkNeedsYouLabel, GuideToastMsgTrack) + 12f;
            var ph = tiny + 6f;
            if (rx - x < pw) return;
            var pmin = new Vector2(x, cy - ph * 0.5f);
            dl.AddRectFilled(pmin, pmin + new Vector2(pw, ph), U(ToneAccent, 0.9f * alpha), 0f);
            var ink = U(GuideInk, alpha);
            GuideTrackedText(dl, font, tiny, new Vector2(x + 6f, cy - tiny * 0.5f), ink, WorkNeedsYouLabel, GuideToastMsgTrack);
            GuideTrackedText(dl, font, tiny, new Vector2(x + 6.6f, cy - tiny * 0.5f), ink, WorkNeedsYouLabel, GuideToastMsgTrack);
            x += pw + 8f;
        }
        var room = rx - x;
        if (room < 24f) return;
        if (!ReferenceEquals(ui.TailSrc, r.Tail) || ui.TailW != room) { ui.TailSrc = r.Tail; ui.TailW = room; ui.TailOut = GuideClipText(r.Tail, room / k); }
        var col = r.TailKind switch
        {
            WorkTailKind.LiveFix => U(ToneAccent, alpha),
            WorkTailKind.Nobody => U(ToneWarn, alpha),
            WorkTailKind.Waiting when r.TailWarn => U(ToneWarn, alpha),
            WorkTailKind.NeedsYou => U(GuideToastText, 0.9f * alpha),
            _ => U(GuideToastMuted, alpha),
        };
        dl.AddText(font, small, new Vector2(x, cy - small * 0.5f), col, ui.TailOut);
    }

    /// <summary>
    /// The track: five phase dots (r 3.5, 14 px pitch) joined by 1 px hairlines. Done = filled in the phase tone, current =
    /// filled accent with a 1.5 px ring breathing over 2.6 s, upcoming = a neutral ring at 40%. Done fills all five in ToneOk
    /// (left to right over 0.5 s after it arrived), Failed fills the reached ones in ToneBad, Dropped greys them all. The dot
    /// that just moved pops for 0.35 s (a ring leaves it, it grows and settles).
    /// </summary>
    private static void DrawWorkTrack(ImDrawListPtr dl, Vector2 c0, WorkRowView r, WorkRowUi ui, Vector4 tone, double now, float alpha)
    {
        var fillT = r.Stage == "Done" ? HlEase((now - ui.FillAt) / WorkFillSec) : 1f;
        var pop = 1f - HlEase((now - ui.PopAt) / WorkPopSec);
        var breathe = 0.5f + 0.5f * MathF.Sin((float)(now * Math.PI * 2 / WorkBreatheSec));
        for (var k = 0; k < 5; k++)
        {
            var c = new Vector2(c0.X + k * WorkDotPitch, c0.Y);
            if (k > 0)
            {
                var reachedSeg = r.Stage == "Done" ? k / 4f <= fillT : r.Stage == "Failed" ? k <= r.ReachedPhase : r.Stage != "Dropped" && k <= r.PhaseIndex;
                dl.AddLine(new Vector2(c.X - WorkDotPitch + WorkDotR, c.Y), new Vector2(c.X - WorkDotR, c.Y),
                    U(reachedSeg ? tone : GuideToastLine, (reachedSeg ? 0.55f : 1f) * alpha), 1f);
            }
            if (r.Stage == "Done")
            {
                var on = k / 4f <= fillT;
                if (on) dl.AddCircleFilled(c, WorkDotR, U(ToneOk, alpha), 12);
                else dl.AddCircle(c, WorkDotR, U(ToneNeutral, 0.4f * alpha), 12, 1f);
            }
            else if (r.Stage == "Failed")
            {
                if (k <= r.ReachedPhase) dl.AddCircleFilled(c, WorkDotR, U(ToneBad, alpha), 12);
                else dl.AddCircle(c, WorkDotR, U(ToneNeutral, 0.4f * alpha), 12, 1f);
            }
            else if (r.Stage == "Dropped") dl.AddCircleFilled(c, WorkDotR, U(ToneNeutral, 0.45f * alpha), 12);
            else if (k < r.PhaseIndex) dl.AddCircleFilled(c, WorkDotR, U(tone, 0.9f * alpha), 12);
            else if (k == r.PhaseIndex)
            {
                var grow = 1f + 0.5f * pop;
                dl.AddCircleFilled(c, WorkDotR * grow, U(ToneAccent, alpha), 14);
                dl.AddCircle(c, WorkDotR + 2.2f + 0.8f * breathe, U(ToneAccent, (0.25f + 0.35f * breathe) * alpha), 16, 1.5f);
                if (pop > 0.01f) dl.AddCircle(c, WorkDotR + 2f + 7f * (1f - pop), U(ToneAccent, 0.6f * pop * alpha), 16, 1.2f);
            }
            else dl.AddCircle(c, WorkDotR, U(ToneNeutral, 0.4f * alpha), 12, 1f);
        }
    }

    /// <summary>The who-dot of an item nobody has yet: the neutral tone on the ink ring (the same construction as a hue dot).</summary>
    private static void DrawNeutralDot(ImDrawListPtr dl, Vector2 c, float alpha = 1f)
    {
        dl.AddCircleFilled(c, GuideDotR + 1.5f, U(GuideToastInk, 0.85f * alpha), 16);
        dl.AddCircleFilled(c, GuideDotR, U(ToneNeutral, 0.8f * alpha), 16);
    }

    private static readonly string[] WorkRowIds = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, WorkStripLines), i => "##work_row_" + i));   // one id per strip line

    /// <summary>A 12 px spinner arc (a Task row working), turning at the card glyph's pace.</summary>
    private static void DrawWorkSpinner(ImDrawListPtr dl, Vector2 c, uint col, double now)
    {
        var a0 = (float)(now * 4.5 % (Math.PI * 2));
        dl.PathClear();
        dl.PathArcTo(c, 5f, a0, a0 + 4.2f, 14);
        dl.PathStroke(col, ImDrawFlags.None, 1.4f);
    }
}
