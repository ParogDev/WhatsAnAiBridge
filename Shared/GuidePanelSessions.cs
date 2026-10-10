using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// The guide panel's "who is asking what" surfaces (state in Sessions.cs; drawn from GuidePanel.DrawGuideBody):
///   - the restart card: an agent waits to restart the HUD. Blocked, it lists what it waits for (whose test, how long
///     left) in the warn tone; free, it counts down the hold in the accent tone. Restart now grants it over the
///     blockers; Not now denies it. Calm like the queue card: a restart is routine, the user only needs the choice.
///   - the "who is asking" strip: one line naming the agents that ask the user for something right now, shown only
///     while two or more do (and the panel is up anyway), so two requests never read as one voice. Hover for what each
///     asks. Idle and disconnected sessions, branches and folders never appear: they are nothing the player can act on.
///   - the sessions chip (DrawSessionsChip): the always-there, quiet member of the family. A small ink pill in the
///     toast look beside the card's strip, at title-row height, card or no card (background draw list, no window; the
///     hover is a hit-test like the quiet dot's): the two-rings glyph and how many agents share the HUD, at half
///     strength. It says more only when something could disrupt play: an agent waiting to restart the HUD adds the
///     restart glyph (warn tone while blocked by someone's work, accent with the seconds while counting down), a granted
///     restart turns it with RESTARTING and a slow shallow breathe (never a throb: that is for cards asking the user to
///     act), and an MCP rollout in progress (supervised servers not swapped yet, which cuts nothing) is a 2 px accent
///     hairline along its bottom edge, filled by how many are on the deployed build. It ghosts with the card while a
///     highlight carries the words. Hover: the panel's hover sheet (GuideHover) with who does what, each MCP's build
///     and mode, the restart and the rollout, its texts rebuilt once a second.
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

    private void DrawRestartCard(RestartRequest rr, AgentsView ag, PanelTheme th, double now, DateTime utc)
    {
        var dl = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var blocked = rr.Blockers.Count > 0;
        var tone = blocked ? ToneWarn : ToneAccent;
        var p = ImGui.GetCursorScreenPos();
        var x0 = p.X + GuideEdge;
        var w = GuideWidth - GuideEdge * 2;
        const float pad = 12f;
        var innerW = w - pad * 2;
        var headH = f + 12;
        var btnH = f + 10;
        const float bulletIndent = 14f;

        // Text first, then measure: the background needs the full height.
        var shown = Math.Min(rr.Blockers.Count, RestartBlockersShown);
        var secondsToGo = rr.HoldUntil is DateTime h ? Math.Max(0, (int)Math.Ceiling((h - utc).TotalSeconds)) : 0;
        var lead = blocked
            ? (rr.Blockers.Count == 1 ? "Waits for:" : $"Waits for {rr.Blockers.Count} things:")
            : secondsToGo > 0 ? $"Restarts in {secondsToGo} s - nothing running would be lost." : "Restarting now.";
        var who = ag.NameOf(rr.Who);
        var meta = $"{who} asked {GuideElapsed(utc - rr.At)} ago" + (rr.Reason != null ? $" - {rr.Reason}" : "");
        var bodyH = f + 4                                   // lead line
                    + (blocked ? shown * (small + 3) + (rr.Blockers.Count > shown ? small + 3 : 0) : 0)
                    + small + 8                             // meta
                    + btnH;
        var hgt = headH + bodyH + 10;
        var min = new Vector2(x0, p.Y + GuideEdge);
        var max = new Vector2(x0 + w, p.Y + GuideEdge + hgt);

        dl.AddRectFilled(min, max, U(th.Card, 0.94f), 6f);
        var flashT = (now - _guideUi.RestartArrivedAt) / GuideFlashSec;
        if (flashT < 1) dl.AddRectFilled(min, max, U(tone, 0.28f * (float)(1 - flashT)), 6f);
        dl.AddRect(min, max, U(tone, 0.55f), 6f, ImDrawFlags.None, 1f);

        // Header: restart glyph + label in the tone; right: who asked, dimmed.
        var hy = min.Y + headH * 0.5f;
        var x = min.X + pad;
        DrawRestartGlyph(dl, new Vector2(x + 6, hy), U(tone), blocked ? 0 : now);
        x += 18;
        var label = blocked ? "HUD RESTART WAITS" : "HUD RESTART";
        dl.AddText(font, small, new Vector2(x, hy - small * 0.5f), U(tone), label);
        x += ImGui.CalcTextSize(label).X * (small / f) + 10;
        var rx = max.X - pad;
        var whoW = ImGui.CalcTextSize(who).X * (small / f);
        if (rx - whoW > x) { dl.AddText(font, small, new Vector2(rx - whoW, hy - small * 0.5f), U(th.TextDim), who); rx -= whoW + 10; }

        // Body: the lead line, then the blockers as bullets (whose, what, how long left).
        var y = min.Y + headH + 2;
        var bx = min.X + pad;
        dl.AddText(font, f, new Vector2(bx, y), U(blocked ? th.Text : tone), GuideClipText(lead, innerW));
        y += f + 4;
        if (blocked)
        {
            for (var i = 0; i < shown; i++)
            {
                var b = rr.Blockers[i];
                var line = (b.Who != null ? $"{ag.NameOf(b.Who)}: " : "") + b.Label;
                if (b.Until is DateTime u && u > utc) line += $" ({GuideElapsed(u - utc)} left)";
                if (b.Yours) line += " (its own)";
                dl.AddCircleFilled(new Vector2(bx + 4, y + small * 0.5f), 2f, U(th.TextDim), 8);
                dl.AddText(font, small, new Vector2(bx + bulletIndent, y), U(th.TextDim), GuideClipText(line, innerW - bulletIndent));
                y += small + 3;
            }
            if (rr.Blockers.Count > shown)
            {
                dl.AddText(font, small, new Vector2(bx + bulletIndent, y), U(th.TextDim, 0.8f), $"+{rr.Blockers.Count - shown} more");
                y += small + 3;
            }
        }
        dl.AddText(font, small, new Vector2(bx, y), U(th.TextDim, 0.85f), GuideClipText(meta, innerW));
        y += small + 8;

        // Buttons: Restart now (primary) and Not now. The tooltip says what Restart now would interrupt.
        var interrupt = blocked
            ? "Restart now anyway. It interrupts " + string.Join(", ", rr.Blockers.Take(3).Select(b => b.Who != null ? $"{ag.NameOf(b.Who)}'s {b.Label}" : b.Label)) + "; they are told why in the log."
            : "Don't wait the countdown.";
        QueuePillButton(dl, "##restart_now", new Vector2(bx, y), btnH, "Restart now", true, th, out var nowClick, interrupt);
        if (nowClick) RestartAllow(rr.Id);
        var notW = ImGui.CalcTextSize("Not now").X + 24;
        QueuePillButton(dl, "##restart_deny", new Vector2(max.X - pad - notW, y), btnH, "Not now", false, th, out var denyClick,
            "Decline this restart. The agent is told to ask you in chat or try later.");
        if (denyClick) RestartDeny(rr.Id);

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, hgt + GuideEdge * 2));
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

    /// <summary>
    /// "WHO IS ASKING  Claude, Tester": the agents that ask the user for something right now, by readable name, askers
    /// first (AgentsView's order). Drawn only when two or more ask (DrawGuideBody). Hover: one line per agent, "Name:
    /// what it asks" in plain words, with what else it does after a dash. Nothing about idle or gone sessions, branches
    /// or folders: the player can act on none of it.
    /// </summary>
    private void DrawSessionsStrip(AgentsView ag, PanelTheme th)
    {
        var dl = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var p = ImGui.GetCursorScreenPos();
        var x0 = p.X + GuideEdge;
        var w = GuideWidth - GuideEdge * 2;
        const float pad = 12f;
        var h = f + 10;
        var min = new Vector2(x0, p.Y);
        var max = new Vector2(x0 + w, p.Y + h);
        dl.AddRectFilled(min, max, U(th.Card, 0.92f), 5f);
        dl.AddRect(min, max, U(ToneNeutral, 0.4f), 5f);

        // The line and the hover text are recomposed only when the agents view changes (Sessions.cs rebuilds it at 4 Hz,
        // and only while something is going on): no per-frame string building.
        var u = _guideUi;
        if (!ReferenceEquals(u.SessionsAgents, ag))
        {
            u.SessionsAgents = ag;
            var sb = new System.Text.StringBuilder();
            var tip = new System.Text.StringBuilder();
            foreach (var x1 in ag.Active)
            {
                if (x1.Asks.Length == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(x1.Name);
                for (var i = 0; i < x1.Asks.Length; i++)
                {
                    if (tip.Length > 0) tip.Append('\n');
                    tip.Append(x1.Name).Append(": ").Append(AskWords(x1.Asks[i]));
                    if (i == 0 && x1.Doing.Length > 0) tip.Append(" - also ").Append(string.Join(", ", x1.Doing));
                }
            }
            u.SessionsLine = sb.ToString();
            u.SessionsTip = tip.ToString();
        }

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##sessions_strip", new Vector2(w, h));
        // The hover, in the toast family under the card's stack: one line per ask, "Name: what it asks".
        if (ImGui.IsItemHovered() && u.SessionsTip != null) GuideHover("Who is asking you for something", u.SessionsTip, null, null, ToneNeutral);

        var hy = min.Y + h * 0.5f;
        var x = min.X + pad;
        DrawSessionsGlyph(dl, new Vector2(x + 6, hy), U(ToneNeutral));
        x += 18;
        const string label = "WHO IS ASKING";
        dl.AddText(font, small, new Vector2(x, hy - small * 0.5f), U(ToneNeutral), label);
        x += ImGui.CalcTextSize(label).X * (small / f) + 10;
        var rx = max.X - pad;
        if (rx - x > 40 && u.SessionsLine != null) dl.AddText(new Vector2(x, hy - f * 0.5f), U(th.TextDim), GuideClipText(u.SessionsLine, rx - x));

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, h + GuideEdge));
    }

    /// <summary>
    /// A pilot blocker's label (Sessions.cs BlockersLocked) as the strip's hover says it: the step itself for "waiting
    /// for the user: X" (the strip already says it is asking), "asks: X" for a question, the rest as written.
    /// </summary>
    private static string AskWords(string ask)
    {
        const string waiting = "waiting for the user: ", question = "question for the user: ";
        if (ask.StartsWith(waiting, StringComparison.Ordinal)) return ask[waiting.Length..];
        if (ask.StartsWith(question, StringComparison.Ordinal)) return "asks: " + ask[question.Length..];
        return ask;
    }

    /// <summary>12 px "several" glyph: two overlapping rings.</summary>
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
        var showCount = rs.Connected > 0;
        var wanted = showCount || waiting != null || granted != null || rollout != null;

        // Hover: the mouse against the rect drawn last frame (a hit-test; no window, no focus taken).
        var hovered = u.ChipAlpha > 0.05f && io.MousePos.X >= u.ChipMin.X && io.MousePos.X < u.ChipMax.X
                      && io.MousePos.Y >= u.ChipMin.Y && io.MousePos.Y < u.ChipMax.Y;
        u.ChipHover += ((hovered ? 1f : 0f) - u.ChipHover) * (1f - MathF.Exp(-io.DeltaTime / (float)ChipHoverSec));

        // Strength: calm at half, more when a restart is queued, nearly full when one is granted (with a slow shallow
        // breathe); ghosted with the card while a highlight carries the words (attention), full on hover.
        var blocked = waiting is { Blockers.Count: > 0 };
        var tone = granted != null ? ToneAccent : waiting == null ? ToneNeutral : blocked ? ToneWarn : ToneAccent;
        var breathe = granted != null ? 0.5f + 0.5f * MathF.Sin((float)(now * Math.PI * 2 / GuideDotBreatheSec)) : 0f;
        var baseA = granted != null ? ChipAlphaGranted - 0.12f * breathe : waiting != null ? ChipAlphaWaiting : ChipAlphaCalm;
        var target = wanted ? baseA * at.CardAlpha : 0f;
        target = target + (1f - target) * u.ChipHover;
        u.ChipAlpha += (target - u.ChipAlpha) * (1f - MathF.Exp(-io.DeltaTime / (float)(ChipFadeSec / 3)));
        if (u.ChipAlpha < 0.02f) { u.ChipMin = u.ChipMax = default; return; }
        var a = u.ChipAlpha;

        var dl = ImGui.GetBackgroundDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;

        // Texts, rebuilt only when their number changes.
        if (u.ChipCount != rs.Connected) { u.ChipCount = rs.Connected; u.ChipCountText = rs.Connected.ToString(); }
        var secs = waiting?.HoldUntil is DateTime h && !blocked ? Math.Max(0, (int)Math.Ceiling((h - utc).TotalSeconds)) : -1;
        if (u.ChipSecs != secs) { u.ChipSecs = secs; u.ChipSecsText = secs < 0 ? "" : secs + "s"; }

        // Measure: [rings count] [restart-glyph seconds|RESTARTING], 12 px glyphs, the texts at the small size.
        var countW = showCount ? 13f + 5f + ImGui.CalcTextSize(u.ChipCountText).X * (small / f) : 0f;
        var stateText = granted != null ? ChipRestartingText : secs >= 0 ? u.ChipSecsText : "";
        var stateTextW = stateText.Length > 0 ? (granted != null ? GuideTrackedWidth(font, small, stateText, GuideToastMsgTrack) : ImGui.CalcTextSize(stateText).X * (small / f)) : 0f;
        var stateW = waiting != null || granted != null ? 12f + (stateTextW > 0 ? 4f + stateTextW : 0f) : 0f;
        var w = ChipPadX * 2 + countW + (countW > 0 && stateW > 0 ? 8f : 0f) + stateW;
        if (w < ChipH) w = ChipH;

        // Place: right of the card's strip at title-row height (the quiet dot's row), following the card when it slides
        // clear of a highlight; left of it when the right side would leave the screen.
        var cy = GuideDotCenter(u).Y;
        var left = u.Home.X + u.Shift.X + GuideWidth + ChipGap;
        if (left + w > io.DisplaySize.X - GuideEdge) left = u.Home.X + u.Shift.X - ChipGap - w;
        var min = new Vector2(MathF.Round(left), MathF.Round(cy - ChipH * 0.5f));
        var max = min + new Vector2(MathF.Round(w), ChipH);
        u.ChipMin = min; u.ChipMax = max;

        // The pill: the toast ink, its hairline (the tone's at half strength while a restart is on), square corners.
        dl.AddRectFilled(min, max, U(GuideToastInk, 0.94f * a), 0f);
        dl.AddRect(min, max, waiting != null || granted != null ? U(tone, 0.55f * a) : U(GuideToastLine, a), 0f);
        var x = min.X + ChipPadX;
        if (showCount)
        {
            DrawSessionsGlyph(dl, new Vector2(x + 6.5f, cy), U(ToneNeutral, a));
            x += 13f + 5f;
            dl.AddText(font, small, new Vector2(x, cy - small * 0.5f), U(GuideToastText, 0.9f * a), u.ChipCountText);
            x += countW - 18f;
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

        if (hovered) DrawSessionsChipHover(rs, AgentsSnapshot(), tone, utc);
    }

    /// <summary>
    /// The chip's hover, on the panel's hover sheet (GuideHover, under the card's stack like every other hover): the
    /// agents on this HUD by readable name with what each is doing (from the HUD's own state, BlockersSnapshot) and its
    /// MCP build and mode, then the restart (whose, what it waits for or how long to go) and the rollout. The texts are
    /// rebuilt once a second or when a view changes; GuideHover caches its lines by text, so hovering allocates only then.
    /// </summary>
    private void DrawSessionsChipHover(SessionsView rs, AgentsView ag, Vector4 tone, DateTime utc)
    {
        var u = _guideUi;
        var second = utc.Ticks / TimeSpan.TicksPerSecond;
        if (!ReferenceEquals(u.ChipTipView, rs) || !ReferenceEquals(u.ChipTipAgents, ag) || u.ChipTipSecond != second)
        {
            u.ChipTipView = rs; u.ChipTipAgents = ag; u.ChipTipSecond = second;
            var blockers = BlockersSnapshot();
            var body = new System.Text.StringBuilder();
            foreach (var s in rs.Sessions)
            {
                if (!s.Connected) continue;
                if (body.Length > 0) body.Append('\n');
                body.Append(s.Name).Append(": ");
                var n = 0;
                foreach (var b in blockers) if (b.Who == s.Label) { if (n++ > 0) body.Append("; "); body.Append(b.Label); }
                if (n == 0) body.Append("idle");
                if (s.Mcp is { } m)
                {
                    body.Append(" [mcp ").Append(m.Version ?? ShortSha(m.Sha) ?? "?");
                    if (m.Local) body.Append(", local build");
                    else if (m.Unsupervised) body.Append(", no supervisor: restart this session for one");
                    else if (rs.Rollout != null && m.Behind(rs.Rollout.DeployedSha)) body.Append(", swapping to ").Append(rs.Rollout.DeployedVersion ?? ShortSha(rs.Rollout.DeployedSha));
                    body.Append(']');
                }
            }
            u.ChipTipTitle = rs.Connected == 1 ? "1 agent on this HUD" : $"{rs.Connected} agents on this HUD";
            u.ChipTipBody = body.Length > 0 ? body.ToString() : null;
            u.ChipTipNote = rs.Granted is { } go
                ? $"Restart: {ag.NameOf(go.Who)} is restarting the HUD now" + (go.Reason != null ? $" ({go.Reason})" : "")
                : rs.Waiting is { } rr
                    ? rr.Blockers.Count > 0
                        ? $"Restart: {ag.NameOf(rr.Who)} waits for " + string.Join(", ", rr.Blockers.Take(3).Select(b => b.Who != null ? $"{ag.NameOf(b.Who)}'s {b.Label}" : b.Label))
                          + (rr.Blockers.Count > 3 ? $" (+{rr.Blockers.Count - 3})" : "")
                        : $"Restart: {ag.NameOf(rr.Who)} restarts the HUD in {(rr.HoldUntil is DateTime h ? Math.Max(0, (int)Math.Ceiling((h - utc).TotalSeconds)) : 0)}s - Not now is on the card"
                    : null;
            u.ChipTipFoot = null;
            if (rs.Rollout is { } ro)
            {
                var build = ro.DeployedVersion ?? ShortSha(ro.DeployedSha);
                var foot = ro.Behind.Length == 0 && ro.Local.Length == 0 && ro.Unsupervised.Length == 0
                    ? $"MCP {build}: all {ro.Total} on it" : $"MCP {build}: {ro.OnDeployed} of {ro.Total} on it";
                if (ro.Behind.Length > 0) foot += $", {ro.Behind.Length} swapping";
                if (ro.Local.Length > 0) foot += $", {ro.Local.Length} local";
                if (ro.Unsupervised.Length > 0) foot += $", {ro.Unsupervised.Length} need a session restart ({string.Join(", ", ro.Unsupervised.Select(l => ag.NameOf(l)))})";
                u.ChipTipFoot = foot;
            }
        }
        GuideHover(u.ChipTipTitle, u.ChipTipBody, u.ChipTipNote, u.ChipTipFoot, tone);
    }

    /// <summary>The first seven characters of a git sha (null stays null).</summary>
    private static string? ShortSha(string? sha) => sha == null ? null : sha.Length > 7 ? sha[..7] : sha;
}
