using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using ImGuiNET;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// The agents board (research\f8-agent-ux.md 5): who is on this HUD, what each is doing, where it is in its pipeline, what
/// it waits on and who needs the player. Two forms of one panel: the sessions chip's hover (480 wide, under the card's
/// stack like every GuideHover, full opacity, no input) and, after a click on the chip, the same panel pinned as a card in
/// the stack where the next strip goes (the board already lists the queue), with row hovers (an item's whole story) and an x
/// to unpin. Rows: who-dot, name (110 px), request (150 px: "F8 #12 Wrong place" or the task's title), the track (F8 items
/// only; a working task shows a spinner), the stage word in the phase tone, the tail (3.2; a PR row says "PR #57 - CI") and
/// the elapsed. Order (Work.cs WorkView.Rows): needs you first, then F8 by seq, then tasks, then present sessions with no
/// item (their Doing as a task row), then stale sessions (seen over 10 min ago: dim, a small dot, "(quiet 12 min)"). At most
/// six rows, then "+N more". Below: "recent:" (final items of the last 10 min), the restart line (unchanged copy), the
/// rollout line and "Shared server: ..." (the infra sessions). Empty: "No agents on this HUD" and how to get one.
/// The stripe is accent while anything needs the player, neutral otherwise, ToneBad for 10 s after a failure. The chip gets
/// the "!N" badge, the ring and the failed hairline in GuidePanelSessions.cs. Texts are rebuilt once a second or when a view
/// changes, never per frame; the pinned board keeps the panel up (the user chose it) and unpins itself after 30 min without
/// any agent (a 3 s agent toast says so), on Esc, the x or a second click on the chip. The pinned board is a card inside the
/// guide panel's own window (no second window): so it ghosts, hides in combat and moves with the stack by the same rules as
/// the strip, and takes the mouse only where the panel does.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int BoardRowsMax = 6;
    private const float BoardRequestMinW = 70f, BoardTailMinW = 64f;
    private const double BoardUnpinAfterSec = 1800, BoardFailedHairlineSec = 10;
    private const string BoardTitle = "AGENTS ON THIS HUD", BoardPinnedText = "pinned", BoardEmptyRow = "No agents on this HUD",
        BoardEmptyFoot = "Press F8 to send the route to Claude. Agents connect through the AI Bridge.", BoardUnpinTip = "Unpin the board (Esc does too). The chip's hover still shows it.";

    /// <summary>One board row, composed by BoardCompose (once a second or on a view change).</summary>
    private sealed class BoardRow
    {
        public WorkRowView? Work;
        public WorkRowUi? Ui;
        public string Name = "";
        public int Hue = -1;
        public bool HasHue, Stale, Track, Spinner, NeedsYou, TailWarn;
        public string Request = "";
        public string? StageWord;
        public Vector4 Tone = ToneNeutral;
        public WorkTailKind TailKind;
        public string? Tail;
        public string Elapsed = "";
        public string? HoverTitle, HoverBody, HoverNote, HoverFoot;
        // Clipped texts, kept until the source or the room changes.
        public string? NameSrc, ReqSrc, TailSrc; public float NameW = -1, ReqW = -1, TailW = -1; public string NameOut = "", ReqOut = "", TailOut = "";
    }

    private sealed class BoardUiState
    {
        public WorkView? Work; public SessionsView? Rs; public AgentsView? Ag; public long Second = -1;
        public readonly List<BoardRow> Rows = new();
        public int More, Active, NeedsYou;
        public string Right = "", MoreText = "";
        public string? Recent, Note, Foot1, Foot2;
        public bool Empty;
        public readonly StringBuilder Sb = new();
        public double HoverAt = -1e9;
        // Rows are reused across composes (Pool[i] is row i), so their clipped texts survive a compose that changed nothing.
        public readonly List<BoardRow> Pool = new();
        public readonly BoardClip RecentClip = new(), NoteClip = new(), Foot1Clip = new(), Foot2Clip = new();
    }

    /// <summary>One line clipped to a width, kept until the text (by value: compose rebuilds equal strings) or the room changes.</summary>
    private sealed class BoardClip
    {
        private string? _src; private float _w = -1; private string _out = "";

        public string Get(string s, float maxW)
        {
            if (_w == maxW && GuideSame(_src, s)) return _out;
            _src = s; _w = maxW; _out = GuideClipText(s, maxW);
            return _out;
        }
    }

    /// <summary>The next row for this compose: a pooled instance, reset, its clip caches kept.</summary>
    private static BoardRow BoardRowNext(BoardUiState b, int i)
    {
        if (i == b.Pool.Count) b.Pool.Add(new BoardRow());
        var r = b.Pool[i];
        r.Work = null; r.Ui = null; r.Name = ""; r.Hue = -1;
        r.HasHue = r.Stale = r.Track = r.Spinner = r.NeedsYou = r.TailWarn = false;
        r.Request = ""; r.StageWord = null; r.Tone = ToneNeutral; r.TailKind = WorkTailKind.None; r.Tail = null; r.Elapsed = "";
        r.HoverTitle = r.HoverBody = r.HoverNote = r.HoverFoot = null;
        return r;
    }

    private readonly BoardUiState _boardUi = new();

    // ── Pin state ────────────────────────────────────────────────────

    /// <summary>Once per frame: the auto-unpin (30 min with no agent at all, Esc), before attention reads the pin.</summary>
    private void BoardTick(WorkView work, SessionsView rs, double now)
    {
        var u = _workUi;
        if (!u.Pinned) { u.NoAgentSince = -1e9; return; }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) { u.Pinned = false; return; }
        if (rs.Agents > 0 || work.Active > 0) { u.NoAgentSince = -1e9; return; }
        if (u.NoAgentSince < -1e8) { u.NoAgentSince = now; return; }
        if (now - u.NoAgentSince < BoardUnpinAfterSec) return;
        u.Pinned = false;
        GuideLog(new JObject { ["title"] = "BOARD", ["kind"] = "agent", ["text"] = "unpinned: no agent on this HUD for 30 min", ["holdSec"] = 3 });
    }

    private bool BoardPinned => _workUi.Pinned;

    // ── Compose ──────────────────────────────────────────────────────

    /// <summary>The board's rows and lines, rebuilt when a view changes or the second moves (elapsed and quiet counts).</summary>
    private void BoardCompose(WorkView work, SessionsView rs, AgentsView ag, DateTime utc)
    {
        var b = _boardUi;
        var second = utc.Ticks / TimeSpan.TicksPerSecond;
        if (ReferenceEquals(b.Work, work) && ReferenceEquals(b.Rs, rs) && ReferenceEquals(b.Ag, ag) && b.Second == second) return;
        b.Work = work; b.Rs = rs; b.Ag = ag; b.Second = second;
        b.Rows.Clear();
        b.More = 0; b.Active = 0; b.NeedsYou = work.NeedsYouCount;
        var total = 0;
        void Add(BoardRow r) { if (b.Rows.Count < BoardRowsMax) b.Rows.Add(r); else b.More++; total++; if (!r.Stale) b.Active++; }
        foreach (var r in work.Rows)
        {
            var f8 = r.Kind == "F8";
            // At most BoardRowsMax + 1 pooled rows: the one past the last is filled, counted by Add and never shown.
            var row = BoardRowNext(b, b.Rows.Count);
            row.Work = r; row.Ui = WorkRowUiOf(r); row.Name = r.OwnerName.Length > 0 ? r.OwnerName : f8 ? "Claude" : r.Title; row.Hue = r.Hue; row.HasHue = r.HasOwner;
            row.Stale = r.Stale; row.Track = f8; row.Spinner = !f8 && !r.Final && !r.Stale;
            row.Request = f8 ? r.Label + " " + r.Request : r.Request; row.StageWord = r.StageWord; row.Tone = WorkToneColor(r.Tone);
            row.TailKind = r.TailKind; row.Tail = r.Tail; row.TailWarn = r.TailWarn; row.NeedsYou = r.NeedsYou;
            row.Elapsed = GuideElapsed(utc - r.StartedAt);
            row.HoverTitle = r.HoverTitle; row.HoverBody = r.HoverBody; row.HoverNote = r.HoverNote; row.HoverFoot = r.HoverFoot;
            if (r.Pr is { } pr && r.TailKind is WorkTailKind.None or WorkTailKind.Text or WorkTailKind.Waiting)
            {
                row.TailKind = WorkTailKind.Text;
                row.Tail = "PR #" + pr.Number + (pr.Checks switch { "pending" => " - CI", "green" => " - CI green", "red" => " - CI red", _ => "" });
            }
            // The owner's session: what else it is doing (a lease, a recording: what a restart may wait on) and its MCP build.
            if (r.Owner != null)
                foreach (var s in rs.Sessions)
                {
                    if (s.Id != r.Owner) continue;
                    var extra = s.Doing.Length > 0 ? "Doing: " + string.Join("; ", s.Doing) : null;
                    if (BoardMcpLine(s, rs) is { } mcp) extra = extra != null ? extra + "\n" + mcp : mcp;
                    if (extra != null) row.HoverNote = row.HoverNote != null ? row.HoverNote + "\n" + extra : extra;
                    break;
                }
            if (r.Stale) { row.StageWord = null; row.Track = false; row.Spinner = false; row.TailKind = WorkTailKind.Text; row.Tail = "(quiet " + GuideElapsed(utc - r.UpdatedAt) + ")"; }
            Add(row);
        }
        string? infra = null;
        foreach (var s in rs.Sessions)
        {
            if (s.Infra) { if (s.Present) infra = infra == null ? s.Name : infra + ", " + s.Name; continue; }
            if (!s.Connected) continue;
            var has = false;
            foreach (var r in work.Rows) if (r.Owner == s.Id) { has = true; break; }
            if (has) continue;
            var hue = ag.HueOf(s.Id, s.Label);
            if (s.Present)
            {
                var doing = s.Doing.Length > 0 ? string.Join("; ", s.Doing) : "idle";
                var row = BoardRowNext(b, b.Rows.Count);
                row.Name = s.Name; row.Hue = hue; row.HasHue = true; row.Request = doing; row.Spinner = s.Doing.Length > 0;
                row.HoverTitle = s.Name; row.HoverBody = doing; row.HoverNote = BoardMcpLine(s, rs);
                Add(row);
            }
            else
            {
                var quiet = utc - s.LastSeen;
                if (quiet.TotalMinutes >= 30) continue;   // gone
                var row = BoardRowNext(b, b.Rows.Count);
                row.Name = s.Name; row.Hue = hue; row.HasHue = true; row.Stale = true; row.Request = "(quiet " + GuideElapsed(quiet) + ")";
                row.HoverTitle = s.Name; row.HoverBody = "Seen " + GuideElapsed(quiet) + " ago; gone from the board after 30 min.";
                Add(row);
            }
        }
        b.Empty = total == 0;
        b.Right = b.Empty ? "" : b.Active + (b.Active == 1 ? " active" : " active") + (b.NeedsYou > 0 ? " . " + b.NeedsYou + (b.NeedsYou == 1 ? " needs you" : " need you") : "");
        b.MoreText = b.More > 0 ? "+" + b.More + " more" : "";
        // recent: F8 #11 Done 6 min ago . F8 #9 Failed 20 min ago
        var sb = b.Sb;
        sb.Clear();
        var n = 0;
        foreach (var r in work.Recent)
        {
            if (n++ == 3) break;
            sb.Append(n == 1 ? "recent:  " : " . ").Append(r.Label).Append(' ').Append(r.Stage).Append(' ')
              .Append(Math.Max(0, (int)(utc - (r.FinalAt ?? r.UpdatedAt)).TotalMinutes)).Append(" min ago");
        }
        b.Recent = sb.Length > 0 ? sb.ToString() : null;
        b.Note = rs.Granted is { } go
            ? "Restart: " + ag.NameOf(go.Session, go.Who) + " is restarting the HUD now" + (go.Reason != null ? " (" + go.Reason + ")" : "")
            : rs.Waiting is { } rr
                ? rr.Blockers.Count > 0
                    ? "Restart: " + ag.NameOf(rr.Session, rr.Who) + " waits for " + BoardBlockers(rr, ag)
                    : "Restart: " + ag.NameOf(rr.Session, rr.Who) + " restarts the HUD in " + (rr.HoldUntil is DateTime h ? Math.Max(0, (int)Math.Ceiling((h - utc).TotalSeconds)) : 0) + "s - Not now is on the card"
                : null;
        b.Foot1 = null;
        if (rs.Rollout is { } ro)
        {
            var build = ro.DeployedVersion ?? ShortSha(ro.DeployedSha);
            var foot = ro.Behind.Length == 0 && ro.Local.Length == 0 && ro.Unsupervised.Length == 0
                ? "MCP " + build + ": all " + ro.Total + " on it" : "MCP " + build + ": " + ro.OnDeployed + " of " + ro.Total + " on it";
            if (ro.Behind.Length > 0) foot += ", " + ro.Behind.Length + " swapping";
            if (ro.Local.Length > 0) foot += ", " + ro.Local.Length + " local";
            if (ro.Unsupervised.Length > 0) foot += ", " + ro.Unsupervised.Length + " need a session restart (" + string.Join(", ", Array.ConvertAll(ro.Unsupervised, l => ag.NameOf(l))) + ")";
            b.Foot1 = foot;
        }
        b.Foot2 = infra != null ? "Shared server: " + infra : b.Empty ? BoardEmptyFoot : null;
    }

    /// <summary>A session's MCP build for its row's hover: "MCP 3.58.0, swapping to 3.58.1" while a rollout has not reached it.</summary>
    private static string? BoardMcpLine(SessionView s, SessionsView rs)
    {
        if (s.Mcp is not { } m) return null;
        var line = "MCP " + (m.Version ?? ShortSha(m.Sha) ?? "?");
        if (m.Local) line += ", local build";
        else if (m.Unsupervised) line += ", no supervisor: restart this session for one";
        else if (rs.Rollout is { } ro && m.Behind(ro.DeployedSha)) line += ", swapping to " + (ro.DeployedVersion ?? ShortSha(ro.DeployedSha));
        return line;
    }

    private static string BoardBlockers(RestartRequest rr, AgentsView ag)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < rr.Blockers.Count && i < 3; i++)
        {
            var bl = rr.Blockers[i];
            if (i > 0) sb.Append(", ");
            if (bl.Who != null) sb.Append(ag.NameOf(bl.Session, bl.Who)).Append("'s ");
            sb.Append(bl.Label);
        }
        if (rr.Blockers.Count > 3) sb.Append(" (+").Append(rr.Blockers.Count - 3).Append(')');
        return sb.ToString();
    }

    // ── Draw ─────────────────────────────────────────────────────────

    /// <summary>The board's height for the current composition (the shell needs it before the rows).</summary>
    private float BoardHeight(float f)
    {
        var b = _boardUi;
        var small = f * 0.85f;
        var rowH = f + 8f;
        var h = 10f + f + 6f;
        h += (b.Empty ? 1 : b.Rows.Count) * rowH;
        if (b.More > 0) h += small + 4f;
        if (b.Recent != null) h += 4f + small + 2f;
        if (b.Note != null) h += 2f + small + 2f;
        if (b.Foot1 != null) h += 2f + small + 2f;
        if (b.Foot2 != null) h += 2f + small + 2f;
        return h + 10f;
    }

    /// <summary>The stripe's tone: ToneBad for 10 s after a failure, accent while anything needs the player, else neutral.</summary>
    private static Vector4 BoardTone(WorkView work, DateTime utc) =>
        work.RecentFailedAt is { } fa && (utc - fa).TotalSeconds < BoardFailedHairlineSec ? ToneBad : work.NeedsYouCount > 0 ? ToneAccent : ToneNeutral;

    /// <summary>The chip's hover: the board under the card's stack (GuideHover's place), foreground draw list, no input.</summary>
    private void DrawBoardHover(WorkView work, SessionsView rs, AgentsView ag, double now, DateTime utc)
    {
        BoardCompose(work, rs, ag, utc);
        var u = _guideUi;
        var f = ImGui.GetFontSize();
        var w = GuideWidth - GuideEdge * 2;
        var h = BoardHeight(f);
        var disp = ImGui.GetIO().DisplaySize;
        var min = new Vector2(u.HoverX, u.HoverBelowY + 6f);
        if (min.Y + h > disp.Y - GuideEdge) min.Y = u.HoverAboveY - 6f - h;
        min.X = Math.Clamp(min.X, GuideEdge, MathF.Max(GuideEdge, disp.X - GuideEdge - w));
        min.Y = MathF.Max(GuideEdge, min.Y);
        DrawBoardPanel(ImGui.GetForegroundDrawList(), min, w, h, work, utc, now, 1f, false, out _);
    }

    /// <summary>The pinned board as a card in the stack (DrawGuideBody, where the next strip goes), with row hovers and the x.</summary>
    private void DrawBoardCard(WorkView work, SessionsView rs, AgentsView ag, double now, DateTime utc)
    {
        BoardCompose(work, rs, ag, utc);
        var f = ImGui.GetFontSize();
        var w = GuideWidth - GuideEdge * 2;
        var h = BoardHeight(f);
        var p = ImGui.GetCursorScreenPos();
        var min = new Vector2(p.X + GuideEdge, p.Y);
        DrawBoardPanel(ImGui.GetWindowDrawList(), min, w, h, work, utc, now, 1f, true, out var unpin);
        if (unpin) _workUi.Pinned = false;
        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(GuideWidth, h + GuideEdge));
    }

    /// <summary>The panel itself (both forms). <paramref name="pinned"/>: rows take the mouse (InvisibleButtons) and the title row has the x.</summary>
    private void DrawBoardPanel(ImDrawListPtr dl, Vector2 min, float w, float h, WorkView work, DateTime utc, double now, float alpha, bool pinned, out bool unpin)
    {
        unpin = false;
        var b = _boardUi;
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var k = small / f;
        var max = min + new Vector2(w, h);
        var tone = BoardTone(work, utc);
        dl.AddRectFilled(min, max, U(GuideToastInk, 0.96f * alpha), 0f);
        dl.AddRect(min, max, U(tone == ToneBad ? ToneBad : GuideToastLine, alpha), 0f);
        dl.AddRectFilled(min, new Vector2(min.X + GuideToastStripe, max.Y), U(tone, alpha), 0f);

        // Title row: the tracked caps title; right: the counts (muted), and when pinned "pinned" + the x.
        var x0 = min.X + GuideToastStripe + GuideToastPadX;
        var y = min.Y + 10f;
        var hy = y + f * 0.5f;
        var tcol = U(GuideToastText, alpha);
        GuideTrackedText(dl, font, f, new Vector2(x0, y), tcol, BoardTitle, GuideToastTitleTrack);
        GuideTrackedText(dl, font, f, new Vector2(x0 + 0.6f, y), tcol, BoardTitle, GuideToastTitleTrack);
        var rx = max.X - GuideToastPadX;
        if (pinned)
        {
            var bx = new Vector2(rx - 16, hy - 8);
            ImGui.SetCursorScreenPos(bx);
            ImGui.InvisibleButton("##board_unpin", new Vector2(16, 16));
            var hov = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) unpin = true;
            if (hov) { GuideTooltip(BoardUnpinTip); dl.AddRectFilled(bx, bx + new Vector2(16, 16), U(GuideToastLine), 0f); }
            DrawCloseGlyph(dl, new Vector2(rx - 8, hy), 3.5f, U(hov ? GuideToastText : GuideToastMuted, hov ? 1f : 0.8f));
            rx -= 16 + 8;
            var pw = ImGui.CalcTextSize(BoardPinnedText).X * k;
            dl.AddText(font, small, new Vector2(rx - pw, hy - small * 0.5f), U(GuideToastMuted, 0.85f * alpha), BoardPinnedText);
            rx -= pw + 12;
        }
        if (b.Right.Length > 0)
        {
            var rw = ImGui.CalcTextSize(b.Right).X * k;
            dl.AddText(font, small, new Vector2(rx - rw, hy - small * 0.5f), U(b.NeedsYou > 0 ? ToneAccent : GuideToastMuted, alpha), b.Right);
        }
        y += f + 6f;

        // Rows.
        var rowH = f + 8f;
        if (b.Empty)
        {
            DrawNeutralDot(dl, new Vector2(x0 + GuideDotR, y + rowH * 0.5f), 0.6f * alpha);
            dl.AddText(font, small, new Vector2(x0 + WhoDotW, y + rowH * 0.5f - small * 0.5f), U(GuideToastMuted, alpha), BoardEmptyRow);
            y += rowH;
        }
        for (var i = 0; i < b.Rows.Count; i++)
        {
            DrawBoardRow(dl, b.Rows[i], new Vector2(min.X, y), w, rowH, now, alpha, pinned, i);
            y += rowH;
        }
        if (b.More > 0)
        {
            dl.AddText(font, small, new Vector2(x0 + WhoDotW, y), U(GuideToastMuted, 0.85f * alpha), b.MoreText);
            y += small + 4f;
        }
        if (b.Recent != null)
        {
            y += 4f;
            dl.AddText(font, small, new Vector2(x0, y), U(GuideToastMuted, 0.9f * alpha), b.RecentClip.Get(b.Recent, (max.X - GuideToastPadX - x0) / k));
            y += small + 2f;
        }
        if (b.Note != null)
        {
            y += 2f;
            dl.AddText(font, small, new Vector2(x0, y), U(GuideToastMuted, alpha), b.NoteClip.Get(b.Note, (max.X - GuideToastPadX - x0) / k));
            y += small + 2f;
        }
        if (b.Foot1 != null)
        {
            y += 2f;
            dl.AddText(font, small, new Vector2(x0, y), U(GuideToastMuted, 0.75f * alpha), b.Foot1Clip.Get(b.Foot1, (max.X - GuideToastPadX - x0) / k));
            y += small + 2f;
        }
        if (b.Foot2 != null)
        {
            y += 2f;
            dl.AddText(font, small, new Vector2(x0, y), U(GuideToastMuted, 0.75f * alpha), b.Foot2Clip.Get(b.Foot2, (max.X - GuideToastPadX - x0) / k));
        }
    }

    /// <summary>One board row (5.2): who-dot, name, request, the track or spinner, the stage word, the tail, the elapsed.</summary>
    private void DrawBoardRow(ImDrawListPtr dl, BoardRow r, Vector2 min, float w, float h, double now, float alpha, bool pinned, int index)
    {
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var k = small / f;
        var cy = min.Y + h * 0.5f;
        var a = (r.Stale ? 0.55f : 1f) * alpha;
        var x = min.X + GuideToastStripe + GuideToastPadX;
        var rx = min.X + w - GuideToastPadX;
        if (pinned)
        {
            ImGui.SetCursorScreenPos(new Vector2(x, min.Y));
            ImGui.InvisibleButton(index < BoardRowIds.Length ? BoardRowIds[index] : "##board_row_x", new Vector2(rx - x, h));
            if (ImGui.IsItemHovered())
            {
                dl.AddRectFilled(new Vector2(min.X + GuideToastStripe, min.Y), new Vector2(min.X + w, min.Y + h), U(GuideToastLine, 0.35f * alpha), 0f);
                if (r.HoverTitle != null || r.HoverBody != null) GuideHover(r.HoverTitle, r.HoverBody, r.HoverNote, r.HoverFoot, r.NeedsYou ? ToneAccent : r.Tone);
            }
        }
        // Who-dot: the hue (pulsing 2.6 s while the row needs the player), neutral for an unclaimed press, a small dot when stale.
        var dc = new Vector2(x + GuideDotR, cy);
        if (r.Stale) dl.AddCircleFilled(dc, 2f, U(GuideToastMuted, a), 10);
        else if (r.HasHue)
        {
            if (r.NeedsYou)
            {
                var breathe = 0.5f + 0.5f * MathF.Sin((float)(now * Math.PI * 2 / WorkBreatheSec));
                dl.AddCircle(dc, GuideDotR + 2f + 1.5f * breathe, U(AttnHueColor(r.Hue), (0.2f + 0.4f * breathe) * a), 16, 1.2f);
            }
            DrawWhoDot(dl, dc, r.Hue, a);
        }
        else DrawNeutralDot(dl, dc, a);
        x += WhoDotW;
        // Name (110 px) and request (150 px), clipped; the clipped forms are kept until the text or the room changes.
        if (!GuideSame(r.NameSrc, r.Name) || r.NameW != WorkNameW) { r.NameSrc = r.Name; r.NameW = WorkNameW; r.NameOut = GuideClipText(r.Name, (WorkNameW - 8f) / k); }
        dl.AddText(font, small, new Vector2(x, cy - small * 0.5f), U(GuideToastText, 0.9f * a), r.NameOut);
        x += WorkNameW;
        // The right side first (elapsed, the stage word, the track and a minimum tail): the request column gives way so the
        // stage word never runs into the elapsed and the tail ("waits for CI", "PR #57 - CI") keeps a readable slot.
        var ew = r.Elapsed.Length > 0 ? ImGui.CalcTextSize(r.Elapsed).X * k + 10f : 0f;
        var trackW = r.Track && r.Work != null && r.Ui != null ? WorkDotR * 2 + WorkDotPitch * 4 + 12f : r.Spinner ? 18f : 0f;
        var wordW = r.StageWord != null ? GuideTrackedWidth(font, small, r.StageWord, GuideToastTitleTrack) + 10f : 0f;
        var tailMin = r.Tail != null ? BoardTailMinW : 0f;
        var reqW = Math.Clamp(rx - ew - x - trackW - wordW - tailMin, BoardRequestMinW, WorkRequestW);
        if (!GuideSame(r.ReqSrc, r.Request) || r.ReqW != reqW) { r.ReqSrc = r.Request; r.ReqW = reqW; r.ReqOut = GuideClipText(r.Request, (reqW - 8f) / k); }
        dl.AddText(font, small, new Vector2(x, cy - small * 0.5f), U(GuideToastText, 0.8f * a), r.ReqOut);
        x += reqW;
        if (r.Track && r.Work != null && r.Ui != null)
        {
            DrawWorkTrack(dl, new Vector2(x + WorkDotR, cy), r.Work, r.Ui, r.Tone, now, a);
            x += WorkDotR * 2 + WorkDotPitch * 4 + 12f;
        }
        else if (r.Spinner)
        {
            DrawWorkSpinner(dl, new Vector2(x + 6f, cy), U(ToneAccent, a), now);
            x += 18f;
        }
        if (r.StageWord != null)
        {
            var wcol = U(r.Tone, a);
            GuideTrackedText(dl, font, small, new Vector2(x, cy - small * 0.5f), wcol, r.StageWord, GuideToastTitleTrack);
            GuideTrackedText(dl, font, small, new Vector2(x + 0.6f, cy - small * 0.5f), wcol, r.StageWord, GuideToastTitleTrack);
            x += GuideTrackedWidth(font, small, r.StageWord, GuideToastTitleTrack) + 10f;
        }
        if (r.Elapsed.Length > 0)
        {
            dl.AddText(font, small, new Vector2(rx - (ew - 10f), cy - small * 0.5f), U(r.Work is { Stalled: true, Final: false } ? ToneWarn : GuideToastMuted, a), r.Elapsed);
            rx -= ew;
        }
        if (r.Tail == null || rx - x < 24f) return;
        if (r.TailKind == WorkTailKind.NeedsYou)
        {
            var tiny = f * 0.75f;
            var pw = GuideTrackedWidth(font, tiny, WorkNeedsYouLabel, GuideToastMsgTrack) + 12f;
            var ph = tiny + 6f;
            if (rx - x < pw) return;
            var pmin = new Vector2(x, cy - ph * 0.5f);
            dl.AddRectFilled(pmin, pmin + new Vector2(pw, ph), U(ToneAccent, 0.9f * a), 0f);
            var ink = U(GuideInk, a);
            GuideTrackedText(dl, font, tiny, new Vector2(x + 6f, cy - tiny * 0.5f), ink, WorkNeedsYouLabel, GuideToastMsgTrack);
            GuideTrackedText(dl, font, tiny, new Vector2(x + 6.6f, cy - tiny * 0.5f), ink, WorkNeedsYouLabel, GuideToastMsgTrack);
            x += pw + 8f;
        }
        var room = rx - x;
        if (room < 24f) return;
        if (!GuideSame(r.TailSrc, r.Tail) || r.TailW != room) { r.TailSrc = r.Tail; r.TailW = room; r.TailOut = GuideClipText(r.Tail, room / k); }
        var col = r.TailKind switch
        {
            WorkTailKind.LiveFix => U(ToneAccent, a),
            WorkTailKind.Nobody => U(ToneWarn, a),
            WorkTailKind.Waiting when r.TailWarn => U(ToneWarn, a),
            WorkTailKind.NeedsYou => U(GuideToastText, 0.9f * a),
            _ => U(GuideToastMuted, a),
        };
        dl.AddText(font, small, new Vector2(x, cy - small * 0.5f), col, r.TailOut);
    }

    private static readonly string[] BoardRowIds = ["##board_row_0", "##board_row_1", "##board_row_2", "##board_row_3", "##board_row_4", "##board_row_5"];
}
