using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Work items: what each agent is doing for the player, one line per item (scaffolding research/f8-agent-ux.md, sections
/// 3, 5, 8 and 11). Two kinds:
///   F8   "f8:&lt;seq&gt;", one per Whats A Route F8 press, built by the bridge itself from the pub/sub events it carries:
///        feedback (creates it at Pressed; a "works"-only press is recorded Done "Noted: works here" at once, C7),
///        feedback.reply (Stage / WaitingOn / Text / Detail / Ask / Pr; the legacy lowercase {ref, status, text} maps
///        queued -> Queued, claimed -> Claimed, working -> Investigating, done -> Done, failed -> Failed, C5; keys in
///        either casing), route.agent.progress (the owner's agent route) and the Route plugin's "route" trace
///        route-reloaded / live-reloaded while the item is Investigating / Fixing (-> LiveFix, C4). Its owner is the
///        session labelled route-f8-&lt;seq&gt; once it says hello.
///   Task one per session (Key = its session id), set by the session itself with work.set (MCP agent_status).
/// WaitingOn = Player is derived here, never set by an agent: from the owner session's open attention item (ask / step /
/// flow, showing or queued), its guide step waiting on the player, an unanswered asked highlight in its layer, a queued
/// experiment step it waits to have started, or its agent route on a Manual step. Ask is then that question / instruction
/// / step / route title. Recomputed by SessionsTick at 4 Hz (WorkTickLocked).
///
/// TCP:
///   work.set  {Key?, Kind?, Title?, Stage?, Text?, Detail?, Pr?, Ref?, WaitingOn?}  upserts the caller's item (Key defaults
///             to its session id; an f8:&lt;seq&gt; key, or Ref = seq, only from infra or that press's own handler session);
///             WaitingOn may be None|Session|Ci|Review|OtherAgent|Restart, never Player (waiting_on_player). -> {ok, item}
///   work.list {activeOnly?}                       -> {ok, rev, items (active, in board order), recent (final, 10 min)}
///   work.end  {Key?, Stage?: Done|Failed|Dropped (default Done), Text?}  -> {ok, item}
///   session.list carries work[] per session, and top-level work[] (every active item) and recent[].
/// Pub/sub: kind "work" (data = the item, AgentWorkDto) on every change, at most once a second per item (the last state
///   always goes out).
/// PluginBridge (BCL types only; after a bridge reload the old delegates answer bridge_reloaded):
///   AIBridge.WorkList     Func&lt;string&gt;: JSON array of AgentWorkDto, active plus final ones from the last 10 min
///   AIBridge.HighlightFor Func&lt;string ownerSession, string json, string&gt;: a guide.highlight params object drawn in that
///                         session's highlight layer (normal lifetime and panel rules; durationSec defaults to 25 s so a
///                         step highlight never lingers); {"clear":true} clears that layer. -> {ok, ...} or {error, message}.
/// Toasts (guide.log path, holdSec): Queued "QUEUED", Claimed "ON IT", LiveFix "TRY IT NOW" (step), Done "DONE #n" (result,
///   20 s), Failed "FAILED #n" (error, 30 s), Dropped "DROPPED #n" (agent), a works-only press "NOTED" (result, 1.2 s), and
///   once per wait a "STILL SHIPPING" (agent) when WaitingOn Ci / Review / Restart / OtherAgent lasts over 90 s.
/// Persistence: active items in &lt;BridgeDirectory&gt;\work\items.json, restored on load (each entry checked; a bad file is
///   logged and ignored). Final items are kept 10 min in memory (recent).
/// State lives under the sessions lock. Events arrive on any thread (PubPublish -> WorkEnqueue, lock-free) and are
/// applied under the sessions lock by SessionsTick, or first thing in work.* calls. "work" publications and the file
/// write happen after the lock is released.
/// </summary>
public partial class WhatsAnAiBridge
{
    // ── Contract (research/f8-agent-ux.md 8.2) ───────────────────────

    /// <summary>A pull request an item is landing through.</summary>
    internal sealed class PrRefDto
    {
        public string? Repo { get; set; }
        public string? Url { get; set; }
        public int Number { get; set; }
        /// <summary>pending | green | red</summary>
        public string? Checks { get; set; }
    }

    /// <summary>One stage an item went through, oldest first in History.</summary>
    internal sealed class WorkStageDto
    {
        public string Stage { get; set; } = "";
        public DateTime At { get; set; }
        public string? Text { get; set; }
    }

    /// <summary>
    /// The owner's agent route (Whats A Route), from route.agent.progress (and the title from route.agent set). Leading:
    /// the route card is showing it now (started .. resumed); Manual: its current step waits on the player's Did it.
    /// </summary>
    internal sealed record AgentRouteView(string Id, string? Title, long? Ref, string Event, int StepIndex, int StepCount, string? Label,
        bool Manual, bool Leading, DateTime At);

    /// <summary>
    /// One work item (AgentWorkDto, 8.2), as work.list, session.list, the "work" event, AIBridge.WorkList and items.json
    /// carry it. PascalCase, nulls omitted. Derived by the bridge (never accepted from agents): WaitingOn = Player, Ask,
    /// NeedsYou, OwnerName, Hue, Phase, Stale, NobodyListening, Stalled, Route.
    /// </summary>
    internal sealed class AgentWorkDto
    {
        public string Key { get; set; } = "";               // "f8:<seq>" or the owner's session id
        public string Kind { get; set; } = "Task";          // F8 | Task
        public long? Seq { get; set; }                      // F8: the press's pub/sub seq
        public string? Owner { get; set; }                  // session id (F8: the route-f8-<seq> session once it said hello)
        public string OwnerName { get; set; } = "";         // display name ("" while no owner)
        public int Hue { get; set; } = -1;                  // 0-5, -1 while no owner (a neutral dot)
        public string Title { get; set; } = "";             // <= 60
        public string Stage { get; set; } = "Pressed";      // F8Stage
        public string Phase { get; set; } = "Sent";         // F8Phase, from Stage
        public string WaitingOn { get; set; } = "None";     // None | Session | Player | Ci | Review | OtherAgent | Restart
        public string? WaitingOnSet { get; set; }           // what the agent said it waits on (Player never)
        public string? Ask { get; set; }                    // <= 120, while WaitingOn = Player (derived; null otherwise)
        public string? AskSet { get; set; }                 // the agent's own Ask line (reply / work.set): Ask's fallback text
        public string? NeedsYou { get; set; }               // what the player is asked through: answer | step | recording | route
        public string? Text { get; set; }                   // <= 400
        public string? Detail { get; set; }                 // <= 400
        public PrRefDto? Pr { get; set; }
        public string[]? Flags { get; set; }                // F8: the press's flag ids (2.3)
        public string? Scope { get; set; }                  // F8: step | panel | area | marker | route
        public string? Note { get; set; }                   // F8: the player's note, <= 300
        public DateTime StartedAt { get; set; }
        public DateTime StageAt { get; set; }
        public DateTime UpdatedAt { get; set; }             // the last change an event or a call made (derived changes don't count)
        public DateTime? FinalAt { get; set; }
        public List<WorkStageDto> History { get; set; } = new();   // oldest first, <= 40
        public bool Final { get; set; }                     // Done | Failed | Dropped
        public bool NobodyListening { get; set; }           // F8 still Pressed 30 s after the press: no dispatcher took it
        public bool Stale { get; set; }                     // the owner session is not present (gone or idle)
        public bool Stalled { get; set; }                   // a wait on CI / review / a restart / an agent over 90 s, or 15 min without a stage change
        public AgentRouteView? Route { get; set; }          // the owner's agent route, while it has one
        [JsonIgnore] internal DateTime? WaitSince;          // WaitingOn last became a non-player wait
        [JsonIgnore] internal bool WaitToasted;             // that wait's STILL SHIPPING toast went out
        [JsonIgnore] internal bool PubDirty;                // a "work" event is due
        [JsonIgnore] internal DateTime LastPubAt = DateTime.MinValue;
    }

    // ── View for the drawer (work strip, chip badge, board) ──────────

    /// <summary>Stripe / stage-word tone of a row: the drawer maps it to ToneNeutral / ToneAccent / ToneOk / ToneWarn / ToneBad.</summary>
    internal enum WorkTone { Neutral, Accent, Ok, Warn, Bad }

    /// <summary>Which tail a row shows (3.2, one, in this priority): the NEEDS YOU pill + NeedsYouText, "live fix", a wait,
    /// "nobody listening yet", else the item's text (Done: the final text; Failed: why).</summary>
    internal enum WorkTailKind { None, NeedsYou, LiveFix, Waiting, Nobody, Text }

    /// <summary>
    /// One row of the work strip / the board, immutable and precomputed (no per-frame formatting except the elapsed time).
    ///   Label         "F8 #12" (F8) or the task title; Request: the strip's request text (F8: the first flag's label, else the
    ///                 note's first 24 chars, else "Feedback"; Task: the title) - the drawer caps it with GuideCaps (cache it)
    ///   HasOwner/Hue  the who-dot: the owner's hue once known, else a ToneNeutral dot (Hue -1)
    ///   Phase / PhaseIndex  0 Sent, 1 Looking, 2 Fixing, 3 Shipping, 4 Checking, 5 Ended; ReachedPhase: the furthest of 0-4
    ///                 reached (Failed fills the reached dots, Done fills all five, Dropped greys them)
    ///   StageWord     SENT | LOOKING | FIXING | SHIPPING | CHECKING | DONE | FAILED | DROPPED, in Tone
    ///   NeedsYou      WaitingOn = Player: the accent [NEEDS YOU] pill + NeedsYouText ("answer above", "do the step above",
    ///                 "start the recording", "follow Claude 2's route"); Ask: the player-facing line
    ///   TailKind/Tail the one tail (see WorkTailKind); TailWarn: draw it in ToneWarn (a wait over 90 s, nobody listening)
    ///   StartedAt     elapsed = now - StartedAt (right-aligned); StageAt: ToneWarn past 15 min without a stage change
    ///                 (Stalled says so already); FinalAt: the terminal hold (Done 6 s, Failed 10 s, Dropped 4 s) runs from it
    ///   Hover*        the whole-strip / board-row hover (GuideHover title, body = stage history, note = Text + Detail,
    ///                 foot = the PR line, the dispatcher hint, or where to watch it)
    ///   Rev           changes whenever anything in the row changed: key animations (pops, washes) off Stage/StageAt.
    /// </summary>
    internal sealed record WorkRowView(
        string Key, string Kind, long? Seq, string? Owner, string OwnerName, int Hue, bool HasOwner,
        string Label, string Request, string Title,
        string Stage, string Phase, int PhaseIndex, int ReachedPhase, string StageWord, WorkTone Tone,
        string WaitingOn, bool NeedsYou, string? NeedsYouText, string? Ask,
        WorkTailKind TailKind, string? Tail, bool TailWarn,
        DateTime StartedAt, DateTime StageAt, DateTime UpdatedAt, DateTime? FinalAt,
        bool Final, bool Stale, bool Stalled, bool NobodyListening,
        string HoverTitle, string HoverBody, string? HoverNote, string? HoverFoot,
        PrRefDto? Pr, AgentRouteView? Route);

    /// <summary>
    /// Everything the work strip, the chip and the board draw, published by SessionsTick (4 Hz) only when something changed
    /// (WorkSnapshot(), read per frame by reference, never under a lock).
    ///   Rows          active items in board order (5.2): WaitingOn = Player first, then F8 by seq, then tasks by start, then
    ///                 stale ones; the strip takes the F8 rows (F8Rows: the same rows, F8 only, NeedsYou first then by seq)
    ///   Recent        final items of the last 10 min, newest first (the board's "recent:" line, the strip's terminal hold)
    ///   Active        active rows; NeedsYouCount: rows with WaitingOn = Player (the chip's !N badge)
    ///   AnyF8Running  an F8 item between Pressed and Verifying (the chip's ring, the dot's ring in combat)
    ///   RecentFailedAt the newest Failed item's FinalAt within 10 min (the chip's ToneBad hairline for 10 s after it)
    /// </summary>
    internal sealed record WorkView(long Rev, WorkRowView[] Rows, WorkRowView[] F8Rows, WorkRowView[] Recent, int Active, int NeedsYouCount,
        bool AnyF8Running, DateTime? RecentFailedAt)
    {
        public static readonly WorkView Empty = new(0, [], [], [], 0, 0, false, null);
    }

    // ── State (under _sessionLock) ───────────────────────────────────

    public const string WorkListMethodName = "AIBridge.WorkList", HighlightForMethodName = "AIBridge.HighlightFor";
    private static readonly string[] WorkStages =
        ["Pressed", "Queued", "Claimed", "Reading", "Investigating", "Fixing", "LiveFix", "Reviewing", "Merging", "Deploying", "Verifying", "Done", "Failed", "Dropped"];
    private static readonly string[] WorkPhases = ["Sent", "Looking", "Fixing", "Shipping", "Checking", "Ended"];
    private static readonly string[] WorkPhaseWords = ["SENT", "LOOKING", "FIXING", "SHIPPING", "CHECKING"];
    private static readonly string[] WorkWaits = ["None", "Session", "Player", "Ci", "Review", "OtherAgent", "Restart"];
    private static readonly HashSet<string> WorkEventKinds = new(StringComparer.Ordinal)
        { "feedback", "feedback.reply", "route.agent", "route.agent.progress", "route" };
    private static readonly Dictionary<string, string> WorkFlagLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wrong-place"] = "Wrong place", ["not-advancing"] = "Not advancing", ["missing-step"] = "Missing step", ["cant-reach"] = "Can't reach it",
        ["wrong-words"] = "Wrong words", ["wrong-marker"] = "Wrong marker", ["no-marker"] = "No marker", ["too-early"] = "Too early",
        ["already-done"] = "Already done", ["odd-path"] = "Odd path", ["something-else"] = "Something else", ["works"] = "Works here",
        ["note"] = "Note", ["heads-up"] = "Heads-up",
    };
    private static readonly JsonSerializerSettings WorkJson = new() { NullValueHandling = NullValueHandling.Ignore, DateTimeZoneHandling = DateTimeZoneHandling.Utc };
    private static readonly JsonSerializer WorkSerializer = JsonSerializer.Create(WorkJson);
    private static readonly TimeSpan WorkRecentKeep = TimeSpan.FromMinutes(10), WorkTaskGone = TimeSpan.FromMinutes(30), WorkF8NoNews = TimeSpan.FromHours(3),
        WorkPubEvery = TimeSpan.FromSeconds(1), WorkNobodyAfter = TimeSpan.FromSeconds(30), WorkWaitToastAfter = TimeSpan.FromSeconds(90),
        WorkStallAfter = TimeSpan.FromMinutes(15), WorkRouteKeep = TimeSpan.FromMinutes(10), WorkRouteGrace = TimeSpan.FromSeconds(10);
    private const int WorkHistoryMax = 40, WorkRouteTtlDefault = 600, WorkRouteTtlMax = 1800;

    private readonly Dictionary<string, AgentWorkDto> _work = new(StringComparer.Ordinal);   // active and final (recent) items
    private readonly ConcurrentQueue<JObject> _workInbox = new();                             // pub/sub events to apply (any thread)
    private readonly ConcurrentQueue<JObject> _workOutbox = new();                            // "work" events to publish (one pool worker)
    private int _workOutboxBusy;
    private readonly object _workFileLock = new();                                           // items.json writes in snapshot order (never taken under the sessions lock; may nest it)
    private long _workSaveGen, _workWrittenGen;
    private readonly HashSet<long> _workEndedSeqs = new();                                    // F8 seqs that ended and left recent (this load)
    private readonly Queue<long> _workEndedOrder = new();
    private const int WorkEndedKeep = 500;
    private readonly Dictionary<string, AgentRouteView> _agentRoutes = new(StringComparer.Ordinal);   // owner session id -> its agent route
    private readonly Dictionary<string, (string? Owner, string? Title, int TtlSec, long? Ref)> _agentRouteInfo = new(StringComparer.Ordinal);   // route id -> set
    private long _workRev, _workViewRev = -1, _workSavedRev = -1;
    private string? _workSavedKey;
    private bool _workLoaded;
    private DateTime _workLoadedAt;
    private string? _workTickError;
    private volatile bool _workClosed;
    private volatile WorkView _workView = WorkView.Empty;

    private string WorkFile => Path.Combine(_bridgeDir, "work", "items.json");

    /// <summary>What the work strip, the chip and the board draw (rebuilt by SessionsTick only on change; no lock).</summary>
    internal WorkView WorkSnapshot() => _workView;

    // ── PluginBridge ─────────────────────────────────────────────────

    // _workClosed is reset by RegisterBridgeExports (WhatsAnAiBridge.cs) right before the exports are saved.

    /// <summary>From OnClose / hot reload: delegates other plugins still hold answer bridge_reloaded.</summary>
    private void WorkClose() => _workClosed = true;

    #pragma warning disable HXB007 // legacy AIBridge.* name: see the HXB007 note in WhatsAnAiBridge.csproj
    [BridgeExport(WorkListMethodName)]
    private string WorkListFromPlugin()
    #pragma warning restore HXB007
    {
        try
        {
            if (_workClosed) return Err("bridge_reloaded", $"This {WorkListMethodName} belongs to a bridge load that has ended: look the method up again.").ToString(Formatting.None);
            lock (_sessionLock)
            {
                WorkLoadLocked();
                return JsonConvert.SerializeObject(WorkOrderedLocked(activeOnly: false), WorkJson);
            }
        }
        catch (Exception ex) { return Err("work_list_failed", ex.Message).ToString(Formatting.None); }
    }

    /// <summary>
    /// AIBridge.HighlightFor: an agent route's step highlight (Whats A Route), drawn in the route owner's own layer as if that
    /// session had called guide.highlight: its asked targets queue behind its attention turn, its lifetime and the big-panel
    /// rule apply. durationSec defaults to the default life (25 s): a step highlight never lingers.
    /// </summary>
    #pragma warning disable HXB007 // legacy AIBridge.* name: see the HXB007 note in WhatsAnAiBridge.csproj
    [BridgeExport(HighlightForMethodName)]
    private string HighlightForFromPlugin(string ownerSession, string json)
    #pragma warning restore HXB007
    {
        try
        {
            if (_workClosed) return Err("bridge_reloaded", $"This {HighlightForMethodName} belongs to a bridge load that has ended: look the method up again.").ToString(Formatting.None);
            if (string.IsNullOrWhiteSpace(ownerSession))
                return Err("missing_owner", $"{HighlightForMethodName}(ownerSession, json): ownerSession is empty (the agent route's Owner, stamped by the bridge on route.agent).").ToString(Formatting.None);
            JObject p;
            try { p = string.IsNullOrWhiteSpace(json) ? new JObject() : JToken.Parse(json) as JObject ?? throw new JsonException("not a JSON object"); }
            catch (JsonException ex) { return Err("bad_json", $"{HighlightForMethodName}: json does not parse as a guide.highlight params object: {ex.Message}").ToString(Formatting.None); }
            SessionInfo? me;
            lock (_sessionLock)
            {
                if (!_sessions.TryGetValue(ownerSession, out me))
                    return Err("unknown_session", $"{HighlightForMethodName}: no session '{ownerSession}' on this HUD ({_sessions.Count} known): the route's owner never said hello here, or was forgotten 30 min after it left.").ToString(Formatting.None);
                if (!me.Connected && p["clear"]?.Value<bool>() != true)
                    return Err("owner_gone", $"{HighlightForMethodName}: session '{ownerSession}' ({DisplayNameLocked(me)}) is disconnected: its highlights are not drawn.").ToString(Formatting.None);
            }
            if (p["clear"]?.Value<bool>() == true)
                return HighlightSetLayer(HlOwnerOf(me), me.Id, me.Label, new JObject { ["clear"] = true }).ToString(Formatting.None);   // its own layer only, never force
            p.Remove("force");
            if (p["durationSec"] == null || p["durationSec"]!.Type == JTokenType.Null) p["durationSec"] = HlDefaultLifeSec;
            return HighlightSetAs(me, p).ToString(Formatting.None);
        }
        catch (Exception ex) { return Err("highlight_failed", $"{HighlightForMethodName}: {ex.GetType().Name}: {ex.Message}").ToString(Formatting.None); }
    }

    // ── Events in (any thread) ───────────────────────────────────────

    /// <summary>From PubPublish (under the pub/sub lock, so in seq order): the kinds work items are built from go to the inbox (lock-free).</summary>
    private void WorkEnqueue(string kind, JObject e)
    {
        if (WorkEventKinds.Contains(kind)) _workInbox.Enqueue(e);
    }

    /// <summary>
    /// route.agent published over TCP: the bridge stamps the caller as its owner (Owner = session id, OwnerName, Hue) before
    /// delivery, so the Route plugin knows whose route it is (the MCP never sends them; a client-sent owner is overwritten).
    /// </summary>
    private void WorkStampRouteAgent(string? kind, JObject data)
    {
        if (kind != "route.agent") return;
        // A client-sent owner never goes through, in any casing: without a session there is no owner at all (it would draw
        // in another session's highlight layer and take its attention turn).
        foreach (var p in data.Properties().Where(p => p.Name.Equals("Owner", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Equals("OwnerName", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Hue", StringComparison.OrdinalIgnoreCase)).ToList())
            p.Remove();
        if (CurrentSession() is not { } me) return;
        lock (_sessionLock)
        {
            data["Owner"] = me.Id;
            data["OwnerName"] = DisplayNameLocked(me);
            data["Hue"] = me.Hue is >= 0 and < AttnHues ? me.Hue : AttnHueFallback;
        }
    }

    // ── TCP ──────────────────────────────────────────────────────────

    private string? ProcessWorkMethod(string method, JToken? p) => method switch
    {
        "work.set" => SafeMemory(() => WorkSetCall(p)),
        "work.list" => SafeMemory(() => WorkListCall(p)),
        "work.end" => SafeMemory(() => WorkEndCall(p)),
        _ => null,
    };

    private JObject WorkSetCall(JToken? p)
    {
        var me = CurrentSession();
        if (me == null) return Err("no_session", "Send session.hello on this connection first: a work item belongs to a session.");
        var o = p as JObject;
        if (WorkStr(o, "WaitingOn", 20) is { } wo)
        {
            if (wo.Equals("Player", StringComparison.OrdinalIgnoreCase))
                return Err("waiting_on_player", "WaitingOn = Player is derived by the bridge from what you show the player (an ask, a guide step, an asked highlight, a Manual agent-route step): ask through those instead.");
            if (WorkParseWait(wo) == null) return Err("bad_waiting_on", $"WaitingOn '{wo}' is not one of {string.Join(", ", WorkWaits.Where(w => w != "Player"))}.");
        }
        string? stage = null;
        if (WorkStr(o, "Stage", 20) is { } st && (stage = WorkParseStage(st)) == null)
            return Err("bad_stage", $"Stage '{st}' is not one of {string.Join(", ", WorkStages)}.");
        var refSeq = WorkLong(o, "Ref");
        var key = WorkStr(o, "Key", 100) ?? (refSeq is > 0 ? "f8:" + refSeq : me.Id);
        var f8 = key.StartsWith("f8:", StringComparison.Ordinal);
        long seq = 0;
        if (f8 && !long.TryParse(key.AsSpan(3), out seq)) return Err("bad_key", $"Key '{key}': an F8 key is f8:<seq> (the press's pub/sub seq).");
        if (WorkStr(o, "Kind", 10) is { } k && !k.Equals(f8 ? "F8" : "Task", StringComparison.OrdinalIgnoreCase))
            return Err("bad_kind", $"Kind '{k}' does not fit key '{key}': f8:<seq> keys are F8, a session's own key is Task.");
        var pubs = new List<JObject>();
        JObject reply;
        lock (_sessionLock)
        {
            WorkLoadLocked();
            var now = DateTime.UtcNow;
            WorkDrainLocked(now);
            if (f8 ? !WorkMayMoveF8Locked(me, seq) : key != me.Id)
                return Err("not_yours", f8
                    ? $"{key} moves through feedback.reply events (pubsub.publish kind feedback.reply) or from the shared server (infra) or its own handler session (route-f8-{seq}); you are {me.Label}."
                    : $"A session sets only its own item (Key = its session id, the default); '{key}' is not yours.");
            _work.TryGetValue(key, out var item);
            if (item is { Final: true } && stage is not ("Done" or "Failed" or "Dropped"))
            {
                if (f8) return Err("final", $"{key} is {item.Stage} already: a final F8 item does not reopen (a new press does).");
                _work.Remove(key);   // a session's next task after a finished one starts fresh
                item = null;
            }
            if (item == null && f8 && _workEndedSeqs.Contains(seq))
                return Err("final", $"{key} ended and left the board (recent keeps 10 min): a final F8 item does not reopen (a new press does).");
            if (item == null)
            {
                item = f8 ? WorkNewF8Locked(seq, now, null) : new AgentWorkDto
                {
                    Key = key, Kind = "Task", Owner = me.Id, Title = Clip(DisplayNameLocked(me), 60)!, Stage = "Investigating", Phase = "Looking",
                    StartedAt = now, StageAt = now, UpdatedAt = now, History = [new WorkStageDto { Stage = "Investigating", At = now }],
                };
                _work[key] = item;
            }
            WorkApplyFieldsLocked(item, o, now, fromReply: false);
            if (stage != null) WorkSetStageLocked(item, stage, WorkStr(o, "Text", 400), now, toast: f8);
            WorkTouchLocked(item, now);
            WorkPublishDueLocked(now, pubs);
            reply = new JObject { ["ok"] = true, ["item"] = JObject.FromObject(item, WorkSerializer) };
        }
        WorkFlush(pubs);
        WorkSaveIfChanged();
        return reply;
    }

    private JObject WorkEndCall(JToken? p)
    {
        var me = CurrentSession();
        if (me == null) return Err("no_session", "Send session.hello on this connection first.");
        var o = p as JObject;
        var stage = WorkStr(o, "Stage", 20) is { } st ? WorkParseStage(st) : "Done";
        if (stage is not ("Done" or "Failed" or "Dropped")) return Err("bad_stage", "work.end Stage is Done (default), Failed or Dropped.");
        var key = WorkStr(o, "Key", 100) ?? me.Id;
        var pubs = new List<JObject>();
        JObject reply;
        lock (_sessionLock)
        {
            WorkLoadLocked();
            var now = DateTime.UtcNow;
            WorkDrainLocked(now);
            if (!_work.TryGetValue(key, out var item))
                return Err("unknown_item", $"No work item '{key}' (active: {string.Join(", ", _work.Values.Where(w => !w.Final).Select(w => w.Key).DefaultIfEmpty("none"))}).");
            var f8 = item.Kind == "F8";
            if (f8 ? !WorkMayMoveF8Locked(me, item.Seq ?? 0) : key != me.Id)
                return Err("not_yours", $"'{key}' is not yours to end (you are {me.Label}).");
            WorkSetStageLocked(item, stage, WorkStr(o, "Text", 400), now, toast: f8);
            WorkTouchLocked(item, now);
            WorkPublishDueLocked(now, pubs);
            reply = new JObject { ["ok"] = true, ["item"] = JObject.FromObject(item, WorkSerializer) };
        }
        WorkFlush(pubs);
        WorkSaveIfChanged();
        return reply;
    }

    private JObject WorkListCall(JToken? p)
    {
        var activeOnly = p?["activeOnly"]?.Type == JTokenType.Boolean && p["activeOnly"]!.Value<bool>();
        var pubs = new List<JObject>();
        JObject reply;
        lock (_sessionLock)
        {
            WorkLoadLocked();
            var now = DateTime.UtcNow;
            WorkDrainLocked(now);
            WorkPublishDueLocked(now, pubs);
            reply = new JObject
            {
                ["ok"] = true, ["rev"] = _workRev,
                ["items"] = new JArray(WorkOrderedLocked(activeOnly: true).Select(w => JObject.FromObject(w, WorkSerializer))),
                ["file"] = WorkFile,
            };
            if (!activeOnly) reply["recent"] = WorkRecentJsonLocked();
        }
        WorkFlush(pubs);
        return reply;
    }

    /// <summary>An F8 item moves by events; over TCP only from infra (the shared server mirroring feedback_reply) or its own handler.</summary>
    private bool WorkMayMoveF8Locked(SessionInfo me, long seq) =>
        me.Infra || WorkIsHandlerLabel(me.Label, seq) || (_work.TryGetValue("f8:" + seq, out var w) && w.Owner == me.Id);

    /// <summary>The handler of press seq says hello as route-f8-&lt;seq&gt; (its X-Hexile-Agent identity).</summary>
    private static bool WorkIsHandlerLabel(string label, long seq)
    {
        const string prefix = "route-f8-";
        if (!label.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = label.AsSpan(prefix.Length);
        var n = 0;
        while (n < rest.Length && char.IsDigit(rest[n])) n++;
        return n > 0 && long.TryParse(rest[..n], out var s) && s == seq && (n == rest.Length || !char.IsLetterOrDigit(rest[n]));
    }

    /// <summary>For session.list: one session's items (active and recent).</summary>
    private JArray WorkOfSessionLocked(string session) =>
        new(_work.Values.Where(w => w.Owner == session).OrderBy(w => w.Final).ThenBy(w => w.StartedAt).Select(w => JObject.FromObject(w, WorkSerializer)));

    private JArray WorkActiveJsonLocked() => new(WorkOrderedLocked(activeOnly: true).Select(w => JObject.FromObject(w, WorkSerializer)));

    private JArray WorkRecentJsonLocked() =>
        new(_work.Values.Where(w => w.Final).OrderByDescending(w => w.FinalAt).Select(w => JObject.FromObject(w, WorkSerializer)));

    /// <summary>Items in board order (5.2): needs you, F8 by seq, tasks by start, stale; then (unless activeOnly) recent, newest first.</summary>
    private List<AgentWorkDto> WorkOrderedLocked(bool activeOnly)
    {
        var list = _work.Values.Where(w => !w.Final)
            .OrderBy(w => w.Stale ? 1 : 0).ThenBy(w => w.WaitingOn == "Player" ? 0 : 1).ThenBy(w => w.Kind == "F8" ? 0 : 1)
            .ThenBy(w => w.Seq ?? long.MaxValue).ThenBy(w => w.StartedAt).ToList();
        if (!activeOnly) list.AddRange(_work.Values.Where(w => w.Final).OrderByDescending(w => w.FinalAt));
        return list;
    }

    // ── Applying events (under the sessions lock) ────────────────────

    /// <summary>Apply every event that arrived since the last drain.</summary>
    private void WorkDrainLocked(DateTime now)
    {
        while (_workInbox.TryDequeue(out var e))
        {
            try { WorkApplyEventLocked(e, now); }
            catch (Exception ex) { LogFault($"[Work] event {e["seq"]} ({e["kind"]}) skipped: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void WorkApplyEventLocked(JObject e, DateTime now)
    {
        var kind = e["kind"]?.ToString();
        var d = e["data"] as JObject;
        var seq = e["seq"]?.Type == JTokenType.Integer ? e["seq"]!.Value<long>() : 0;
        switch (kind)
        {
            case "feedback": WorkOnPressLocked(seq, d, now); break;
            case "feedback.reply": WorkOnReplyLocked(d, now); break;
            case "route.agent": WorkOnRouteAgentLocked(d); break;
            case "route.agent.progress": WorkOnRouteProgressLocked(d, now); break;
            case "route": WorkOnRouteTraceLocked(d, now); break;
        }
    }

    /// <summary>A press: the F8 item at Pressed (C2). A works-only press is recorded Done at once and starts nothing (C7).</summary>
    private void WorkOnPressLocked(long seq, JObject? d, DateTime now)
    {
        if (seq <= 0) return;
        var fresh = WorkNewF8Locked(seq, now, d);
        if (_work.TryGetValue(fresh.Key, out var item))
        {
            // A reply was applied first (a bare "F8 #n" item): the press's own data fills it in; anything else stays.
            if (item.Flags != null || item.Note != null || item.Scope != null) return;
            item.Flags = fresh.Flags; item.Note = fresh.Note; item.Scope = fresh.Scope;
            if (item.Title == $"F8 #{seq}") item.Title = fresh.Title;
            if (item.Final) { WorkTouchLocked(item, now); return; }
        }
        else _work[fresh.Key] = item = fresh;
        WorkTouchLocked(item, now);
        var flags = item.Flags ?? [];
        var verdict = WorkStr(d, "Verdict", 20);
        var worksOnly = item.Note == null && ((flags.Length == 1 && flags[0] == "works") || (verdict == "works" && flags.All(f => f == "works")));
        if (worksOnly)
        {
            WorkSetStageLocked(item, "Done", "Noted: works here", now, toast: false);
            GuideLog(new JObject { ["text"] = $"#{seq}: noted as working", ["kind"] = "result", ["title"] = "NOTED", ["holdSec"] = 1.2 });
        }
    }

    /// <summary>A new F8 item for press seq (from the press's data when there is one; a reply for an unknown press makes a bare one).</summary>
    private AgentWorkDto WorkNewF8Locked(long seq, DateTime now, JObject? d)
    {
        string[]? flags = null;
        if (WorkGet(d, "Flags") is JArray fa)
            flags = fa.Select(f => Clip(f?.ToString()?.Trim(), 24)).Where(f => !string.IsNullOrEmpty(f)).Take(12).ToArray()!;
        var evt = WorkStr(d, "Event", 30);
        if ((flags == null || flags.Length == 0) && evt != null && evt != "f8") flags = [evt];
        var note = WorkStr(d, "Note", 300);
        string title;
        if (flags is { Length: > 0 } && flags.Any(f => f != "something-else"))
            title = string.Join(", ", flags.Where(f => f != "something-else").Select(WorkFlagLabel));
        else if (note != null) title = note;
        else if (flags is { Length: > 0 }) title = WorkFlagLabel(flags[0]);
        else title = $"F8 #{seq}";
        return new AgentWorkDto
        {
            Key = "f8:" + seq, Kind = "F8", Seq = seq, Title = Clip(title, 60)!, Stage = "Pressed", Phase = "Sent", WaitingOn = "Session",
            Flags = flags is { Length: > 0 } ? flags : null, Note = note, Scope = WorkStr(d, "Scope", 20),
            StartedAt = now, StageAt = now, UpdatedAt = now, History = [new WorkStageDto { Stage = "Pressed", At = now }],
        };
    }

    private static string WorkFlagLabel(string id) => WorkFlagLabels.TryGetValue(id, out var l) ? l : id;

    /// <summary>A feedback.reply: new fields (Stage, WaitingOn, Text, Detail, Ask, Pr) in either casing, legacy status mapped (C5).</summary>
    private void WorkOnReplyLocked(JObject? d, DateTime now)
    {
        var refSeq = WorkLong(d, "Ref");
        if (refSeq is not > 0) return;
        var stage = WorkStr(d, "Stage", 20) is { } st ? WorkParseStage(st) : null;
        if (stage == null && WorkStr(d, "Status", 20) is { } status)
            stage = status.ToLowerInvariant() switch
            {
                "queued" => "Queued", "claimed" => "Claimed", "working" => "Investigating", "done" => "Done", "failed" => "Failed",
                _ => WorkParseStage(status),
            };
        var key = "f8:" + refSeq;
        if (!_work.TryGetValue(key, out var item))
        {
            if (_workEndedSeqs.Contains(refSeq.Value)) return;   // a late reply for a press that ended and left recent: no phantom row
            item = WorkNewF8Locked(refSeq.Value, now, null);   // a reply for a press this load never saw (it came before the HUD did)
            _work[key] = item;
        }
        // A late non-final reply ("claimed" after "done") never reopens a final item.
        if (item.Final && stage is not ("Done" or "Failed" or "Dropped")) return;
        WorkApplyFieldsLocked(item, d, now, fromReply: true);
        if (stage != null) WorkSetStageLocked(item, stage, WorkStr(d, "Text", 400), now, toast: true);
        WorkTouchLocked(item, now);
    }

    /// <summary>Text / Detail / Ask / Pr / WaitingOn / Title from a reply or a work.set (only the keys present change).</summary>
    private static void WorkApplyFieldsLocked(AgentWorkDto item, JObject? o, DateTime now, bool fromReply)
    {
        if (o == null) return;
        if (WorkGet(o, "Text") != null) item.Text = WorkStr(o, "Text", 400);
        if (WorkGet(o, "Detail") != null) item.Detail = WorkStr(o, "Detail", 400);
        if (!fromReply && item.Kind == "Task" && WorkStr(o, "Title", 60) is { } t) item.Title = t;
        if (WorkGet(o, "WaitingOn") is { } w)
        {
            var parsed = w.Type == JTokenType.Null ? null : WorkParseWait(w.ToString());
            item.WaitingOnSet = parsed is null or "None" or "Player" ? null : parsed;
        }
        if (WorkGet(o, "Ask") != null) item.AskSet = WorkStr(o, "Ask", 120);   // the agent's own line: the fallback while the bridge derives Player
        if (WorkGet(o, "Pr") is { } pr)
        {
            if (pr is JObject po)
                item.Pr = new PrRefDto
                {
                    Repo = WorkStr(po, "Repo", 100), Url = WorkStr(po, "Url", 300),
                    Number = (int)(WorkLong(po, "Number") ?? 0), Checks = WorkStr(po, "Checks", 20)?.ToLowerInvariant(),
                };
            else if (pr.Type == JTokenType.Null) item.Pr = null;
        }
    }

    /// <summary>
    /// Move an item to a stage: history (only on a change of stage), final bookkeeping, and the bridge's own F8 toasts (3.1's
    /// toast rule) when <paramref name="toast"/>.
    /// </summary>
    private void WorkSetStageLocked(AgentWorkDto item, string stage, string? text, DateTime now, bool toast)
    {
        if (text != null) item.Text = text;
        if (item.Stage == stage) return;
        item.Stage = stage;
        item.Phase = WorkPhases[WorkPhaseIndex(stage)];
        item.StageAt = now;
        item.History.Add(new WorkStageDto { Stage = stage, At = now, Text = text });
        if (item.History.Count > WorkHistoryMax) item.History.RemoveRange(0, item.History.Count - WorkHistoryMax);
        item.Final = stage is "Done" or "Failed" or "Dropped";
        item.FinalAt = item.Final ? now : null;
        if (item.Final) { item.WaitingOnSet = null; item.WaitingOn = "None"; item.Ask = item.AskSet = null; item.NeedsYou = null; }
        if (toast && item.Kind == "F8") WorkToastStageLocked(item);
    }

    /// <summary>The bridge's own toasts on F8 stage changes (3.1): names through AgentsView.NameOf by session id.</summary>
    private void WorkToastStageLocked(AgentWorkDto item)
    {
        var n = item.Seq ?? 0;
        var who = WorkNameLocked(item);
        JObject? log = item.Stage switch
        {
            "Queued" => new JObject
            {
                ["title"] = "QUEUED", ["kind"] = "result",
                ["text"] = _work.Values.Where(w => w.Kind == "F8" && !w.Final && w != item && (w.Seq ?? 0) < n && WorkPhaseIndex(w.Stage) >= 1)
                               .OrderBy(w => w.Seq).FirstOrDefault() is { } first
                    ? $"#{n} waits: {WorkNameLocked(first)} finishes #{first.Seq} first"
                    : item.Text ?? $"#{n} waits for its turn",
            },
            "Claimed" => new JObject { ["title"] = "ON IT", ["kind"] = "result", ["text"] = $"{who} took #{n}" + (item.Note != null ? " - reading your note" : "") },
            "LiveFix" => new JObject { ["title"] = "TRY IT NOW", ["kind"] = "step", ["text"] = $"#{n}: {item.Text ?? "a fix is live in the route"}" },
            "Done" => new JObject { ["title"] = $"DONE #{n}", ["kind"] = "result", ["text"] = item.Text ?? "Fixed", ["holdSec"] = 20 },
            "Failed" => new JObject { ["title"] = $"FAILED #{n}", ["kind"] = "error", ["text"] = item.Text ?? "No reason given", ["holdSec"] = 30 },
            "Dropped" => new JObject { ["title"] = $"DROPPED #{n}", ["kind"] = "agent", ["text"] = item.Text ?? "Not needed any more" },
            _ => null,
        };
        if (log != null) GuideLog(log);
    }

    /// <summary>The owner's display name for the player (AgentsView.NameOf by session id), "Claude" while nobody has it.</summary>
    private string WorkNameLocked(AgentWorkDto item)
    {
        if (item.Owner == null) return "Claude";
        if (item.OwnerName.Length > 0) return item.OwnerName;
        var label = _sessions.TryGetValue(item.Owner, out var s) ? s.Label : null;
        var name = AgentsSnapshot().NameOf(item.Owner, label);
        return name.Length > 0 ? name : "Claude";
    }

    /// <summary>route.agent set: remember its title, ttl and ref by route id (the progress events carry the rest).</summary>
    private void WorkOnRouteAgentLocked(JObject? d)
    {
        var id = WorkStr(d, "Id", 120);
        if (id == null || !string.Equals(WorkStr(d, "Op", 10) ?? "set", "set", StringComparison.OrdinalIgnoreCase)) return;
        var ttl = (int)Math.Clamp(WorkLong(d, "TtlSec") ?? WorkRouteTtlDefault, 30, WorkRouteTtlMax);
        _agentRouteInfo[id] = (WorkStr(d, "Owner", 80), WorkStr(d, "Title", 40), ttl, WorkLong(d, "Ref"));
        if (_agentRouteInfo.Count > 40) _agentRouteInfo.Remove(_agentRouteInfo.Keys.First());
    }

    /// <summary>route.agent.progress: the owner's agent route state (row 16, and Manual -> WaitingOn Player).</summary>
    private void WorkOnRouteProgressLocked(JObject? d, DateTime now)
    {
        var id = WorkStr(d, "Id", 120);
        var evt = WorkStr(d, "Event", 20)?.ToLowerInvariant();
        if (id == null || evt == null) return;
        _agentRouteInfo.TryGetValue(id, out var info);
        var owner = WorkStr(d, "Owner", 80) ?? info.Owner ?? (id.LastIndexOf(':') is var c and > 0 ? id[..c] : null);
        if (owner == null) return;
        var leading = evt is "started" or "arrived" or "step-done" or "paused" or "resumed";
        // A terminal event of a route that is no longer this owner's current one (replaced) changes nothing.
        if (!leading && _agentRoutes.TryGetValue(owner, out var cur) && cur.Id != id) return;
        _agentRoutes[owner] = new AgentRouteView(id, WorkStr(d, "Title", 40) ?? info.Title, WorkLong(d, "Ref") ?? info.Ref, evt,
            (int)(WorkLong(d, "StepIndex") ?? 0), (int)(WorkLong(d, "StepCount") ?? 0), WorkStr(d, "Label", 40),
            leading && WorkGet(d, "Manual")?.Type == JTokenType.Boolean && WorkGet(d, "Manual")!.Value<bool>(), leading, now);
        _workRev++;
    }

    /// <summary>The Route plugin reloaded its route (route-reloaded / live-reloaded): a fix is live for items being looked at or fixed (C4).</summary>
    private void WorkOnRouteTraceLocked(JObject? d, DateTime now)
    {
        if (WorkStr(d, "Event", 30) is not ("route-reloaded" or "live-reloaded")) return;
        var note = WorkStr(d, "Note", 200);
        foreach (var item in _work.Values.Where(w => w.Kind == "F8" && w.Stage is "Investigating" or "Fixing").ToList())
        {
            WorkSetStageLocked(item, "LiveFix", null, now, toast: true);
            if (note != null && item.History.Count > 0 && item.History[^1].Stage == "LiveFix") item.History[^1].Text = note;
            WorkTouchLocked(item, now);
        }
    }

    private void WorkTouchLocked(AgentWorkDto item, DateTime now)
    {
        item.UpdatedAt = now;
        item.PubDirty = true;
        _workRev++;
    }

    // ── Tick (SessionsTick, under the sessions lock) ─────────────────

    /// <summary>
    /// The 4 Hz pass: apply queued events, link F8 owners, derive WaitingOn = Player / Ask / NeedsYou and the rest of the
    /// derived fields, toast long waits once, expire, publish due "work" events (into pubs, sent after the lock) and rebuild
    /// the drawer's view when anything changed. hlAsks: per highlight layer owner, its first unanswered question (read before
    /// the lock); guide: the guide card's status, instruction and session (read before the lock).
    /// </summary>
    private void WorkTickLocked(DateTime now, (string status, string? instruction, string? who, string? session) guide,
        Dictionary<string, string>? hlAsks, List<JObject> pubs)
    {
        WorkLoadLocked();
        WorkDrainLocked(now);
        // Agent routes: a route whose owner is gone, or that went past its ttl without news, no longer leads.
        if (_agentRoutes.Count > 0)
            foreach (var (owner, r) in _agentRoutes.ToList())
            {
                var ttl = _agentRouteInfo.TryGetValue(r.Id, out var info) ? info.TtlSec : WorkRouteTtlMax;
                var ownerGone = !_sessions.TryGetValue(owner, out var os) || (!os.Connected && os.DisconnectedAt is { } da && now - da > WorkRouteGrace);
                if (r.Leading && (ownerGone || now - r.At > TimeSpan.FromSeconds(ttl + 60)))
                {
                    _agentRoutes[owner] = r with { Leading = false, Manual = false, Event = ownerGone ? "owner-gone" : "expired", At = now };
                    _workRev++;
                }
                else if (!r.Leading && now - r.At > WorkRouteKeep) { _agentRoutes.Remove(owner); _workRev++; }
            }
        if (_work.Count == 0) { WorkPublishViewLocked(now); return; }
        List<string>? remove = null;
        foreach (var item in _work.Values)
        {
            // Expiry: recent items after 10 min; a task whose session is gone (forgotten, or absent 30 min); an F8 item with
            // no news for 3 h is dropped (a dispatcher that died mid-way never leaves it on the strip forever).
            if (item.Final) { if (now - (item.FinalAt ?? item.UpdatedAt) > WorkRecentKeep) (remove ??= new()).Add(item.Key); continue; }
            // The F8 owner: the connected route-f8-<seq> session. Re-linked while the linked one is not connected, so a
            // replacement handler (same label, a new session id) takes the item over.
            SessionInfo? owner = null;
            if (item.Owner != null) _sessions.TryGetValue(item.Owner, out owner);
            if (item.Kind == "F8" && owner is not { Connected: true } && item.Seq is long sq)
            {
                SessionInfo? handler = null;
                foreach (var s in _sessions.Values)
                    if (s.Connected && WorkIsHandlerLabel(s.Label, sq) && (handler == null || s.LastSeen > handler.LastSeen)) handler = s;
                if (handler != null && handler.Id != item.Owner) { owner = handler; item.Owner = handler.Id; WorkTouchLocked(item, now); }
            }
            if (item.Kind == "Task" && (owner == null ? now - Max(item.UpdatedAt, _workLoadedAt) > WorkTaskGone : !PresentLocked(owner, now) && now - owner.LastSeen > WorkTaskGone))
            {
                (remove ??= new()).Add(item.Key);
                continue;
            }
            if (item.Kind == "F8" && now - item.UpdatedAt > WorkF8NoNews)
            {
                WorkSetStageLocked(item, "Dropped", "No news for 3 h", now, toast: false);
                WorkTouchLocked(item, now);
                continue;
            }
            WorkDeriveLocked(item, owner, now, guide, hlAsks);
        }
        if (remove != null)
        {
            foreach (var k in remove)
            {
                if (_work.Remove(k, out var gone) && gone is { Kind: "F8", Final: true, Seq: long es })
                {
                    // Remembered (bounded) so a late reply for it never makes a new, non-final row.
                    if (_workEndedSeqs.Add(es)) _workEndedOrder.Enqueue(es);
                    while (_workEndedOrder.Count > WorkEndedKeep) _workEndedSeqs.Remove(_workEndedOrder.Dequeue());
                }
            }
            _workRev++;
        }
        WorkPublishDueLocked(now, pubs);
        WorkPublishViewLocked(now);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>The derived fields of one active item; a change marks it for a "work" event and the view (not UpdatedAt).</summary>
    private void WorkDeriveLocked(AgentWorkDto item, SessionInfo? owner, DateTime now,
        (string status, string? instruction, string? who, string? session) guide, Dictionary<string, string>? hlAsks)
    {
        var name = owner != null ? DisplayNameLocked(owner) : "";
        var hue = owner == null ? -1 : owner.Hue is >= 0 and < AttnHues ? owner.Hue : AttnHueFallback;
        var stale = owner != null ? !PresentLocked(owner, now) : item.Kind == "Task";
        _agentRoutes.TryGetValue(item.Owner ?? "", out var route);
        // WaitingOn = Player: what the owner shows the player (C1): answer > step > recording > route.
        string? needs = null, ask = null;
        if (item.Owner is { } sid)
        {
            foreach (var i in _attn)
            {
                if (i.SessionId != sid || i.Status is not ("queued" or "showing")) continue;
                if (i.Kind == "ask") { needs = "answer"; ask = i.Question ?? i.Title; break; }
                if (needs == null) { needs = "step"; ask = i.PendingParams?["instruction"]?.ToString() is { Length: > 0 } ins ? ins : i.Title; }
            }
            if (needs != "answer" && hlAsks != null && hlAsks.TryGetValue(sid, out var q)) { needs = "answer"; ask = q; }
            if (needs == null && guide.session == sid && guide.instruction != null && guide.status is "waiting" or "detected" or "settling")
            { needs = "step"; ask = guide.instruction; }
            if (needs == null)
                foreach (var st in Queue())
                    if (st.Status == "queued" && st.Session == sid && !st.Chain) { needs = "recording"; ask = st.Title ?? st.Label; break; }
            if (needs == null && route is { Leading: true, Manual: true }) { needs = "route"; ask = route.Title ?? route.Label; }
        }
        var waiting = needs != null ? "Player" : item.WaitingOnSet ?? (item.Kind == "F8" && item.Stage == "Pressed" ? "Session" : "None");
        ask = needs != null ? Clip(ask ?? item.AskSet, 120) : null;   // never a stale derived line: Ask exists only while WaitingOn = Player
        if (waiting != item.WaitingOn) { item.WaitSince = waiting is "Ci" or "Review" or "Restart" or "OtherAgent" ? now : null; item.WaitToasted = false; }
        var nobody = item.Kind == "F8" && item.Stage == "Pressed" && now - item.StartedAt > WorkNobodyAfter;
        var stalled = (item.WaitSince is { } ws && now - ws > WorkWaitToastAfter) || (item.Kind == "F8" && now - item.StageAt > WorkStallAfter);
        var routeView = route is { Leading: true } || (route != null && now - route.At < TimeSpan.FromSeconds(5)) ? route : null;
        if (name != item.OwnerName || hue != item.Hue || stale != item.Stale || waiting != item.WaitingOn || needs != item.NeedsYou
            || ask != item.Ask || nobody != item.NobodyListening || stalled != item.Stalled || !Equals(routeView, item.Route))
        {
            item.OwnerName = name; item.Hue = hue; item.Stale = stale; item.WaitingOn = waiting; item.NeedsYou = needs;
            item.Ask = ask;
            item.NobodyListening = nobody; item.Stalled = stalled; item.Route = routeView;
            item.PubDirty = true;
            _workRev++;
        }
        // A long CI / review / restart / agent wait: one quiet toast (3.1).
        if (item.Kind == "F8" && !item.WaitToasted && item.WaitSince is { } since && now - since > WorkWaitToastAfter)
        {
            item.WaitToasted = true;
            var mins = Math.Max(1, (int)Math.Round((now - since).TotalMinutes));
            GuideLog(new JObject
            {
                ["title"] = "STILL SHIPPING", ["kind"] = "agent",
                ["text"] = $"#{item.Seq} waits for {WorkWaitWords(item.WaitingOn)} ({mins} min)",
            });
        }
    }

    private static string WorkWaitWords(string waiting) => waiting switch
    {
        "Ci" => "CI", "Review" => "review", "Restart" => "a restart", "OtherAgent" => "another agent", _ => waiting.ToLowerInvariant(),
    };

    /// <summary>Queue "work" events for items that changed, at most once a second each (the newest state always follows).</summary>
    private void WorkPublishDueLocked(DateTime now, List<JObject> pubs)
    {
        foreach (var item in _work.Values)
        {
            if (!item.PubDirty || now - item.LastPubAt < WorkPubEvery) continue;
            item.PubDirty = false;
            item.LastPubAt = now;
            pubs.Add(JObject.FromObject(item, WorkSerializer));
        }
    }

    /// <summary>
    /// Outside the sessions lock: the "work" events (pub/sub kind work, source bridge). Handed to one pool worker at a time
    /// (the events.jsonl append and the local handlers never run on the render thread; order kept), never after WorkClose.
    /// </summary>
    private void WorkFlush(List<JObject> pubs)
    {
        if (pubs.Count == 0 || _workClosed) return;
        foreach (var o in pubs) _workOutbox.Enqueue(o);
        if (Interlocked.CompareExchange(ref _workOutboxBusy, 1, 0) == 0) ThreadPool.QueueUserWorkItem(_ => WorkOutboxDrain());
    }

    private void WorkOutboxDrain()
    {
        while (true)
        {
            while (_workOutbox.TryDequeue(out var o))
            {
                if (_workClosed) continue;   // a later bridge load hands out these seqs: drop, never publish from an ended load
                try { PubPublish("work", o, "bridge"); }
                catch (Exception ex) { LogFault($"[Work] publishing work {o["Key"]}: {ex.Message}"); }
            }
            Volatile.Write(ref _workOutboxBusy, 0);
            // An enqueue that saw busy = 1 just before it was cleared: take it up here (else the next WorkFlush would).
            if (_workOutbox.IsEmpty || Interlocked.CompareExchange(ref _workOutboxBusy, 1, 0) != 0) return;
        }
    }

    // ── The drawer's view ────────────────────────────────────────────

    /// <summary>Rebuild WorkView when anything changed since the last one (rev), else keep the instance.</summary>
    private void WorkPublishViewLocked(DateTime now)
    {
        if (_workViewRev == _workRev) return;   // recent items leaving (10 min) and every derived change bump the rev
        _workViewRev = _workRev;
        var rows = WorkOrderedLocked(activeOnly: true).Select(WorkRowOf).ToArray();
        var recent = _work.Values.Where(w => w.Final).OrderByDescending(w => w.FinalAt).Select(WorkRowOf).ToArray();
        var f8 = rows.Where(r => r.Kind == "F8").OrderBy(r => r.NeedsYou ? 0 : 1).ThenBy(r => r.Seq).ToArray();
        DateTime? failedAt = null;
        foreach (var r in recent) if (r.Stage == "Failed" && (failedAt == null || r.FinalAt > failedAt)) failedAt = r.FinalAt;
        _workView = new WorkView(_workRev, rows, f8, recent, rows.Length, rows.Count(r => r.NeedsYou), f8.Length > 0, failedAt);
    }

    private WorkRowView WorkRowOf(AgentWorkDto w)
    {
        var f8 = w.Kind == "F8";
        var phase = WorkPhaseIndex(w.Stage);
        var reached = 0;
        foreach (var h in w.History) { var pi = WorkPhaseIndex(h.Stage); if (pi < 5 && pi > reached) reached = pi; }
        if (w.Stage == "Done") reached = 4;
        var word = phase < 5 ? WorkPhaseWords[phase] : w.Stage.ToUpperInvariant();
        var tone = w.Stage switch
        {
            "Done" => WorkTone.Ok, "Failed" => WorkTone.Bad, "Dropped" => WorkTone.Neutral,
            _ when w.Stalled || w.NobodyListening => WorkTone.Warn,
            _ when f8 && phase == 0 => WorkTone.Neutral,
            _ => WorkTone.Accent,
        };
        var needs = w.WaitingOn == "Player";
        string? needsText = !needs ? null : w.NeedsYou switch
        {
            "answer" => "answer above",
            "recording" => "start the recording",
            "route" => $"follow {(w.OwnerName.Length > 0 ? w.OwnerName : "Claude")}'s route",
            _ => "do the step above",
        };
        WorkTailKind tailKind; string? tail; var tailWarn = false;
        if (needs) { tailKind = WorkTailKind.NeedsYou; tail = needsText; }
        else if (w.Stage == "LiveFix") { tailKind = WorkTailKind.LiveFix; tail = "live fix"; }
        else if (!w.Final && w.WaitingOn is "Ci" or "Review" or "Restart" or "OtherAgent")
        { tailKind = WorkTailKind.Waiting; tail = "waits for " + WorkWaitWords(w.WaitingOn); tailWarn = w.Stalled; }
        else if (w.NobodyListening) { tailKind = WorkTailKind.Nobody; tail = "nobody listening yet"; tailWarn = true; }
        else if (w.Text != null) { tailKind = WorkTailKind.Text; tail = w.Text; }
        else { tailKind = WorkTailKind.None; tail = null; }
        var label = f8 ? $"F8 #{w.Seq}" : w.Title;
        var request = f8
            ? w.Flags is { Length: > 0 } fl && fl.FirstOrDefault(f => f != "something-else") is { } first ? WorkFlagLabel(first)
              : w.Note != null ? Clip(w.Note, 24)! : "Feedback"
            : w.Title;
        // The hover (3.2): title, the stage history, the current text and detail, and a foot.
        // A bare item (a reply for a press this load never saw) has the placeholder title "F8 #n": name the request instead
        // of repeating the number ("F8 #12 - F8 #12").
        var hoverWhat = f8 && w.Title == label ? request : w.Title;
        var hoverTitle = f8 ? $"{label} - {hoverWhat}" + (w.Scope != null ? $" - {WorkScopeWords(w.Scope)}" : "") : w.Title;
        var body = new StringBuilder();
        foreach (var h in w.History)
        {
            if (body.Length > 0) body.Append('\n');
            body.Append(h.At.ToLocalTime().ToString("HH:mm:ss")).Append("  ").Append(h.Stage);
            if (h.Stage == "Claimed" && w.OwnerName.Length > 0) body.Append(" by ").Append(w.OwnerName);
            if (h.Text != null) body.Append(" - ").Append(Clip(h.Text, 80));
        }
        var note = w.Text != null && w.Detail != null ? w.Text + "\n" + w.Detail : w.Text ?? w.Detail;
        if (needs && w.Ask != null) note = "Needs you: " + w.Ask + (note != null ? "\n" + note : "");
        string? foot = null;
        if (w.Pr is { } pr)
            foot = $"PR #{pr.Number}" + (pr.Checks switch { "pending" => " - CI running", "green" => " - CI green", "red" => " - CI red", _ => "" });
        else if (w.NobodyListening)
            foot = "The F8 dispatcher runs on the shared MCP server; start any Claude session on this machine to bring it up.";
        else if (f8 && !w.Final)
            foot = $"Open \"F8 #{w.Seq}: ...\" in Claude to watch it";
        return new WorkRowView(w.Key, w.Kind, w.Seq, w.Owner, w.OwnerName, w.Hue, w.Owner != null && w.Hue >= 0,
            label, request, w.Title, w.Stage, w.Phase, phase, reached, word, tone,
            w.WaitingOn, needs, needsText, needs ? w.Ask : null, tailKind, tail, tailWarn,
            w.StartedAt, w.StageAt, w.UpdatedAt, w.FinalAt, w.Final, w.Stale, w.Stalled, w.NobodyListening,
            hoverTitle, body.ToString(), note, foot, w.Pr, w.Route);
    }

    private static string WorkScopeWords(string scope) => scope.ToLowerInvariant() switch
    {
        "step" => "this step", "panel" => "this panel", "area" => "this area", "marker" => "this marker", "route" => "the route", _ => scope,
    };

    /// <summary>One line per item for the chip's hover until the board replaces it: "F8 #12: fixing - live fix".</summary>
    internal static string WorkHoverLine(WorkRowView r) =>
        $"{r.Label}: {r.StageWord.ToLowerInvariant()}" + (r.Tail != null ? " - " + (r.NeedsYou ? "needs you, " : "") + (r.Tail.Length > 60 ? r.Tail[..58] + ".." : r.Tail) : "");

    // ── Agent route as the owner's Doing (row 16) ────────────────────

    /// <summary>
    /// Leading agent routes as blockers of kind "route" (BlockersLocked): the owner's Doing ("leading you to the lever room")
    /// and a restart waits for it, only while the owner is connected and the route is within its ttl (WorkTickLocked ends it
    /// otherwise). Not a pilot blocker: it is not an ask (a Manual step shows as WaitingOn = Player on the work item instead).
    /// </summary>
    private void WorkRouteBlockersLocked(List<Blocker> list, string? requesterLabel)
    {
        if (_agentRoutes.Count == 0) return;
        foreach (var (owner, r) in _agentRoutes)
        {
            if (!r.Leading || !_sessions.TryGetValue(owner, out var s) || !s.Connected) continue;
            var ttl = _agentRouteInfo.TryGetValue(r.Id, out var info) ? info.TtlSec : WorkRouteTtlDefault;
            list.Add(new Blocker("route", $"leading you to {r.Label ?? r.Title ?? "a place on the map"}", s.Label, r.At.AddSeconds(ttl),
                requesterLabel != null && s.Label == requesterLabel, Session: s.Id));
        }
    }

    // ── Persistence ──────────────────────────────────────────────────

    private void WorkLoadLocked()
    {
        if (_workLoaded) return;
        _workLoaded = true;
        _workLoadedAt = DateTime.UtcNow;
        try
        {
            if (string.IsNullOrEmpty(_bridgeDir) || !File.Exists(WorkFile)) return;
            var dropped = 0;
            foreach (var t in JArray.Parse(File.ReadAllText(WorkFile)))
            {
                AgentWorkDto? i = null;
                if (t is JObject o)
                {
                    o.Remove("Route");   // derived, never restored
                    try { i = o.ToObject<AgentWorkDto>(WorkSerializer); }
                    catch (Exception) { i = null; }   // one entry of the wrong shape is skipped, the rest restored
                }
                if (WorkRestoredOk(i) is not { } ok || _work.ContainsKey(ok.Key)) { dropped++; continue; }
                _work[ok.Key] = ok;
            }
            if (dropped > 0) LogFault($"[Work] {WorkFile}: {dropped} entr{(dropped == 1 ? "y" : "ies")} unreadable or incomplete, skipped (the rest restored)");
            _workRev++;
            _workSavedKey = WorkSaveKeyLocked();
        }
        catch (Exception ex) { LogFault($"[Work] {WorkFile} unreadable, starting empty: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>One entry of items.json, checked (a hand-edited or half-written file never breaks the HUD), or null to skip it.
    /// Only active items are persisted; derived fields are recomputed by the next tick.</summary>
    private static AgentWorkDto? WorkRestoredOk(AgentWorkDto? i)
    {
        if (i == null || string.IsNullOrWhiteSpace(i.Key) || i.Key.Length > 100) return null;
        var kind = i.Kind?.Equals("F8", StringComparison.OrdinalIgnoreCase) == true ? "F8" : i.Kind?.Equals("Task", StringComparison.OrdinalIgnoreCase) == true ? "Task" : null;
        if (kind == null) return null;
        if (kind == "F8" && (i.Seq is not > 0 || i.Key != "f8:" + i.Seq)) return null;
        if (kind == "Task" && (i.Key.StartsWith("f8:", StringComparison.Ordinal) || i.Owner != i.Key)) return null;
        var stage = WorkParseStage(i.Stage);
        if (stage == null || stage is "Done" or "Failed" or "Dropped") return null;
        var now = DateTime.UtcNow;
        i.Kind = kind; i.Stage = stage; i.Phase = WorkPhases[WorkPhaseIndex(stage)];
        if (i.StartedAt == default || i.StartedAt > now) i.StartedAt = now;
        if (i.StageAt == default || i.StageAt > now) i.StageAt = i.StartedAt;
        if (i.UpdatedAt == default || i.UpdatedAt > now) i.UpdatedAt = i.StageAt;
        i.Title = Clip(string.IsNullOrWhiteSpace(i.Title) ? (kind == "F8" ? $"F8 #{i.Seq}" : "Task") : i.Title, 60)!;
        i.Text = Clip(i.Text, 400); i.Detail = Clip(i.Detail, 400); i.Note = Clip(i.Note, 300); i.AskSet = Clip(i.AskSet, 120); i.Ask = null;
        i.Owner = Clip(i.Owner, 80); i.Scope = Clip(i.Scope, 20);
        i.Flags = i.Flags?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Clip(f, 24)!).Take(12).ToArray();
        i.WaitingOnSet = WorkParseWait(i.WaitingOnSet) is { } w and not ("None" or "Player") ? w : null;
        i.History = (i.History ?? new()).Where(h => h != null && WorkParseStage(h.Stage) != null).TakeLast(WorkHistoryMax).ToList();
        foreach (var h in i.History) { h.Stage = WorkParseStage(h.Stage)!; h.Text = Clip(h.Text, 400); }
        i.Final = false; i.FinalAt = null; i.Route = null; i.OwnerName = ""; i.Hue = -1; i.NeedsYou = null;
        i.WaitingOn = i.WaitingOnSet ?? (kind == "F8" && stage == "Pressed" ? "Session" : "None");
        // The derive pass starts a wait's clock only when WaitingOn changes, and a restored wait already holds its value: start
        // it here (from the restore; the time before it is unknown), so the 90 s stall and STILL SHIPPING still come.
        i.WaitSince = i.WaitingOn is "Ci" or "Review" or "Restart" or "OtherAgent" ? now : null;
        i.WaitToasted = false;
        i.Stale = i.NobodyListening = i.Stalled = false;
        return i;
    }

    private string WorkSaveKeyLocked() =>
        string.Join(";", _work.Values.Where(w => !w.Final).OrderBy(w => w.Key)
            .Select(w => $"{w.Key}:{w.Stage}:{w.UpdatedAt.Ticks}:{w.Owner}:{w.WaitingOnSet}"));

    /// <summary>Outside the sessions lock: write items.json when the active items changed (tmp + move).</summary>
    private void WorkSaveIfChanged()
    {
        string? json = null;
        long gen;
        lock (_sessionLock)
        {
            if (!_workLoaded || _workSavedRev == _workRev) return;
            _workSavedRev = _workRev;
            var key = WorkSaveKeyLocked();
            if (key == _workSavedKey) return;
            _workSavedKey = key;
            // Without the derived agent route (it is rebuilt from progress events; the record never has to deserialize).
            var arr = new JArray();
            foreach (var w in _work.Values.Where(w => !w.Final).OrderBy(w => w.StartedAt))
            {
                var o = JObject.FromObject(w, WorkSerializer);
                o.Remove("Route");
                arr.Add(o);
            }
            json = arr.ToString(Formatting.Indented);
            gen = ++_workSaveGen;
        }
        // TCP threads (work.set / work.end) and the render thread (SessionsTick) save: one writer at a time, and a snapshot
        // older than the one already written is skipped (it would overwrite newer items; the newer key is already recorded).
        lock (_workFileLock)
        {
            if (gen <= _workWrittenGen) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(WorkFile)!);
                var tmp = WorkFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, WorkFile, true);
                _workWrittenGen = gen;
            }
            catch (Exception ex)
            {
                LogFault($"[Work] save failed ({WorkFile}): {ex.Message}");
                lock (_sessionLock) { if (_workSaveGen == gen) _workSavedKey = null; }   // the next change writes again
            }
        }
    }

    // ── Parsing helpers ──────────────────────────────────────────────

    /// <summary>A key in either casing (the MCP's legacy replies are lowercase {ref, status, text}, the new fields PascalCase).</summary>
    private static JToken? WorkGet(JObject? o, string name) => o?.GetValue(name, StringComparison.OrdinalIgnoreCase);

    private static string? WorkStr(JObject? o, string name, int max)
    {
        var t = WorkGet(o, name);
        if (t == null || t.Type is JTokenType.Null or JTokenType.Undefined or JTokenType.Object or JTokenType.Array) return null;
        var s = t.ToString().Trim();
        return s.Length == 0 ? null : Clip(s, max);
    }

    private static long? WorkLong(JObject? o, string name)
    {
        var t = WorkGet(o, name);
        if (t == null) return null;
        if (t.Type == JTokenType.Integer) return t.Value<long>();
        if (t.Type == JTokenType.Float) return (long)t.Value<double>();
        return t.Type == JTokenType.String && long.TryParse(t.ToString(), out var v) ? v : null;
    }

    private static string? WorkParseStage(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().Replace("-", "").Replace("_", "");
        foreach (var x in WorkStages) if (x.Equals(t, StringComparison.OrdinalIgnoreCase)) return x;
        return null;
    }

    private static string? WorkParseWait(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().Replace("-", "").Replace("_", "");
        foreach (var x in WorkWaits) if (x.Equals(t, StringComparison.OrdinalIgnoreCase)) return x;
        return null;
    }

    /// <summary>F8Phase index (8.1): 0 Sent, 1 Looking, 2 Fixing, 3 Shipping, 4 Checking, 5 Ended.</summary>
    private static int WorkPhaseIndex(string stage) => stage switch
    {
        "Pressed" or "Queued" => 0,
        "Claimed" or "Reading" or "Investigating" => 1,
        "Fixing" or "LiveFix" or "Reviewing" => 2,
        "Merging" or "Deploying" => 3,
        "Verifying" => 4,
        _ => 5,
    };
}
