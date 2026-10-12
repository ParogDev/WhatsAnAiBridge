using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// The guide panel's sessions surfaces (state in Sessions.cs):
///   - the restart card (drawn from GuidePanel.DrawGuideBody): an agent waits to restart the HUD. Blocked, it lists what
///     it waits for (whose test, how long left; the attention queue's floor holder as "Claude: your answer to: ...") in
///     the warn tone under the floor card; free, it takes the top slot with a flash in the requester's hue and counts
///     down the hold in the accent tone - the countdown starts only then (Sessions.cs RestartEvaluateLocked), so the
///     player sees all of it. Restart now grants it over the blockers; Not now denies it. Calm like the queue card: a
///     restart is routine, the user only needs the choice. The "who is asking" line is the next strip's second form
///     (GuidePanelQueue.cs).
///   - the sessions chip (DrawSessionsChip): the always-there, quiet member of the family. A small ink pill in the
///     toast look beside the card's strip, at title-row height, card or no card (background draw list, no window; the
///     hover is a hit-test like the quiet dot's): the two-rings glyph and how many agents share the HUD, at half
///     strength. It says more only when something could disrupt play: an agent waiting to restart the HUD adds the
///     restart glyph (warn tone while blocked by someone's work, accent with the seconds while counting down), a granted
///     restart turns it with RESTARTING and a slow shallow breathe (never a throb: that is for cards asking the user to
///     act), and an MCP rollout in progress (supervised servers not swapped yet, which cuts nothing) is a 2 px accent
///     hairline along its bottom edge, filled by how many are on the deployed build. It ghosts with the card while a
///     highlight carries the words. Work items (Work.cs, f8-agent-ux.md 5.1) add a "!N" badge in the accent while N items
///     wait on the player (the hairline goes accent too), a 1.5 px accent ring around the rings glyph while an F8 press
///     runs, and a ToneBad hairline for 10 s after one failed. Hover: the agents board (GuidePanelBoard.cs) under the card's
///     stack; a click pins the board into the stack, a second click (or Esc, or its x) unpins it. The chip never names
///     who is next in line: that is the next strip's (the two never say the same thing).
/// Agents are named by their readable name (AgentsView.NameOf: never a system folder, no worktree hash tail, unique).
/// Same rules as the rest of the panel: ASCII only, glyphs on the draw list, colours from PanelTheme plus the fixed tones.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int RestartBlockersShown = 3;
    // Chip: its height (the toast icon box), its gap from the card's strip, the padding inside, how fast it eases in and
    // out and on hover, and the calm / waiting / granted strengths (the breathe period is the quiet dot's).
    private const float ChipH = GuideToastIcon, ChipGap = 2f, ChipPadX = 7f;
    private const double ChipFadeSec = 0.25, ChipHoverSec = 0.08;
    private const float ChipAlphaCalm = 0.5f, ChipAlphaWaiting = 0.85f, ChipAlphaGranted = 0.95f;
    private const string ChipRestartingText = "RESTARTING";

    /// <param name="floorUp">A floor card (or its leaving receipt, or the hand-over gap) is drawn above this card.</param>
    private void DrawRestartCard(RestartRequest rr, bool floorUp, AgentsView ag, PanelTheme th, double now, DateTime utc)
    {
        var dl = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var a = _askUi;
        // During a hand-over the answered ask has left the bridge's blockers but is still leaving the slot (its receipt,
        // its fade, the gap): the card keeps its waiting form and names it, and flips to the countdown with the reflow.
        var handover = floorUp && rr.Blockers.Count == 0 && a.LeaveWho != null && (a.LeaveAt > -1e8 || now < a.GapUntil);
        var blocked = rr.Blockers.Count > 0 || handover;
        var tone = blocked ? ToneWarn : ToneAccent;
        // The top slot: nothing above it and nothing blocking. Taking it flashes the card in the requester's hue and guards
        // its buttons for the click guard, like any card changing owner.
        var onTop = !floorUp && !blocked;
        if (onTop) { if (a.RestartTop != rr.Id) { a.RestartTop = rr.Id; a.RestartTopAt = now; } }
        else a.RestartTop = null;
        var guard = onTop && now - a.RestartTopAt < AskClickGuardSec;
        var p = ImGui.GetCursorScreenPos();
        var x0 = p.X + GuideEdge;
        var w = GuideWidth - GuideEdge * 2;
        const float pad = 12f;
        var innerW = w - pad * 2;
        var headH = f + 12;
        var btnH = f + 10;
        const float bulletIndent = 14f;

        // Text first, then measure: the background needs the full height.
        var bullets = handover ? 1 : rr.Blockers.Count;
        var shown = Math.Min(bullets, RestartBlockersShown);
        var rt = RestartTextsFor(rr, handover, blocked, bullets, shown, ag, utc, innerW, innerW - bulletIndent);
        var who = rt.Who;
        var bodyH = f + 4                                   // lead line
                    + (blocked ? shown * (small + 3) + (bullets > shown ? small + 3 : 0) : 0)
                    + small + 8                             // meta
                    + btnH;
        var hgt = headH + bodyH + 10;
        var min = new Vector2(x0, p.Y + GuideEdge);
        var max = new Vector2(x0 + w, p.Y + GuideEdge + hgt);

        dl.AddRectFilled(min, max, U(th.Card, 0.94f), 6f);
        var flashT = (now - _guideUi.RestartArrivedAt) / GuideFlashSec;
        if (flashT < 1) dl.AddRectFilled(min, max, U(tone, 0.28f * (float)(1 - flashT)), 6f);
        var topT = a.RestartTop == rr.Id ? (now - a.RestartTopAt) / GuideFlashSec : 1;
        if (topT < 1) dl.AddRectFilled(min, max, U(AttnHueColor(rr.Hue), 0.35f * (float)(1 - topT)), 6f);
        dl.AddRect(min, max, U(tone, 0.55f), 6f, ImDrawFlags.None, 1f);

        // Header: restart glyph + label in the tone; right: who asked, with its who-dot, dimmed.
        var hy = min.Y + headH * 0.5f;
        var x = min.X + pad;
        DrawRestartGlyph(dl, new Vector2(x + 6, hy), U(tone), blocked ? 0 : now);
        x += 18;
        var label = blocked ? "HUD RESTART WAITS" : "HUD RESTART";
        dl.AddText(font, small, new Vector2(x, hy - small * 0.5f), U(tone), label);
        x += ImGui.CalcTextSize(label).X * (small / f) + 10;
        var rx = max.X - pad;
        var whoW = ImGui.CalcTextSize(who).X * (small / f) + WhoDotW;
        if (rx - whoW > x)
        {
            DrawWhoDot(dl, new Vector2(rx - whoW + GuideDotR, hy), rr.Hue);
            dl.AddText(font, small, new Vector2(rx - whoW + WhoDotW, hy - small * 0.5f), U(th.TextDim), who);
            rx -= whoW + 10;
        }

        // Body: the lead line, then the blockers as bullets (whose, what, how long left).
        var y = min.Y + headH + 2;
        var bx = min.X + pad;
        dl.AddText(font, f, new Vector2(bx, y), U(blocked ? th.Text : tone), rt.Lead);
        y += f + 4;
        if (blocked)
        {
            foreach (var line in rt.Lines)
            {
                dl.AddCircleFilled(new Vector2(bx + 4, y + small * 0.5f), 2f, U(th.TextDim), 8);
                dl.AddText(font, small, new Vector2(bx + bulletIndent, y), U(th.TextDim), line);
                y += small + 3;
            }
            if (rt.More != null)
            {
                dl.AddText(font, small, new Vector2(bx + bulletIndent, y), U(th.TextDim, 0.8f), rt.More);
                y += small + 3;
            }
        }
        dl.AddText(font, small, new Vector2(bx, y), U(th.TextDim, 0.85f), rt.Meta);
        y += small + 8;

        // Buttons: Restart now (primary) and Not now. The tooltip says what Restart now would interrupt. Both ignore clicks
        // for the click guard right after the card took the top slot.
        QueuePillButton(dl, "##restart_now", new Vector2(bx, y), btnH, "Restart now", true, th, out var nowClick, rt.Interrupt);
        if (nowClick && !guard) RestartAllow(rr.Id);
        var notW = ImGui.CalcTextSize("Not now").X + 24;
        QueuePillButton(dl, "##restart_deny", new Vector2(max.X - pad - notW, y), btnH, "Not now", false, th, out var denyClick,
            "Decline this restart. The agent is told to ask you in chat or try later.");
        if (denyClick && !guard) RestartDeny(rr.Id);

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, hgt + GuideEdge * 2));
    }

    /// <summary>The restart card's texts, already clipped to its width (RestartTextsFor).</summary>
    private sealed class RestartTexts
    {
        public RestartRequest? For; public long Second = -1; public int ToGo = -1; public bool Handover; public AgentsView? Ag; public float W;
        public string? LeaveWho, LeaveSession;
        public string Who = "", Lead = "", Meta = "", Interrupt = "";
        public string[] Lines = [];
        public string? More;
    }

    private readonly RestartTexts _restartTexts = new();

    /// <summary>
    /// The restart card's lead, bullets, meta line and Restart now tooltip, composed and clipped only when something they
    /// show changes: the request copy (Sessions.cs republishes it at most 4 Hz), the second (elapsed and time-left
    /// counts), the countdown, the names (the AgentsView instance), the hand-over or the width. Not per frame: the card can
    /// stay up for minutes while a test blocks the restart.
    /// </summary>
    private RestartTexts RestartTextsFor(RestartRequest rr, bool handover, bool blocked, int bullets, int shown, AgentsView ag, DateTime utc, float w, float bulletW)
    {
        var c = _restartTexts;
        var a = _askUi;
        var second = utc.Ticks / TimeSpan.TicksPerSecond;
        var toGo = rr.HoldUntil is DateTime h ? Math.Max(0, (int)Math.Ceiling((h - utc).TotalSeconds)) : 0;
        if (ReferenceEquals(c.For, rr) && c.Second == second && c.ToGo == toGo && c.Handover == handover && ReferenceEquals(c.Ag, ag) && c.W == w
            && (!handover || (c.LeaveWho == a.LeaveWho && c.LeaveSession == a.LeaveSession))) return c;
        c.For = rr; c.Second = second; c.ToGo = toGo; c.Handover = handover; c.Ag = ag; c.W = w; c.LeaveWho = a.LeaveWho; c.LeaveSession = a.LeaveSession;
        c.Who = ag.NameOf(rr.Session, rr.Who);
        var leaveWho = handover ? ag.NameOf(a.LeaveSession, a.LeaveWho) : null;
        c.Lead = GuideClipText(blocked
            ? (bullets == 1 ? "Waits for:" : $"Waits for {bullets} things:")
            : toGo > 0 ? $"Restarts in {toGo} s - nothing running would be lost." : "Restarting now.", w);
        c.Meta = GuideClipText($"{c.Who} asked {GuideElapsed(utc - rr.At)} ago" + (rr.Reason != null ? $" - {rr.Reason}" : "")
                               + (rr.MergedCount > 0 ? $" - +{rr.MergedCount} more agent{(rr.MergedCount == 1 ? " waits" : "s wait")} for it" : ""), w);
        if (handover) { c.Lines = [GuideClipText($"{leaveWho}: your answer", bulletW)]; c.More = null; }
        else if (blocked)
        {
            var lines = new string[shown];
            for (var i = 0; i < shown; i++)
            {
                var b = rr.Blockers[i];
                var line = (b.Who != null ? $"{ag.NameOf(b.Session, b.Who)}: " : "") + b.Label;
                if (b.Until is DateTime u && u > utc) line += $" ({GuideElapsed(u - utc)} left)";
                if (b.Yours) line += " (its own)";
                lines[i] = GuideClipText(line, bulletW);
            }
            c.Lines = lines;
            c.More = rr.Blockers.Count > shown ? $"+{rr.Blockers.Count - shown} more" : null;
        }
        else { c.Lines = []; c.More = null; }
        c.Interrupt = rr.Blockers.Count > 0
            ? "Restart now anyway. It interrupts " + string.Join(", ", rr.Blockers.Take(3).Select(b => b.Who != null ? $"{ag.NameOf(b.Session, b.Who)}'s {b.Label}" : b.Label)) + "; they are told why in the log."
            : handover ? $"Restart now anyway. It interrupts {leaveWho}'s hand-over; they are told why in the log."
            : "Don't wait the countdown.";
        return c;
    }

    /// <summary>12 px restart glyph: a three-quarter arc with an arrow head; it turns slowly while counting down (t = 0: still).</summary>
    private static void DrawRestartGlyph(ImDrawListPtr dl, Vector2 c, uint col, double t)
    {
        const float r = 5.5f;
        var a0 = (float)(t * 1.2 % (Math.PI * 2)) - MathF.PI * 0.75f;
        dl.PathClear();
        dl.PathArcTo(c, r, a0 + MathF.PI * 0.35f, a0 + MathF.PI * 1.85f, 14);
        dl.PathStroke(col, ImDrawFlags.None, 1.4f);
        var tip = c + new Vector2(MathF.Cos(a0 + MathF.PI * 0.35f), MathF.Sin(a0 + MathF.PI * 0.35f)) * r;
        var dir = new Vector2(-MathF.Sin(a0 + MathF.PI * 0.35f), MathF.Cos(a0 + MathF.PI * 0.35f));   // tangent
        var n = new Vector2(-dir.Y, dir.X);
        dl.AddTriangleFilled(tip - dir * 3.2f, tip + dir * 1.2f + n * 2.6f, tip + dir * 1.2f - n * 2.6f, col);
    }

    /// <summary>12 px "several" glyph: two overlapping rings (the next strip's "WHO IS ASKING" form, GuidePanelQueue.cs).</summary>
    private static void DrawSessionsGlyph(ImDrawListPtr dl, Vector2 c, uint col)
    {
        dl.AddCircle(new Vector2(c.X - 2.5f, c.Y), 4.2f, col, 14, 1.3f);
        dl.AddCircle(new Vector2(c.X + 2.5f, c.Y), 4.2f, col, 14, 1.3f);
    }

    // ── Sessions chip ────────────────────────────────────────────────

    /// <summary>
    /// The chip (see the class summary). Drawn every frame the panel runs, from DrawGuidePanel after the dot; it allocates
    /// nothing per frame: the count and countdown strings are rebuilt only when their number changes, the hover detail
    /// once a second while hovered.
    /// </summary>
    private void DrawSessionsChip(SessionsView rs, in GuideAttention at, double now, DateTime utc)
    {
        var u = _guideUi;
        var io = ImGui.GetIO();
        var waiting = rs.Waiting;
        var granted = rs.Granted;
        var rollout = rs.Rollout is { InProgress: true } ? rs.Rollout : null;
        var work = WorkSnapshot();   // the F8 presses and tasks (Work.cs): the "!N" badge, the ring, the failed hairline, the board
        var showCount = rs.Agents > 0 || work.Active > 0;
        var wanted = showCount || waiting != null || granted != null || rollout != null;
        var badge = work.NeedsYouCount;
        var failed = work.RecentFailedAt is { } fa && (utc - fa).TotalSeconds < BoardFailedHairlineSec;

        // Hover: the mouse against the rect drawn last frame (a hit-test; no window, no focus taken). A click pins the
        // board into the stack, a second one unpins it (f8-agent-ux.md 5.1).
        var hovered = u.ChipAlpha > 0.05f && io.MousePos.X >= u.ChipMin.X && io.MousePos.X < u.ChipMax.X
                      && io.MousePos.Y >= u.ChipMin.Y && io.MousePos.Y < u.ChipMax.Y;
        u.ChipHover += ((hovered ? 1f : 0f) - u.ChipHover) * (1f - MathF.Exp(-io.DeltaTime / (float)ChipHoverSec));
        var down = io.MouseDown[0];
        if (hovered && down && !_workUi.ChipWasDown) _workUi.Pinned = !_workUi.Pinned;
        _workUi.ChipWasDown = down;

        // Strength: calm at half, more when a restart is queued or something needs the player, nearly full when one is
        // granted (with a slow shallow breathe); ghosted with the card while a highlight carries the words (attention),
        // full on hover.
        var blocked = waiting is { Blockers.Count: > 0 };
        var tone = granted != null ? ToneAccent : waiting == null ? ToneNeutral : blocked ? ToneWarn : ToneAccent;
        var breathe = granted != null ? 0.5f + 0.5f * MathF.Sin((float)(now * Math.PI * 2 / GuideDotBreatheSec)) : 0f;
        var baseA = granted != null ? ChipAlphaGranted - 0.12f * breathe : waiting != null || badge > 0 ? ChipAlphaWaiting : ChipAlphaCalm;
        var target = wanted ? baseA * at.CardAlpha : 0f;
        target = target + (1f - target) * u.ChipHover;
        u.ChipAlpha += (target - u.ChipAlpha) * (1f - MathF.Exp(-io.DeltaTime / (float)(ChipFadeSec / 3)));
        if (u.ChipAlpha < 0.02f) { u.ChipMin = u.ChipMax = default; return; }
        var a = u.ChipAlpha;

        var dl = ImGui.GetBackgroundDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var tiny = f * 0.75f;

        // Texts, rebuilt only when their number changes.
        if (u.ChipCount != rs.Agents) { u.ChipCount = rs.Agents; u.ChipCountText = rs.Agents.ToString(); }
        if (u.ChipBadge != badge) { u.ChipBadge = badge; u.ChipBadgeText = badge > 0 ? "!" + badge : ""; }
        var secs = waiting?.HoldUntil is DateTime h && !blocked ? Math.Max(0, (int)Math.Ceiling((h - utc).TotalSeconds)) : -1;
        if (u.ChipSecs != secs) { u.ChipSecs = secs; u.ChipSecsText = secs < 0 ? "" : secs + "s"; }

        // Measure: [rings count !N] [restart-glyph seconds|RESTARTING], 12 px glyphs, the texts at the small size.
        var countW = showCount ? 13f + 5f + ImGui.CalcTextSize(u.ChipCountText).X * (small / f) : 0f;
        var badgeW = badge > 0 ? 5f + ImGui.CalcTextSize(u.ChipBadgeText).X * (tiny / f) : 0f;
        var stateText = granted != null ? ChipRestartingText : secs >= 0 ? u.ChipSecsText : "";
        var stateTextW = stateText.Length > 0 ? (granted != null ? GuideTrackedWidth(font, small, stateText, GuideToastMsgTrack) : ImGui.CalcTextSize(stateText).X * (small / f)) : 0f;
        var stateW = waiting != null || granted != null ? 12f + (stateTextW > 0 ? 4f + stateTextW : 0f) : 0f;
        var w = ChipPadX * 2 + countW + badgeW + (countW + badgeW > 0 && stateW > 0 ? 8f : 0f) + stateW;
        if (w < ChipH) w = ChipH;

        // Place: right of the card's strip at title-row height (the quiet dot's row), following the card when it slides
        // clear of a highlight; left of it when the right side would leave the screen.
        var cy = GuideDotCenter(u).Y;
        var left = u.Home.X + u.Shift.X + GuideWidth + ChipGap;
        if (left + w > io.DisplaySize.X - GuideEdge) left = u.Home.X + u.Shift.X - ChipGap - w;
        var min = new Vector2(MathF.Round(left), MathF.Round(cy - ChipH * 0.5f));
        var max = min + new Vector2(MathF.Round(w), ChipH);
        u.ChipMin = min; u.ChipMax = max;

        // The pill: the toast ink, its hairline (the tone's at half strength while a restart is on, accent while something
        // needs the player, ToneBad for 10 s after a failed press), square corners.
        dl.AddRectFilled(min, max, U(GuideToastInk, 0.94f * a), 0f);
        dl.AddRect(min, max, failed ? U(ToneBad, 0.8f * a) : waiting != null || granted != null ? U(tone, 0.55f * a) : badge > 0 ? U(ToneAccent, 0.55f * a) : U(GuideToastLine, a), 0f);
        if (_workUi.Pinned) dl.AddRectFilled(new Vector2(min.X + 1f, min.Y + 1f), new Vector2(max.X - 1f, max.Y - 1f), U(ToneAccent, 0.08f * a), 0f);
        var x = min.X + ChipPadX;
        if (showCount)
        {
            // The rings glyph, with a 1.5 px accent ring around it while an F8 press runs (Pressed .. Verifying).
            if (work.AnyF8Running) dl.AddCircle(new Vector2(x + 6.5f, cy), 8.5f, U(ToneAccent, 0.85f * a), 20, 1.5f);
            DrawSessionsGlyph(dl, new Vector2(x + 6.5f, cy), U(ToneNeutral, a));
            x += 13f + 5f;
            dl.AddText(font, small, new Vector2(x, cy - small * 0.5f), U(GuideToastText, 0.9f * a), u.ChipCountText);
            x += countW - 18f;
            if (badge > 0)
            {
                // "!N": N items wait on the player (accent, 0.75 f).
                x += 5f;
                dl.AddText(font, tiny, new Vector2(x, cy - tiny * 0.5f), U(ToneAccent, a), u.ChipBadgeText);
                dl.AddText(font, tiny, new Vector2(x + 0.6f, cy - tiny * 0.5f), U(ToneAccent, a), u.ChipBadgeText);
                x += badgeW - 5f;
            }
            if (stateW > 0) x += 8f;
        }
        if (stateW > 0)
        {
            // Still while blocked (nothing moves until the blockers finish), turning while counting down or granted.
            DrawRestartGlyph(dl, new Vector2(x + 6f, cy), U(tone, a), blocked ? 0 : now);
            x += 12f;
            if (stateTextW > 0)
            {
                x += 4f;
                if (granted != null) GuideTrackedText(dl, font, small, new Vector2(x, cy - small * 0.5f), U(tone, a), stateText, GuideToastMsgTrack);
                else dl.AddText(font, small, new Vector2(x, cy - small * 0.5f), U(tone, a), stateText);
            }
        }
        // Rollout: a hairline along the bottom edge, inside the frame, filled by how many of the supervised servers are on
        // the deployed build. Informational: the accent, nothing moves.
        if (rollout != null)
        {
            var span = rollout.OnDeployed + rollout.Behind.Length;
            var frac = span > 0 ? (float)rollout.OnDeployed / span : 0f;
            var y0 = max.Y - 3f;
            var x0 = min.X + 1f;
            var x1 = max.X - 1f;
            dl.AddRectFilled(new Vector2(x0, y0), new Vector2(x1, y0 + 2f), U(ToneAccent, 0.22f * a), 0f);
            if (frac > 0) dl.AddRectFilled(new Vector2(x0, y0), new Vector2(x0 + (x1 - x0) * frac, y0 + 2f), U(ToneAccent, 0.9f * a), 0f);
        }

        // Hover: the agents board (GuidePanelBoard.cs) under the card's stack; while it is pinned there already, a one-line hint.
        if (hovered)
        {
            if (_workUi.Pinned) GuideTooltip(ChipPinnedTip);
            else DrawBoardHover(work, rs, AgentsSnapshot(), now, utc);
        }
    }

    private const string ChipPinnedTip = "The agents board is pinned under the card. Click to unpin it (Esc does too).";

    /// <summary>The first seven characters of a git sha (null stays null).</summary>
    private static string? ShortSha(string? sha) => sha == null ? null : sha.Length > 7 ? sha[..7] : sha;
}
