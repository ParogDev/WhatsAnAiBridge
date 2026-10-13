using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// The outage card (state in Outage.cs, every string in OutageCardText.cs): a planned user restart of game + HUD, in the
/// restart card's family (DrawRestartCard: same geometry, pills, who-dot, hover). Drawn at the top of the guide panel's
/// stack (restarts go first) while Outage.cs says it is shown; it keeps the panel up and stays drawn in combat like the
/// restart card (attention RestartUp). Calm in every form (never Urgent, never a throb): the user asked for this.
/// Four forms:
///   - waits (announced / holding, agents busy): warn tone, still glyph, "Waits for B of T agents to finish:", the busy
///     agents as bullets (name: doing, time left or busy for), what happens next, who asked, Restart now / Not now.
///   - ready (nobody busy): accent, still glyph, the lead asks for the press (the runner never stops on "nothing busy"
///     alone), Restart now / Not now.
///   - stopping (Restart now pressed, phase not down yet): accent, turning glyph, "Stopping the HUD - N agents told.", no pills.
///   - countdown (phase down, stopAt ahead): accent, turning glyph, "Stops the HUD in N s - then:", the three steps
///     (restart the game, start the HUD, log in), how long the HUD startup takes, Cancel (gone at 0).
/// A form change washes the card in the accent and re-arms the click guard (a click aimed at the old form must not land
/// on a pill). Texts are composed once a second or when the outage, the names or the width change, never per frame.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int OutageBusyShown = 3, OutageReadyNamed = 6;
    private const int OutageFormWaits = 0, OutageFormReady = 1, OutageFormStopping = 2, OutageFormCountdown = 3;

    private sealed class OutageUiState
    {
        public string? SeenId;
        public double ArrivedAt = -1e9, FlipAt = -1e9;
        public int ShownForm = -1;
        // Composed texts (OutageCompose) and what they were composed for.
        public OutageView? For;
        public AgentsView? Ag;
        public long Second = -1;
        public int ToGo = -1;
        public float W;
        public int Form, Busy, All;
        public string Who = "", Title = "", Lead = "", Explain = "", Meta = "", NowTip = "";
        public string[] Lines = [];
        public string? More;
        public int Hue = AttnHueFallback;
        public string? HoverBody, HoverNote, HoverFoot;
    }

    private readonly OutageUiState _outageUi = new();

    /// <summary>Draws the card when an outage asks the player; returns whether it drew.</summary>
    private bool DrawOutageSlot(AgentsView ag, PanelTheme th, double now, DateTime utc)
    {
        var v = OutageCardSnapshot();
        if (v == null) { _outageUi.SeenId = null; _outageUi.ShownForm = -1; return false; }
        DrawOutageCard(v, ag, th, now, utc);
        ImGui.Dummy(new Vector2(GuideWidth, 5));
        return true;
    }

    private void DrawOutageCard(OutageView v, AgentsView ag, PanelTheme th, double now, DateTime utc)
    {
        var u = _outageUi;
        var dl = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var p = ImGui.GetCursorScreenPos();
        var x0 = p.X + GuideEdge;
        var w = GuideWidth - GuideEdge * 2;
        const float pad = 12f, bulletIndent = 14f;
        var innerW = w - pad * 2;
        var headH = f + 12;
        var btnH = f + 10;
        OutageCompose(v, ag, utc, innerW, innerW - bulletIndent);

        if (u.SeenId != v.Id) { u.SeenId = v.Id; u.ArrivedAt = now; u.ShownForm = u.Form; }
        else if (u.ShownForm != u.Form) { u.ShownForm = u.Form; u.FlipAt = now; }
        var guard = now - u.ArrivedAt < AskClickGuardSec || now - u.FlipAt < AskClickGuardSec;
        var form = u.Form;
        var tone = form == OutageFormWaits ? ToneWarn : ToneAccent;
        var turning = form is OutageFormStopping or OutageFormCountdown;
        var pills = form is OutageFormWaits or OutageFormReady || (form == OutageFormCountdown && u.ToGo > 0);

        var hgt = headH + (f + 4) + u.Lines.Length * (small + 3) + (u.More != null ? small + 3 : 0)
                  + (small + 3) + (small + 8) + (pills ? btnH : 0) + 10;
        var min = new Vector2(x0, p.Y + GuideEdge);
        var max = new Vector2(x0 + w, p.Y + GuideEdge + hgt);
        dl.AddRectFilled(min, max, U(th.Card, 0.94f), 6f);
        var flashT = (now - u.ArrivedAt) / GuideFlashSec;
        if (flashT < 1) dl.AddRectFilled(min, max, U(tone, 0.28f * (float)(1 - flashT)), 6f);
        var flipT = (now - u.FlipAt) / GuideFlashSec;
        if (flipT < 1) dl.AddRectFilled(min, max, U(ToneAccent, 0.20f * (float)(1 - flipT)), 6f);
        dl.AddRect(min, max, U(tone, 0.55f), 6f, ImDrawFlags.None, 1f);

        // Header: glyph + caps title in the tone; right: who asked, with its who-dot, dimmed.
        var hy = min.Y + headH * 0.5f;
        var x = min.X + pad;
        DrawRestartGlyph(dl, new Vector2(x + 6, hy), U(tone), turning ? now : 0);
        x += 18;
        dl.AddText(font, small, new Vector2(x, hy - small * 0.5f), U(tone), u.Title);
        x += ImGui.CalcTextSize(u.Title).X * (small / f) + 10;
        var rx = max.X - pad;
        if (u.Who.Length > 0)
        {
            var whoW = ImGui.CalcTextSize(u.Who).X * (small / f) + WhoDotW;
            if (rx - whoW > x)
            {
                DrawWhoDot(dl, new Vector2(rx - whoW + GuideDotR, hy), u.Hue);
                dl.AddText(font, small, new Vector2(rx - whoW + WhoDotW, hy - small * 0.5f), U(th.TextDim), u.Who);
            }
        }

        // Body: the lead, bullets (busy agents) or the steps (countdown), the explain line, the meta line.
        var y = min.Y + headH + 2;
        var bx = min.X + pad;
        var textTop = y;
        dl.AddText(font, f, new Vector2(bx, y), U(form == OutageFormWaits ? th.Text : tone), u.Lead);
        y += f + 4;
        var steps = form == OutageFormCountdown;
        foreach (var line in u.Lines)
        {
            if (!steps) dl.AddCircleFilled(new Vector2(bx + 4, y + small * 0.5f), 2f, U(th.TextDim), 8);
            dl.AddText(font, small, new Vector2(bx + bulletIndent, y), steps ? U(th.Text) : U(th.TextDim), line);
            y += small + 3;
        }
        if (u.More != null)
        {
            dl.AddText(font, small, new Vector2(bx + bulletIndent, y), U(th.TextDim, 0.8f), u.More);
            y += small + 3;
        }
        dl.AddText(font, small, new Vector2(bx, y), U(th.TextDim, 0.85f), u.Explain);
        y += small + 3;
        dl.AddText(font, small, new Vector2(bx, y), U(th.TextDim, 0.85f), u.Meta);
        y += small + 8;

        // Hover over the text block: the whole story (reason, every busy agent, who is ready).
        ImGui.SetCursorScreenPos(new Vector2(bx, textTop));
        ImGui.InvisibleButton("##outage_text", new Vector2(innerW, MathF.Max(1, y - 5 - textTop)));
        if (ImGui.IsItemHovered()) GuideHover(OutageCardText.HoverTitle, u.HoverBody, u.HoverNote, u.HoverFoot, tone);

        if (form is OutageFormWaits or OutageFormReady)
        {
            QueuePillButton(dl, "##outage_now", new Vector2(bx, y), btnH, OutageCardText.ButtonNow, true, th, out var nowClick, u.NowTip);
            if (nowClick && !guard) OutageAnswer(v.Id, "now", u.Busy);
            var notW = ImGui.CalcTextSize(OutageCardText.ButtonNotNow).X + 24;
            QueuePillButton(dl, "##outage_not_now", new Vector2(max.X - pad - notW, y), btnH, OutageCardText.ButtonNotNow, false, th, out var notClick,
                OutageCardText.ButtonNotNowTip);
            if (notClick && !guard) OutageAnswer(v.Id, "notNow", u.Busy);
        }
        else if (form == OutageFormCountdown && u.ToGo > 0)
        {
            var cW = ImGui.CalcTextSize(OutageCardText.ButtonCancel).X + 24;
            QueuePillButton(dl, "##outage_cancel", new Vector2(max.X - pad - cW, y), btnH, OutageCardText.ButtonCancel, false, th, out var cancelClick,
                OutageCardText.ButtonCancelTip);
            if (cancelClick && !guard) OutageAnswer(v.Id, "notNow", 0);
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, hgt + GuideEdge * 2));
    }

    /// <summary>The card's texts, clipped to its width: rebuilt when the outage, the names or the width change, the
    /// countdown moves, or once a second (who is busy moves, elapsed counts).</summary>
    private void OutageCompose(OutageView v, AgentsView ag, DateTime utc, float w, float bulletW)
    {
        var u = _outageUi;
        var sec = utc.Ticks / TimeSpan.TicksPerSecond;
        var toGo = v.StopAt is DateTime s ? Math.Max(0, (int)Math.Ceiling((s - utc).TotalSeconds)) : 0;
        if (ReferenceEquals(u.For, v) && ReferenceEquals(u.Ag, ag) && u.Second == sec && u.ToGo == toGo && u.W == w) return;
        u.For = v; u.Ag = ag; u.Second = sec; u.ToGo = toGo; u.W = w;

        var label = v.ByLabel ?? v.By;
        var who = v.By == null && label == null ? "" : ag.NameOf(v.By, label);
        u.Who = who.Length == 0 ? "" : GuideClipText(who, w * 0.4f);
        u.Hue = who.Length == 0 ? AttnHueFallback : ag.HueOf(v.By, label);
        var asker = who.Length == 0 ? "An agent" : who;
        var ago = GuideElapsed(utc - v.SetAt);
        u.Meta = GuideClipText(string.IsNullOrWhiteSpace(v.Reason)
            ? string.Format(OutageCardText.MetaNoReason, asker, ago)
            : string.Format(OutageCardText.Meta, asker, ago, v.Reason), w);

        List<OutageParty> busy, ready;
        try { var st = OutageStateNow(); busy = st.Busy; ready = st.Ready; }
        catch { busy = new(); ready = new(); }
        int b = busy.Count, r = ready.Count, t = b + r;
        u.Busy = b; u.All = t;
        u.Form = v.Phase == "down" ? OutageFormCountdown
            : v.Answer == "now" ? OutageFormStopping
            : b > 0 ? OutageFormWaits : OutageFormReady;
        u.Title = u.Form == OutageFormWaits ? OutageCardText.TitleWaits : OutageCardText.Title;
        u.Lines = [];
        u.More = null;
        u.NowTip = OutageCardText.ButtonNowTipReady;

        switch (u.Form)
        {
            case OutageFormWaits:
            {
                u.Lead = GuideClipText(b == 1 && t == 1 ? OutageCardText.LeadWaitsOne : string.Format(OutageCardText.LeadWaits, b, t), w);
                var n = Math.Min(b, OutageBusyShown);
                var lines = new string[n];
                for (var i = 0; i < n; i++) lines[i] = GuideClipText(OutageBusyText(busy[i], ag, utc), bulletW);
                u.Lines = lines;
                u.More = b > n ? string.Format(OutageCardText.MoreBusy, b - n) : null;
                u.Explain = GuideClipText(r > 0 ? string.Format(OutageCardText.ExplainSomeReady, r) : OutageCardText.ExplainNoneReady, w);
                var sb = new StringBuilder();
                for (var i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(string.Format(OutageCardText.Interrupts, ag.NameOf(busy[i].Session, busy[i].Label), busy[i].Doing ?? ""));
                }
                if (b > n) sb.Append(string.Format(OutageCardText.InterruptsMore, b - n));
                u.NowTip = string.Format(OutageCardText.ButtonNowTipBusy, sb.ToString());
                break;
            }
            case OutageFormReady:
                u.Lead = GuideClipText(t == 0 ? OutageCardText.LeadReadyNone : t == 1 ? OutageCardText.LeadReadyOne
                    : string.Format(OutageCardText.LeadReadyAll, OutageCardText.Agents(t)), w);
                u.Explain = GuideClipText(OutageCardText.ExplainGo, w);
                break;
            case OutageFormStopping:
                u.Lead = GuideClipText(b > 0 ? string.Format(OutageCardText.LeadStoppingTold, OutageCardText.Agents(b)) : OutageCardText.LeadStopping, w);
                u.Explain = GuideClipText(OutageCardText.ExplainGo, w);
                break;
            default:   // countdown
            {
                u.Lead = GuideClipText(toGo > 0 ? string.Format(OutageCardText.LeadStopsIn, toGo) : OutageCardText.LeadStopsNow, w);
                u.Lines = [GuideClipText(OutageCardText.Step1, bulletW), GuideClipText(OutageCardText.Step2, bulletW), GuideClipText(OutageCardText.Step3, bulletW)];
                double? typical;
                lock (_hsLock) typical = _hs.TypicalStartupSec;
                u.Explain = GuideClipText(typical is double ts && ts > 0
                    ? string.Format(OutageCardText.StartupTypical, Math.Round(ts).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    : OutageCardText.StartupUsual, w);
                break;
            }
        }

        // Hover: the reason in full, every busy agent, who is ready.
        u.HoverBody = string.IsNullOrWhiteSpace(v.Reason)
            ? string.Format(OutageCardText.HoverBodyNoReason, asker, ago)
            : string.Format(OutageCardText.HoverBody, v.Reason!.TrimEnd('.'), asker, ago);
        if (b > 0 && u.Form != OutageFormCountdown)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < b; i++) { if (i > 0) sb.Append("; "); sb.Append(OutageBusyText(busy[i], ag, utc)); }
            u.HoverNote = string.Format(OutageCardText.HoverBusy, sb.ToString());
        }
        else u.HoverNote = null;
        if (r == 0) u.HoverFoot = null;
        else if (r > OutageReadyNamed) u.HoverFoot = string.Format(OutageCardText.HoverReadyMany, r);
        else
        {
            var sb = new StringBuilder();
            for (var i = 0; i < r; i++) { if (i > 0) sb.Append(", "); sb.Append(ag.NameOf(ready[i].Session, ready[i].Label)); }
            u.HoverFoot = string.Format(OutageCardText.HoverReady, sb.ToString());
        }
    }

    /// <summary>"Name: doing (2m 00s left)" or "(for 1m 40s)".</summary>
    private static string OutageBusyText(OutageParty x, AgentsView ag, DateTime utc)
    {
        var line = string.Format(OutageCardText.BusyLine, ag.NameOf(x.Session, x.Label), x.Doing ?? "");
        if (x.Until is DateTime until && until.ToUniversalTime() > utc) line += string.Format(OutageCardText.BusyLeft, GuideElapsed(until.ToUniversalTime() - utc));
        else if (x.Since is DateTime since) line += string.Format(OutageCardText.BusyFor, GuideElapsed(utc - since.ToUniversalTime()));
        return line;
    }
}
