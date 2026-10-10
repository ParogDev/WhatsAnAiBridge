using System;
using System.Numerics;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// The ask card and the floor's card slot (research\attention-queue-ui.md sections 1, 4.3-4.6 and 5). The ask is the
/// live card with the status QUESTION: the card's shell (slate ink, square corners, the accent stripe, the title row),
/// the question badge, the question on up to two lines, the choice pills with Later pinned at the right end of the
/// first row, and an optional footer (the agent's detail, up to two lines). No default and no primary: every choice
/// weighs the same. Wraps come from GuideWrapCache like the live card's; hovers (a pill's hint, Later, the x, the
/// question block when the card holds more than it shows) are GuideHover panels under the card's stack.
/// The slot (AskSlotUp / DrawAskSlot) owns the hand-over: a click shows its receipt (the chosen pill fills, the others
/// fade, 0.8 s), the card fades (0.25 s), the slot stays empty for the gap (0.6 s, it keeps its height so nothing under
/// it jumps), then the next card arrives washed in its owner's hue when the owner changed, with its controls inert for
/// the click guard (0.5 s; the live card's x, Stop, Done and Not done yet too). The bridge is told when the slot comes
/// free (AttnSlotBusyUntil), so the next card's visibleSince, a queued call's replay and a restart's countdown wait for
/// the same moment the player sees.
/// Agents are named by their display name (AgentsView.NameOf by session id, never a label) and coloured by their
/// session's hue (AgentsView.HueOf), so a rename mid-process changes neither.
/// The queue view, the next strip and the dot's look are in GuidePanelQueue.cs. Same rules as the rest of the panel:
/// ASCII only, glyphs on the draw list, the toast family's fixed colours, no allocation per frame beyond what the live
/// card already does (wraps, names and caps are cached per card).
/// </summary>
public partial class WhatsAnAiBridge
{
    // Timing (attention-queue-ui.md 4.7): the click guard after a card appears or changes owner, the answer receipt, the
    // card's exit fade, the empty gap between two cards (and how long the gap waits for the mouse to leave the old pill
    // row), softening after an ask was ignored, and how fast the stripe and pulse soften.
    private const double AskClickGuardSec = 0.5, AskGuardEaseSec = 0.25, AskReceiptSec = 0.8, AskExitSec = 0.25, AskGapSec = 0.6, AskGapMouseHoldSec = 2.0;
    private const double AskSoftenSec = 60, AskSoftenFadeSec = 0.5;
    // Layout: the loud stripe, the glyph box, the pills (height f + 10, 12 px padding, 8 px gap, 6 px between rows, 64..160 wide).
    private const float AskStripeNow = 5f, AskGlyphBox = 22f, AskPillPadX = 12f, AskPillGap = 8f, AskPillRowGap = 6f, AskPillMinW = 64f, AskPillMaxW = 160f;
    private const float AskCheckW = 14f;   // the receipt's check before the chosen label
    // The text column of the loud card (stripe 5, pad 12, glyph 22, gap 10 = 49), fixed so softening never shifts the text.
    private const float AskTextX = AskStripeNow + GuideToastPadX + AskGlyphBox + GuideToastIconGap;
    private const float WhoDotW = 12f;     // a who-dot's advance before its label: the 6 px dot and a 6 px gap
    private const int AskQuestionLinesMax = 2, AskDetailLinesMax = 2;
    private const string AskLabel = "QUESTION", AskLaterText = "Later", AskAgainText = "again", AskAnsweredText = "answered";
    private const string AskLaterTip = "Ask me again in a calm moment (about 3 minutes).";
    private static readonly string[] AskPillIds = ["##ask_0", "##ask_1", "##ask_2", "##ask_3"];

    // Session hues (attention-queue-ui.md 2.1): identity only, never a status tone. Index = SessionInfo.Hue (AttentionQueue.cs);
    // slate is the fallback for anon, scripts and a seventh session.
    private static readonly Vector4[] AttnHuePalette = [Hex(0xA78BFA), Hex(0xF472B6), Hex(0x60A5FA), Hex(0xFB923C), Hex(0xD9B36C), Hex(0x94A3B8)];
    private static readonly Vector4 GuideInk = new(0.05f, 0.08f, 0.06f, 1f);

