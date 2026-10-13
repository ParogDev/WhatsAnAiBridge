using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// The outage card (state in Outage.cs, every string in OutageCardText.cs): a planned user restart of game + HUD. Drawn
/// at the top of the guide panel's stack (restarts go first), above the floor card and the restart card, while the outage
/// is announced or holding; it keeps the panel up and stays drawn in combat like the restart card (attention RestartUp).
/// Placeholder look in the restart card's family (slate card, hairline in the warn tone, restart glyph, caps header, the
/// asker's who-dot and name, a lead line, who it waits for as bullets, Restart now / Not now); the design pass replaces
/// it. Texts are composed once a second or when the outage changes, never per frame; buttons ignore clicks for the click
/// guard after the card arrives.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int OutageBusyShown = 3;

    private sealed class OutageUiState
    {
        public string? SeenId;
        public double ArrivedAt = -1e9;
        public OutageView? For;
        public long Second = -1;
        public float W;
        public string Who = "", Lead = "", How = "", Meta = "";
        public string[] Lines = [];
        public string? More;
        public int Hue = AttnHueFallback;
    }

    private readonly OutageUiState _outageUi = new();

    /// <summary>Draws the card when an outage asks the player; returns whether it drew.</summary>
    private bool DrawOutageSlot(AgentsView ag, PanelTheme th, double now, DateTime utc)
    {
        var v = OutageCardSnapshot();
        if (v == null) { _outageUi.SeenId = null; return false; }
        DrawOutageCard(v, ag, th, now, utc);
        ImGui.Dummy(new Vector2(GuideWidth, 5));
        return true;
    }

    private void DrawOutageCard(OutageView v, AgentsView ag, PanelTheme th, double now, DateTime utc)
    {
        var u = _outageUi;
        if (u.SeenId != v.Id) { u.SeenId = v.Id; u.ArrivedAt = now; }
        var guard = now - u.ArrivedAt < AskClickGuardSec;
        var answered = v.Answer != null;
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
        var tone = answered ? ToneAccent : ToneWarn;

        var bodyH = f + 4 + small + 4
                    + small + 3 + u.Lines.Length * (small + 3) + (u.More != null ? small + 3 : 0)
                    + 6 + (answered ? 0 : btnH);
        var hgt = headH + bodyH + 10;
        var min = new Vector2(x0, p.Y + GuideEdge);
        var max = new Vector2(x0 + w, p.Y + GuideEdge + hgt);
        dl.AddRectFilled(min, max, U(th.Card, 0.94f), 6f);
        var flashT = (now - u.ArrivedAt) / GuideFlashSec;
        if (flashT < 1) dl.AddRectFilled(min, max, U(tone, 0.28f * (float)(1 - flashT)), 6f);
        dl.AddRect(min, max, U(tone, 0.55f), 6f, ImDrawFlags.None, 1f);

        // Header: glyph + caps title in the tone; right: who asked, with its who-dot, dimmed.
        var hy = min.Y + headH * 0.5f;
        var x = min.X + pad;
        DrawRestartGlyph(dl, new Vector2(x + 6, hy), U(tone), answered ? now : 0);
        x += 18;
        var title = answered ? OutageCardText.TitleAnswered : OutageCardText.Title;
        dl.AddText(font, small, new Vector2(x, hy - small * 0.5f), U(tone), title);
        x += ImGui.CalcTextSize(title).X * (small / f) + 10;
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

        // Body: lead, how it goes, then who it waits for.
        var y = min.Y + headH + 2;
        var bx = min.X + pad;
        dl.AddText(font, f, new Vector2(bx, y), U(th.Text), u.Lead);
        y += f + 4;
        dl.AddText(font, small, new Vector2(bx, y), U(th.TextDim, 0.9f), u.How);
        y += small + 4;
        dl.AddText(font, small, new Vector2(bx, y), U(th.TextDim, 0.85f), u.Meta);
        y += small + 3;
        foreach (var line in u.Lines)
        {
            dl.AddCircleFilled(new Vector2(bx + 4, y + small * 0.5f), 2f, U(th.TextDim), 8);
            dl.AddText(font, small, new Vector2(bx + bulletIndent, y), U(th.TextDim), line);
            y += small + 3;
        }
        if (u.More != null)
        {
            dl.AddText(font, small, new Vector2(bx + bulletIndent, y), U(th.TextDim, 0.8f), u.More);
            y += small + 3;
        }
        y += 6;

        if (!answered)
        {
            QueuePillButton(dl, "##outage_now", new Vector2(bx, y), btnH, OutageCardText.ButtonNow, true, th, out var nowClick, OutageCardText.ButtonNowTip);
            if (nowClick && !guard) OutageAnswer(v.Id, "now");
            var notW = ImGui.CalcTextSize(OutageCardText.ButtonNotNow).X + 24;
            QueuePillButton(dl, "##outage_not_now", new Vector2(max.X - pad - notW, y), btnH, OutageCardText.ButtonNotNow, false, th, out var notClick,
                OutageCardText.ButtonNotNowTip);
            if (notClick && !guard) OutageAnswer(v.Id, "notNow");
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, hgt + GuideEdge * 2));
    }

    /// <summary>The card's texts, clipped to its width: rebuilt when the outage changes, the width changes or once a second
    /// (who is busy moves).</summary>
    private void OutageCompose(OutageView v, AgentsView ag, DateTime utc, float w, float bulletW)
    {
        var u = _outageUi;
        var sec = utc.Ticks / TimeSpan.TicksPerSecond;
        if (ReferenceEquals(u.For, v) && u.Second == sec && u.W == w) return;
        u.For = v; u.Second = sec; u.W = w;
        var by = v.By;
        u.Who = by == null ? "" : GuideClipText(string.Format(OutageCardText.AskedBy, ag.NameOf(by, by)), w * 0.4f);
        u.Hue = by == null ? AttnHueFallback : ag.HueOf(by, by);
        u.Lead = GuideClipText(string.IsNullOrWhiteSpace(v.Reason) ? OutageCardText.LeadNoReason : string.Format(OutageCardText.Lead, v.Reason), w);
        u.How = GuideClipText(v.StopOnly ? OutageCardText.HowStopOnly : OutageCardText.HowFull, w);
        if (v.Answer == "now")
        {
            u.Meta = OutageCardText.AnsweredNow; u.Lines = []; u.More = null;
            return;
        }
        List<OutageParty> busy;
        try { busy = OutageStateNow().Busy; } catch { busy = new(); }
        if (busy.Count == 0) { u.Meta = OutageCardText.NobodyBusy; u.Lines = []; u.More = null; return; }
        u.Meta = OutageCardText.WaitingFor;
        var n = Math.Min(busy.Count, OutageBusyShown);
        var lines = new string[n];
        for (var i = 0; i < n; i++)
            lines[i] = GuideClipText(string.Format(OutageCardText.BusyLine, ag.NameOf(busy[i].Session, busy[i].Label), busy[i].Doing ?? ""), bulletW);
        u.Lines = lines;
        u.More = busy.Count > n ? string.Format(OutageCardText.MoreBusy, busy.Count - n) : null;
    }
}
