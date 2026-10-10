using System;
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
/// Agents are named by their readable name (AgentsView.NameOf: never a system folder, no worktree hash tail, unique).
/// Same rules as the rest of the panel: ASCII only, glyphs on the draw list, colours from PanelTheme plus the fixed tones.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int RestartBlockersShown = 3;

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
            var tip = new System.Text.StringBuilder("Who is asking you for something");
            foreach (var x1 in ag.Active)
            {
                if (x1.Asks.Length == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(x1.Name);
                for (var i = 0; i < x1.Asks.Length; i++)
                {
                    tip.Append('\n').Append(x1.Name).Append(": ").Append(AskWords(x1.Asks[i]));
                    if (i == 0 && x1.Doing.Length > 0) tip.Append(" - also ").Append(string.Join(", ", x1.Doing));
                }
            }
            u.SessionsLine = sb.ToString();
            u.SessionsTip = tip.ToString();
        }

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##sessions_strip", new Vector2(w, h));
        if (ImGui.IsItemHovered() && u.SessionsTip != null) GuideTooltip(u.SessionsTip);

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
}
