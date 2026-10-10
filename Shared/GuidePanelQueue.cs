using System;
using System.Numerics;
using System.Text;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// The attention queue's quiet surfaces (research\attention-queue-ui.md 2.2, 2.4, 6.4, 6.5), drawn from the guide panel:
///   - the next strip: one line under the cards, in two forms. Someone in line: NEXT (THEN while a restart card is on
///     screen) with a who-dot, the first waiting item ("Claude: Which stash tab?") and one tail (+N more / when things
///     calm down / after this); hover lists the queue in order. Nobody in line but two or more agents asking at once (a
///     card and a question on the map, a step recording and a flow): WHO IS ASKING with each asker's who-dot and name;
///     hover says what each asks. Drawn only when it is not the obvious single agent (NextStripWanted), and only while
///     the panel is up anyway: a waiting item alone is the dot's job. Never idle or disconnected sessions, branches or
///     folders (the user: "I have no idea what 'system32 layer-test - main - main' mean"): names come from AgentsView.
///   - the dot's look: the floor item's tone while combat hides its card (the card comes back on hover), accent and
///     steady while items wait with nothing on screen (hover gives the floor to the next one, calm or not), else as today.
/// The strip's line is recomposed only when the queue view, the agents view or the restart card changes, and never
/// during a hand-over (GuidePanelAsk.cs), so the player who looks down to see who is next reads a stable line through
/// the whole hand-over. Hovers are GuideHover panels under the card's stack.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int NextStripTipRows = 6;
    private const string StripNext = "NEXT", StripThen = "THEN", StripAsking = "WHO IS ASKING";

    // ── Dot ──────────────────────────────────────────────────────────

    /// <summary>
    /// The dot's tone and hover label this frame (6.5; the bridge-down red is the dot's own rule). The floor item's tone
    /// while combat hides its card (no label: hovering brings the card back); accent while items wait with nothing on
    /// screen, a granted ask the player cannot see yet (combat, the gap) included, labelled "2 WAITING - CLAUDE, TESTER"
    /// (display names, composed when the queue or the names change); else neutral.
    /// </summary>
    private (Vector4 tone, string? tip) GuideDotLookFor(in GuideAttention at, string guideStatus, bool hasCard, bool askHolder, AttnQueueView queue, AgentsView ag)
    {
        var a = _askUi;
        if (at.FloorHidden && (a.Shown != null || hasCard || askHolder)) return (a.Shown == null && !askHolder && guideStatus == "failed" ? ToneBad : ToneAccent, null);
        if (queue.Rows.Length == 0) return askHolder ? (ToneAccent, null) : (ToneNeutral, null);
        if (!ReferenceEquals(a.DotFor, queue) || !ReferenceEquals(a.DotAg, ag))
        {
            a.DotFor = queue; a.DotAg = ag;
            var sb = new StringBuilder().Append(queue.Rows.Length).Append(" waiting - ");
            var seen = new string[queue.Rows.Length];
            var n = 0;
            foreach (var r in queue.Rows)
            {
                var name = ag.NameOf(r.Session, r.Who);
                if (Array.IndexOf(seen, name, 0, n) >= 0) continue;
                if (n > 0) sb.Append(", ");
                sb.Append(name);
                seen[n++] = name;
            }
            a.DotLabel = GuideCaps(sb.ToString());
        }
        return (ToneAccent, a.DotLabel);
    }

    /// <summary>Hovering the accent dot while items wait with nothing to bring back gives the floor to the next one (once per hover).</summary>
    private void AskDotHover(bool dotHovered, bool peekable, AttnQueueView queue)
    {
        var a = _askUi;
        if (dotHovered && !a.DotWasHovered && !peekable && queue.Rows.Length > 0) AttnUserPeek();
        a.DotWasHovered = dotHovered;
    }

    // ── Next strip ───────────────────────────────────────────────────

    /// <summary>
    /// Whether the strip has a job: someone waits in line and the cards name their owners (not the obvious single agent:
    /// GuideShowWho), or, with nobody in line, two or more agents ask the user for something at once.
    /// </summary>
    private bool NextStripWanted(AgentsView ag) => (_askUi.Queue.Rows.Length > 0 && _guideUi.ShowWho) || GuideAskers(ag) >= 2;

    private void DrawNextStrip(AgentsView ag, bool restartUp, PanelTheme th, double now, DateTime utc)
    {
        var a = _askUi;
        var q = a.Queue;
        var dl = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var k = small / f;
        var p = ImGui.GetCursorScreenPos();
        var x0 = p.X + GuideEdge;
        var w = GuideWidth - GuideEdge * 2;
        const float pad = 12f;
        var h = f + 10;
        var min = new Vector2(x0, p.Y);
        var max = new Vector2(x0 + w, p.Y + h);
        dl.AddRectFilled(min, max, U(th.Card, 0.92f), 5f);
        dl.AddRect(min, max, U(ToneNeutral, 0.4f), 5f);

        var frozen = a.LeaveAt > -1e8 || now < a.GapUntil;
        if (!frozen) StripCompose(a, q, ag, restartUp, utc);

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##next_strip", new Vector2(w, h));
        if (ImGui.IsItemHovered())
        {
            if (a.StripNext) GuideHover("Waiting for you", StripTooltip(q, ag, restartUp, utc), null, null, ToneNeutral);
            else if (a.StripTip != null) GuideHover("Who is asking you for something", a.StripTip, null, null, ToneNeutral);
        }

        var hy = min.Y + h * 0.5f;
        var x = min.X + pad;
        if (a.StripNext) DrawNextGlyph(dl, new Vector2(x + 6, hy), U(ToneNeutral));
        else DrawSessionsGlyph(dl, new Vector2(x + 6, hy), U(ToneNeutral));
        x += 18;
        dl.AddText(font, small, new Vector2(x, hy - small * 0.5f), U(ToneNeutral), a.StripLabel);
        x += ImGui.CalcTextSize(a.StripLabel).X * k + 10;
        var rx = max.X - pad;
        if (a.StripTail != null)
        {
            var tw = ImGui.CalcTextSize(a.StripTail).X * k;
            dl.AddText(font, small, new Vector2(rx - tw, hy - small * 0.5f), U(th.TextDim, 0.8f), a.StripTail);
            rx -= tw + 10;
        }
        if (a.StripNext)
        {
            DrawWhoDot(dl, new Vector2(x + GuideDotR, hy), a.StripHue);
            x += WhoDotW;
            if (rx - x > 40) dl.AddText(new Vector2(x, hy - f * 0.5f), U(th.TextDim), GuideClipText(a.StripText!, rx - x));
        }
        else
        {
            for (var i = 0; i < a.StripAgents.Length && rx - x > 40; i++)
            {
                if (i > 0) { dl.AddText(new Vector2(x, hy - f * 0.5f), U(th.TextDim, 0.6f), ","); x += ImGui.CalcTextSize(",").X + 8; }
                DrawWhoDot(dl, new Vector2(x + GuideDotR, hy), a.StripAgentHues[i]);
                x += WhoDotW;
                var t = GuideClipText(a.StripAgents[i], rx - x);
                dl.AddText(new Vector2(x, hy - f * 0.5f), U(th.TextDim), t);
                if (!ReferenceEquals(t, a.StripAgents[i])) break;
                x += ImGui.CalcTextSize(t).X;
            }
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, h + GuideEdge));
    }

    /// <summary>
    /// The strip's line: recomposed when the queue view, the agents view (names, who asks) or the restart card changes.
    /// NEXT / THEN with the first waiting item while someone is in line; otherwise WHO IS ASKING with the agents that ask
    /// the user for something right now (AgentsView: askers first), and their hover text ("Name: what it asks").
    /// </summary>
    private static void StripCompose(AskUiState a, AttnQueueView q, AgentsView ag, bool restartUp, DateTime utc)
    {
        if (ReferenceEquals(a.StripFor, q) && ReferenceEquals(a.StripAg, ag) && a.StripRestart == restartUp) return;
        a.StripFor = q; a.StripAg = ag; a.StripRestart = restartUp;
        if (q.Rows.Length > 0)
        {
            var r = q.Rows[0];
            a.StripNext = true;
            a.StripLabel = restartUp ? StripThen : StripNext;
            a.StripHue = r.Hue;
            a.StripText = $"{ag.NameOf(r.Session, r.Who)}: {r.Text}";
            a.StripTail = q.Rows.Length > 1 ? $"+{q.Rows.Length - 1} more"
                : r.Urgency == "calm" && (!q.Calm || (r.EligibleAt is { } e && e > utc)) ? "when things calm down"
                : q.HolderWho != null && r.Urgency == "now" ? "after this"
                : null;
            a.StripTip = null;
            return;
        }
        a.StripNext = false; a.StripText = null; a.StripTail = null;
        a.StripLabel = StripAsking;
        var n = 0;
        foreach (var x in ag.Active) if (x.Asks.Length > 0) n++;
        var names = new string[n];
        var hues = new int[n];
        var tip = new StringBuilder();
        n = 0;
        foreach (var x in ag.Active)
        {
            if (x.Asks.Length == 0) continue;
            names[n] = x.Name;
            hues[n] = ag.HueOf(null, x.Label);
            n++;
            for (var i = 0; i < x.Asks.Length; i++)
            {
                if (tip.Length > 0) tip.Append('\n');
                tip.Append(x.Name).Append(": ").Append(AskWords(x.Asks[i]));
                if (i == 0 && x.Doing.Length > 0) tip.Append(" - also ").Append(string.Join(", ", x.Doing));
            }
        }
        a.StripAgents = names; a.StripAgentHues = hues;
        a.StripTip = tip.ToString();
    }

    /// <summary>On hover only: the queue in order, by display name (kind, how long it waits, why not yet).</summary>
    private static string StripTooltip(AttnQueueView q, AgentsView ag, bool restartUp, DateTime utc)
    {
        var sb = new StringBuilder();
        var shown = Math.Min(q.Rows.Length, NextStripTipRows);
        for (var i = 0; i < shown; i++)
        {
            var r = q.Rows[i];
            var kind = r.Kind switch { "ask" => "question", "flow" => "guided task", _ => "step" };
            var why = restartUp ? ", after the HUD restart"
                : q.Combat ? ", after the fight"
                : r.EligibleAt is { } e && e > utc ? $", Later runs out in {GuideElapsed(e - utc)}"
                : r.Urgency == "calm" && !q.Calm ? ", when things calm down"
                : "";
            if (i > 0) sb.Append('\n');
            sb.Append(i + 1).Append("  ").Append(ag.NameOf(r.Session, r.Who)).Append(": ").Append(r.Text).Append("   ").Append(kind).Append(", ").Append(GuideElapsed(utc - r.QueuedAt)).Append(why);
        }
        if (q.Rows.Length > shown) sb.Append("\n+").Append(q.Rows.Length - shown).Append(" more");
        return sb.ToString();
    }

    /// <summary>
    /// A pilot blocker's label (Sessions.cs BlockersLocked) as the strip's hover says it: the step itself for "waiting
    /// for the user: X" (the strip already says it is asking), "asks: X" for a question (a highlight's, or the attention
    /// queue's floor ask "your answer to: X"), the rest as written.
    /// </summary>
    private static string AskWords(string ask)
    {
        const string waiting = "waiting for the user: ", question = "question for the user: ", answer = "your answer to: ";
        if (ask.StartsWith(waiting, StringComparison.Ordinal)) return ask[waiting.Length..];
        if (ask.StartsWith(question, StringComparison.Ordinal)) return "asks: " + ask[question.Length..];
        if (ask.StartsWith(answer, StringComparison.Ordinal)) return "asks: " + ask[answer.Length..];
        return ask;
    }

    /// <summary>How many active agents ask the user for something right now (a step, a question, a flow, a recording step).</summary>
    private static int GuideAskers(AgentsView ag)
    {
        var n = 0;
        foreach (var x in ag.Active) if (x.Asks.Length > 0) n++;
        return n;
    }

    /// <summary>12 px "next" glyph: a chevron pointing right, 1.4 px, in the strip's neutral.</summary>
    private static void DrawNextGlyph(ImDrawListPtr dl, Vector2 c, uint col)
    {
        dl.AddLine(new Vector2(c.X - 2.5f, c.Y - 4.5f), new Vector2(c.X + 2.5f, c.Y), col, 1.4f);
        dl.AddLine(new Vector2(c.X + 2.5f, c.Y), new Vector2(c.X - 2.5f, c.Y + 4.5f), col, 1.4f);
    }
}
