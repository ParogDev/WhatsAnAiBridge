using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace WhatsAnAiBridge;

using GuideSnap = (string? title, string? instruction, int? step, int? steps, string status, string? detail,
    DateTime updatedAt, DateTime? instructionSince, int rev, WhatsAnAiBridge.GuideLogEntry[] log);
using FlowSnap = (string? title, (string label, string state)[] steps, string? showing, string status, int rev);

/// <summary>
/// In-HUD agent guide panel (ImGui): the agent's current instruction for the user as a sticky card. One surface over
/// the state in AgentGuide.cs (GuideSnapshot, taken once per frame; the only write is GuideDismiss). Loud only while
/// the user must act (status waiting, and failed: the step needs redoing), quiet for progress and results, hidden
/// when there is nothing to say.
/// The agent's log is not a panel: each new line is a small dark toast under the card for ~3 s (no window, no
/// input), and the full recent log appears as a selectable sheet only while the user HOLDS SHIFT.
/// A guided flow (GuideFlow.cs) adds a checklist of its steps to the card, re-read every frame from FlowSnapshot():
/// done steps get a check, the current one the accent badge, later ones a dim ring; a step going back animates out.
/// Steps an agent QUEUED (ExperimentQueue.cs) get their own calm card under the live one: what to do, what will be
/// recorded, and a Start button - nothing is captured until the user presses it.
/// Game-agnostic: ImGui only; nothing here touches ExileCore* directly (Input comes from the per-game GlobalUsings).
/// </summary>
public partial class WhatsAnAiBridge
{
    private const float GuideWidth = 520f;   // fits the gap between the stash (x 0..665) and the inventory at 1080p
    private const float GuideEdge = 4f;      // inset so the pulse glow is not clipped by the window rect
    private const double GuideQuietHideSec = 120, GuideCapturedLoudSec = 4, GuideFlashSec = 0.9;
    // Toasts: a line shows for ToastSec, the last ToastFadeSec of it fading; at most ToastMax at once.
    private const double GuideToastSec = 3.0, GuideToastFadeSec = 0.6, GuideToastInSec = 0.15;
    private const int GuideToastMax = 3;
    // Log sheet: Shift must be held for HoldSec before it opens (a tap in combat does nothing); it fades on release.
    private const double GuideSheetHoldSec = 0.2, GuideSheetInSec = 0.15, GuideSheetOutSec = 0.2;
    private const int GuideSheetLines = 20, GuideSheetRowsMax = 24;
    // Flow plan: a finished flow keeps its "all done" line on the card for this long, then the card is as usual.
    // Rows slide to their new place over SlideSec when the plan re-orders; a step coming back is marked for UndoSec.
    private const double GuideFlowDoneShowSec = 3.0, GuideFlowSlideSec = 0.28, GuideFlowUndoSec = 0.6, GuideFlowPopSec = 0.3;
    private const int GuideFlowNextMax = 4;      // "then" rows under the current action; the rest is "+N more"
    private const float GuideRailW = 24f;        // the badge column left of the instruction and the plan rows

    /// <summary>Panel-local state: what the panel last saw, for the arrival and change animations.</summary>
    private sealed class GuideUiState
    {
        public int SeenRev = -1;
        public DateTime? SeenSince;
        public string SeenStatus = "";
        public double ArrivedAt = -1e9;    // ImGui time the current instruction appeared
        public double StatusAt = -1e9;     // ImGui time the status last changed
        public string? SeenQueueTop;       // id of the queued step at the front last frame
        public double QueueArrivedAt = -1e9; // ImGui time a new step reached the front of the queue
        public bool QueueWatchOpen;        // the "Will record" line expanded to the exact watch specs
        // Where the panel window is, so toasts and the log sheet (no window / their own window) sit under it.
        public Vector2 WinPos = new(float.NaN, float.NaN);
        public float WinH;                 // 0 while the window is not drawn
        // Shift-held log sheet.
        public double ShiftDownAt = -1e9;  // ImGui time Shift went down (sheet opens HoldSec later)
        public double ShiftUpAt = -1e9;    // ImGui time Shift was released while the sheet was open
        public bool SheetOpen;
        // Flow plan: the plan last frame and when it changed (rows slide from their old place), how many steps were
        // done (a drop = the user went back: the done line is marked), and when the flow ended.
        public int FlowRev = -1;
        public string FlowStatus = "idle";
        public string[] FlowPlan = [];
        public string[] FlowPlanPrev = [];
        public double FlowPlanAt = -1e9;
        public int FlowDoneCount;
        public double FlowDoneAt = -1e9;
        public bool FlowUndo;              // the last done-count change was a step coming back
        public double FlowEndedAt = -1e9;  // ImGui time the flow left "running"
        public string? LastError;
    }

    /// <summary>
    /// The queue as the panel sees it this frame: the next step to offer, the one recording, how many wait, how many
    /// chained steps of the same experiment follow Next (they start by themselves), and whether Next itself will
    /// start by itself once the running step is captured.
    /// </summary>
    private readonly record struct QueueView(QueuedStep? Next, QueuedStep? Running, int QueuedCount, int ChainAfterNext, bool NextAuto);

