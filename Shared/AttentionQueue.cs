using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// The attention queue (research\attention-queue.md, card rules in research\attention-queue-ui.md): several agent sessions
/// share one player, so one of them has the floor at a time - the guide card plus the right to start a step, a flow or an
/// ask - and everyone else is queued and knows where. Restarts go first, turns are round-robin by session, and nothing new
/// shows in combat, on a loading screen or in the Esc menu.
///   attention.ask     {title?, question, options:[{id?, label, hint?} | "label"] (2-4), urgency?: now|calm, ttlSec?, showTtlSec?, detail?}
///   attention.request {kind: step|flow, title?, urgency?, ttlSec?}
///                     both -> the item {id, status, position, ahead:[who], ...}; status=showing means you hold the floor now
///   attention.result  {id}            the item (status, answer, laterCount, eligibleAt, mergedInto, reason)
///   attention.cancel  {id}            own items only
///   attention.release {id}            own items only; marks it done -> the item plus next
///   attention.state   {}              {seq, floor, queue:[...], restart, player:{state, combat, calm, town, idleSec, ...}}
/// position: 0 = you hold the floor; otherwise your place in line counting the holder and a waiting restart, so
/// position 2 with ahead [whats-a-route] reads "2nd in line, after whats-a-route".
/// Later (the card's button) is a transition: back to queued as calm, not eligible for 180 s (then 600 s); a third Later
/// is dismissed (reason later_x3). A showing ask expires after showTtlSec (900 s) counted from when the player could see
/// it (floor.visibleSince: after the hand-over gap, outside combat). Identical asks of different sessions within 60 s
/// share one card and one answer (mergedInto / answer.sharedWith). Queued asks survive a HUD restart
/// (&lt;BridgeDirectory&gt;\attention\queue.json).
/// Ownership: guide.set with an instruction, guide.flow and experiment.start from a session that does not hold the floor
/// are queued as implicit items (the call is replayed when its turn comes), never written over the holder's card; a
/// guide.highlight with asked targets from such a session draws its boxes but holds its question strips until its turn.
/// State is in memory under the sessions lock (Sessions.cs): the same lock as restarts, so "restart first" is one
/// decision. Lock order as in Sessions.cs: the sessions lock may take the guide / flow / highlight locks, never the
/// other way round; side effects of a decision (replaying a call, clearing a card) run after the lock is released.
/// </summary>
public partial class WhatsAnAiBridge
{
    internal sealed class AttnOption { public string Id = ""; public string Label = ""; public string? Hint; }
    internal sealed class AttnAnswer { public string OptionId = ""; public string Label = ""; public DateTime At; public List<string>? SharedWith; }

    internal sealed class AttentionItem
    {
        public string Id = "";
        public string SessionId = "";       // owner (SessionInfo.Id); "queue:<step>" for a queued step with no session
        public string Who = "";             // owner label at creation: what writes made later from Render stamp on the card
        public string Kind = "ask";         // ask | step | flow (restarts are mirrored from Sessions.cs, not stored here)
        public string Urgency = "now";      // now | calm
        public string Title = "";
        public string? Question, Detail;
        public List<AttnOption> Options = new();
        public DateTime CreatedAt;
        public DateTime QueuedAt;           // in line since (CreatedAt, or the last Later): order within a session and ttl
        public DateTime? EligibleAt;        // after a Later: not before this
        public int LaterCount;
        public int TtlSec;
        public int ShowTtlSec = AttnShowTtlDefault;
        public string Status = "queued";    // queued | showing | answered | dismissed | expired | cancelled | done
        public AttnAnswer? Answer;
        public DateTime? ShownAt, VisibleSince, EndedAt;
        public string? MergedInto;          // an identical ask of another session carries this one (one card, one answer)
        public bool Implicit;               // made by a guide.set / guide.flow / experiment.start / asked highlight from a non-holder
        public bool Detached;               // a queued experiment step: runs without its agent, not cleaned on disconnect
        public bool Priority;               // the user pressed Start (queued step): next after a restart
        public string? Pending;             // the call to replay when granted: guide.set | guide.flow | experiment.start
        public JToken? PendingParams;
        [JsonIgnore] public bool ReplayDue; // granted, the replay waits for the hand-over gap (the previous card's receipt and fade)
        [JsonIgnore] public DateTime? IdleSince; // an explicit step / flow turn whose card or flow is not up (AttnTickLocked: turn_idle)
        public string? StepId;              // the queued experiment step it covers
        public string? HlOwner;             // the highlight layer whose question strips it holds (asked highlight)
        public string? Reason;              // why it ended, as a code: later_x3 | unanswered | disconnected | ...
        public string? Why;                 // the same in words
    }

    /// <summary>The player as the arbiter saw it last tick (Render, 4 Hz); the request path reads only this copy.</summary>
    private readonly record struct AttnPlayer(string State, bool Combat, bool Calm, bool Town, double IdleSec, double NoCombatSec, bool BigPanel, string? Broken)
    {
        /// <summary>Loading screens and the Esc menu are neither calm nor combat: nothing new shows, nothing hides.</summary>
        public bool Paused => State is "loading" or "menu";
    }

    /// <summary>
    /// The showing ask, as the card draws it (a copy, rebuilt only when the queue changes). Whos / Sessions / Hues list
    /// the owner first, then the sessions whose identical ask shares this card (labels and ids: the panel names them
    /// through AgentsView, never by label). LaterAt: when the player last said Later (the "again" pill). Softened: the
    /// session re-asked within 60 s of the player's x, so the card arrives without flash or pulse. Hidden: the hand-over
    /// gap has not passed yet (visibleSince unset).
    /// </summary>
    internal sealed record AskView(string Id, string[] Whos, string[] Sessions, int[] Hues, string Title, string Question, string? Detail, AttnOption[] Options,
        string Urgency, DateTime CreatedAt, DateTime ShownAt, DateTime? VisibleSince, int LaterCount, DateTime? LaterAt, bool Softened, bool Hidden);

    /// <summary>
    /// One waiting item for the next strip and its hover: who (label and session id; the panel shows the display name),
    /// in which hue, what (an ask's question, else the title), and why not yet.
    /// </summary>
    internal sealed record AttnQueueRow(string Who, string Session, int Hue, string Kind, string Text, string Urgency, DateTime QueuedAt, DateTime? EligibleAt);

    /// <summary>
    /// The queue as the panel shows it (rebuilt only when the queue changes): the waiting items in turn order, the floor
    /// holder (for the strip's "after this" and whose card it is), and the player's state (for "when things calm down").
    /// Only what waits for the player: idle sessions are not in it (the in-game UI never lists them).
    /// </summary>
    internal sealed record AttnQueueView(AttnQueueRow[] Rows, string? HolderWho, string? HolderSession, string? HolderKind, string? HolderUrgency, bool RestartWaiting,
        bool Calm, bool Combat, bool Paused)
    {
        public static readonly AttnQueueView Empty = new([], null, null, null, null, false, false, false, false);
    }

    private static readonly HashSet<string> AttnEnded = new(StringComparer.Ordinal) { "answered", "dismissed", "expired", "cancelled", "done" };
    private const int AttnTtlDefaultAsk = 600, AttnTtlDefaultRequest = 300, AttnTtlImplicit = 300, AttnTtlMin = 10, AttnTtlMax = 3600;
    private const int AttnShowTtlDefault = 900;                                             // AskShowingMaxSec
    private const double AttnCalmNoCombatSec = 8, AttnCalmIdleSec = 5, AttnHandoverGapSec = 0.6, AttnDedupWindowSec = 60;
    private static readonly int[] AttnLaterHoldSec = [180, 600];                            // the third Later dismisses
    private static readonly TimeSpan AttnKeepEnded = TimeSpan.FromMinutes(30), AttnDisconnectGrace = TimeSpan.FromSeconds(10),
        AttnImplicitGrace = TimeSpan.FromSeconds(1),
        // A turn that shows nothing (an explicit step or flow turn with no card or flow of its own up) or whose agent went
        // silent ends after these while someone else waits (an item, a restart): one agent never holds the player.
        AttnTurnIdle = TimeSpan.FromSeconds(30), AttnOwnerQuiet = TimeSpan.FromMinutes(5),
        // Asks restored from queue.json whose session never says hello again on this HUD are cancelled after this.
        AttnRestoreGrace = TimeSpan.FromSeconds(60);
    internal const int AttnHues = 6, AttnHueFallback = 5;                                   // violet rose sky orange sand slate