    private static Vector4 AttnHueColor(int hue) => AttnHuePalette[hue >= 0 && hue < AttnHuePalette.Length ? hue : AttnHueFallback];

    /// <summary>Panel-local state of the card slot and the ask card: what is shown, what is leaving, and the caches.</summary>
    private sealed class AskUiState
    {
        public AskView? Shown;               // the ask drawn (or leaving) this frame; null: the slot is empty
        public double ArrivedAt = -1e9;      // ImGui time the shown ask appeared
        public bool Flash;                   // the arrival wash runs (not for a re-ask after the player's x)
        public Vector4 FlashTone = ToneAccent; // the accent, or the new owner's hue when the owner changed
        public float Loud = 1f;              // eased loudness: 1 = stripe 5 and the calm pulse, 0 = the calm look
        public double LeaveAt = -1e9;        // ImGui time the card started leaving (-1e9: not leaving)
        public bool Receipt;                 // it leaves with the answer receipt first
        public string? ChosenId;             // the option the receipt fills
        public string? LeaveWho, LeaveSession; // the leaving card's owner: the restart card under it names it until the slot is free
        public float LastH;                  // the card's height last frame: the empty gap keeps it
        public double GapUntil = -1e9;       // the slot stays empty until then (then the next card may arrive)
        public double GapHoldMax = -1e9;     // ...and waits for the mouse to leave the old pill row at most until then
        public Vector2 PillsMin, PillsMax;   // the pill rows' rect last frame
        public string? SlotOwner;            // the label of the last card's owner (ask or instruction): a change washes the next card in its hue
        public double GuardFrom = -1e9;      // click guard start: a card appeared or the owner changed
        public Vector4? CardFlashHue;        // the instruction card's arrival wash when its owner changed (null: its tone)
        public int GuideWhoHue = AttnHueFallback; // the hue of the instruction card's owner
        // The ask card's caches, rebuilt per card (id + shown time), names (the AgentsView instance) and width: the who
        // names in caps, the title in caps, the pill labels clipped to the widest pill, the x's and the hover's texts.
        public string? CacheId; public DateTime CacheShown; public float CacheW; public int CacheWhos; public AgentsView? CacheAg;
        public readonly GuideWrapCache QWrap = new(), DWrap = new();
        public string[] WhoCaps = []; public string? TitleCaps;
        public string[] Labels = [];
        public string? SkipTip, AskFoot;
        // Who the card names (GuideShowWho), decided when the agents view, the queue view or the owner changes.
        public AgentsView? ShowWhoAg; public AttnQueueView? ShowWhoQueue; public string? ShowWhoOwner; public bool ShowWhoValue;
        // Other surfaces (GuidePanelQueue.cs, GuidePanelSessions.cs).
        public AttnQueueView Queue = AttnQueueView.Empty;
        public AttnQueueView? StripFor; public AgentsView? StripAg; public bool StripRestart;
        public string StripLabel = ""; public string? StripText, StripTail, StripTip; public int StripHue; public bool StripNext;
        public string[] StripAgents = []; public int[] StripAgentHues = [];
        public AttnQueueView? DotFor; public AgentsView? DotAg; public string DotLabel = "";
        public string? RestartTop; public double RestartTopAt = -1e9;
        public bool DotWasHovered;
    }

    private readonly AskUiState _askUi = new();

    // ── The slot ─────────────────────────────────────────────────────

    /// <summary>
    /// Once per frame, before attention: what the floor's slot holds. The shown ask stays until it leaves (a click, or the
    /// bridge ended it: expired, withdrawn, its session gone); a leaving card runs its receipt and fade; then the gap; then
    /// the next ask may arrive, once the bridge says it is visible and the mouse is off the old pill row. Returns whether
    /// the slot is occupied (a card, a leaving card or the gap) - the panel stays up for it.
    /// </summary>
    private bool AskSlotUp(AskView? ask, double now, DateTime utc, Vector2 mouse)
    {
        var a = _askUi;
        if (a.LeaveAt > -1e8 && now - a.LeaveAt >= AskLeaveDur(a))
        {
            a.LeaveAt = -1e9; a.Shown = null; a.ChosenId = null;
            a.GapUntil = now + AskGapSec; a.GapHoldMax = now + AskGapSec + AskGapMouseHoldSec;
        }
        var leaving = a.LeaveAt > -1e8;
        if (!leaving && a.Shown != null && !(ask != null && AskSame(a.Shown, ask)))
        {
            AskStartLeave(a, now, utc, false, null);   // gone without a click: fades, no receipt
            leaving = true;
        }
        if (!leaving && ask is { Hidden: false })
        {
            if (a.Shown == null)
            {
                var mouseOnPills = now < a.GapHoldMax && mouse.X >= a.PillsMin.X && mouse.X < a.PillsMax.X && mouse.Y >= a.PillsMin.Y && mouse.Y < a.PillsMax.Y;
                if (now >= a.GapUntil && !mouseOnPills) AskArrive(a, ask, now);
            }
            else a.Shown = ask;   // the same card, a refreshed copy (visibleSince, a merged follower)
        }
        return a.Shown != null || now < a.GapUntil;
    }

