using System;
using System.Numerics;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// The ask card: the attention queue's floor holder when it is a question (AttentionQueue.cs AttnAskSnapshot). Minimal
/// and functional on purpose - the question, its 2-4 choices as pills and Later - built from the restart card's parts
/// (GuidePanelSessions.cs) and QueuePillButton. The polished card (who-dots, receipt, click guard, softening, the next
/// strip) is specified in research\attention-queue-ui.md and replaces this method; nothing else draws asks.
/// </summary>
public partial class WhatsAnAiBridge
{
    private void DrawAskCard(AskView ask, PanelTheme th, double now, DateTime utc)
    {
        var dl = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var p = ImGui.GetCursorScreenPos();
        var x0 = p.X + GuideEdge;
        var w = GuideWidth - GuideEdge * 2;
        const float pad = 12f, gap = 8f;
        var innerW = w - pad * 2;
        var headH = f + 12;
        var btnH = f + 10;

        // Measure first: the wrapped question, the detail line, and the pill rows (choices then Later, wrapping).
        var qSize = ImGui.CalcTextSize(ask.Question, false, innerW);
        var laterW = ImGui.CalcTextSize("Later").X + 24;
        var rows = 1;
        var rowX = 0f;
        foreach (var o in ask.Options)
        {
            var ow = ImGui.CalcTextSize(o.Label).X + 24;
            if (rowX > 0 && rowX + ow > innerW) { rows++; rowX = 0; }
            rowX += ow + gap;
        }
        if (rowX + laterW > innerW) rows++;
        var hgt = headH + qSize.Y + 6 + (ask.Detail != null ? small + 4 : 0) + rows * (btnH + 6) + 8;
        var min = new Vector2(x0, p.Y + GuideEdge);
        var max = new Vector2(x0 + w, p.Y + GuideEdge + hgt);

        dl.AddRectFilled(min, max, U(th.Card, 0.94f), 6f);
        dl.AddRectFilled(min, new Vector2(min.X + 3, max.Y), U(ToneAccent));
        dl.AddRect(min, max, U(ToneAccent, 0.55f), 6f, ImDrawFlags.None, 1f);

        // Header: QUESTION (or AGAIN after a Later), the ask's title dimmed, who asks at the right.
        var hy = min.Y + headH * 0.5f;
        var x = min.X + pad;
        var label = ask.LaterCount > 0 ? "QUESTION - AGAIN" : "QUESTION";
        dl.AddText(font, small, new Vector2(x, hy - small * 0.5f), U(ToneAccent), label);
        x += ImGui.CalcTextSize(label).X * (small / f) + 10;
        var who = string.Join(", ", ask.Whos);
        var whoW = ImGui.CalcTextSize(who).X * (small / f);
        var rx = max.X - pad;
        if (rx - whoW > x) { dl.AddText(font, small, new Vector2(rx - whoW, hy - small * 0.5f), U(th.TextDim), who); rx -= whoW + 10; }
        if (rx - x > 40) dl.AddText(font, small, new Vector2(x, hy - small * 0.5f), U(th.TextDim), GuideClipText(ask.Title, rx - x));

        var y = min.Y + headH;
        dl.AddText(font, f, new Vector2(min.X + pad, y), U(th.Text), ask.Question, innerW);
        y += qSize.Y + 6;
        if (ask.Detail != null)
        {
            dl.AddText(font, small, new Vector2(min.X + pad, y), U(th.TextDim), GuideClipText(ask.Detail, innerW));
            y += small + 4;
        }

        // Pills: each choice answers; Later puts it back for a calmer moment (the bridge decides when).
        var bx = min.X + pad;
        for (var i = 0; i < ask.Options.Length; i++)
        {
            var o = ask.Options[i];
            var ow = ImGui.CalcTextSize(o.Label).X + 24;
            if (bx > min.X + pad && bx + ow > max.X - pad) { bx = min.X + pad; y += btnH + 6; }
            QueuePillButton(dl, $"##ask_{ask.Id}_{i}", new Vector2(bx, y), btnH, o.Label, false, th, out var click, o.Hint ?? $"Answer: {o.Label}");
            if (click) AttnAnswerFromCard(ask.Id, o.Id);
            bx += ow + gap;
        }
        if (bx + laterW > max.X - pad) y += btnH + 6;
        QueuePillButton(dl, $"##ask_{ask.Id}_later", new Vector2(max.X - pad - laterW, y), btnH, "Later", false, th, out var later,
            "Not now: it comes back at a calm moment (in a few minutes). The third Later dismisses it.");
        if (later) AttnAnswerFromCard(ask.Id, null);

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, hgt + GuideEdge * 2));
    }
}