    private readonly List<AttentionItem> _attn = new();            // under _sessionLock; ended items kept AttnKeepEnded
    private readonly Dictionary<string, DateTime> _attnTurnEnded = new(StringComparer.Ordinal);   // session -> its last turn ended
    private int _attnIds;
    private long _attnSeq;                                          // attention.state seq: bumped on every change
    private AttnPlayer _attnPlayer = new("loading", false, false, false, 0, 0, false, null);
    private string _attnRestartKey = "";                            // waiting/go restarts last tick (a change bumps seq)
    private double _attnLastCombatAt = -1e9;
    // The panel's copies (AttnPublishLocked): rebuilt under the lock when the queue changed, read by Render without it.
    private volatile AskView? _askPub;
    private volatile AttnQueueView _queuePub = AttnQueueView.Empty;
    private long _attnPubSeq = -1;
    private bool _attnPubRestart;
    // The panel's card slot: when the card that is leaving (answer receipt, fade) is gone. The hand-over gap counts from
    // here (attention-queue-ui.md 5): the next card's visibleSince, a queued call's replay and a restart's countdown wait
    // for max(granted, slot free) + the gap, so the player sees one card leave before the next arrives.
    private DateTime _attnSlotFreeAt = DateTime.MinValue;
    private bool _attnLoaded;
    private DateTime _attnLoadedAt;
    private string _attnSavedKey = "";

    private string? ProcessAttentionMethod(string method, JToken? p) => method switch
    {
        "attention.ask" => SafeMemory(() => AttnAsk(p)),
        "attention.request" => SafeMemory(() => AttnRequest(p)),
        "attention.result" => SafeMemory(() => AttnResult(p?["id"]?.ToString())),
        "attention.cancel" => SafeMemory(() => AttnCancel(p?["id"]?.ToString())),
        "attention.release" => SafeMemory(() => AttnRelease(p?["id"]?.ToString())),
        "attention.state" => SafeMemory(AttnState),
        _ => null,
    };

    // ── Requests ─────────────────────────────────────────────────────

    private JObject AttnAsk(JToken? p)
    {
        var me = CurrentSession();
        if (me == null) return Err("no_session", "Send session.hello on this connection first: an ask belongs to a session (the MCP server does this on connect).");
        var question = Clip(p?["question"]?.ToString(), 120);
        if (string.IsNullOrWhiteSpace(question)) return Err("missing_question", "Pass question: what you want to know, in game words (up to 120 characters).");
        if (p?["options"] is not JArray raw) return Err("missing_options", "Pass options: 2-4 choices, each \"label\" or {id?, label, hint?}. The card adds Later itself.");
        if (raw.Count is < 2 or > 4) return Err("bad_options", $"Pass 2-4 options (got {raw.Count}); the card adds Later itself.");
        var options = new List<AttnOption>();
        foreach (var o in raw)
        {
            var label = Clip(o.Type == JTokenType.Object ? o["label"]?.ToString() : o.ToString(), 24);
            if (string.IsNullOrWhiteSpace(label)) return Err("bad_option", $"Option {options.Count + 1} has no label.");
            var id = Clip(o.Type == JTokenType.Object ? o["id"]?.ToString() : null, 40);
            if (string.IsNullOrWhiteSpace(id)) id = AttnSlug(label!, options.Count + 1);
            if (options.Any(x => x.Id == id)) return Err("bad_option", $"Option id '{id}' is used twice: ids must be unique.");
            options.Add(new AttnOption { Id = id!, Label = label!, Hint = Clip(o.Type == JTokenType.Object ? o["hint"]?.ToString() : null, 80) });
        }
        if (!AttnUrgency(p?["urgency"], out var urgency)) return Err("bad_urgency", "urgency must be now or calm.");
        var item = new AttentionItem
        {
            Kind = "ask", Title = Clip(p?["title"]?.ToString(), 40) ?? "Question", Question = question, Detail = Clip(p?["detail"]?.ToString(), 160),
            Options = options, Urgency = urgency, TtlSec = AttnTtl(p?["ttlSec"], AttnTtlDefaultAsk),
            ShowTtlSec = Math.Clamp(p?["showTtlSec"]?.Type is JTokenType.Integer or JTokenType.Float ? p["showTtlSec"]!.Value<int>() : AttnShowTtlDefault, 30, AttnTtlMax),
        };
        return AttnAdd(me, item);
    }

    private JObject AttnRequest(JToken? p)
    {
        var me = CurrentSession();
        if (me == null) return Err("no_session", "Send session.hello on this connection first: a turn belongs to a session.");
        var kind = p?["kind"]?.ToString();
        if (kind is not ("step" or "flow")) return Err("bad_kind", "kind must be step or flow (asks go through attention.ask; restarts through restart.request).");
        if (!AttnUrgency(p?["urgency"], out var urgency)) return Err("bad_urgency", "urgency must be now or calm.");
        var item = new AttentionItem
        {
            Kind = kind, Title = Clip(p?["title"]?.ToString(), 80) ?? AttnKindWord(kind),
            Urgency = urgency, TtlSec = AttnTtl(p?["ttlSec"], AttnTtlDefaultRequest),
        };
        return AttnAdd(me, item);
    }