    private static bool AskSame(AskView x, AskView y) => x.Id == y.Id && x.ShownAt == y.ShownAt;

    private static double AskLeaveDur(AskUiState a) => (a.Receipt ? AskReceiptSec : 0) + AskExitSec;

    private static void AskArrive(AskUiState a, AskView ask, double now)
    {
        var owner = ask.Whos[0];
        a.Flash = !ask.Softened;
        a.FlashTone = a.SlotOwner != null && a.SlotOwner != owner ? AttnHueColor(ask.Hues[0]) : ToneAccent;
        a.SlotOwner = owner; a.Shown = ask; a.ArrivedAt = now; a.GuardFrom = now;
        a.Loud = ask.Urgency == "now" && !ask.Softened ? 1f : 0f;
        a.ChosenId = null; a.LeaveWho = a.LeaveSession = null;
    }

    /// <summary>The shown card starts leaving; the bridge's slot clock runs until its receipt and fade are over.</summary>
    private void AskStartLeave(AskUiState a, double now, DateTime utc, bool receipt, string? chosenId)
    {
        a.LeaveAt = now; a.Receipt = receipt; a.ChosenId = chosenId; a.LeaveWho = a.Shown?.Whos[0]; a.LeaveSession = a.Shown?.Sessions[0];
        AttnSlotBusyUntil(utc.AddSeconds(AskLeaveDur(a)));
    }

    /// <summary>
    /// Right after ObserveGuide: an instruction card that arrived this frame takes the slot's ownership too, so a card from
    /// another owner than the last one is washed in its hue (DrawGuideCard reads CardFlashHue) and its controls are guarded.
    /// </summary>
    private void AskObserveGuideOwner(string? who, string? session, AgentsView ag, double now)
    {
        var a = _askUi;
        a.GuideWhoHue = ag.HueOf(session, who);
        if (_guideUi.ArrivedAt != now || who == null) return;
        a.CardFlashHue = a.SlotOwner != null && a.SlotOwner != who ? AttnHueColor(a.GuideWhoHue) : null;
        a.SlotOwner = who; a.GuardFrom = now;
    }

    /// <summary>Clicks on the floor card's controls are ignored for ClickGuardSec after it appeared or changed owner (section 5).</summary>
    private bool AskClickGuarded(double now) => now - _askUi.GuardFrom < AskClickGuardSec;

    /// <summary>
    /// Whether the cards name their owner: only when it is not the obvious single agent - two or more agents active
    /// (AgentsView.Ambiguous), or someone other than the card's owner waiting in the attention queue. Recomputed only when
    /// one of its inputs changes (the views are rebuilt at most 4 Hz and kept by reference).
    /// </summary>
    private bool GuideShowWho(AgentsView ag, AttnQueueView queue, string? owner)
    {
        var a = _askUi;
        if (ReferenceEquals(a.ShowWhoAg, ag) && ReferenceEquals(a.ShowWhoQueue, queue) && ReferenceEquals(a.ShowWhoOwner, owner)) return a.ShowWhoValue;
        a.ShowWhoAg = ag; a.ShowWhoQueue = queue; a.ShowWhoOwner = owner;
        var show = ag.Ambiguous;
        // One more agent in line than the one on the card (or two different ones in line while no card is up).
        string? first = owner ?? queue.HolderWho;
        foreach (var x in ag.Active) { if (show) break; if (first == null) first = x.Label; else if (x.Label != first) show = true; }
        foreach (var r in queue.Rows) { if (show) break; if (first == null) first = r.Who; else if (r.Who != first) show = true; }
        return a.ShowWhoValue = show;
    }

