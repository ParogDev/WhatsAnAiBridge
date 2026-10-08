using System;
using System.Numerics;
using ImGuiNET;

namespace WhatsAnAiBridge;

using GuideSnap = (string? title, string? instruction, int? step, int? steps, string status, string? detail,
    DateTime updatedAt, DateTime? instructionSince, int rev, WhatsAnAiBridge.GuideLogEntry[] log);

/// <summary>
/// In-HUD agent guide panel (ImGui): the agent's current instruction for the user as a sticky card, and a short
/// "combat log" of what the agent is doing under it. One surface over the state in AgentGuide.cs (GuideSnapshot,
/// taken once per frame; the only write is GuideDismiss). Loud only while the user must act (status waiting, and
/// failed: the step needs redoing), quiet for progress and results, hidden when there is nothing to say.
/// Game-agnostic: ImGui only; nothing here touches ExileCore* directly.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const float GuideWidth = 520f;   // fits the gap between the stash (x 0..665) and the inventory at 1080p
    private const float GuideEdge = 4f;      // inset so the pulse glow is not clipped by the window rect
    private const int GuideLogRows = 6;
    private const double GuideQuietHideSec = 120, GuideCapturedLoudSec = 4, GuideFlashSec = 0.9, GuideLogFlashSec = 1.5;

    /// <summary>Panel-local state: what the panel last saw, for the arrival and change animations.</summary>
    private sealed class GuideUiState
    {
        public int SeenRev = -1;
        public DateTime? SeenSince;
        public string SeenStatus = "";
        public DateTime SeenLastLogAt = DateTime.MinValue;
        public double ArrivedAt = -1e9;    // ImGui time the current instruction appeared
        public double StatusAt = -1e9;     // ImGui time the status last changed
        public double LogAt = -1e9;        // ImGui time the newest log line arrived
        public string? LastError;
    }

    private readonly GuideUiState _guideUi = new();

    /// <summary>How a status looks: its label, tone, whether the card shouts (big text, strong frame) and pulses.</summary>
    private readonly record struct GuideLook(string Label, Vector4 Tone, bool Loud, bool Pulse);

    private static GuideLook GuideLookFor(string status) => status switch
    {
        "waiting" => new("DO THIS NOW", ToneAccent, true, true),
        "detected" => new("CHANGE SEEN", ToneAccent, false, false),
        "settling" => new("HOLDING STILL", ToneAccent, false, false),
        "captured" => new("CAPTURED", ToneOk, false, false),
        "failed" => new("TRY AGAIN", ToneBad, true, false),
        "done" => new("DONE", ToneNeutral, false, false),
        "info" => new("NOTE", ToneNeutral, false, false),
        _ => new("", ToneNeutral, false, false),
    };

    // ── Frame ────────────────────────────────────────────────────────

    private void DrawGuidePanel()
    {
        if (!Settings.ShowAgentGuide.Value) return;

        var g = GuideSnapshot();
        var now = ImGui.GetTime();
        var utc = DateTime.UtcNow;
        ObserveGuide(g, now);

        // Visibility: a card exists while there is an instruction or a non-idle status. The panel hides itself once
        // everything is quiet (idle / captured / done / info) and nothing happened for two minutes; waiting, failed
        // and progress states stay until the agent or the user clears them. It comes back on the next rev change.
        var hasCard = g.instruction != null || g.status != "idle";
        var needsUser = g.status is "waiting" or "failed" or "detected" or "settling";
        var lastActivity = g.updatedAt;
        if (g.log.Length > 0 && g.log[^1].At > lastActivity) lastActivity = g.log[^1].At;
        var quietFor = (utc - lastActivity).TotalSeconds;
        if (!needsUser && quietFor > GuideQuietHideSec) return;
        if (!hasCard && g.log.Length == 0) return;

        var io = ImGui.GetIO();
        // Default: top centre, under the skill bar; clear of the stash (left) and the inventory (right) at 1080p.
        ImGui.SetNextWindowPos(new Vector2(MathF.Round((io.DisplaySize.X - GuideWidth) * 0.5f), 84), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(GuideWidth, 0), new Vector2(GuideWidth, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize
                                       | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse
                                       | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
                                       | ImGuiWindowFlags.NoBackground;
        var shown = ImGui.Begin("Agent Guide###bridge_guide_panel", flags);
        try
        {
            if (shown) DrawGuideBody(g, hasCard, now, utc);
        }
        catch (Exception ex)
        {
            if (_guideUi.LastError != ex.Message)
            {
                _guideUi.LastError = ex.Message;
                LogError($"[GuidePanel] {ex}");
            }
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(3);
        }
    }

    /// <summary>Notice what changed since last frame, so arrivals can flash and the log can highlight its newest line.</summary>
    private void ObserveGuide(in GuideSnap g, double now)
    {
        var u = _guideUi;
        if (g.rev == u.SeenRev) return;
        var first = u.SeenRev < 0;
        u.SeenRev = g.rev;
        if (g.instructionSince != u.SeenSince)
        {
            u.SeenSince = g.instructionSince;
            if (!first && g.instruction != null) u.ArrivedAt = now;
        }
        if (g.status != u.SeenStatus)
        {
            u.SeenStatus = g.status;
            u.StatusAt = first ? -1e9 : now;
            // A status that asks for the user again should catch the eye like a new instruction does.
            if (!first && g.status is "waiting" or "failed") u.ArrivedAt = now;
        }
        // The newest line's timestamp identifies it even once the log is full and old lines fall off the front.
        var lastAt = g.log.Length > 0 ? g.log[^1].At : DateTime.MinValue;
        if (lastAt != u.SeenLastLogAt)
        {
            if (!first && g.log.Length > 0) u.LogAt = now;
            u.SeenLastLogAt = lastAt;
        }
    }

    // ── Body ─────────────────────────────────────────────────────────

    private void DrawGuideBody(in GuideSnap g, bool hasCard, double now, DateTime utc)
    {
        var th = PanelTheme.Current();
        if (hasCard)
        {
            DrawGuideCard(g, th, now, utc);
            ImGui.Dummy(new Vector2(GuideWidth, 5));
        }
        if (g.log.Length > 0) DrawGuideLog(g, th, now);
    }

    private void DrawGuideCard(in GuideSnap g, PanelTheme th, double now, DateTime utc)
    {
        var dl = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var look = GuideLookFor(g.status);
        var sinceStatus = now - _guideUi.StatusAt;

        // Captured shouts for a few seconds, then shrinks to a one-line receipt until the next step arrives.
        var compact = g.status == "captured" && sinceStatus > GuideCapturedLoudSec;
        var loud = look.Loud && g.instruction != null;

        var p = ImGui.GetCursorScreenPos();
        var x0 = p.X + GuideEdge;
        var w = GuideWidth - GuideEdge * 2;
        const float pad = 12f;
        var innerW = w - pad * 2;
        var small = f * 0.85f;
        var big = MathF.Round(f * 1.5f);
        var headH = f + 12;

        // Measure first: the card background needs the full height.
        var bodyH = 0f;
        string? sub = GuideSubline(g, now);
        Vector2 instrSize = default, subSize = default, detailSize = default;
        if (!compact && g.instruction != null)
        {
            instrSize = font.CalcTextSizeA(loud ? big : f, float.MaxValue, innerW, g.instruction);
            bodyH += instrSize.Y + 4;
        }
        if (!compact && sub != null)
        {
            subSize = font.CalcTextSizeA(f, float.MaxValue, innerW, sub);
            bodyH += subSize.Y + 4;
        }
        if (!compact && g.detail != null && g.detail != sub)
        {
            detailSize = font.CalcTextSizeA(f, float.MaxValue, innerW, g.detail);
            bodyH += detailSize.Y + 4;
        }
        var h = headH + (bodyH > 0 ? bodyH + 6 : 2);
        var min = new Vector2(x0, p.Y + GuideEdge);
        var max = new Vector2(x0 + w, p.Y + GuideEdge + h);

        // Fill, arrival flash, frame. The frame pulses only while the user must act.
        var pulse = look.Pulse ? (float)(0.5 + 0.5 * Math.Sin(now * Math.PI * 1.6)) : 0f;
        dl.AddRectFilled(min, max, U(th.Card, 0.94f), 6f);
        var flashT = (now - _guideUi.ArrivedAt) / GuideFlashSec;
        if (flashT < 1) dl.AddRectFilled(min, max, U(look.Tone, 0.35f * (float)(1 - flashT)), 6f);
        if (look.Pulse)
        {
            dl.AddRect(min - new Vector2(3, 3), max + new Vector2(3, 3), U(look.Tone, 0.10f + 0.16f * pulse), 8f, ImDrawFlags.None, 3f);
            dl.AddRect(min, max, U(look.Tone, 0.6f + 0.4f * pulse), 6f, ImDrawFlags.None, 2f);
        }
        else if (loud || (g.status == "captured" && !compact))
            dl.AddRect(min, max, U(look.Tone, 0.9f), 6f, ImDrawFlags.None, 2f);
        else
            dl.AddRect(min, max, U(look.Tone, g.status == "idle" ? 0.25f : 0.55f), 6f, ImDrawFlags.None, 1f);

        // Header: status glyph + label, the experiment title, then (right) step pill, elapsed, dismiss.
        var hy = min.Y + headH * 0.5f;
        var x = min.X + pad;
        DrawGuideStatusGlyph(dl, g.status, new Vector2(x + 6, hy), look.Tone, now, pulse);
        x += 18;
        if (look.Label.Length > 0)
        {
            dl.AddText(font, small, new Vector2(x, hy - small * 0.5f), U(look.Tone), look.Label);
            // Faux bold: the HUD font has no bold face.
            if (loud) dl.AddText(font, small, new Vector2(x + 0.6f, hy - small * 0.5f), U(look.Tone), look.Label);
            x += ImGui.CalcTextSize(look.Label).X * (small / f) + 10;
        }

        // Right side, laid out from the edge inwards.
        var rx = max.X - pad;
        var showDismiss = g.instruction != null || g.status != "idle";
        if (showDismiss)
        {
            var bx = new Vector2(rx - 16, hy - 8);
            ImGui.SetCursorScreenPos(bx);
            ImGui.InvisibleButton("##guide_dismiss", new Vector2(16, 16));
            var hov = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) GuideDismiss();
            if (hov) ImGui.SetTooltip("Dismiss this instruction");
            if (hov) dl.AddRectFilled(bx, bx + new Vector2(16, 16), U(th.Tile), 3f);
            DrawCloseGlyph(dl, new Vector2(rx - 8, hy), 3.5f, U(hov ? th.Text : th.TextDim, hov ? 1f : 0.8f));
            rx -= 16 + 8;
        }
        if (g.status is "waiting" or "failed" && g.instructionSince is DateTime since)
        {
            var t = GuideElapsed(utc - since);
            var tw = ImGui.CalcTextSize(t).X;
            dl.AddText(new Vector2(rx - tw, hy - f * 0.5f), U(th.TextDim), t);
            rx -= tw + 10;
        }
        if (g.step is int step)
        {
            var t = g.steps is int steps ? $"step {step} / {steps}" : $"step {step}";
            var tw = ImGui.CalcTextSize(t).X * (small / f);
            var pillW = tw + 12;
            var pillH = small + 6;
            var pmin = new Vector2(rx - pillW, hy - pillH * 0.5f);
            dl.AddRectFilled(pmin, pmin + new Vector2(pillW, pillH), U(th.Tile), pillH * 0.5f);
            dl.AddRect(pmin, pmin + new Vector2(pillW, pillH), U(th.Border, 0.6f), pillH * 0.5f);
            dl.AddText(font, small, new Vector2(pmin.X + 6, hy - small * 0.5f), U(th.TextDim), t);
            rx -= pillW + 10;
        }

        // Title (or the instruction itself when compact), clipped to the space left in the header.
        var titleText = compact && g.title == null ? g.instruction : g.title;
        if (titleText != null && rx - x > 40)
        {
            var t = GuideClipText(titleText, rx - x);
            dl.AddText(new Vector2(x, hy - f * 0.5f), U(th.TextDim), t);
        }

        // Body
        if (!compact)
        {
            var y = min.Y + headH + 2;
            var bx = min.X + pad;
            if (g.instruction != null)
            {
                var col = g.status is "captured" or "done" or "detected" or "settling" ? U(th.Text, 0.85f) : U(th.Text);
                dl.AddText(font, loud ? big : f, new Vector2(bx, y), col, g.instruction, innerW);
                y += instrSize.Y + 4;
            }
            if (sub != null)
            {
                dl.AddText(font, f, new Vector2(bx, y), U(look.Tone, 0.95f), sub, innerW);
                y += subSize.Y + 4;
            }
            if (g.detail != null && g.detail != sub)
            {
                dl.AddText(font, f, new Vector2(bx, y), U(th.TextDim), g.detail, innerW);
                y += detailSize.Y + 4;
            }
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, h + GuideEdge * 2));
    }

    /// <summary>The one-line meaning of the status, under the instruction. Progress states animate their dots.</summary>
    private static string? GuideSubline(in GuideSnap g, double now)
    {
        var dots = new string('.', 1 + (int)(now * 2.5) % 3);
        return g.status switch
        {
            "detected" => "Change seen" + dots + " hold still while it settles",
            "settling" => "Still changing" + dots + " keep holding still",
            "captured" => g.detail ?? "Recorded.",
            "failed" => g.detail ?? "Nothing lasting changed. Do it once more.",
            _ => null,
        };
    }

    /// <summary>12 px status glyph: pulsing dot (waiting), spinner (progress), check (captured, done), bang (failed), i (info).</summary>
    private static void DrawGuideStatusGlyph(ImDrawListPtr dl, string status, Vector2 c, Vector4 tone, double now, float pulse)
    {
        var col = U(tone);
        switch (status)
        {
            case "waiting":
                dl.AddCircle(c, 5 + pulse * 3, U(tone, 0.6f - pulse * 0.5f), 16, 1.5f);
                dl.AddCircleFilled(c, 3.2f, col);
                break;
            case "detected":
            case "settling":
            {
                var a0 = (float)(now * 4.5 % (Math.PI * 2));
                dl.PathArcTo(c, 5f, a0, a0 + 4.2f, 14);
                dl.PathStroke(col, ImDrawFlags.None, 2f);
                if (status == "settling") dl.AddCircleFilled(c, 1.8f, col);
                break;
            }
            case "captured":
            case "done":
                dl.AddCircleFilled(c, 6f, U(tone, status == "done" ? 0.35f : 1f));
                var ck = status == "done" ? col : U(new Vector4(0.05f, 0.08f, 0.06f, 1f));
                dl.AddLine(new Vector2(c.X - 3.2f, c.Y + 0.2f), new Vector2(c.X - 0.8f, c.Y + 2.6f), ck, 1.8f);
                dl.AddLine(new Vector2(c.X - 0.8f, c.Y + 2.6f), new Vector2(c.X + 3.4f, c.Y - 2.6f), ck, 1.8f);
                break;
            case "failed":
                dl.AddCircle(c, 6f, col, 16, 1.6f);
                dl.AddLine(new Vector2(c.X, c.Y - 3.5f), new Vector2(c.X, c.Y + 0.8f), col, 1.8f);
                dl.AddCircleFilled(new Vector2(c.X, c.Y + 3.2f), 1.1f, col);
                break;
            case "info":
                dl.AddCircle(c, 6f, col, 16, 1.4f);
                dl.AddCircleFilled(new Vector2(c.X, c.Y - 3f), 1.1f, col);
                dl.AddLine(new Vector2(c.X, c.Y - 0.8f), new Vector2(c.X, c.Y + 3.5f), col, 1.6f);
                break;
            default:
                dl.AddCircleFilled(c, 3f, U(tone, 0.6f));
                break;
        }
    }

    // ── Log ──────────────────────────────────────────────────────────

    /// <summary>The agent's log as a compact combat log: the newest lines at the bottom, older ones fading.</summary>
    private void DrawGuideLog(in GuideSnap g, PanelTheme th, double now)
    {
        var dl = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var open = Settings.GuideLogOpen.Value;
        var p = ImGui.GetCursorScreenPos();
        var x0 = p.X + GuideEdge;
        var w = GuideWidth - GuideEdge * 2;
        const float pad = 10f;
        var headH = f + 6;
        var rowH = f + 4;
        var shown = Math.Min(GuideLogRows, g.log.Length);
        var h = headH + (open ? shown * rowH + 6 : 0);
        var min = new Vector2(x0, p.Y);
        var max = new Vector2(x0 + w, p.Y + h);

        dl.AddRectFilled(min, max, U(th.Card, 0.92f), 5f);
        dl.AddRect(min, max, U(th.Border, 0.5f), 5f);

        // Header: disclosure, title, and (collapsed) the newest line so the log is never fully silent.
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##guide_log_head", new Vector2(w, headH));
        var hov = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked()) Settings.GuideLogOpen.Value = !open;
        if (hov) ImGui.SetTooltip(open ? "Collapse the agent log" : "Expand the agent log");
        var hy = min.Y + headH * 0.5f;
        var col = U(hov ? th.Text : th.TextDim);
        var tx = min.X + pad;
        if (open) dl.AddTriangleFilled(new Vector2(tx, hy - 2), new Vector2(tx + 7, hy - 2), new Vector2(tx + 3.5f, hy + 2.5f), col);
        else dl.AddTriangleFilled(new Vector2(tx + 1, hy - 3.5f), new Vector2(tx + 5.5f, hy), new Vector2(tx + 1, hy + 3.5f), col);
        var title = "AGENT LOG";
        dl.AddText(font, small, new Vector2(tx + 13, hy - small * 0.5f), col, title);
        var titleW = ImGui.CalcTextSize(title).X * (small / f);
        var count = g.log.Length.ToString();
        var countW = ImGui.CalcTextSize(count).X * (small / f);
        dl.AddText(font, small, new Vector2(max.X - pad - countW, hy - small * 0.5f), U(th.TextDim, 0.7f), count);
        if (!open)
        {
            var last = g.log[^1];
            var lx = tx + 13 + titleW + 10;
            var avail = max.X - pad - countW - 8 - lx;
            if (avail > 60)
            {
                dl.AddRectFilled(new Vector2(lx, hy - 5), new Vector2(lx + 3, hy + 5), U(GuideKindTone(last.Kind, th)), 1f);
                dl.AddText(new Vector2(lx + 8, hy - f * 0.5f), U(th.TextDim), GuideClipText(last.Text, avail - 8));
            }
        }

        if (open)
        {
            var y = min.Y + headH + 2;
            var timeW = ImGui.CalcTextSize("00:00:00").X;
            var textX = min.X + pad + timeW + 8 + 3 + 7;
            var textW = max.X - pad - textX;
            var newestFlash = (now - _guideUi.LogAt) / GuideLogFlashSec;
            for (var i = g.log.Length - shown; i < g.log.Length; i++)
            {
                var e = g.log[i];
                var idx = i - (g.log.Length - shown);
                var age = shown > 1 ? (float)idx / (shown - 1) : 1f;     // 0 oldest .. 1 newest
                var alpha = 0.55f + 0.45f * age;   // floor keeps the oldest line legible over bright game UI
                var tone = GuideKindTone(e.Kind, th);
                var rowMin = new Vector2(min.X + 4, y);
                var rowMax = new Vector2(max.X - 4, y + rowH);
                if (i == g.log.Length - 1 && newestFlash < 1)
                    dl.AddRectFilled(rowMin, rowMax, U(tone, 0.22f * (float)(1 - newestFlash)), 3f);

                ImGui.SetCursorScreenPos(rowMin);
                ImGui.InvisibleButton("##guide_log_" + i, rowMax - rowMin);
                var rowHov = ImGui.IsItemHovered();

                dl.AddText(new Vector2(min.X + pad, y + 2), U(th.TextDim, alpha * 0.9f), e.At.ToLocalTime().ToString("HH:mm:ss"));
                var bx = min.X + pad + timeW + 8;
                dl.AddRectFilled(new Vector2(bx, y + 3), new Vector2(bx + 3, y + rowH - 3), U(tone, alpha), 1f);
                var textCol = e.Kind == "agent" ? th.Text : tone;
                var clipped = GuideClipText(e.Text, textW);
                dl.AddText(new Vector2(textX, y + 2), U(textCol, alpha), clipped);
                if (rowHov && !ReferenceEquals(clipped, e.Text)) ImGui.SetTooltip(e.Text);
                y += rowH;
            }
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, h + GuideEdge));
    }

    private static Vector4 GuideKindTone(string kind, PanelTheme th) => kind switch
    {
        "step" => ToneAccent,
        "result" => ToneOk,
        "warn" => ToneWarn,
        _ => th.TextDim,
    };

    // ── Text helpers ─────────────────────────────────────────────────

    /// <summary>Clips to one line of <paramref name="maxW"/> px with a ".." tail. Returns the same instance when it fits.</summary>
    private static string GuideClipText(string s, float maxW)
    {
        if (ImGui.CalcTextSize(s).X <= maxW) return s;
        var tail = ImGui.CalcTextSize("..").X;
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(s[..mid]).X + tail <= maxW) lo = mid; else hi = mid - 1;
        }
        return lo <= 0 ? ".." : s[..lo].TrimEnd() + "..";
    }

    private static string GuideElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }
}