    private JObject AttnAdd(SessionInfo me, AttentionItem item)
    {
        var effects = new List<Action>();
        JObject reply;
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            AttnLoadLocked();
            item.Id = AttnNextIdLocked(); item.SessionId = me.Id; item.Who = me.Label; item.CreatedAt = item.QueuedAt = now;
            if (item.Kind == "ask" && AttnDedupLeaderLocked(item, now) is { } leader) item.MergedInto = leader.Id;
            _attn.Add(item);
            _attnSeq++;
            // A step or flow turn asked for while this session holds the floor with an implicit turn (a plain guide.set it
            // made earlier): the new turn takes over at once. Waiting behind its own old card would hold it until that card
            // rests, which may be never. Same owner: no hand-over, no card cleared.
            if (item.Kind is "step" or "flow" && AttnHolderLocked() is { Implicit: true, Detached: false, StepId: null, ReplayDue: false, HlOwner: null } own
                && own.SessionId == me.Id && own.Kind == "step")
            {
                AttnEndLocked(own, "done", now, "superseded", "a new turn of the same agent took over", effects);
                item.Status = "showing"; item.ShownAt = now; item.VisibleSince = own.VisibleSince;
            }
            AttnArbitrateLocked(now, effects);
            reply = AttnItemJsonLocked(item, me.Id, now);
            // Queued behind someone (attention-queue-ui.md 6.6): an agent-kind line (off as a toast by default), so the
            // player who turned those on hears that a question waits, and after whom.
            if (item is { Kind: "ask", Question: not null, Status: "queued", MergedInto: null } && AttnHolderLocked() is { } holder)
            {
                var who = AttnNameLocked(item); var after = AttnNameLocked(holder);
                effects.Add(() => GuideLog(new JObject { ["text"] = $"{who} has a question for you - after {after}", ["kind"] = "agent", ["title"] = "Queued" }));
            }
        }
        AttnRun(effects);
        return reply;
    }

    /// <summary>An ask of another session with the same question and option labels, asked within the de-dup window.</summary>
    private AttentionItem? AttnDedupLeaderLocked(AttentionItem ask, DateTime now) =>
        _attn.FirstOrDefault(i => i.Kind == "ask" && i.Question != null && i.SessionId != ask.SessionId && i.MergedInto == null
            && i.Status is "queued" or "showing" && (now - i.CreatedAt).TotalSeconds <= AttnDedupWindowSec
            && string.Equals(i.Question, ask.Question, StringComparison.OrdinalIgnoreCase)
            && i.Options.Count == ask.Options.Count
            && i.Options.Zip(ask.Options).All(z => string.Equals(z.First.Label, z.Second.Label, StringComparison.OrdinalIgnoreCase)));

    private JObject AttnResult(string? id)
    {
        var me = CurrentSession();
        lock (_sessionLock)
        {
            AttnLoadLocked();
            var item = _attn.FirstOrDefault(i => i.Id == id);
            if (item == null) return AttnUnknown(id);
            return AttnItemJsonLocked(item, me?.Id, DateTime.UtcNow);
        }
    }

    private JObject AttnCancel(string? id) => AttnEndOwn(id, "cancelled", "cancel");

    private JObject AttnRelease(string? id) => AttnEndOwn(id, "done", "release");

    private JObject AttnEndOwn(string? id, string status, string verb)
    {
        var me = CurrentSession();
        if (me == null) return Err("no_session", $"Send session.hello on this connection first: only the owner can {verb} an item.");
        var effects = new List<Action>();
        JObject reply;
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            AttnLoadLocked();
            var item = _attn.FirstOrDefault(i => i.Id == id);
            if (item == null) return AttnUnknown(id);
            if (item.SessionId != me.Id) return Err("not_yours", $"Item {id} is {item.Who}'s ({item.Kind}: {item.Title}); {verb} acts only on your own items.");
            if (!AttnEnded.Contains(item.Status))
            {
                if (status == "cancelled" && item is { Kind: "ask", Status: "showing", VisibleSince: not null })
                {
                    var who = AttnNameLocked(item);
                    effects.Add(() => GuideLog(new JObject { ["text"] = $"{who} no longer needs an answer", ["kind"] = "step", ["title"] = "Withdrawn" }));
                }
                AttnEndLocked(item, status, now, verb == "cancel" ? "withdrawn" : null, verb == "cancel" ? "the agent withdrew it" : null, effects);
            }
            AttnArbitrateLocked(now, effects);
            reply = AttnItemJsonLocked(item, me.Id, now);
            var holder = AttnHolderLocked();
            reply["next"] = holder == null ? null : new JObject { ["id"] = holder.Id, ["who"] = holder.Who, ["kind"] = holder.Kind, ["title"] = holder.Title, ["hue"] = AttnHueLocked(holder.SessionId, holder.Who) };
        }
        AttnRun(effects);
        return reply;
    }

    private JObject AttnState()
    {
        var me = CurrentSession();
        lock (_sessionLock) { AttnLoadLocked(); return AttnStateJsonLocked(me?.Id, DateTime.UtcNow); }
    }

    private static JObject AttnUnknown(string? id) =>
        Err("unknown_item", $"No attention item '{id}': ended items are kept {AttnKeepEnded.TotalMinutes:0} min, queued asks survive a HUD restart, steps and flows do not (attention.state lists what is open).");

    private string AttnNextIdLocked() => $"A{++_attnIds}";

    // ── Ownership gate (guide.set / guide.flow / experiment.start / asked highlights) ──

    /// <summary>What the gate decided for a call that would put something in front of the player.</summary>
    private readonly record struct AttnGateResult(bool Proceed, AttentionItem? Item, JObject? Reply);

    /// <summary>
    /// A call that shows the player something (an instruction, a flow, a step): proceed when the caller holds the floor,
    /// or the floor is free and may be taken now (an implicit item is made for it); otherwise it is queued as an implicit
    /// item that replays the call when its turn comes, and the reply says where it is. A connection without a session
    /// (old scripts) proceeds only while nobody holds the floor.
    /// </summary>
    private AttnGateResult AttnGate(SessionInfo? me, string method, JToken? p, string kind, string? title, string? stepId = null)
    {
        var effects = new List<Action>();
        AttnGateResult result;
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            AttnLoadLocked();
            AttnArbitrateLocked(now, effects);
            var holder = AttnHolderLocked();
            if (me == null)
            {
                result = holder == null && !AttnRestartWaitingLocked()
                    ? new AttnGateResult(true, null, null)
                    : new AttnGateResult(false, null, Err("no_session", (holder != null ? $"{holder.Who} has the floor ({holder.Kind}: {holder.Title})" : "A HUD restart is waiting") +
                                                                     ": send session.hello on this connection so the call can wait its turn in the attention queue."));
            }
            else if (holder != null && holder.SessionId == me.Id) result = new AttnGateResult(true, holder, null);
            else if (holder == null && AttnMayGrantNowLocked() && !AttnOrderLocked(now).Any(i => AttnEligibleLocked(i, now)))
            {
                var item = new AttentionItem
                {
                    Id = AttnNextIdLocked(), SessionId = me.Id, Who = me.Label, Kind = kind, Title = title ?? AttnKindWord(kind), CreatedAt = now, QueuedAt = now,
                    TtlSec = AttnTtlImplicit, Implicit = true, StepId = stepId,
                };
                _attn.Add(item);
                AttnGrantLocked(item, now, effects, replay: false);
                result = new AttnGateResult(true, item, null);
            }
            else
            {
                // Queued: one implicit item per session and call kind; a newer call replaces what the older one would replay.
                var item = _attn.FirstOrDefault(i => i.SessionId == me.Id && i.Implicit && i.Status == "queued" && i.Pending == method && (stepId == null || i.StepId == stepId));
                if (item == null)
                {
                    item = new AttentionItem { Id = AttnNextIdLocked(), SessionId = me.Id, Who = me.Label, Kind = kind, CreatedAt = now, QueuedAt = now, TtlSec = AttnTtl(p?["ttlSec"], AttnTtlImplicit), Implicit = true };
                    _attn.Add(item);
                }
                item.Title = title ?? AttnKindWord(kind);
                item.Pending = method; item.PendingParams = p?.DeepClone(); item.StepId = stepId;
                _attnSeq++;
                result = new AttnGateResult(false, item, AttnQueuedReply(item, me.Id, now));
            }
        }
        AttnRun(effects);
        return result;
    }

    private JObject AttnQueuedReply(AttentionItem item, string? me, DateTime now)
    {
        var o = AttnItemJsonLocked(item, me, now);
        o["ok"] = true;
        o["queued"] = true;
        o["attention"] = item.Id;
        o["message"] = $"Queued, not shown: {AttnLineText(o)}. It shows by itself when it is your turn; attention.result {{id: {item.Id}}} follows it.";
        return o;
    }

    /// <summary>
    /// A guide.highlight with asked targets (C7): the boxes always draw, the question strips only while the asking session
    /// holds the floor. Otherwise one ask item stands in line for them ("N questions on ..."); its turn shows the strips,
    /// and it ends when every question is answered or the layer goes. Returns whether the strips are held now, and a
    /// note for the reply. The caller sets the layer with that hold (so a held strip never shows for a frame).
    /// </summary>
    private (bool hold, string? note) AttnHighlightAsks(SessionInfo? me, string layer, int questions, string? title)
    {
        if (me == null) return (false, null);
        var effects = new List<Action>();
        bool hold;
        string? note = null;
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            AttnLoadLocked();
            // A newer highlight from the same session replaces the questions the older one stood in line with.
            foreach (var old in _attn.Where(i => i.HlOwner == layer && i.Status == "queued").ToList()) AttnEndLocked(old, "cancelled", now, "replaced", "a newer highlight replaced it", effects);
            AttnArbitrateLocked(now, effects);
            var holder = AttnHolderLocked();
            var label = $"{questions} question{(questions == 1 ? "" : "s")}" + (title != null ? $" on {title}" : "");
            if (holder != null && holder.SessionId == me.Id) hold = false;
            else
            {
                var item = new AttentionItem
                {
                    Id = AttnNextIdLocked(), SessionId = me.Id, Who = me.Label, Kind = "ask", Title = Clip(label, 40)!, CreatedAt = now, QueuedAt = now,
                    TtlSec = AttnTtlImplicit, Implicit = true, HlOwner = layer,
                };
                _attn.Add(item);
                _attnSeq++;
                AttnArbitrateLocked(now, effects);
                hold = item.Status != "showing";
                if (hold) note = $"Boxes are on screen; the question strips wait for your turn: {AttnLineText(AttnItemJsonLocked(item, me.Id, now))} (attention item {item.Id}).";
            }
        }
        AttnRun(effects);
        return (hold, note);
    }

    /// <summary>"2nd in line, after whats-a-route" / "waiting for a calm moment" for a queued item's JSON.</summary>
    private static string AttnLineText(JObject item)
    {
        var pos = item["position"]?.Value<int>() ?? 0;
        var ahead = (item["ahead"] as JArray ?? []).Select(a => a.ToString()).ToList();
        var text = pos == 0 ? "you hold the floor" : $"{AttnOrdinal(pos)} in line" + (ahead.Count > 0 ? $", after {string.Join(", ", ahead)}" : "");
        if (item["waitingFor"]?.ToString() is { Length: > 0 } wf) text += $" (waiting for {wf})";
        return text;
    }

    private static string AttnOrdinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    // ── Arbiter ──────────────────────────────────────────────────────

    private AttentionItem? AttnHolderLocked() => _attn.FirstOrDefault(i => i.Status == "showing" && i.MergedInto == null);

    private bool AttnRestartWaitingLocked() => _restarts.Any(r => r.Status is "waiting" or "go");

    /// <summary>Nothing new while a restart waits, in combat, on a loading screen or in the Esc menu.</summary>
    private bool AttnMayGrantNowLocked() => !AttnRestartWaitingLocked() && !_attnPlayer.Combat && !_attnPlayer.Paused;

    /// <summary>A queued item may take a free floor now: calm items only in a calm moment, a Later'd one not before its hold.</summary>
    private bool AttnEligibleLocked(AttentionItem i, DateTime now) =>
        (i.Urgency != "calm" || _attnPlayer.Calm) && (i.EligibleAt == null || i.EligibleAt <= now);

    /// <summary>
    /// The queued items in turn order (followers of a de-duplicated ask excluded): what the user started first (by when),
    /// then round-robin by session: the session that has waited longest since its last turn (or since its first item, if
    /// that is later) goes next with its oldest item, then goes behind the others. Eligibility is the caller's.
    /// </summary>
    private List<AttentionItem> AttnOrderLocked(DateTime now)
    {
        var queued = _attn.Where(i => i.Status == "queued" && i.MergedInto == null).OrderBy(i => i.QueuedAt).ToList();
        var order = queued.Where(i => i.Priority).ToList();
        var bySession = queued.Where(i => !i.Priority).GroupBy(i => i.SessionId).ToDictionary(g => g.Key, g => new Queue<AttentionItem>(g));
        var key = bySession.ToDictionary(kv => kv.Key, kv =>
        {
            var first = kv.Value.Peek().QueuedAt;
            return _attnTurnEnded.TryGetValue(kv.Key, out var ended) && ended > first ? ended : first;
        });
        var behind = now;
        while (bySession.Count > 0)
        {
            var next = key.Where(kv => bySession.ContainsKey(kv.Key)).OrderBy(kv => kv.Value).First().Key;
            var q = bySession[next];
            order.Add(q.Dequeue());
            if (q.Count == 0) bySession.Remove(next);
            else key[next] = behind = behind.AddTicks(1);   // just had a (simulated) turn: behind everyone waiting now
        }
        return order;
    }

    /// <summary>
    /// One decision: expire what waited too long (in line, or on screen unanswered), then, when nobody holds the floor,
    /// hand it to the next eligible item.
    /// </summary>
    private void AttnArbitrateLocked(DateTime now, List<Action> effects)
    {
        AttnLoadLocked();
        foreach (var i in _attn.Where(i => i.Status == "queued" && i.MergedInto == null && now - i.QueuedAt > TimeSpan.FromSeconds(i.TtlSec)).ToList())
            AttnEndLocked(i, "expired", now, "ttl", $"waited {i.TtlSec} s in line without a turn", effects);
        if (AttnHolderLocked() is { Kind: "ask", VisibleSince: { } vs } a && now - vs > TimeSpan.FromSeconds(a.ShowTtlSec))
        {
            var who = AttnWhosLocked(a);
            var q = a.Question ?? a.Title;
            AttnEndLocked(a, "expired", now, "unanswered", $"on screen {a.ShowTtlSec} s without an answer", effects);
            effects.Add(() => GuideLog(new JObject { ["text"] = $"{who} goes on without an answer: {q}", ["kind"] = "step", ["title"] = "Unanswered" }));
        }
        if (_attn.Count > 0) _attn.RemoveAll(i => i.EndedAt is { } e && now - e > AttnKeepEnded);
        if (AttnHolderLocked() != null || !AttnMayGrantNowLocked()) return;
        var next = AttnOrderLocked(now).FirstOrDefault(i => AttnEligibleLocked(i, now));
        if (next != null) AttnGrantLocked(next, now, effects, replay: true);
    }

    private void AttnGrantLocked(AttentionItem item, DateTime now, List<Action> effects, bool replay)
    {
        item.Status = "showing"; item.ShownAt = now; item.VisibleSince = null;
        foreach (var f in _attn.Where(f => f.MergedInto == item.Id)) { f.Status = "showing"; f.ShownAt = now; }
        _attnSeq++;
        var who = item.Who;
        // A new owner: the previous holder's card goes (an instruction nobody stands behind any more). The new holder's
        // own card, and cards nobody owns (a measurement), stay.
        var session = item.SessionId;
        effects.Add(() => GuideClearUnlessOwnedBy(session, who));
        if (item.HlOwner is { } layer) effects.Add(() => HighlightHoldAsks(layer, false));
        // A replayed call puts a card up at once, so it waits for the hand-over gap like an ask's visibleSince does
        // (AttnTickLocked runs it when the gap has passed, outside combat).
        if (replay && item.Pending != null) item.ReplayDue = true;
    }

    /// <summary>When the hand-over gap after <paramref name="shownAt"/> ends: the gap after the later of the grant and the slot coming free.</summary>
    private DateTime AttnGapEndLocked(DateTime shownAt) => (shownAt > _attnSlotFreeAt ? shownAt : _attnSlotFreeAt).AddSeconds(AttnHandoverGapSec);

    /// <summary>When the top slot of the panel is free for a card that is not a floor item (a restart): now, unless a card is leaving.</summary>
    private DateTime AttnSlotTopAtLocked(DateTime now)
    {
        var gapEnd = _attnSlotFreeAt.AddSeconds(AttnHandoverGapSec);
        return gapEnd > now ? gapEnd : now;
    }

    private void AttnReplayEffect(AttentionItem item, List<Action> effects)
    {
        item.ReplayDue = false;
        var method = item.Pending!;
        var p = item.PendingParams ?? new JObject();
        var who = item.Who;
        var id = item.Id;
        // A queued experiment step has no session of its own ("queue:<step>"): its card is HUD-run, so no disconnect clears it.
        var session = item.SessionId.StartsWith("queue:", StringComparison.Ordinal) ? null : item.SessionId;
        effects.Add(() => AttnReplay(method, p, who, session, id));
    }

    /// <summary>End an item (and the followers of a shared ask with it). reason: a code for agents; why: words.</summary>
    private void AttnEndLocked(AttentionItem item, string status, DateTime now, string? reason, string? why, List<Action> effects)
    {
        var wasShowing = item.Status == "showing";
        item.Status = status; item.EndedAt = now; item.Reason = reason; item.Why = why;
        _attnSeq++;
        if (wasShowing) _attnTurnEnded[item.SessionId] = now;
        var followers = _attn.Where(f => f.MergedInto == item.Id && !AttnEnded.Contains(f.Status)).ToList();
        if (followers.Count == 0) return;
        if (status is "answered" or "dismissed" || reason == "unanswered")
        {
            // One card, one answer: every follower ends the way the card did, with the answer and who shared it.
            var all = new[] { item }.Concat(followers).ToList();
            foreach (var f in followers)
            {
                f.Status = status; f.EndedAt = now; f.Reason = reason; f.Why = why;
                if (item.Answer is { } ans)
                {
                    var idx = item.Options.FindIndex(o => o.Id == ans.OptionId);
                    var opt = idx >= 0 && idx < f.Options.Count ? f.Options[idx] : null;
                    f.Answer = new AttnAnswer { OptionId = opt?.Id ?? ans.OptionId, Label = opt?.Label ?? ans.Label, At = ans.At, SharedWith = all.Where(x => x != f).Select(x => x.Who).ToList() };
                }
                if (wasShowing) _attnTurnEnded[f.SessionId] = now;
            }
            if (item.Answer != null) item.Answer.SharedWith = followers.Select(f => f.Who).ToList();
            return;
        }
        // The leader went away for its own reasons (withdrawn, expired in line): the first follower carries the card on.
        var lead = followers[0];
        lead.MergedInto = null;
        if (lead.Status == "showing") { lead.Status = "queued"; lead.ShownAt = null; }
        foreach (var f in followers.Skip(1)) { f.MergedInto = lead.Id; if (f.Status == "showing") { f.Status = "queued"; f.ShownAt = null; } }
    }

    /// <summary>Replay a queued call now that its turn came (Render or the request thread, outside the sessions lock).</summary>
    private void AttnReplay(string method, JToken p, string who, string? session, string itemId)
    {
        try
        {
            JObject r = method switch
            {
                "guide.set" => GuideSet(p, who, session),   // stamped with the asker's session: its disconnect cleanup clears it
                "guide.flow" => FlowStart(p, who, itemId),
                "experiment.start" => QueueStartCore(p["id"]?.ToString(), p["from"]?.ToString() ?? "agent", itemId),
                _ => Err("unknown_replay", method),
            };
            if (r["error"] != null)
            {
                LogError($"[Attention] {who}'s queued {method} failed when its turn came: {r["error"]} {r["message"]}");
                AttnEndById(itemId, "cancelled", "replay_failed", $"{method} failed when its turn came: {r["error"]}: {r["message"]}");
            }
        }
        catch (Exception ex)
        {
            LogError($"[Attention] replay of {who}'s {method}: {ex.Message}");
            AttnEndById(itemId, "cancelled", "replay_failed", $"{method} threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>End an item from outside the lock (a flow or a queued step finished, a replay failed) and hand the floor on.</summary>
    internal void AttnEndById(string? id, string status, string? reason = null, string? why = null)
    {
        if (id == null) return;
        var effects = new List<Action>();
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            if (_attn.FirstOrDefault(i => i.Id == id) is { } item && !AttnEnded.Contains(item.Status)) AttnEndLocked(item, status, now, reason, why, effects);
            AttnArbitrateLocked(now, effects);
        }
        AttnRun(effects);
    }

    private void AttnRun(List<Action> effects)
    {
        foreach (var e in effects)
            try { e(); }
            catch (Exception ex) { LogError($"[Attention] {ex.GetType().Name}: {ex.Message}"); }
        AttnSaveIfChanged();
    }

    /// <summary>
    /// Clear the guide card when it shows another owner's instruction (a hand-over): the session id decides when the card
    /// carries one, else the label (HUD-run cards: the queue runner, a flow, a measurement name who they are for).
    /// </summary>
    private void GuideClearUnlessOwnedBy(string session, string who)
    {
        bool clear;
        lock (_guideLock)
            clear = _guide.Who != null && (_guide.Session != null ? _guide.Session != session : _guide.Who != who) && (_guide.Instruction != null || _guide.Status != "idle");
        if (clear) GuideSet(new JObject { ["clear"] = true });
    }

    // ── Tick (Render, from SessionsTick at 4 Hz) ─────────────────────

    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>
    /// The player now, read outside every lock (game reads). state: loading (no game, loading screen) | menu (Esc menu) |
    /// combat (CombatNear) | calm (town or hideout; or no hostile for 8 s and no mouse/key input for 5 s or a large panel
    /// open) | playing. A read that throws names its link in broken; the player then counts as not calm.
    /// </summary>
    private AttnPlayer AttnSamplePlayer()
    {
        string step = "GameController.InGame";
        try
        {
            if (!GameController.InGame || GameController.IsLoading) return new AttnPlayer("loading", false, false, false, 0, 0, false, _combatBroken);
            step = "GameController.Game.IsEscapeState";
            if (GameController.Game.IsEscapeState) return new AttnPlayer("menu", false, false, false, 0, 0, false, _combatBroken);
            step = "CombatNear";
            var t = ImGuiNET.ImGui.GetTime();
            var combat = CombatNear(t);
            if (combat) _attnLastCombatAt = t;
            var noCombat = t - _attnLastCombatAt;
            step = "Area.CurrentArea";
            var area = GameController.Area?.CurrentArea;
            var town = area != null && (area.IsTown || area.IsHideout);
            step = "GetLastInputInfo";
            var lii = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            var idle = GetLastInputInfo(ref lii) ? unchecked((uint)Environment.TickCount - lii.Time) / 1000.0 : 0;
            step = "IngameUi panels";
            var big = !combat && noCombat >= AttnCalmNoCombatSec && (LabBigPanelOpen() || GameController.IngameState.IngameUi.InventoryPanel.IsVisible);
            var calm = town || (!combat && noCombat >= AttnCalmNoCombatSec && (idle >= AttnCalmIdleSec || big));
            return new AttnPlayer(combat ? "combat" : calm ? "calm" : "playing", combat, calm, town, idle, Math.Min(noCombat, 1e6), big, _combatBroken);
        }
        catch (Exception ex)
        {
            return new AttnPlayer("playing", false, false, false, 0, 0, false, $"player sense: {step}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Per tick: the player sense, disconnect cleanup after the grace, the floor card's visible clock, implicit holders whose
    /// card went quiet, asked highlights answered or gone, waiting restarts (their arrival or end is a change), then one
    /// arbitration.
    /// </summary>
    private void AttnTickLocked(DateTime now, AttnPlayer player, (string status, string? instruction, string? who, string? session) guide, bool flowRunning, string? flowWho,
        Dictionary<string, int> hlPending, List<Action> effects)
    {
        AttnLoadLocked();
        if (player.State != _attnPlayer.State || player.Calm != _attnPlayer.Calm || player.Town != _attnPlayer.Town || player.Broken != _attnPlayer.Broken) _attnSeq++;
        _attnPlayer = player;
        var restartKey = string.Join(",", _restarts.Where(r => r.Status is "waiting" or "go").Select(r => r.Id + r.Status));
        if (restartKey != _attnRestartKey) { _attnRestartKey = restartKey; _attnSeq++; }

        // A disconnected session gets AttnDisconnectGrace to come back (a reconnect keeps everything); then one cleanup
        // releases what it left in front of the player (AttnSessionGoneLocked).
        foreach (var s in _sessions.Values.Where(s => !s.Connected && !s.AttnCleaned && s.DisconnectedAt is { } d && now - d >= AttnDisconnectGrace).ToList())
            AttnSessionGoneLocked(s, now, flowRunning, flowWho, effects);

        var h = AttnHolderLocked();
        // The card is visible once the hand-over gap has passed outside combat and pauses (C5); an ask's showTtlSec counts
        // from here. The gap runs from the grant, or from the previous card leaving the panel's slot, whichever is later.
        var gapOver = h != null && !player.Combat && !player.Paused && now >= AttnGapEndLocked(h.ShownAt!.Value);
        if (h is { VisibleSince: null } && gapOver)
        {
            h.VisibleSince = now;
            foreach (var f in _attn.Where(f => f.MergedInto == h.Id)) f.VisibleSince = now;
            _attnSeq++;
        }
        // A granted call replays once the gap has passed: the new owner's card arrives after the old one left.
        if (h is { ReplayDue: true, Pending: not null } && gapOver) AttnReplayEffect(h, effects);
        // An implicit step turn (a plain guide.set) lasts while its card asks the player to act; once the card rests
        // (captured, info, done, cleared) or belongs to someone else, the floor is free again. Not before its replay ran.
        if (h is { Implicit: true, Kind: "step", StepId: null, ReplayDue: false } && now - h.ShownAt!.Value > AttnImplicitGrace
            && !((guide.session != null ? guide.session == h.SessionId : guide.who == h.Who) && guide.instruction != null && guide.status is "waiting" or "detected" or "settling" or "failed"))
            AttnEndLocked(h, "done", now, "card_quiet", "the card went quiet", effects);
        if (h is { Implicit: true, Kind: "flow", ReplayDue: false } && now - h.ShownAt!.Value > AttnImplicitGrace && !(flowRunning && flowWho == h.Who))
            AttnEndLocked(h, "done", now, "flow_ended", "the flow ended", effects);
        h = AttnHolderLocked();
        // A turn must not hold the player for nothing while someone else waits (an item in line, a restart: the holder is
        // a restart blocker). An explicit step or flow turn (attention.request) whose card or flow is not up for
        // AttnTurnIdle - its MCP call was cancelled, crashed or never got to it - ends as turn_idle; a step turn whose
        // agent sent nothing for AttnOwnerQuiet ends as owner_quiet and its waiting card goes with it (GuideOwnerGone).
        // HUD-run work (a queued experiment step, a Detached item) ends on its own terms.
        if (h is { Kind: "step" or "flow", Detached: false, StepId: null, ReplayDue: false })
        {
            var active = h.Kind == "flow"
                ? flowRunning && flowWho == h.Who
                : (guide.session != null ? guide.session == h.SessionId : guide.who == h.Who) && guide.instruction != null && guide.status is "waiting" or "detected" or "settling" or "failed";
            if (active || h.Implicit) h.IdleSince = null;
            else h.IdleSince ??= now;
            var othersWait = AttnRestartWaitingLocked() || _attn.Any(i => i.Status == "queued" && i.MergedInto == null && i.SessionId != h.SessionId);
            if (othersWait && h.IdleSince is { } idle && now - idle > AttnTurnIdle)
                AttnEndLocked(h, "done", now, "turn_idle", $"nothing of its own on screen for {AttnTurnIdle.TotalSeconds:0} s while others waited", effects);
            else if (othersWait && h.Kind == "step" && _sessions.TryGetValue(h.SessionId, out var hs) && hs.Connected && now - hs.LastSeen > AttnOwnerQuiet)
            {
                var sid = h.SessionId;
                AttnEndLocked(h, "done", now, "owner_quiet", $"its agent sent nothing for {AttnOwnerQuiet.TotalMinutes:0} min while others waited", effects);
                effects.Add(() => GuideOwnerGone(sid, "went quiet"));
            }
        }
        // Asks restored from queue.json belong to sessions of the previous HUD: one that does not say hello again within
        // AttnRestoreGrace will never read the answer, so its asks leave the line instead of being shown to nobody.
        if (now - _attnLoadedAt > AttnRestoreGrace)
            foreach (var i in _attn.Where(i => i.Status == "queued" && !i.Detached && !i.SessionId.StartsWith("queue:", StringComparison.Ordinal) && !_sessions.ContainsKey(i.SessionId)).ToList())
                AttnEndLocked(i, "cancelled", now, "owner_gone", "its agent did not come back after the HUD restart", effects);
        // Asked highlights: every question answered ends the turn; a layer that went (cleared, timed out) takes its item with it.
        foreach (var i in _attn.Where(i => i.HlOwner != null && i.Status is "queued" or "showing" && now - i.CreatedAt > AttnImplicitGrace).ToList())
        {
            var pending = hlPending.TryGetValue(i.HlOwner!, out var n) ? n : 0;
            if (pending == 0) AttnEndLocked(i, i.Status == "showing" && hlPending.ContainsKey(i.HlOwner!) ? "answered" : "cancelled", now,
                hlPending.ContainsKey(i.HlOwner!) ? null : "highlight_gone", hlPending.ContainsKey(i.HlOwner!) ? "every question answered" : "the highlight was cleared", effects);
        }

        AttnArbitrateLocked(now, effects);
    }

    /// <summary>
    /// The one cleanup for a session that left (disconnected for AttnDisconnectGrace without coming back; SessionsTick,
    /// under the sessions lock, side effects after it): its open attention items are cancelled (the floor passes on), the
    /// step it left waiting on the card is cleared (GuideOwnerGone: only a card stamped with its session id, only while
    /// the player is still asked to act; a finished card stays as a receipt) and its highlight layer goes. What the HUD
    /// runs by itself outlives its asker and ends on its own terms: a queued experiment step (Detached), and a flow
    /// (the card and highlight of a flow belong to no session) - its turn is kept and ends with the flow.
    /// </summary>
    private void AttnSessionGoneLocked(SessionInfo s, DateTime now, bool flowRunning, string? flowWho, List<Action> effects)
    {
        s.AttnCleaned = true;
        foreach (var i in _attn.Where(i => i.SessionId == s.Id && !i.Detached && !AttnEnded.Contains(i.Status)).ToList())
        {
            if (i is { Status: "showing", Kind: "flow" } && flowRunning && flowWho == i.Who) { i.Implicit = true; continue; }   // ends with the flow (AttnTickLocked)
            // An experiment step it started that is recording now runs in the HUD (the runner's card has no session): its
            // turn ends with the step (FinishQueued, QueueCancel), not with the agent. Cancelling it would hand the floor
            // to someone else mid-recording and clear the runner's card.
            if (i is { Status: "showing", StepId: { } step } && _queueRun?.Id == step) { i.Detached = true; continue; }
            AttnEndLocked(i, "cancelled", now, "disconnected", $"{s.Label} disconnected", effects);
        }
        var sid = s.Id;
        effects.Add(() => GuideOwnerGone(sid));
        effects.Add(() => HighlightDropLayer(sid));
    }

    // ── The card (GuidePanelAsk.cs draws it) ─────────────────────────

    /// <summary>The ask that holds the floor, for the card (null when the floor holder is not a card question): the copy published last, no lock.</summary>
    internal AskView? AttnAskSnapshot() => _askPub;

    /// <summary>The waiting items, holder and player state for the next strip and the dot (GuidePanelQueue.cs): the copy published last, no lock.</summary>
    internal AttnQueueView AttnQueueSnapshot() => _queuePub;

    /// <summary>
    /// Publish the panel's copies of the queue (the ask on the floor, the waiting items) when it changed since the last
    /// publish (seq, or a restart starting or ending to wait). Under the sessions lock: from AttnRun after every change
    /// (outside the arbiter's lock, in its own short one) and from SessionsPublishLocked (the 4 Hz tick, a hello, a
    /// connection closing, a restart request). The panel reads them by reference every frame and never takes the lock.
    /// </summary>
    private void AttnPublishLocked()
    {
        var restart = AttnRestartWaitingLocked();
        if (_attnPubSeq == _attnSeq && _attnPubRestart == restart) return;
        _attnPubSeq = _attnSeq; _attnPubRestart = restart;
        var now = DateTime.UtcNow;
        _askPub = AttnAskViewLocked(now);
        _queuePub = AttnQueueViewLocked(now, restart);
    }

    private AskView? AttnAskViewLocked(DateTime now)
    {
        var h = AttnHolderLocked();
        if (h is not { Kind: "ask", Question: not null }) return null;
        var whos = new[] { h }.Concat(_attn.Where(f => f.MergedInto == h.Id && f.Status == "showing")).ToList();
        // Re-asked after the player's x within the de-dup window: the card may stay, it may not shout (4.5).
        var softened = _attn.Any(d => d != h && d.SessionId == h.SessionId && d is { Status: "dismissed", Reason: "user_dismissed", Question: not null, EndedAt: { } e }
            && (now - e).TotalSeconds <= AttnDedupWindowSec && string.Equals(d.Question, h.Question, StringComparison.OrdinalIgnoreCase));
        return new AskView(h.Id, whos.Select(x => x.Who).ToArray(), whos.Select(x => x.SessionId).ToArray(), whos.Select(x => AttnHueLocked(x.SessionId, x.Who)).ToArray(), h.Title, h.Question,
            h.Detail, h.Options.ToArray(), h.Urgency, h.CreatedAt, h.ShownAt ?? now, h.VisibleSince, h.LaterCount, h.LaterCount > 0 ? h.QueuedAt : null,
            softened, h.VisibleSince == null);
    }

    private AttnQueueView AttnQueueViewLocked(DateTime now, bool restart)
    {
        var holder = AttnHolderLocked();
        var order = AttnOrderLocked(now);
        if (order.Count == 0 && holder == null && !restart) return AttnQueueView.Empty;
        var rows = order.Select(i => new AttnQueueRow(i.Who, i.SessionId, AttnHueLocked(i.SessionId, i.Who), i.Kind, i.Question ?? i.Title, i.Urgency, i.QueuedAt, i.EligibleAt)).ToArray();
        return new AttnQueueView(rows, holder?.Who, holder?.SessionId, holder?.Kind, holder?.Urgency, restart, _attnPlayer.Calm, _attnPlayer.Combat, _attnPlayer.Paused);
    }

    /// <summary>The panel's card slot stays busy until <paramref name="until"/> (a card's answer receipt and fade): the hand-over gap counts from then.</summary>
    internal void AttnSlotBusyUntil(DateTime until)
    {
        lock (_sessionLock) if (until > _attnSlotFreeAt) _attnSlotFreeAt = until;
    }

    /// <summary>
    /// The player hovered the accent dot while items waited with nothing on screen: give the floor to the next one now,
    /// calm or not, Later hold or not. The player reaching for the dot is the player deciding (principle 4).
    /// </summary>
    internal void AttnUserPeek()
    {
        var effects = new List<Action>();
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            AttnLoadLocked();
            if (AttnHolderLocked() != null || !AttnMayGrantNowLocked()) return;
            var next = AttnOrderLocked(now).FirstOrDefault();
            if (next == null) return;
            next.EligibleAt = null;
            AttnGrantLocked(next, now, effects, replay: true);
        }
        AttnRun(effects);
    }

    /// <summary>
    /// The player's answer on the card: an option, or Later (optionId null). Later puts the ask back in line as a calm
    /// item, not before 180 s (then 600 s); the third Later dismisses it (later_x3). An answer logs a result line
    /// ("WHATS-A-ROUTE / North stairs"), a Later an agent line.
    /// </summary>
    internal void AttnAnswerFromCard(string id, string? optionId)
    {
        var effects = new List<Action>();
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            var item = _attn.FirstOrDefault(i => i.Id == id);
            if (item is not { Status: "showing" }) return;
            var who = AttnWhosLocked(item);
            if (optionId == null)
            {
                if (item.LaterCount + 1 > AttnLaterHoldSec.Length) AttnEndLocked(item, "dismissed", now, "later_x3", "the player said Later three times", effects);
                else
                {
                    var hold = AttnLaterHoldSec[item.LaterCount];
                    foreach (var x in new[] { item }.Concat(_attn.Where(f => f.MergedInto == item.Id)))
                    {
                        x.LaterCount++; x.Status = "queued"; x.Urgency = "calm"; x.ShownAt = x.VisibleSince = null;
                        x.QueuedAt = now; x.EligibleAt = now.AddSeconds(hold);
                        x.TtlSec = Math.Clamp(Math.Max(x.TtlSec, hold + 600), AttnTtlMin, AttnTtlMax);   // the hold must not use up its time in line
                        _attnTurnEnded[x.SessionId] = now;
                    }
                    _attnSeq++;
                }
                effects.Add(() => GuideLog(new JObject { ["text"] = $"{who}: later", ["kind"] = "agent", ["title"] = "Later" }));
            }
            else if (item.Options.FirstOrDefault(o => o.Id == optionId) is { } opt)
            {
                item.Answer = new AttnAnswer { OptionId = opt.Id, Label = opt.Label, At = now };
                AttnEndLocked(item, "answered", now, null, null, effects);
                var label = opt.Label;
                effects.Add(() => GuideLog(new JObject { ["text"] = label, ["kind"] = "result", ["title"] = who }));
            }
            else return;
            AttnArbitrateLocked(now, effects);
        }
        AttnRun(effects);
    }

    /// <summary>The player dismissed a card: the turn behind it (if that session holds the floor) ends as dismissed.</summary>
    internal void AttnDismissedByUser(string? who, string? itemId = null)
    {
        var effects = new List<Action>();
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            if (AttnHolderLocked() is { } h && (itemId != null ? h.Id == itemId : who != null && h.Who == who && h.Kind != "ask"))
                AttnEndLocked(h, "dismissed", now, "user_dismissed", "the player dismissed the card", effects);
            AttnArbitrateLocked(now, effects);
        }
        AttnRun(effects);
    }

    /// <summary>
    /// The user pressed Start on a queued experiment step (or a chained step follows on): it waits its turn like everything
    /// else, but ahead of the agents' items (the player chose it). Its owner is the agent that queued it, which may be
    /// gone: such items are never cleaned on disconnect.
    /// </summary>
    private JObject AttnUserStart(QueuedStep s, string from)
    {
        var effects = new List<Action>();
        JObject reply;
        string? toast = null;
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            AttnLoadLocked();
            var item = _attn.FirstOrDefault(i => i.StepId == s.Id && !AttnEnded.Contains(i.Status));
            if (item == null)
            {
                item = new AttentionItem
                {
                    Id = AttnNextIdLocked(), SessionId = s.Session ?? $"queue:{s.Id}", Who = s.By ?? "queued step", Kind = "step",
                    Title = s.Title ?? $"{s.Experiment}: {s.Label}", CreatedAt = now, QueuedAt = now, TtlSec = AttnTtlMax, Implicit = true, Detached = true,
                    Priority = true, Pending = "experiment.start", PendingParams = new JObject { ["id"] = s.Id, ["from"] = from }, StepId = s.Id,
                };
                _attn.Add(item);
                _attnSeq++;
                AttnArbitrateLocked(now, effects);
            }
            reply = AttnItemJsonLocked(item, null, now);
            reply["ok"] = true;
            reply["queued"] = item.Status == "queued";
            reply["attention"] = item.Id;
            if (item.Status == "queued") toast = $"Starts when it is its turn: {AttnLineForPlayerLocked(item, now)}";
        }
        AttnRun(effects);
        if (toast != null) GuideLog(new JObject { ["text"] = toast, ["kind"] = "step", ["title"] = "Queued step" });
        return reply;
    }

    /// <summary>
    /// "2nd in line, after Claude" for a toast: AttnLineText's words with display names instead of labels (agents read
    /// the labels in the JSON; the player reads names).
    /// </summary>
    private string AttnLineForPlayerLocked(AttentionItem i, DateTime now)
    {
        var (pos, _, waitingFor) = AttnPlaceLocked(i, now);
        if (pos <= 0) return "it has the floor";
        var ahead = new List<string>();
        if (AttnHolderLocked() is { } h) ahead.Add(AttnNameLocked(h));
        foreach (var r in _restarts.Where(r => r.Status is "waiting" or "go").OrderBy(r => r.At)) ahead.Add($"{DisplayNameOfLocked(r.Who)}'s HUD restart");
        foreach (var x in AttnOrderLocked(now)) { if (x == i) break; ahead.Add(AttnNameLocked(x)); }
        var text = $"{AttnOrdinal(pos)} in line" + (ahead.Count > 0 ? $", after {string.Join(", ", ahead.Distinct())}" : "");
        return waitingFor != null ? $"{text} (waiting for {waitingFor})" : text;
    }

    // ── Hues (C1) ────────────────────────────────────────────────────

    /// <summary>
    /// A session's hue (0-5): a stable hash of its label, moved to the next free hue when a connected session has it
    /// already; when all six are taken the hash stands. Assigned at hello, kept for the HUD's lifetime.
    /// </summary>
    private int AttnPickHueLocked(SessionInfo s)
    {
        var taken = _sessions.Values.Where(o => o != s && o.Connected).Select(o => o.Hue).ToHashSet();
        if (s.Hue is >= 0 and < AttnHues && !taken.Contains(s.Hue)) return s.Hue;   // a re-hello keeps its hue
        var h = AttnHueOfLabel(s.Label);
        for (var k = 0; k < AttnHues; k++) if (!taken.Contains((h + k) % AttnHues)) return (h + k) % AttnHues;
        return h;
    }

    private static int AttnHueOfLabel(string label)
    {
        uint hash = 2166136261;   // FNV-1a: string.GetHashCode is randomised per process
        foreach (var c in label) { hash ^= c; hash *= 16777619; }
        return (int)(hash % AttnHues);
    }

    /// <summary>The hue of an item's owner: its session's, else the session with its label, else slate (scripts, gone agents).</summary>
    private int AttnHueLocked(string session, string? who)
    {
        if (_sessions.TryGetValue(session, out var s)) return s.Hue;
        if (who != null && _sessions.Values.FirstOrDefault(x => x.Label == who) is { } byLabel) return byLabel.Hue;
        return AttnHueFallback;
    }

    // ── Persistence (C6): queued asks survive a HUD restart ──────────

    private string AttnFile => Path.Combine(_bridgeDir, "attention", "queue.json");

    private bool AttnPersisted(AttentionItem i) => i.Kind == "ask" && i.Question != null && !i.Implicit && i.Status is "queued" or "showing";

    private void AttnLoadLocked()
    {
        if (_attnLoaded) return;
        _attnLoaded = true;
        _attnLoadedAt = DateTime.UtcNow;
        try
        {
            if (string.IsNullOrEmpty(_bridgeDir) || !File.Exists(AttnFile)) return;
            var items = JsonConvert.DeserializeObject<List<AttentionItem>>(File.ReadAllText(AttnFile)) ?? new();
            foreach (var i in items.Where(i => i.Kind == "ask" && i.Question != null && !string.IsNullOrEmpty(i.Id)))
            {
                // On screen when the HUD went down: back in line (it will be seen again), keeping its place and its Laters.
                if (i.Status == "showing") { i.Status = "queued"; i.ShownAt = i.VisibleSince = null; }
                if (i.Status != "queued" || _attn.Any(x => x.Id == i.Id)) continue;
                _attn.Add(i);
                if (int.TryParse(i.Id.TrimStart('A'), out var n) && n > _attnIds) _attnIds = n;
            }
            _attnSavedKey = AttnSaveKeyLocked();
            if (_attn.Count > 0) _attnSeq++;
        }
        catch (Exception ex) { LogError($"[Attention] {AttnFile} unreadable, starting empty: {ex.Message}"); }
    }

    private string AttnSaveKeyLocked() =>
        string.Join(";", _attn.Where(AttnPersisted).Select(i => $"{i.Id}:{i.Status}:{i.LaterCount}:{i.MergedInto}"));

    private void AttnSaveIfChanged()
    {
        string? json = null;
        lock (_sessionLock)
        {
            AttnPublishLocked();   // the panel's copies, after every change (AttnRun runs once per change, outside the arbiter's lock)
            if (!_attnLoaded) return;
            var key = AttnSaveKeyLocked();
            if (key == _attnSavedKey) return;
            _attnSavedKey = key;
            json = JsonConvert.SerializeObject(_attn.Where(AttnPersisted).ToList(), Formatting.Indented);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AttnFile)!);
            var tmp = AttnFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AttnFile, true);
        }
        catch (Exception ex) { LogError($"[Attention] save failed ({AttnFile}): {ex.Message}"); }
    }

    // ── JSON ─────────────────────────────────────────────────────────

    private JObject AttnItemJsonLocked(AttentionItem i, string? me, DateTime now)
    {
        var o = new JObject
        {
            ["ok"] = true, ["id"] = i.Id, ["kind"] = i.Kind, ["who"] = i.Who, ["session"] = i.SessionId, ["hue"] = AttnHueLocked(i.SessionId, i.Who),
            ["mine"] = me != null && i.SessionId == me, ["title"] = i.Title, ["status"] = i.Status, ["urgency"] = i.Urgency,
            ["createdAt"] = i.CreatedAt.ToString("O"), ["ttlSec"] = i.TtlSec,
            ["waitedSec"] = (int)((i.ShownAt ?? i.EndedAt ?? now) - i.QueuedAt).TotalSeconds,
        };
        if (i.Question != null) o["question"] = i.Question;
        if (i.Detail != null) o["detail"] = i.Detail;
        if (i.Kind == "ask" && i.Question != null) o["showTtlSec"] = i.ShowTtlSec;
        if (i.Options.Count > 0) o["options"] = new JArray(i.Options.Select(x => new JObject { ["id"] = x.Id, ["label"] = x.Label, ["hint"] = x.Hint }));
        if (i.ShownAt is { } sa) o["shownAt"] = sa.ToString("O");
        if (i.VisibleSince is { } vs) o["visibleSince"] = vs.ToString("O");
        if (i.EndedAt is { } ea) o["endedAt"] = ea.ToString("O");
        if (i.Answer is { } a)
            o["answer"] = new JObject { ["optionId"] = a.OptionId, ["label"] = a.Label, ["at"] = a.At.ToString("O"), ["sharedWith"] = a.SharedWith == null ? null : new JArray(a.SharedWith) };
        if (i.LaterCount > 0) o["laterCount"] = i.LaterCount;
        if (i.EligibleAt is { } el && i.Status == "queued") o["eligibleAt"] = el.ToString("O");
        if (i.MergedInto != null) o["mergedInto"] = i.MergedInto;
        if (i.Reason != null) o["reason"] = i.Reason;
        if (i.Why != null) o["why"] = i.Why;
        if (i.Implicit) o["implicit"] = true;
        if (i.StepId != null) o["stepId"] = i.StepId;
        var (position, ahead, waitingFor) = AttnPlaceLocked(i, now);
        o["position"] = position;
        o["ahead"] = new JArray(ahead);
        if (waitingFor != null) o["waitingFor"] = waitingFor;
        return o;
    }

    /// <summary>
    /// Where an item stands: 0 while it holds the floor; queued, 1 + everything ahead of it (the holder, a waiting
    /// restart, the items before it in turn order), plus what it waits for when that is more than its turn. A follower
    /// of a shared ask stands where its leader stands.
    /// </summary>
    private (int position, List<string> ahead, string? waitingFor) AttnPlaceLocked(AttentionItem i, DateTime now)
    {
        if (i.MergedInto != null && _attn.FirstOrDefault(x => x.Id == i.MergedInto) is { } leader && leader != i) return AttnPlaceLocked(leader, now);
        if (i.Status == "showing") return (0, [], null);
        if (i.Status != "queued") return (-1, [], null);
        var ahead = new List<string>();
        var holder = AttnHolderLocked();
        if (holder != null) ahead.Add(holder.Who);
        foreach (var r in _restarts.Where(r => r.Status is "waiting" or "go").OrderBy(r => r.At)) ahead.Add($"{r.Who} (HUD restart)");
        foreach (var x in AttnOrderLocked(now)) { if (x == i) break; ahead.Add(x.Who); }
        string? waitingFor = AttnRestartWaitingLocked() ? "a HUD restart to finish"
            : _attnPlayer.Combat ? "the fight to end"
            : _attnPlayer.Paused ? $"the player to leave the {(_attnPlayer.State == "menu" ? "Esc menu" : "loading screen")}"
            : i.EligibleAt is { } el && el > now ? $"the Later to run out ({(int)(el - now).TotalSeconds} s), then a calm moment"
            : i.Urgency == "calm" && !_attnPlayer.Calm ? "a calm moment (town, hideout, or a pause in play)"
            : null;
        return (ahead.Count + 1, ahead, waitingFor);
    }

    private JObject AttnStateJsonLocked(string? me, DateTime now)
    {
        var holder = AttnHolderLocked();
        var queue = new JArray();
        if (holder != null) queue.Add(AttnEntryJson(holder, me, 0, now));
        var pos = holder != null ? 1 : 0;
        var restarts = _restarts.Where(r => r.Status is "waiting" or "go").OrderBy(r => r.At).ToList();
        foreach (var r in restarts)
            queue.Add(new JObject
            {
                ["id"] = r.Id, ["who"] = r.Who, ["hue"] = AttnHueLocked(r.Session, r.Who), ["kind"] = "restart", ["title"] = r.Reason ?? "HUD restart",
                ["status"] = r.Status == "go" ? "showing" : "queued", ["position"] = ++pos, ["waitingSec"] = (int)(now - r.At).TotalSeconds,
                ["mine"] = me != null && r.Session == me, ["urgency"] = "now",
            });
        foreach (var i in AttnOrderLocked(now)) queue.Add(AttnEntryJson(i, me, ++pos, now));
        // Followers of a shared ask: listed with the card that carries them, never as waiting.
        foreach (var f in _attn.Where(f => f.MergedInto != null && f.Status is "queued" or "showing"))
        {
            var e = AttnEntryJson(f, me, AttnPlaceLocked(f, now).position, now);
            e["mergedInto"] = f.MergedInto;
            queue.Add(e);
        }
        var first = restarts.FirstOrDefault();
        return new JObject
        {
            ["ok"] = true, ["seq"] = _attnSeq, ["you"] = me,
            ["floor"] = holder == null ? null : new JObject
            {
                ["itemId"] = holder.Id, ["sessionId"] = holder.SessionId, ["who"] = holder.Who, ["hue"] = AttnHueLocked(holder.SessionId, holder.Who),
                ["kind"] = holder.Kind, ["title"] = holder.Title, ["since"] = holder.ShownAt?.ToString("O"),
                ["visibleSince"] = holder.VisibleSince?.ToString("O"),
                ["hidden"] = _attnPlayer.Combat ? "combat" : _attnPlayer.Paused ? _attnPlayer.State : holder.VisibleSince == null ? "gap" : "none",
                ["mine"] = me != null && holder.SessionId == me,
            },
            ["queue"] = queue,
            ["restart"] = first == null ? null : new JObject
            {
                ["id"] = first.Id, ["who"] = first.Who, ["hue"] = AttnHueLocked(first.Session, first.Who), ["reason"] = first.Reason,
                ["status"] = first.Status, ["at"] = first.At.ToString("O"), ["blockers"] = new JArray(first.Blockers.Select(BlockerJson)),
            },
            ["player"] = new JObject
            {
                ["state"] = _attnPlayer.State, ["combat"] = _attnPlayer.Combat, ["calm"] = _attnPlayer.Calm, ["town"] = _attnPlayer.Town,
                ["idleSec"] = Math.Round(_attnPlayer.IdleSec, 1), ["noCombatSec"] = Math.Round(Math.Min(_attnPlayer.NoCombatSec, 86400), 1),
                ["bigPanel"] = _attnPlayer.BigPanel, ["broken"] = _attnPlayer.Broken,
            },
        };
    }

    private JObject AttnEntryJson(AttentionItem i, string? me, int position, DateTime now)
    {
        var o = new JObject
        {
            ["id"] = i.Id, ["who"] = i.Who, ["hue"] = AttnHueLocked(i.SessionId, i.Who), ["kind"] = i.Kind, ["title"] = i.Title, ["status"] = i.Status,
            ["position"] = position, ["waitingSec"] = (int)(now - i.QueuedAt).TotalSeconds, ["mine"] = me != null && i.SessionId == me, ["urgency"] = i.Urgency,
        };
        if (i.LaterCount > 0) o["laterCount"] = i.LaterCount;
        if (i.EligibleAt is { } el && i.Status == "queued") o["eligibleAt"] = el.ToString("O");
        if (i.Status == "queued" && AttnPlaceLocked(i, now).waitingFor is { } wf) o["waitingFor"] = wf;
        return o;
    }

    /// <summary>
    /// The queued guide.set of a step (guide.user's "not shown yet"): the open implicit item whose call carries this
    /// stepId, as {id, position}, or null. Taken under the sessions lock alone (GuideUser calls it after its guide lock).
    /// </summary>
    private JObject? AttnQueuedStep(string stepId)
    {
        lock (_sessionLock)
        {
            var i = _attn.FirstOrDefault(x => x is { Status: "queued", Pending: "guide.set" } && x.PendingParams?["stepId"]?.ToString() == stepId);
            return i == null ? null : new JObject { ["id"] = i.Id, ["position"] = AttnPlaceLocked(i, DateTime.UtcNow).position };
        }
    }

    /// <summary>The open items of one session, for session.list (hud_sessions).</summary>
    private JArray AttnOfSessionLocked(string session, DateTime now) =>
        new(_attn.Where(i => i.SessionId == session && i.Status is "queued" or "showing").Select(i =>
        {
            var o = new JObject { ["id"] = i.Id, ["kind"] = i.Kind, ["title"] = i.Title, ["status"] = i.Status, ["position"] = AttnPlaceLocked(i, now).position };
            if (i.MergedInto != null) o["mergedInto"] = i.MergedInto;
            return o;
        }));

    /// <summary>The floor holder as a restart blocker (C8), in the player's words: "Claude: your answer to: Which exit?" (names: NameOf at the card).</summary>
    private Blocker? AttnFloorBlockerLocked(string? requesterLabel)
    {
        if (AttnHolderLocked() is not { } h) return null;
        return new Blocker("pilot", h is { Kind: "ask", Question: { } q } ? $"your answer to: {q}" : h.Kind == "ask" ? "your answers" : "the step you are doing", h.Who, null, requesterLabel != null && h.Who == requesterLabel);
    }

    // ── Small helpers ────────────────────────────────────────────────

    private static bool AttnUrgency(JToken? t, out string urgency)
    {
        urgency = t?.ToString() is { Length: > 0 } u ? u : "now";
        return urgency is "now" or "calm";
    }

    private static int AttnTtl(JToken? t, int fallback) =>
        Math.Clamp(t?.Type is JTokenType.Integer or JTokenType.Float ? t.Value<int>() : fallback, AttnTtlMin, AttnTtlMax);

    private static string AttnKindWord(string kind) => kind switch { "ask" => "Question", "flow" => "Guided task", "restart" => "HUD restart", _ => "Guided step" };

    /// <summary>The names on a card, as the player reads them: the owner's display name, then those of the sessions sharing it.</summary>
    private string AttnWhosLocked(AttentionItem i) =>
        string.Join(", ", new[] { i }.Concat(_attn.Where(f => f.MergedInto == i.Id && f.Status == "showing")).Select(AttnNameLocked));

    /// <summary>
    /// An item's owner as the player reads it (Sessions.cs display names: never a label, a branch or a folder): its
    /// session's name by id, else the name of the label it was made under (a queued step's "by", a session gone).
    /// </summary>
    private string AttnNameLocked(AttentionItem i) => _sessions.TryGetValue(i.SessionId, out var s) ? DisplayNameLocked(s) : DisplayNameOfLocked(i.Who);

    private static string AttnSlug(string label, int n)
    {
        var s = new string(label.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Length is > 0 and <= 40 ? s : $"o{n}";
    }
}