    /// <summary>The floor's slot in the stack: the ask card (shown or leaving), or the empty gap at the last card's height. Returns whether it drew.</summary>
    private bool DrawAskSlot(in GuideAttention at, AgentsView ag, PanelTheme th, double now, DateTime utc)
    {
        var a = _askUi;
        if (at.FloorHidden) return false;   // combat: the dot stands in (the slot state keeps running)
        if (a.Shown != null) { DrawAskCard(at, ag, th, now, utc); return true; }
        if (now < a.GapUntil && a.LastH > 0) { ImGui.Dummy(new Vector2(GuideWidth, a.LastH)); return true; }
        return false;
    }

    // ── The card ─────────────────────────────────────────────────────

    private void DrawAskCard(in GuideAttention at, AgentsView ag, PanelTheme th, double now, DateTime utc)
    {
        var a = _askUi;
        var ask = a.Shown!;
        var dl = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var k = small / f;
        var io = ImGui.GetIO();

        var leaving = a.LeaveAt > -1e8;
        var leaveT = leaving ? now - a.LeaveAt : 0;
        var receipt = leaving && a.Receipt;
        var fadeT = leaving ? Math.Clamp((leaveT - (receipt ? AskReceiptSec : 0)) / AskExitSec, 0, 1) : 0;
        var alpha = 1f - (float)fadeT;

        // Loudness: a `now` ask is loud until it softens (ignored 60 s on screen); calm, softened and re-asked ones are not.
        var seen = ask.VisibleSince is { } vs ? (utc - vs).TotalSeconds : 0;
        var loudTarget = ask.Urgency == "now" && !ask.Softened && seen < AskSoftenSec ? 1f : 0f;
        a.Loud += (loudTarget - a.Loud) * (1f - MathF.Exp(-io.DeltaTime / (float)(AskSoftenFadeSec / 3)));
        var loud = a.Loud;
        var stripe = GuideToastStripe + (AskStripeNow - GuideToastStripe) * loud;
        var padY = GuideToastPadY + (10f - GuideToastPadY) * loud;

        var p = ImGui.GetCursorScreenPos();
        var x0 = p.X + GuideEdge;
        var w = GuideWidth - GuideEdge * 2;
        var textX = x0 + AskTextX;
        var textMax = x0 + w - GuideToastPadX - textX;
        var pillH = f + 10f;

        // Measure. The question wraps onto two lines at most, the footer too (GuideWrapCache: cached until the text or
        // width changes); the pills flow left to right, and Later keeps the right end of the first row (12 glyph + 6 +
        // text + 16), so the first row is shorter. Names, caps and labels are cached per card, width and names.
        AskCache(a, ask, ag, textMax);
        var qLines = a.QWrap.Wrap(ask.Question, f, textMax, AskQuestionLinesMax);
        var dLines = ask.Detail == null ? 0 : a.DWrap.Wrap(ask.Detail, small, textMax, AskDetailLinesMax);
        var laterW = 12f + 6f + ImGui.CalcTextSize(AskLaterText).X * k + 16f;
        var rows = AskPillRows(a.Labels, textMax, laterW, receipt ? a.ChosenId : null, ask.Options);
        var qH = qLines * f + (qLines - 1) * 2f;
        var footH = dLines == 0 ? 0f : 6f + dLines * small + (dLines - 1) * 2f;
        var h = padY + f + GuideToastLineGap + qH + 8f + rows * pillH + (rows - 1) * AskPillRowGap + footH + padY;
        var min = new Vector2(x0, p.Y + GuideEdge);
        var max = new Vector2(x0 + w, p.Y + GuideEdge + h);

        // Shell: slate ink, square corners; the arrival wash (the tone, or the new owner's hue); the hairline, with the
        // calm pulse's accent frame over it while loud. Never the Urgent throb: a question is not an emergency.
        dl.AddRectFilled(min, max, U(GuideToastInk, 0.94f * alpha), 0f);
        var flashT = (now - a.ArrivedAt) / GuideFlashSec;
        if (a.Flash && flashT < 1) dl.AddRectFilled(min, max, U(a.FlashTone, 0.35f * (float)(1 - flashT) * alpha), 0f);
        dl.AddRect(min, max, U(GuideToastLine, alpha), 0f);
        if (loud > 0.01f && !leaving)
        {
            var pulse = at.CardPulse ? (float)(0.5 + 0.5 * Math.Sin(now * Math.PI * 1.6)) : 0.5f;
            dl.AddRect(min - new Vector2(2, 2), max + new Vector2(2, 2), U(ToneAccent, (0.12f + 0.16f * pulse) * loud * alpha), 0f, ImDrawFlags.None, 2.5f);
            dl.AddRect(min, max, U(ToneAccent, (0.6f + 0.4f * pulse) * loud * alpha), 0f, ImDrawFlags.None, 1.2f);
        }
        dl.AddRectFilled(min, new Vector2(min.X + stripe, max.Y), U(ToneAccent, alpha), 0f);

        // Title row: the question badge in the glyph box (the same badge as an asked highlight: "asking" has one shape on
        // the HUD), QUESTION tracked and faux bold in the tone, then the who-dots and the title run in the space left.
        // From the right: the x, the elapsed (or "answered" during the receipt), the again pill when it came back from Later.
        var hy = min.Y + padY + f * 0.5f;
        HlQuestionBadge(dl, font, small, new Vector2(x0 + AskStripeNow + GuideToastPadX + AskGlyphBox * 0.5f, hy), alpha);
        var x = textX;
        var tcol = U(ToneAccent, alpha);
        var tp = new Vector2(x, hy - f * 0.5f);
        GuideTrackedText(dl, font, f, tp, tcol, AskLabel, GuideToastTitleTrack);
        GuideTrackedText(dl, font, f, tp + new Vector2(0.6f, 0), tcol, AskLabel, GuideToastTitleTrack);
        x += GuideTrackedWidth(font, f, AskLabel, GuideToastTitleTrack) + 14f;

        var guard = !leaving && now - a.GuardFrom < AskClickGuardSec;
        var rx = max.X - GuideToastPadX;
        if (!leaving)
        {
            var bx = new Vector2(rx - 16, hy - 8);
            ImGui.SetCursorScreenPos(bx);
            ImGui.InvisibleButton("##ask_x", new Vector2(16, 16));
            var hov = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked() && !guard) { AttnDismissedByUser(null, ask.Id); AskStartLeave(a, now, utc, false, null); }
            if (hov) GuideTooltip(a.SkipTip!);
            if (hov) dl.AddRectFilled(bx, bx + new Vector2(16, 16), U(GuideToastLine), 0f);
            DrawCloseGlyph(dl, new Vector2(rx - 8, hy), 3.5f, U(hov ? GuideToastText : GuideToastMuted, hov ? 1f : 0.8f));
        }
        rx -= 16 + 8;
        {
            var t = receipt ? AskAnsweredText : GuideElapsed(utc - ask.CreatedAt);
            var tw = ImGui.CalcTextSize(t).X * k;
            dl.AddText(font, small, new Vector2(rx - tw, hy - small * 0.5f), U(GuideToastMuted, alpha), t);
            rx -= tw + 10;
        }
        if (ask.LaterCount > 0)
        {
            var tw = ImGui.CalcTextSize(AskAgainText).X * k;
            var pillW = tw + 12;
            var ph = small + 6;
            var pmin = new Vector2(rx - pillW, hy - ph * 0.5f);
            ImGui.SetCursorScreenPos(pmin);
            ImGui.InvisibleButton("##ask_again", new Vector2(pillW, ph));
            if (ImGui.IsItemHovered() && ask.LaterAt is { } la) GuideTooltip($"You said Later {GuideElapsed(utc - la)} ago.");
            dl.AddRectFilled(pmin, pmin + new Vector2(pillW, ph), U(GuideToastLine, 0.7f * alpha), 0f);
            dl.AddRect(pmin, pmin + new Vector2(pillW, ph), U(GuideToastLine, alpha), 0f);
            dl.AddText(font, small, new Vector2(pmin.X + 6, hy - small * 0.5f), U(GuideToastMuted, alpha), AskAgainText);
            rx -= pillW + 10;
        }
        if (_guideUi.ShowWho && rx - x > 60) DrawAskWhoTitle(dl, font, small, ask, x, rx, hy, alpha);