    private static QueueView QueueViewOf(List<QueuedStep> open)
    {
        var next = open.FirstOrDefault(s => s.Status == "queued");
        var running = open.FirstOrDefault(s => s.Status == "running");
        var chain = 0;
        if (next != null)
            foreach (var s in open.Where(s => s.Status == "queued").SkipWhile(s => s != next).Skip(1))
            {
                if (!s.Chain || s.Experiment != next.Experiment) break;
                chain++;
            }
        var auto = running != null && next is { Chain: true } && next.Experiment == running.Experiment;
        return new QueueView(next, running, open.Count(s => s.Status == "queued"), chain, auto);
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

    /// <summary>
    /// The guided flow as the card shows it this frame: whether the flow section is drawn at all (running, or done
    /// within the last seconds), the live plan (first entry = what to do now; empty once the flow ended), the labels
    /// of the steps already done, and how many steps there are.
    /// </summary>
    private readonly record struct FlowView(bool Show, bool Running, string[] Plan, string[] DoneLabels, int StepCount);

    private void DrawGuidePanel()
    {
        if (!Settings.ShowAgentGuide.Value) return;

        var g = GuideSnapshot();
        // Once per frame: it takes the queue lock. Open steps only, in queue order.
        var q = QueueViewOf(QueueSnapshot());
        var fl = FlowSnapshot();
        var plan = fl.status == "running" ? FlowPlan() : [];
        var now = ImGui.GetTime();
        var utc = DateTime.UtcNow;
        ObserveGuide(g, q, now);
        ObserveFlow(fl, plan, now);
        var th = PanelTheme.Current();
        var fv = new FlowView(
            fl.status == "running" || (fl.status == "done" && now - _guideUi.FlowEndedAt < GuideFlowDoneShowSec),
            fl.status == "running", plan, fl.steps.Where(s => s.state == "done").Select(s => s.label).ToArray(), fl.steps.Length);

        // The log lives outside the window: toasts (no input) or, while Shift is held, its own selectable sheet.
        // Both anchor under the panel's last known rectangle, so they show even while the panel is hidden.
        var io = ImGui.GetIO();
        var defaultPos = new Vector2(MathF.Round((io.DisplaySize.X - GuideWidth) * 0.5f), 84);
        if (float.IsNaN(_guideUi.WinPos.X)) _guideUi.WinPos = defaultPos;
        try
        {
            var anchor = new Vector2(_guideUi.WinPos.X, _guideUi.WinPos.Y + _guideUi.WinH + (_guideUi.WinH > 0 ? 2 : 0));
            if (!DrawGuideLogSheet(g, th, now, anchor)) DrawGuideToasts(g, th, now, utc, anchor);
        }
        catch (Exception ex) { GuideReport(ex); }

        // Visibility: a card exists while there is an instruction or a non-idle status. The panel hides itself once
        // everything is quiet (idle / captured / done / info) and nothing happened for two minutes; waiting, failed
        // and progress states stay until the agent or the user clears them. It comes back on the next rev change.
        // A queued step keeps the panel up too (calmly): the user may come back hours later and must find it.
        var hasCard = g.instruction != null || g.status != "idle";
        var needsUser = g.status is "waiting" or "failed" or "detected" or "settling";
        var quietFor = (utc - g.updatedAt).TotalSeconds;
        var hide = (!needsUser && q.Next == null && quietFor > GuideQuietHideSec) || (!hasCard && q.Next == null);
        if (hide) { _guideUi.WinH = 0; return; }

        // Default: top centre, under the skill bar; clear of the stash (left) and the inventory (right) at 1080p.
        ImGui.SetNextWindowPos(defaultPos, ImGuiCond.FirstUseEver);
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
            _guideUi.WinPos = ImGui.GetWindowPos();
            _guideUi.WinH = shown ? ImGui.GetWindowSize().Y : 0;
            if (shown) DrawGuideBody(g, q, fv, hasCard, needsUser, th, now, utc);
        }
        catch (Exception ex) { GuideReport(ex); }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(3);
        }
    }

    private void GuideReport(Exception ex)
    {
        if (_guideUi.LastError == ex.Message) return;
        _guideUi.LastError = ex.Message;
        LogError($"[GuidePanel] {ex}");
    }

    /// <summary>Notice what changed since last frame, so arrivals can flash.</summary>
    private void ObserveGuide(in GuideSnap g, in QueueView q, double now)
    {
        var u = _guideUi;
        var first = u.SeenRev < 0;
        // The queue has no rev of its own: watch the id at its front. A new front step gets a soft flash.
        if (q.Next?.Id != u.SeenQueueTop)
        {
            if (!first && q.Next != null) u.QueueArrivedAt = now;
            u.SeenQueueTop = q.Next?.Id;
            u.QueueWatchOpen = false;
        }
        if (g.rev == u.SeenRev) return;
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
    }

    /// <summary>
    /// Notice the flow re-planning: a changed plan slides its rows, a drop in done steps (the user went back, e.g.
    /// closed a dialog) marks the done line, and leaving "running" starts the short "all done" display.
    /// </summary>
    private void ObserveFlow(in FlowSnap fl, string[] plan, double now)
    {
        var u = _guideUi;
        var first = u.FlowRev < 0;
        var statusChanged = fl.status != u.FlowStatus;
        if (statusChanged)
        {
            if (!first && u.FlowStatus == "running") u.FlowEndedAt = now;
            u.FlowStatus = fl.status;
        }
        var doneCount = 0;
        foreach (var s in fl.steps) if (s.state == "done") doneCount++;
        if (doneCount != u.FlowDoneCount)
        {
            u.FlowUndo = !first && !statusChanged && fl.status == "running" && doneCount < u.FlowDoneCount;
            u.FlowDoneAt = first ? -1e9 : now;
            u.FlowDoneCount = doneCount;
        }
        if (!plan.SequenceEqual(u.FlowPlan))
        {
            u.FlowPlanPrev = u.FlowPlan;
            u.FlowPlan = plan;
            u.FlowPlanAt = first || statusChanged ? -1e9 : now;   // a new flow's rows appear with the card, no slide
        }
        u.FlowRev = fl.rev;
    }

    // ── Body ─────────────────────────────────────────────────────────

    private void DrawGuideBody(in GuideSnap g, in QueueView q, in FlowView fv, bool hasCard, bool needsUser, PanelTheme th, double now, DateTime utc)
    {
        if (hasCard)
        {
            DrawGuideCard(g, q.Running, fv, th, now, utc);
            ImGui.Dummy(new Vector2(GuideWidth, 5));
        }
        if (q.Next != null)
        {
            // The live card wins while the user has something to do (an agent's direct instruction, a failed step to
            // redo, or the queued step that is recording): the queue then shrinks to a one-line "next" strip.
            if (needsUser || q.Running != null) DrawQueueStrip(q, th);
            else DrawQueueCard(q, th, now, utc);
            ImGui.Dummy(new Vector2(GuideWidth, 5));
        }
    }

    private void DrawGuideCard(in GuideSnap g, QueuedStep? running, in FlowView fv, PanelTheme th, double now, DateTime utc)
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

        // Measure first: the card background needs the full height. A flow adds a badge rail left of the text: the
        // done line above the instruction, the "now" badge on the instruction, the next actions as rows under it.
        var flow = fv.Show && !compact;
        var rail = flow ? GuideRailW : 0f;
        var textW = innerW - rail;
        var rowH = f + 6;
        var bodyH = 0f;
        string? sub = GuideSubline(g, now);
        Vector2 instrSize = default, subSize = default, detailSize = default;
        string? doneLine = null;
        var nextRows = Array.Empty<string>();
        var moreRows = 0;
        if (flow)
        {
            if (!fv.Running) doneLine = fv.StepCount == 1 ? "Done" : $"All {fv.StepCount} steps done";
            else if (fv.DoneLabels.Length > 0) doneLine = "Done: " + string.Join(", ", fv.DoneLabels);
            if (doneLine != null) bodyH += small + 6;
            if (fv.Plan.Length > 1)
            {
                nextRows = fv.Plan.Skip(1).Take(GuideFlowNextMax).ToArray();
                moreRows = fv.Plan.Length - 1 - nextRows.Length;
            }
        }
        if (!compact && g.instruction != null)
        {
            instrSize = font.CalcTextSizeA(loud ? big : f, float.MaxValue, textW, g.instruction);
            bodyH += instrSize.Y + 4;
        }
        if (!compact && sub != null)
        {
            subSize = font.CalcTextSizeA(f, float.MaxValue, textW, sub);
            bodyH += subSize.Y + 4;
        }
        if (!compact && g.detail != null && g.detail != sub)
        {
            detailSize = font.CalcTextSizeA(f, float.MaxValue, textW, g.detail);
            bodyH += detailSize.Y + 4;
        }
        if (nextRows.Length > 0) bodyH += 2 + nextRows.Length * rowH + (moreRows > 0 ? small + 4 : 0);
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

        // Right side, laid out from the edge inwards. While a queued step records, the dismiss x gives way to Stop:
        // hiding the card would leave the recorder running with nothing to stop it from.
        var rx = max.X - pad;
        var showDismiss = g.instruction != null || g.status != "idle";
        if (running != null)
        {
            const string label = "Stop";
            var tw = ImGui.CalcTextSize(label).X * (small / f);
            var pillW = tw + 27;
            var pillH = small + 8;
            var pmin = new Vector2(rx - pillW, hy - pillH * 0.5f);
            ImGui.SetCursorScreenPos(pmin);
            ImGui.InvisibleButton("##guide_stop", new Vector2(pillW, pillH));
            var hov = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) QueueCancel(running.Id);
            if (hov) ImGui.SetTooltip("Stop recording this step. It is marked cancelled; an agent can queue it again.");
            var sc = hov ? ToneBad : th.TextDim;
            dl.AddRectFilled(pmin, pmin + new Vector2(pillW, pillH), hov ? U(ToneBad, 0.22f) : U(th.Tile), pillH * 0.5f);
            dl.AddRect(pmin, pmin + new Vector2(pillW, pillH), U(hov ? ToneBad : th.Border, 0.7f), pillH * 0.5f);
            dl.AddRectFilled(new Vector2(pmin.X + 8, hy - 3.5f), new Vector2(pmin.X + 15, hy + 3.5f), U(sc), 1f);
            dl.AddText(font, small, new Vector2(pmin.X + 19, hy - small * 0.5f), U(sc), label);
            rx -= pillW + 8;
        }
        else if (showDismiss)
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
            var tx = bx + rail;                 // text column (the rail holds the badges)
            var cx = bx + GuideRailW * 0.5f - 2; // badge centre in the rail
            if (doneLine != null)
            {
                // Steps already done, collapsed to one dim line. A step coming back (the user went back) marks it
                // for a moment: neutral wash, text at full strength, the check greyed - calm, but legible.
                var undo = _guideUi.FlowUndo ? (float)((now - _guideUi.FlowDoneAt) / GuideFlowUndoSec) : 1f;
                var mark = undo < 1 ? 1f - undo : 0f;
                var ly = y + (small + 2) * 0.5f;
                if (mark > 0) dl.AddRectFilled(new Vector2(bx - 4, y - 2), new Vector2(max.X - pad + 4, y + small + 4), U(ToneNeutral, 0.14f * mark), 3f);
                var pop = fv.Running ? 1f : HlEase((now - _guideUi.FlowEndedAt) / GuideFlowPopSec);
                DrawFlowCheck(dl, new Vector2(cx, ly), 6f, mark > 0 ? ToneNeutral : ToneOk, pop, fv.Running ? 0.75f : 1f);
                var dcol = mark > 0 ? U(th.Text, 0.6f + 0.4f * mark) : U(th.TextDim);
                dl.AddText(font, small, new Vector2(tx, ly - small * 0.5f), dcol, GuideClipText(doneLine, textW));
                y += small + 6;
            }
            var instrY = y;
            if (g.instruction != null)
            {
                var col = g.status is "captured" or "done" or "detected" or "settling" ? U(th.Text, 0.85f) : U(th.Text);
                if (flow && fv.Running)
                {
                    // The "now" badge: the same filled accent disc with the step number as the in-game highlight badge.
                    var lineH = loud ? big : f;
                    DrawFlowBadge(dl, new Vector2(cx, y + lineH * 0.5f), 7.5f, 0, g.step?.ToString(), 1f, th);
                }
                dl.AddText(font, loud ? big : f, new Vector2(tx, y), col, g.instruction, textW);
                y += instrSize.Y + 4;
            }
            if (sub != null)
            {
                dl.AddText(font, f, new Vector2(tx, y), U(look.Tone, 0.95f), sub, textW);
                y += subSize.Y + 4;
            }
            if (g.detail != null && g.detail != sub)
            {
                dl.AddText(font, f, new Vector2(tx, y), U(th.TextDim), g.detail, textW);
                y += detailSize.Y + 4;
            }
            if (nextRows.Length > 0)
            {
                // The rest of the plan, best order from here, as dim rows on the rail. When the plan re-orders a row
                // slides from where its text was last frame; a row new to the plan fades in.
                y += 2;
                var listY = y;
                var k = HlEase((now - _guideUi.FlowPlanAt) / GuideFlowSlideSec);
                float YOf(int i) => i <= 0 ? instrY : listY + Math.Min(i - 1, nextRows.Length) * rowH;
                var railTop = instrY + (loud ? big : f) * 0.5f + 9;
                var railBottom = listY + (nextRows.Length - 1) * rowH + rowH * 0.5f - 7;
                if (railBottom > railTop) dl.AddLine(new Vector2(cx, railTop), new Vector2(cx, railBottom), U(ToneNeutral, 0.3f), 1f);
                for (var i = 0; i < nextRows.Length; i++)
                {
                    var label = nextRows[i];
                    var planIdx = i + 1;
                    var prevIdx = Array.IndexOf(_guideUi.FlowPlanPrev, label);
                    var ry = YOf(planIdx);
                    var alpha = 1f;
                    if (k < 1)
                    {
                        if (prevIdx >= 0) ry = YOf(prevIdx) + (ry - YOf(prevIdx)) * k;
                        else alpha = k;
                    }
                    var rcy = ry + rowH * 0.5f;
                    DrawFlowBadge(dl, new Vector2(cx, rcy), 5f, 1, null, alpha, th);
                    dl.AddText(new Vector2(tx, rcy - f * 0.5f), U(th.TextDim, 0.9f * alpha), GuideClipText(label, textW));
                }
                y += nextRows.Length * rowH;
                if (moreRows > 0)
                {
                    dl.AddText(font, small, new Vector2(tx, y + 1), U(th.TextDim, 0.7f), $"+{moreRows} more");
                    y += small + 4;
                }
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

    // ── Flow glyphs ──────────────────────────────────────────────────

    /// <summary>
    /// The plan's rail badge, matching the in-game highlight badge: state 0 = the action to take now (filled accent
    /// disc, the step number in ink), 1 = a later action (dim neutral ring, no number).
    /// </summary>
    private static void DrawFlowBadge(ImDrawListPtr dl, Vector2 c, float r, int state, string? n, float alpha, PanelTheme th)
    {
        if (state == 0)
        {
            dl.AddCircleFilled(c, r, U(ToneAccent, alpha), 20);
            if (n != null)
            {
                var f = ImGui.GetFontSize();
                var small = f * 0.85f;
                var tw = ImGui.CalcTextSize(n).X * (small / f);
                var tp = new Vector2(c.X - tw * 0.5f, c.Y - small * 0.5f);
                var ink = U(new Vector4(0.05f, 0.08f, 0.06f, 1f), alpha);
                dl.AddText(ImGui.GetFont(), small, tp, ink, n);
                dl.AddText(ImGui.GetFont(), small, tp + new Vector2(0.6f, 0), ink, n);   // faux bold
            }
        }
        else
        {
            dl.AddCircleFilled(c, r, U(th.Card, 0.9f * alpha), 16);
            dl.AddCircle(c, r, U(ToneNeutral, 0.7f * alpha), 16, 1.2f);
        }
    }

    /// <summary>A disc with a check, as on a done highlight step. pop 0..1 scales the check in.</summary>
    private static void DrawFlowCheck(ImDrawListPtr dl, Vector2 c, float r, Vector4 tone, float pop, float alpha)
    {
        dl.AddCircleFilled(c, r, U(tone, 0.85f * alpha), 20);
        var s = (0.4f + 0.6f * pop) * (r / 7f);
        var ck = U(new Vector4(0.05f, 0.08f, 0.06f, 1f), alpha);
        dl.AddLine(new Vector2(c.X - 3.6f * s, c.Y + 0.2f * s), new Vector2(c.X - 1f * s, c.Y + 2.8f * s), ck, 1.6f);
        dl.AddLine(new Vector2(c.X - 1f * s, c.Y + 2.8f * s), new Vector2(c.X + 3.8f * s, c.Y - 2.8f * s), ck, 1.6f);
    }

    // ── Log: toasts ──────────────────────────────────────────────────

    /// <summary>
    /// Each new log line as a small dark toast under the panel for ~3 s: newest on top (older ones slide down to
    /// make room), the last 0.6 s fading, at most three at once. Drawn on the background draw list: no window, so
    /// the mouse passes straight through to the game. Nothing stays.
    /// </summary>
    private void DrawGuideToasts(in GuideSnap g, PanelTheme th, double now, DateTime utc, Vector2 anchor)
    {
        if (g.log.Length == 0) return;
        var dl = ImGui.GetBackgroundDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var rowH = small + 8;
        const float gap = 3f, pad = 8f;
        var maxW = GuideWidth - GuideEdge * 2;
        var x0 = anchor.X + GuideEdge;
        // The newest toast's slide-in pushes the older ones down with it.
        var newestAge = (utc - g.log[^1].At).TotalSeconds;
        var push = (float)(1 - HlEase(newestAge / GuideToastInSec));
        var y = anchor.Y + GuideEdge;
        var shown = 0;
        for (var i = g.log.Length - 1; i >= 0 && shown < GuideToastMax; i--)
        {
            var e = g.log[i];
            var age = (utc - e.At).TotalSeconds;
            if (age >= GuideToastSec) break;
            var alpha = (float)Math.Clamp((GuideToastSec - age) / GuideToastFadeSec, 0, 1) * HlEase(age / GuideToastInSec);
            if (shown > 0) y -= push * (rowH + gap);   // older rows catch up with the push from above
            var tone = GuideKindTone(e.Kind, th);
            var tail = shown == 0 ? "shift: log" : null;
            var tailW = tail != null ? ImGui.CalcTextSize(tail).X * (small / f) + 10 : 0f;
            var textMax = maxW - pad * 2 - 3 - 6 - tailW;
            var text = GuideClipText(e.Text, textMax * (f / small)); // clip measured at the small size
            var textW = ImGui.CalcTextSize(text).X * (small / f);
            var w = pad + 3 + 6 + textW + tailW + pad;
            var min = new Vector2(x0, y);
            var max = new Vector2(x0 + w, y + rowH);
            // Fixed dark ink, low contrast: a toast is for the corner of the eye, whatever the HUD theme.
            dl.AddRectFilled(min, max, U(GuideToastInk, 0.8f * alpha), 4f);
            dl.AddRectFilled(new Vector2(min.X + pad, min.Y + 4), new Vector2(min.X + pad + 3, max.Y - 4), U(tone, 0.8f * alpha), 1f);
            var tcol = e.Kind == "agent" ? U(th.Text, 0.78f * alpha) : U(tone, 0.9f * alpha);
            dl.AddText(font, small, new Vector2(min.X + pad + 9, min.Y + 4), tcol, text);
            if (tail != null) dl.AddText(font, small, new Vector2(max.X - pad - tailW + 10, min.Y + 4), U(th.TextDim, 0.45f * alpha), tail);
            y += rowH + gap;
            shown++;
        }
    }

    private static readonly Vector4 GuideToastInk = new(0.07f, 0.07f, 0.09f, 1f);

    private static Vector4 GuideKindTone(string kind, PanelTheme th) => kind switch
    {
        "step" => ToneAccent,
        "result" => ToneOk,
        "warn" => ToneWarn,
        _ => th.TextDim,
    };

    // ── Log: Shift-held sheet ────────────────────────────────────────

    /// <summary>
    /// While Shift is held (0.2 s, so a tap in combat does nothing) the recent log opens as a sheet under the panel:
    /// the last 20 lines, newest at the bottom, in a read-only text field so lines can be selected and copied with
    /// ctrl+c, plus a Copy all pill. It fades out on release and takes no input while fading. Key state comes from
    /// the HUD's Input (GetKeyState), which sees Shift while the game has focus - ImGui only gets keys sent to the
    /// overlay window. Returns true while the sheet is on screen (toasts then stay quiet).
    /// </summary>
    private bool DrawGuideLogSheet(in GuideSnap g, PanelTheme th, double now, Vector2 anchor)
    {
        var u = _guideUi;
        var shift = g.log.Length > 0 && Input.GetKeyState(System.Windows.Forms.Keys.ShiftKey);
        if (shift) { if (u.ShiftDownAt < 0) u.ShiftDownAt = now; }
        else
        {
            if (u.SheetOpen && u.ShiftDownAt >= 0) u.ShiftUpAt = now;
            u.ShiftDownAt = -1;
        }
        var held = shift && now - u.ShiftDownAt >= GuideSheetHoldSec;
        if (held) u.SheetOpen = true;
        var fading = !held && u.SheetOpen && now - u.ShiftUpAt < GuideSheetOutSec;
        if (!held && !fading) { u.SheetOpen = false; return false; }

        var alpha = held ? HlEase((now - u.ShiftDownAt - GuideSheetHoldSec) / GuideSheetInSec) : 1f - (float)((now - u.ShiftUpAt) / GuideSheetOutSec);
        if (held) alpha = MathF.Max(alpha, 0.35f);   // visible from the first frame, then eases in
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        const float pad = 10f;
        var w = GuideWidth - GuideEdge * 2;
        var innerW = w - pad * 2;
        var headH = f + 12;
        var lineH = ImGui.GetTextLineHeight();

        // Text: "HH:mm:ss  kind    text", long lines wrapped under their text column, newest last. Rows are counted
        // from the newest so the sheet never needs to scroll: the oldest lines give way.
        var timeCol = "00:00:00  ";
        var kindW = "result  ".Length;
        var indent = new string(' ', timeCol.Length + kindW);
        var indentW = ImGui.CalcTextSize(indent).X;
        var lines = new List<string>();
        var rows = 0;
        for (var i = g.log.Length - 1; i >= 0 && lines.Count < GuideSheetLines; i--)
        {
            var e = g.log[i];
            var kind = e.Kind == "agent" ? "" : e.Kind;
            var head = e.At.ToLocalTime().ToString("HH:mm:ss") + "  " + kind.PadRight(kindW);
            var wrapped = GuideWrapLines(e.Text, innerW - indentW - 8);
            if (rows + wrapped.Count > GuideSheetRowsMax && lines.Count > 0) break;
            var sb = new System.Text.StringBuilder(head);
            for (var k = 0; k < wrapped.Count; k++) { if (k > 0) sb.Append('\n').Append(indent); sb.Append(wrapped[k]); }
            lines.Add(sb.ToString());
            rows += wrapped.Count;
        }
        lines.Reverse();
        var text = string.Join("\n", lines);
        var boxH = rows * lineH + 10;
        var h = headH + boxH + pad;

        ImGui.SetNextWindowPos(new Vector2(anchor.X + GuideEdge, anchor.Y + GuideEdge), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(w, h), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(alpha, 0.02f, 1f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, U(th.Card, 0.96f));
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!held) flags |= ImGuiWindowFlags.NoInputs;
        var shown = ImGui.Begin("Agent Log###bridge_guide_log", flags);
        try
        {
            if (shown)
            {
                var dl = ImGui.GetWindowDrawList();
                var min = ImGui.GetWindowPos();
                var max = min + new Vector2(w, h);
                dl.AddRect(min, max, U(th.Border, 0.6f), 6f);
                // Header: title, count, how to keep it, Copy all.
                var hy = min.Y + headH * 0.5f;
                var x = min.X + pad;
                dl.AddRectFilled(new Vector2(x, hy - 5), new Vector2(x + 3, hy + 5), U(ToneNeutral, 0.9f), 1f);
                const string title = "AGENT LOG";
                dl.AddText(font, small, new Vector2(x + 9, hy - small * 0.5f), U(th.TextDim), title);
                x += 9 + ImGui.CalcTextSize(title).X * (small / f) + 10;
                var copyW = ImGui.CalcTextSize("Copy all").X + 24;
                var hint = $"last {lines.Count} of {g.log.Length} - hold Shift, select, ctrl+c";
                var hintW = max.X - pad - copyW - 10 - x;
                if (hintW > 40) dl.AddText(font, small, new Vector2(x, hy - small * 0.5f), U(th.TextDim, 0.7f), GuideClipText(hint, hintW * (f / small)));
                var btnH = small + 8;
                QueuePillButton(dl, "##guide_log_copy", new Vector2(max.X - pad - copyW, hy - btnH * 0.5f), btnH, "Copy all", false, th,
                    out var copy, "Copy these lines to the clipboard");
                if (copy) ImGui.SetClipboardText(string.Join("\n", lines));

                // The text: read-only, so it is selectable; frame transparent so it reads as part of the card.
                ImGui.SetCursorScreenPos(new Vector2(min.X + pad, min.Y + headH));
                ImGui.PushStyleColor(ImGuiCol.FrameBg, 0);
                ImGui.PushStyleColor(ImGuiCol.Text, U(th.Text, 0.9f));
                ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, U(ToneAccent, 0.35f));
                ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, 0);
                ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, U(th.Text, 0.18f));
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 4));
                ImGui.InputTextMultiline("##guide_log_text", ref text, (uint)text.Length + 1, new Vector2(innerW, boxH),
                    ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.NoHorizontalScroll);
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(5);
            }
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(4);
        }
        return true;
    }

    /// <summary>Word-wraps to lines of at most <paramref name="maxW"/> px (a word longer than that is cut).</summary>
    private static List<string> GuideWrapLines(string s, float maxW)
    {
        var lines = new List<string>();
        if (maxW < 20 || ImGui.CalcTextSize(s).X <= maxW) { lines.Add(s); return lines; }
        var words = s.Split(' ');
        var cur = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            var candidate = cur.Length == 0 ? word : cur + " " + word;
            if (ImGui.CalcTextSize(candidate).X <= maxW) { cur.Clear(); cur.Append(candidate); continue; }
            if (cur.Length > 0) { lines.Add(cur.ToString()); cur.Clear(); }
            var rest = word;
            while (ImGui.CalcTextSize(rest).X > maxW && rest.Length > 1)
            {
                int lo = 1, hi = rest.Length - 1;
                while (lo < hi) { var mid = (lo + hi + 1) / 2; if (ImGui.CalcTextSize(rest[..mid]).X <= maxW) lo = mid; else hi = mid - 1; }
                lines.Add(rest[..lo]);
                rest = rest[lo..];
            }
            cur.Append(rest);
        }
        if (cur.Length > 0) lines.Add(cur.ToString());
        return lines;
    }

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
        if (t.TotalDays < 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }

    // ── Queue ────────────────────────────────────────────────────────

    /// <summary>
    /// The step at the front of the queue, offered to the user as two numbered steps: 1 is the Start button itself,
    /// 2 is the instruction, dimmed and prefixed "Then:" so it reads as what comes AFTER Start. (The first live run
    /// failed because the big instruction read as "do this now": the user did it, then pressed Start, and the baseline
    /// already held the change.) Calm on purpose: neutral frame, no pulse, one accent control; nothing records before Start.
    /// </summary>
    private void DrawQueueCard(in QueueView q, PanelTheme th, double now, DateTime utc)
    {
        var next = q.Next!;
        var dl = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var tone = ToneNeutral;
        var p = ImGui.GetCursorScreenPos();
        var x0 = p.X + GuideEdge;
        var w = GuideWidth - GuideEdge * 2;
        const float pad = 12f;
        var innerW = w - pad * 2;
        var headH = f + 12;
        var btnH = f + 10;
        const float specIndent = 14f;
        const float stepIndent = 26f;        // room for the numbered badge
        var stepW = innerW - stepIndent;

        // Text, then measure: the card background needs the full height.
        var meta = QueueMetaLine(next, q.ChainAfterNext, utc);
        var watchText = "Will record: " + string.Join(", ", next.Watch.Select(QueueWatchSummary));
        const string startNote = "first - records from then on";
        var thenText = "Then: " + next.Instruction;
        const string thenSub = "Only once the card says DO THIS NOW.";
        var thenSize = font.CalcTextSizeA(f, float.MaxValue, stepW, thenText);
        var thenSubSize = font.CalcTextSizeA(small, float.MaxValue, stepW, thenSub);
        var noteSize = next.Note != null ? font.CalcTextSizeA(f, float.MaxValue, innerW, next.Note) : default;
        var specSizes = _guideUi.QueueWatchOpen
            ? next.Watch.Select(s => font.CalcTextSizeA(small, float.MaxValue, innerW - specIndent, s)).ToArray() : [];
        var bodyH = btnH + 8
                    + thenSize.Y + 2 + thenSubSize.Y + 8
                    + (next.Note != null ? noteSize.Y + 4 : 0)
                    + small + 6
                    + f + 4 + specSizes.Sum(s => s.Y + 2);
        var h = headH + bodyH + 8;
        var min = new Vector2(x0, p.Y + GuideEdge);
        var max = new Vector2(x0 + w, p.Y + GuideEdge + h);

        dl.AddRectFilled(min, max, U(th.Card, 0.94f), 6f);
        var flashT = (now - _guideUi.QueueArrivedAt) / GuideFlashSec;
        if (flashT < 1) dl.AddRectFilled(min, max, U(tone, 0.28f * (float)(1 - flashT)), 6f);
        dl.AddRect(min, max, U(tone, 0.55f), 6f, ImDrawFlags.None, 1f);

        // Header: clock glyph + label, the experiment, then (right) how many more wait.
        var hy = min.Y + headH * 0.5f;
        var x = min.X + pad;
        DrawQueueGlyph(dl, new Vector2(x + 6, hy), U(tone));
        x += 18;
        const string label = "QUEUED FOR YOU";
        dl.AddText(font, small, new Vector2(x, hy - small * 0.5f), U(tone), label);
        x += ImGui.CalcTextSize(label).X * (small / f) + 10;
        var rx = max.X - pad;
        if (q.QueuedCount > 1)
        {
            var t = $"+{q.QueuedCount - 1} more";
            var tw = ImGui.CalcTextSize(t).X * (small / f);
            var pillW = tw + 12;
            var pillH = small + 6;
            var pmin = new Vector2(rx - pillW, hy - pillH * 0.5f);
            dl.AddRectFilled(pmin, pmin + new Vector2(pillW, pillH), U(th.Tile), pillH * 0.5f);
            dl.AddRect(pmin, pmin + new Vector2(pillW, pillH), U(th.Border, 0.6f), pillH * 0.5f);
            dl.AddText(font, small, new Vector2(pmin.X + 6, hy - small * 0.5f), U(th.TextDim), t);
            rx -= pillW + 10;
        }
        var title = next.Title ?? $"{next.Experiment}: {next.Label}";
        if (rx - x > 40) dl.AddText(new Vector2(x, hy - f * 0.5f), U(th.TextDim), GuideClipText(title, rx - x));

        // Body. Step 1: the Start button, with Skip at the far right of the same row.
        var y = min.Y + headH + 2;
        var bx = min.X + pad;
        var sx = bx + stepIndent;
        DrawQueueBadge(dl, new Vector2(bx + 8, y + btnH * 0.5f), "1", true, th);
        var series = q.ChainAfterNext > 0 ? $" One Start runs all {q.ChainAfterNext + 1} steps in a row." : "";
        var startW = QueuePillButton(dl, "##queue_start", new Vector2(sx, y), btnH, "Start recording", true, th, out var startClick,
            "Starts recording. The card then says DO THIS NOW - do the action at that point, not before." + series);
        if (startClick)
        {
            var r = QueueStart(next.Id, "hud");
            if (r["error"] != null) LogError($"[GuidePanel] start refused: {r["error"]} {r["message"]}");
        }
        var skipW = ImGui.CalcTextSize("Skip").X + 24;
        QueuePillButton(dl, "##queue_skip", new Vector2(max.X - pad - skipW, y), btnH, "Skip", false, th, out var skipClick,
            "Skip this step. It is marked cancelled; an agent can queue it again.");
        if (skipClick) QueueCancel(next.Id);
        var noteX = sx + startW + 10;
        var noteW = max.X - pad - skipW - 10 - noteX;
        if (noteW > 40) dl.AddText(new Vector2(noteX, y + (btnH - f) * 0.5f), U(th.Text, 0.9f), GuideClipText(startNote, noteW));
        y += btnH + 8;

        // Step 2: the instruction, dimmed - it is what comes after Start, not what to do now.
        DrawQueueBadge(dl, new Vector2(bx + 8, y + f * 0.5f), "2", false, th);
        dl.AddText(font, f, new Vector2(sx, y), U(th.TextDim), thenText, stepW);
        y += thenSize.Y + 2;
        dl.AddText(font, small, new Vector2(sx, y), U(th.TextDim, 0.75f), thenSub, stepW);
        y += thenSubSize.Y + 8;

        if (next.Note != null)
        {
            dl.AddText(font, f, new Vector2(bx, y), U(th.TextDim), next.Note, innerW);
            y += noteSize.Y + 4;
        }
        dl.AddText(font, small, new Vector2(bx, y), U(th.TextDim), GuideClipText(meta, innerW));
        y += small + 6;

        // "Will record" line: a disclosure that opens the exact watch specs, for the developer who wants to know.
        var watchOpen = _guideUi.QueueWatchOpen;
        ImGui.SetCursorScreenPos(new Vector2(bx, y));
        ImGui.InvisibleButton("##queue_watch", new Vector2(innerW, f + 2));
        var watchHov = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked()) _guideUi.QueueWatchOpen = !watchOpen;
        if (watchHov) ImGui.SetTooltip(watchOpen ? "Hide the exact watch specs" : "Show the exact watch specs");
        var wc = U(watchHov ? th.Text : th.TextDim);
        var ty = y + f * 0.5f;
        if (watchOpen) dl.AddTriangleFilled(new Vector2(bx, ty - 2), new Vector2(bx + 7, ty - 2), new Vector2(bx + 3.5f, ty + 2.5f), wc);
        else dl.AddTriangleFilled(new Vector2(bx + 1, ty - 3.5f), new Vector2(bx + 5.5f, ty), new Vector2(bx + 1, ty + 3.5f), wc);
        dl.AddText(new Vector2(bx + specIndent, y), wc, GuideClipText(watchText, innerW - specIndent));
        y += f + 4;
        if (watchOpen)
            for (var i = 0; i < next.Watch.Length; i++)
            {
                dl.AddText(font, small, new Vector2(bx + specIndent, y), U(th.TextDim, 0.85f), next.Watch[i], innerW - specIndent);
                y += specSizes[i].Y + 2;
            }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, h + GuideEdge * 2));
    }

    /// <summary>16 px numbered badge: filled accent for the step to take now, neutral outline for the one after.</summary>
    private static void DrawQueueBadge(ImDrawListPtr dl, Vector2 c, string n, bool active, PanelTheme th)
    {
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var font = ImGui.GetFont();
        var tw = ImGui.CalcTextSize(n).X * (small / f);
        if (active)
        {
            dl.AddCircleFilled(c, 8f, U(ToneAccent), 20);
            dl.AddText(font, small, new Vector2(c.X - tw * 0.5f, c.Y - small * 0.5f), U(new Vector4(0.05f, 0.08f, 0.06f, 1f)), n);
        }
        else
        {
            dl.AddCircle(c, 8f, U(ToneNeutral, 0.8f), 20, 1.3f);
            dl.AddText(font, small, new Vector2(c.X - tw * 0.5f, c.Y - small * 0.5f), U(th.TextDim), n);
        }
    }

    /// <summary>One line standing in for the queue card while the live card has the user's attention.</summary>
    private void DrawQueueStrip(in QueueView q, PanelTheme th)
    {
        var next = q.Next!;
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

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##queue_strip", new Vector2(w, h));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(q.NextAuto
                ? $"Then: {next.Instruction}\nStarts by itself once the current step is captured - no Start needed. Wait for DO THIS NOW."
                : $"Queued next: {next.Instruction}\nFinish or dismiss the current step first; this card then offers Start.");

        var hy = min.Y + h * 0.5f;
        var x = min.X + pad;
        DrawQueueGlyph(dl, new Vector2(x + 6, hy), U(ToneNeutral));
        x += 18;
        var label = q.NextAuto ? "THEN" : "NEXT";
        dl.AddText(font, small, new Vector2(x, hy - small * 0.5f), U(ToneNeutral), label);
        x += ImGui.CalcTextSize(label).X * (small / f) + 10;
        var rx = max.X - pad;
        // Right: whether it starts by itself (chained), and how many wait after it.
        var tail = q.NextAuto ? "starts by itself" + (q.QueuedCount > 1 ? $", +{q.QueuedCount - 1} more" : "")
                 : q.QueuedCount > 1 ? $"+{q.QueuedCount - 1} more" : null;
        if (tail != null)
        {
            var tw = ImGui.CalcTextSize(tail).X * (small / f);
            dl.AddText(font, small, new Vector2(rx - tw, hy - small * 0.5f), U(th.TextDim, 0.8f), tail);
            rx -= tw + 10;
        }
        if (rx - x > 40) dl.AddText(new Vector2(x, hy - f * 0.5f), U(th.TextDim), GuideClipText(next.Instruction, rx - x));

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, h + GuideEdge));
    }

    /// <summary>A pill button drawn on the draw list over an InvisibleButton (takes the mouse only over itself). Returns its width.</summary>
    private static float QueuePillButton(ImDrawListPtr dl, string id, Vector2 pos, float h, string text, bool primary, PanelTheme th,
        out bool clicked, string tooltip)
    {
        var f = ImGui.GetFontSize();
        var glyphW = primary ? 12f : 0f;
        var w = ImGui.CalcTextSize(text).X + glyphW + 24;
        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton(id, new Vector2(w, h));
        var hov = ImGui.IsItemHovered();
        clicked = ImGui.IsItemClicked();
        if (hov) ImGui.SetTooltip(tooltip);
        var max = pos + new Vector2(w, h);
        var cy = pos.Y + h * 0.5f;
        if (primary)
        {
            // Dark text on the bright accent, like the check on a captured dot.
            var ink = U(new Vector4(0.05f, 0.08f, 0.06f, 1f));
            dl.AddRectFilled(pos, max, U(ToneAccent, hov ? 1f : 0.82f), h * 0.5f);
            var tx = pos.X + 12;
            dl.AddTriangleFilled(new Vector2(tx, cy - 4.5f), new Vector2(tx + 7, cy), new Vector2(tx, cy + 4.5f), ink);
            dl.AddText(new Vector2(pos.X + 12 + glyphW, cy - f * 0.5f), ink, text);
            dl.AddText(new Vector2(pos.X + 12.6f + glyphW, cy - f * 0.5f), ink, text);   // faux bold
        }
        else
        {
            dl.AddRectFilled(pos, max, U(th.Tile, hov ? 1f : 0.8f), h * 0.5f);
            dl.AddRect(pos, max, U(th.Border, 0.7f), h * 0.5f);
            dl.AddText(new Vector2(pos.X + 12, cy - f * 0.5f), U(hov ? th.Text : th.TextDim), text);
        }
        return w;
    }

    /// <summary>12 px clock: the step waits for the user, not the other way round.</summary>
    private static void DrawQueueGlyph(ImDrawListPtr dl, Vector2 c, uint col)
    {
        dl.AddCircle(c, 5.5f, col, 16, 1.4f);
        dl.AddLine(c, new Vector2(c.X, c.Y - 3.5f), col, 1.4f);
        dl.AddLine(c, new Vector2(c.X + 2.6f, c.Y + 1.2f), col, 1.4f);
    }

    /// <summary>"Claude asked 3h 05m ago - do it 2x - then 4 more start by themselves": who, when, repeats, chain.</summary>
    private static string QueueMetaLine(QueuedStep s, int chainAfter, DateTime utc)
    {
        var who = string.IsNullOrWhiteSpace(s.By) ? "An agent" : s.By;
        var line = $"{who} asked {GuideElapsed(utc - s.QueuedAt)} ago";
        if (s.Repeats > 1) line += $" - do it {s.Repeats}x";
        if (chainAfter > 0) line += chainAfter == 1 ? " - then 1 more starts by itself" : $" - then {chainAfter} more start by themselves";
        return line;
    }

    /// <summary>A watch spec in plain words: "memory of PlayerStashTabs[33] (72 bytes)", "value of Life.CurHP", "items of ...".</summary>
    private static string QueueWatchSummary(string raw)
    {
        var w = ParseWatch(raw);
        if (w == null) return raw;
        var (kind, path, size, labels) = w.Value;
        var name = QueueShortPath(path);
        return kind switch
        {
            "memory" => $"memory of {name}" + (size > 0 ? $" ({size} bytes)" : ""),
            "collection" => $"items of {name}" + (labels.Length > 0 ? $" ({string.Join(", ", labels)})" : ""),
            _ => $"value of {name}",
        };
    }

    /// <summary>The last one or two segments of a walker path, splitting only on dots outside brackets.</summary>
    private static string QueueShortPath(string path)
    {
        var cuts = new List<int>();
        var depth = 0;
        for (var i = 0; i < path.Length; i++)
        {
            var ch = path[i];
            if (ch is '[' or '(') depth++;
            else if (ch is ']' or ')') depth--;
            else if (ch == '.' && depth == 0) cuts.Add(i);
        }
        if (cuts.Count == 0) return path;
        // Keep two segments when the last one alone is uninformative (e.g. "Value", "[3]", "Count").
        var last = path[(cuts[^1] + 1)..];
        var twoSegments = cuts.Count > 1 && (last.Length <= 5 || last.StartsWith('[')) ? path[(cuts[^2] + 1)..] : last;
        return twoSegments;
    }
}