        // The question: f, near-white, faux bold, up to two lines. Read, not shouted: the pills carry the weight.
        var qy = min.Y + padY + f + GuideToastLineGap;
        var qcol = U(GuideToastText, 0.95f * alpha);
        for (var i = 0; i < qLines; i++)
        {
            var ly = qy + i * (f + 2f);
            dl.AddText(font, f, new Vector2(textX, ly), qcol, a.QWrap.Lines[i]);
            dl.AddText(font, f, new Vector2(textX + 0.6f, ly), qcol, a.QWrap.Lines[i]);
        }
        // Hover over the question: the whole ask in the toast-family panel under the card (title, question, detail, who
        // asked) whenever the card holds more than it shows - something clipped, or an asker worth naming.
        if (!leaving && (a.QWrap.Clipped || (dLines > 0 && a.DWrap.Clipped) || _guideUi.ShowWho))
        {
            ImGui.SetCursorScreenPos(new Vector2(textX, qy));
            ImGui.InvisibleButton("##ask_text", new Vector2(textMax, qH));
            if (ImGui.IsItemHovered()) GuideHover(ask.Title, ask.Question, ask.Detail, _guideUi.ShowWho ? a.AskFoot : null, ToneAccent);
        }

        // The choice row(s). During the click guard the pills are inert and at 60%, then ease to 100%. During the receipt
        // the chosen one fills and the others fade to 35%.
        var py = qy + qH + 8f;
        var firstRowY = py;
        var pillA = alpha * (guard ? 0.6f : 0.6f + 0.4f * HlEase((now - a.GuardFrom - AskClickGuardSec) / AskGuardEaseSec));
        var rowEnd0 = textX + textMax - laterW - AskPillGap;
        var rowEnd = textX + textMax;
        var bx0 = textX;
        var row = 0;
        for (var i = 0; i < ask.Options.Length && i < AskPillIds.Length; i++)
        {
            var o = ask.Options[i];
            var chosen = receipt && o.Id == a.ChosenId;
            var pw = AskPillWidth(a.Labels[i], f) + (chosen ? AskCheckW : 0f);
            if (bx0 > textX && bx0 + pw > (row == 0 ? rowEnd0 : rowEnd)) { bx0 = textX; py += pillH + AskPillRowGap; row++; }
            var state = receipt ? chosen ? 1 : 2 : 0;
            if (AskPill(dl, font, f, AskPillIds[i], new Vector2(bx0, py), pw, pillH, a.Labels[i], state, receipt ? alpha : pillA, o.Hint, !leaving && !guard))
            {
                AttnAnswerFromCard(ask.Id, o.Id);
                AskStartLeave(a, now, utc, true, o.Id);
            }
            bx0 += pw + AskPillGap;
        }
        // Later: a small clock and the word, muted; on hover both brighten and a pill outline appears. Pinned to the right
        // end of the first row, always in the same place and never next to the first choice (one row, two meanings: Later
        // is "not yet", the x above is "no").
        var lx = textX + textMax - laterW;
        ImGui.SetCursorScreenPos(new Vector2(lx, firstRowY));
        ImGui.InvisibleButton("##ask_later", new Vector2(laterW, pillH));
        var lhov = !leaving && !guard && ImGui.IsItemHovered();
        if (lhov) GuideTooltip(AskLaterTip);
        if (!leaving && !guard && ImGui.IsItemClicked()) { AttnAnswerFromCard(ask.Id, null); AskStartLeave(a, now, utc, false, null); }
        var laterA = receipt ? 0.35f * alpha : pillA;
        if (lhov)
        {
            dl.AddRectFilled(new Vector2(lx, firstRowY), new Vector2(lx + laterW, firstRowY + pillH), U(GuideToastLine, 0.7f * alpha), pillH * 0.5f);
            dl.AddRect(new Vector2(lx, firstRowY), new Vector2(lx + laterW, firstRowY + pillH), U(GuideToastLine, alpha), pillH * 0.5f);
        }
        var lcol = U(lhov ? GuideToastText : GuideToastMuted, laterA);
        DrawQueueGlyph(dl, new Vector2(lx + 14f, firstRowY + pillH * 0.5f), lcol);
        dl.AddText(font, small, new Vector2(lx + 26f, firstRowY + pillH * 0.5f - small * 0.5f), lcol, AskLaterText);
        a.PillsMin = new Vector2(textX, firstRowY);
        a.PillsMax = new Vector2(max.X, py + pillH);

        // Footer: the agent's detail, small and muted, up to two lines.
        var fy = py + pillH + 6f;
        var fcol = U(GuideToastMuted, 0.9f * alpha);
        for (var i = 0; i < dLines; i++) dl.AddText(font, small, new Vector2(textX, fy + i * (small + 2f)), fcol, a.DWrap.Lines[i]);

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, h + GuideEdge * 2));
        a.LastH = h + GuideEdge * 2;
    }

    /// <summary>The who-dots with their names (two for a shared ask), then " - " and the title, in dimmed tracked caps, clipped to the room.</summary>
    private void DrawAskWhoTitle(ImDrawListPtr dl, ImFontPtr font, float small, AskView ask, float x, float rx, float hy, float alpha)
    {
        var a = _askUi;
        var col = U(GuideToastMuted, alpha);
        var ty = hy - small * 0.5f;
        for (var i = 0; i < ask.Whos.Length && i < a.WhoCaps.Length; i++)
        {
            if (i > 0)
            {
                GuideTrackedText(dl, font, small, new Vector2(x, ty), col, ", ", GuideToastMsgTrack);
                x += GuideTrackedWidth(font, small, ", ", GuideToastMsgTrack) + GuideToastMsgTrack;
            }
            if (rx - x < 30) return;
            DrawWhoDot(dl, new Vector2(x + GuideDotR, hy), ask.Hues[i], alpha);
            x += WhoDotW;
            var t = GuideClipTracked(a.WhoCaps[i], font, small, GuideToastMsgTrack, rx - x);
            GuideTrackedText(dl, font, small, new Vector2(x, ty), col, t, GuideToastMsgTrack);
            if (!ReferenceEquals(t, a.WhoCaps[i])) return;   // clipped: nothing more fits
            x += GuideTrackedWidth(font, small, t, GuideToastMsgTrack) + GuideToastMsgTrack;
        }
        if (a.TitleCaps == null || rx - x < 30) return;
        var sep = " - ";
        GuideTrackedText(dl, font, small, new Vector2(x, ty), col, sep, GuideToastMsgTrack);
        x += GuideTrackedWidth(font, small, sep, GuideToastMsgTrack) + GuideToastMsgTrack;
        var title = GuideClipTracked(a.TitleCaps, font, small, GuideToastMsgTrack, rx - x);
        GuideTrackedText(dl, font, small, new Vector2(x, ty), col, title, GuideToastMsgTrack);
    }

    /// <summary>
    /// One choice pill: an InvisibleButton (the mouse only over itself) with the look drawn over it. state 0 = live (rest,
    /// or the accent tint on hover with the option's hint in the hover panel), 1 = chosen (filled accent, ink text faux
    /// bold, a check before the label), 2 = the others during the receipt (35%). Returns true on a click that counts.
    /// </summary>
    private bool AskPill(ImDrawListPtr dl, ImFontPtr font, float f, string id, Vector2 pos, float w, float h, string label, int state, float alpha, string? hint, bool live)
    {
        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton(id, new Vector2(w, h));
        var hov = live && ImGui.IsItemHovered();
        var click = live && ImGui.IsItemClicked();
        if (hov && hint != null) GuideTooltip(hint);
        var max = pos + new Vector2(w, h);
        var r = h * 0.5f;
        var cy = pos.Y + r;
        switch (state)
        {
            case 1:
            {
                dl.AddRectFilled(pos, max, U(ToneAccent, 0.85f * alpha), r);
                var ink = U(GuideInk, alpha);
                HlCheckGlyph(dl, new Vector2(pos.X + AskPillPadX + 5f, cy), 1f, ink);
                var tp = new Vector2(pos.X + AskPillPadX + AskCheckW, cy - f * 0.5f);
                dl.AddText(font, f, tp, ink, label);
                dl.AddText(font, f, tp + new Vector2(0.6f, 0), ink, label);
                break;
            }
            case 2:
                dl.AddRectFilled(pos, max, U(GuideToastLine, 0.7f * 0.35f * alpha), r);
                dl.AddRect(pos, max, U(GuideToastLine, 0.35f * alpha), r);
                dl.AddText(font, f, new Vector2(pos.X + AskPillPadX, cy - f * 0.5f), U(GuideToastText, 0.9f * 0.35f * alpha), label);
                break;
            default:
                if (hov)
                {
                    dl.AddRectFilled(pos, max, U(ToneAccent, 0.22f * alpha), r);
                    dl.AddRect(pos, max, U(ToneAccent, 0.9f * alpha), r, ImDrawFlags.None, 1.4f);
                    dl.AddText(font, f, new Vector2(pos.X + AskPillPadX, cy - f * 0.5f), U(GuideToastText, alpha), label);
                }
                else
                {
                    dl.AddRectFilled(pos, max, U(GuideToastLine, 0.7f * alpha), r);
                    dl.AddRect(pos, max, U(GuideToastLine, alpha), r);
                    dl.AddText(font, f, new Vector2(pos.X + AskPillPadX, cy - f * 0.5f), U(GuideToastText, 0.9f * alpha), label);
                }
                break;
        }
        return click;
    }

    private static float AskPillWidth(string label, float f) =>
        Math.Clamp(ImGui.CalcTextSize(label).X + AskPillPadX * 2, AskPillMinW, AskPillMaxW);

    /// <summary>How many pill rows the choices take: Later shortens the first row; the chosen pill of a receipt is wider by its check.</summary>
    private static int AskPillRows(string[] labels, float textMax, float laterW, string? chosenId, AttnOption[] options)
    {
        var f = ImGui.GetFontSize();
        var rows = 1;
        var x = 0f;
        for (var i = 0; i < labels.Length; i++)
        {
            var pw = AskPillWidth(labels[i], f) + (chosenId != null && i < options.Length && options[i].Id == chosenId ? AskCheckW : 0f);
            var end = rows == 1 ? textMax - laterW - AskPillGap : textMax;
            if (x > 0 && x + pw > end) { rows++; x = 0; }
            x += pw + AskPillGap;
        }
        return rows;
    }

    /// <summary>
    /// The card's per-card strings, rebuilt when the card (id + shown time, who count), the names (the AgentsView
    /// instance: a session renamed) or the width changes: display names in caps, the title in caps, the clipped pill
    /// labels, the x's hover and the question hover's "Asked by" foot.
    /// </summary>
    private static void AskCache(AskUiState a, AskView ask, AgentsView ag, float textMax)
    {
        if (a.CacheId == ask.Id && a.CacheShown == ask.ShownAt && a.CacheW == textMax && a.CacheWhos == ask.Whos.Length && ReferenceEquals(a.CacheAg, ag)) return;
        a.CacheId = ask.Id; a.CacheShown = ask.ShownAt; a.CacheW = textMax; a.CacheWhos = ask.Whos.Length; a.CacheAg = ag;
        a.WhoCaps = new string[ask.Whos.Length];
        var names = new string[ask.Whos.Length];
        for (var i = 0; i < ask.Whos.Length; i++) { names[i] = ag.NameOf(ask.Sessions[i], ask.Whos[i]); a.WhoCaps[i] = GuideCaps(names[i]); }
        a.TitleCaps = ask.Title.Length > 0 ? GuideCaps(ask.Title) : null;
        a.Labels = new string[ask.Options.Length];
        for (var i = 0; i < ask.Options.Length; i++) a.Labels[i] = GuideClipText(ask.Options[i].Label, AskPillMaxW - AskPillPadX * 2);
        a.SkipTip = $"Skip this question. {names[0]} is told and goes on without an answer.";
        a.AskFoot = "Asked by " + string.Join(", ", names);
    }

    // ── Shared bits ──────────────────────────────────────────────────

    /// <summary>The who-dot: a 6 px disc in the session's hue on a 1.5 px ink ring (the quiet dot's construction), before a label.</summary>
    private static void DrawWhoDot(ImDrawListPtr dl, Vector2 c, int hue, float alpha = 1f)
    {
        dl.AddCircleFilled(c, GuideDotR + 1.5f, U(GuideToastInk, 0.85f * alpha), 16);
        dl.AddCircleFilled(c, GuideDotR, U(AttnHueColor(hue), alpha), 16);
    }
}
