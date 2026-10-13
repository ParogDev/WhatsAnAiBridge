using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Who is talking to this HUD, what each of them is doing, and who may restart it. Several agents (Claude Code sessions in
/// worktrees, Claude Desktop, scripts) share one HUD; this is the one place that knows them apart.
///   session.hello  {id, label, branch?, cwd?, pid?, kind?, role?, mcp?}   tags this connection; every later request is "by" that session
///       role: agent (default) | infra. Infra is the shared HTTP server's own connections (its control center identity, the F8
///       route-dispatcher): not an agent, so the chip doesn't count it (its hover names it on a dim line). Older MCP servers send
///       no role: kind mcp-http with label "control center" or "route-dispatcher" is infra too.
///   session.touch  {}                                         "still here": an MCP server sends it while a caller's call is open
///       without bridge traffic (incident_wait, a long observe_wait), so the caller stays present (PresentLocked)
///       mcp {version, sha, mode: supervised|local|unsupervised, deployedVersion, deployedSha, workerPid}: which MCP server build
///       the session runs and what was deployed when it said hello (supervised = follows deployments, local = pinned to its own
///       checkout's build, unsupervised = an old launcher that needs a session restart to get a supervisor). Optional: old servers
///       don't send it. A hot swap connects a new worker that says hello with the SAME id: the session moves to the new connection,
///       the old one stays known (PrevClientId) until it closes, so its in-flight calls keep their "who" and its close disconnects nothing.
///   session.list   {}                                         sessions (with mcp), leases, blockers, restart requests, mcpRollout
///       per session: present (counted on the chip), stale (connected but idle: an mcp-http caller with no call for
///       McpHttpIdle), infra, idleSec, and doing: its blockers, attributed by session id (a label several sessions share,
///       such as "main", names one of them, never all). agents: how many the chip counts.
///       mcpRollout {deployedVersion, deployedSha, total, onDeployed, behind, local, unsupervised}: the deployed build from the newest
///       hello that carried one, and the connected sessions that reported mcp sorted against it (behind = supervised, not on it yet).
///   lease.acquire  {kind: perf|pilot|record|reload|other, label, ttlSec?}  -> {id, until}; refused while a restart is granted
///   lease.renew    {id, ttlSec?} / lease.release {id}
///   restart.request {reason?}  -> {id, status: go|waiting|merged|denied, blockers, holdUntil}
///   restart.status  {id} / restart.cancel {id}
/// A restart is blocked while anything that a restart would ruin is running: a measurement (pipeline.trace, profile.plugin),
/// a plugin compiling, a recording, a queued step recording for the user, a guided flow, an instruction the user is acting on
/// (guide status waiting / detected / settling), or an explicit lease. Those are read from the HUD's own state when asked
/// (ImplicitBlockers), never duplicated, so nothing can leak. When nothing blocks, the request waits Settings.RestartHoldSec
/// on the guide card ("whats-a-route restarts the HUD in 5 s - Not now") and is then granted; the requester runs the restart
/// (tools\restart-hud.ps1 in the scaffolding repo), which asks here first. The card's "Restart now" grants over the blockers
/// (their holders are told in the guide log); "Not now" denies. Only one request is granted at a time; others merge into it.
/// The attention queue (AttentionQueue.cs) lives under the same lock: a waiting restart stops new turns (restarts first),
/// the floor holder is a blocker, and a pilot lease asked for while a restart waits is queued behind it, not granted.
/// Each session gets a hue (0-5) at hello for the who-dot on the card.
/// State is in memory: a restart ends it, and a new HUD has nothing running. Written from the request thread and the panel
/// (Render), read in Render, so everything goes through the lock.
/// </summary>
public partial class WhatsAnAiBridge
{
    internal sealed class SessionInfo
    {
        public string Id = "";
        public string Label = "";
        public string? Branch, Cwd, Kind;
        public int? Pid;
        public DateTime HelloAt, LastSeen;
        public long ClientId;              // the connection it last spoke on
        public long PrevClientId;          // a hot swap: the connection it spoke on before, still open (0 when none)
        public bool Connected;
        public string Name = "";           // what the player reads (RenameLocked): never a system folder, unique among the connected
        public SessionMcp? Mcp;            // the MCP server build it runs (null: an old server or a script)
        public int Hue = -1;               // 0-5, the who-dot's colour (AttentionQueue.cs AttnPickHueLocked), stable for the HUD's lifetime
        public DateTime? DisconnectedAt;   // its cleanup runs after a grace (AttentionQueue.cs AttnTickLocked)
        public bool AttnCleaned;            // that cleanup ran (a reconnect resets it)
        public string? Role;               // hello.role: agent | infra (null: an older server, see Infra)
        /// <summary>The shared HTTP server's own connections, not an agent: by hello.role, else by the labels older servers use.</summary>
        public bool Infra => Role == "infra" || (Role == null && Kind == "mcp-http" && Label is "control center" or "route-dispatcher");
    }

    /// <summary>
    /// One agent as the in-game UI shows it: its readable name, what it asks of the user right now (a step to do, a
    /// question, a flow, a step recording) and what else it is doing (a measurement, a reload, a lease).
    /// </summary>
    internal sealed record AgentView(string Name, string? Label, string[] Asks, string[] Doing);

    /// <summary>
    /// The agents the in-game UI may mention, rebuilt by SessionsTick at 4 Hz (read per frame by reference, no lock):
    /// only connected sessions with something going on, plus HUD-run work asking the user under a name nobody is
    /// connected as. Idle and disconnected sessions are left out (session.list keeps them). Names maps every known
    /// label to its display name; Sessions maps every known session id to its display name and who-dot hue, so a
    /// surface that knows the session (an attention item, the card, a restart request) names and colours it the same
    /// way after a re-hello changed its label (ids don't change; Hue is kept per session, AttnPickHueLocked).
    /// </summary>
    internal sealed record AgentsView(AgentView[] Active, IReadOnlyDictionary<string, string> Names,
        Dictionary<string, (string Name, int Hue)> Sessions, Dictionary<string, int> Hues)
    {
        public static readonly AgentsView Empty = new([], new Dictionary<string, string>(), new(), new());
        /// <summary>Two or more agents active: the card says whose step it is.</summary>
        public bool Ambiguous => Active.Length >= 2;
        public string NameOf(string? label) => label == null ? "" : Names.TryGetValue(label, out var n) ? n : BaseDisplayName(label, null);
        /// <summary>The name of a session when it is known by id (stable across a label change), else of its label.</summary>
        public string NameOf(string? session, string? label) => session != null && Sessions.TryGetValue(session, out var s) ? s.Name : NameOf(label);
        /// <summary>A session's who-dot hue by id, else the hue of the newest session with that label, else the fallback (slate).</summary>
        public int HueOf(string? session, string? label) =>
            session != null && Sessions.TryGetValue(session, out var s) ? s.Hue : label != null && Hues.TryGetValue(label, out var h) ? h : AttnHueFallback;
    }

    /// <summary>The MCP server build behind a session, as its hello reported it (hello.mcp).</summary>
    internal sealed record SessionMcp(string? Version, string? Sha, string Mode, string? DeployedVersion, string? DeployedSha, int? WorkerPid)
    {
        public bool Local => Mode == "local";
        public bool Unsupervised => Mode == "unsupervised";
        /// <summary>A supervised server not on <paramref name="deployedSha"/> yet: the swap hasn't reached it.</summary>
        public bool Behind(string deployedSha) => !Local && !Unsupervised && !string.Equals(Sha, deployedSha, StringComparison.Ordinal);
        public JObject Json() => new()
        {
            ["version"] = Version, ["sha"] = Sha, ["mode"] = Mode, ["deployedVersion"] = DeployedVersion, ["deployedSha"] = DeployedSha, ["workerPid"] = WorkerPid,
        };
    }

    /// <summary>The MCP rollout: the deployed build (from the newest hello that carried one) against the connected sessions that reported mcp.</summary>
    internal sealed record McpRollout(string? DeployedVersion, string DeployedSha, int Total, int OnDeployed, string[] Behind, string[] Local, string[] Unsupervised)
    {
        /// <summary>Supervised sessions still wait for their swap: informational, nothing is cut.</summary>
        public bool InProgress => Behind.Length > 0;
        public JObject Json() => new()
        {
            ["deployedVersion"] = DeployedVersion, ["deployedSha"] = DeployedSha, ["total"] = Total, ["onDeployed"] = OnDeployed,
            ["behind"] = new JArray(Behind), ["local"] = new JArray(Local), ["unsupervised"] = new JArray(Unsupervised),
        };
    }

    /// <summary>One session as the panel sees it (an immutable copy; Name is what the player reads).</summary>
    /// <remarks>Present: counted as an agent on this HUD (PresentLocked); Doing: its blockers' labels, by session id.</remarks>
    internal sealed record SessionView(string Id, string Label, string Name, string? Branch, string? Kind, bool Connected, DateTime LastSeen, SessionMcp? Mcp,
        bool Present, bool Infra, string[] Doing);

    /// <summary>
    /// What the guide panel shows about sessions this frame: taken under the lock by SessionsTick (4 Hz) and whenever the state
    /// changes (hello, a connection closing, a restart request, the user's Restart now / Not now), never per frame. Immutable.
    /// Agents: the present sessions that are not infra (the chip's number).
    /// </summary>
    internal sealed record SessionsView(SessionView[] Sessions, int Agents, RestartRequest? Waiting, RestartRequest? Granted, McpRollout? Rollout)
    {
        public static readonly SessionsView Empty = new([], 0, null, null, null);
    }

    internal sealed class Lease
    {
        public string Id = "";
        public string Kind = "other";
        public string Label = "";
        public string Session = "";        // SessionInfo.Id
        public string Who = "";            // its label at acquire time (shown even after the session is forgotten)
        public DateTime Since, Until;
    }

    internal sealed class RestartRequest
    {
        public string Id = "";
        public string Session = "";
        public string Who = "";
        public string? Reason;
        public DateTime At;
        public string Status = "waiting";  // waiting | go | merged | denied | cancelled | expired
        public string? By;                 // who decided: auto | user | <session>
        public string? Into;               // merged: the request that carries this one
        public DateTime? HoldUntil;        // waiting with nothing blocking: granted at this time
        public DateTime? DecidedAt;
        public List<Blocker> Blockers = new();
        // Set on the panel's copy only (RestartCopyLocked): the requester's who-dot hue and how many later requests merged into this one.
        public int Hue = AttnHueFallback;
        public int MergedCount;
    }

    /// <summary>Something a restart would interrupt: what, whose, until when (when known), and whether it is the requester's own.</summary>
    /// <remarks>Session: the owner's session id when known (a lease, the guide step, the attention floor); else the owner is
    /// resolved from Who, its label, to one session (OwnerLocked).</remarks>
    internal readonly record struct Blocker(string Kind, string Label, string? Who, DateTime? Until, bool Yours, string? LeaseId = null, string? Session = null);

    private readonly object _sessionLock = new();
    private readonly Dictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);
    private readonly List<Lease> _leases = new();
    private readonly List<RestartRequest> _restarts = new();
    private int _leaseSeq, _restartSeq;
    private volatile AgentsView _agentsView = AgentsView.Empty;
    private Dictionary<string, List<string>> _doingCache = new(StringComparer.Ordinal);   // per session id, by SessionsTick (under the lock)
    private long _reqClientId;                         // the connection of the request being served (set in DrainRequests)
    private DateTime _sessionsTickAt = DateTime.MinValue;
    private string? _attnTickError;                   // the last attention tick fault logged (SessionsTick): a repeat is not logged again
    private string? _mcpDeployedSha, _mcpDeployedVersion;   // the deployed MCP build, from the newest hello that carried one
    private volatile SessionsView _sessionsView = SessionsView.Empty;   // written under the lock, read by the panel every frame
    // An mcp-http caller (a tools\mcp-call.ps1 label on the shared server) keeps its connection while that server lives, so
    // "connected" says little about it: it is present while it calls (session.touch covers a long call) or did in the last
    // McpHttpIdle, else stale (listed by session.list, left out of the chip and its hover).
    internal static readonly TimeSpan McpHttpIdle = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SessionForget = TimeSpan.FromMinutes(30), RestartGoExpiry = TimeSpan.FromMinutes(3), RestartKeep = TimeSpan.FromMinutes(20);
    private static readonly HashSet<string> LeaseKinds = new(StringComparer.Ordinal) { "perf", "pilot", "record", "reload", "other" };
    private const int LeaseTtlDefault = 120, LeaseTtlMax = 3600;

    private string? ProcessSessionMethod(string method, JToken? p) => method switch
    {
        "session.hello" => SafeMemory(() => SessionHello(p)),
        "session.list" => SafeMemory(SessionList),
        "session.touch" => SafeMemory(SessionTouchReply),
        "lease.acquire" => SafeMemory(() => LeaseAcquire(p)),
        "lease.renew" => SafeMemory(() => LeaseRenew(p)),
        "lease.release" => SafeMemory(() => LeaseRelease(p)),
        "restart.request" => SafeMemory(() => RestartRequestNew(p)),
        "restart.status" => SafeMemory(() => RestartStatus(p?["id"]?.ToString())),
        "restart.cancel" => SafeMemory(() => RestartCancel(p?["id"]?.ToString())),
        _ => ProcessWorkMethod(method, p) ?? ProcessAttentionMethod(method, p),
    };

    // ── Sessions ─────────────────────────────────────────────────────

    /// <summary>The session behind the request being served, or null for a connection that never said hello.</summary>
    internal SessionInfo? CurrentSession()
    {
        var cid = _reqClientId;
        if (cid == 0) return null;
        // A swapped worker's old connection (PrevClientId) still belongs to the session while it finishes its calls.
        lock (_sessionLock) return _sessions.Values.FirstOrDefault(s => (s.ClientId == cid || s.PrevClientId == cid) && s.Connected);
    }

    /// <summary>The current session's label, for "who asked" lines; null when unknown.</summary>
    internal string? CurrentWho() => CurrentSession()?.Label;

    private JObject SessionHello(JToken? p)
    {
        var id = Clip(p?["id"]?.ToString(), 80);
        if (string.IsNullOrWhiteSpace(id)) return Err("missing_id", "Pass id: a stable id for this client (one per MCP server or script run).");
        var label = Clip(p?["label"]?.ToString(), 40);
        if (string.IsNullOrWhiteSpace(label)) label = id!.Length > 40 ? id[..40] : id;
        lock (_sessionLock)
        {
            if (!_sessions.TryGetValue(id!, out var s)) { s = new SessionInfo { Id = id!, HelloAt = DateTime.UtcNow }; _sessions[id!] = s; }
            s.Label = label!;
            s.Branch = Clip(p?["branch"]?.ToString(), 80);
            s.Cwd = Clip(p?["cwd"]?.ToString(), 200);
            s.Kind = Clip(p?["kind"]?.ToString(), 20);
            s.Role = Clip(p?["role"]?.ToString(), 20) is { Length: > 0 } role ? role : null;
            s.Pid = p?["pid"]?.Type == JTokenType.Integer ? p["pid"]!.Value<int>() : null;
            s.Mcp = ParseMcp(p?["mcp"] as JObject);
            if (s.Mcp?.DeployedSha != null) { _mcpDeployedSha = s.Mcp.DeployedSha; _mcpDeployedVersion = s.Mcp.DeployedVersion; }
            // The same session on a new connection (a hot-swapped MCP worker, or a reconnect): the old connection, if still
            // open, stays its own until it closes - the old worker is finishing its in-flight calls on it.
            if (s.ClientId != 0 && s.ClientId != _reqClientId && s.Connected) s.PrevClientId = s.ClientId;
            s.ClientId = _reqClientId;
            s.Connected = true;
            s.LastSeen = DateTime.UtcNow;
            s.DisconnectedAt = null; s.AttnCleaned = false;   // back within the grace: its turn and requests stay
            var hue = AttnPickHueLocked(s);
            if (hue != s.Hue) { s.Hue = hue; _attnSeq++; }   // the cards' who-dots are in the attention queue's published copies
            // Another entry still bound to this connection (a client that re-identified) is no longer on it.
            foreach (var o in _sessions.Values)
                if (o != s && (o.ClientId == _reqClientId || o.PrevClientId == _reqClientId))
                {
                    if (o.Connected) o.DisconnectedAt = DateTime.UtcNow;
                    o.Connected = false; o.PrevClientId = 0;
                }
            RenameLocked();
            var now = DateTime.UtcNow;
            var others = _sessions.Values.Count(o => o != s && !o.Infra && PresentLocked(o, now));
            SessionsPublishLocked();
            var reply = new JObject { ["ok"] = true, ["id"] = s.Id, ["label"] = s.Label, ["name"] = s.Name, ["hue"] = s.Hue, ["others"] = others, ["holdSec"] = Settings.RestartHoldSec.Value };
            if (McpRolloutLocked() is { } ro) reply["mcpRollout"] = ro.Json();
            return reply;
        }
    }

    /// <summary>hello.mcp, clipped; null when absent. An unknown mode is kept as written (and sorted as supervised in the rollout).</summary>
    private static SessionMcp? ParseMcp(JObject? m)
    {
        if (m == null) return null;
        var mode = Clip(m["mode"]?.ToString(), 20);
        if (string.IsNullOrWhiteSpace(mode)) mode = "supervised";
        return new SessionMcp(Clip(m["version"]?.ToString(), 40), Clip(m["sha"]?.ToString(), 40), mode!,
            Clip(m["deployedVersion"]?.ToString(), 40), Clip(m["deployedSha"]?.ToString(), 40),
            m["workerPid"]?.Type == JTokenType.Integer ? m["workerPid"]!.Value<int>() : null);
    }

    /// <summary>
    /// A connection closed (TcpBridgeServer.ClientClosed): its session is disconnected and its name leaves the uniqueness
    /// set; its leases stay until they expire. What it left in front of the player (its waiting step on the card, its
    /// attention items, its highlight layer) is released after a grace by one cleanup (AttnSessionGoneLocked, from
    /// SessionsTick), so a client that reconnects within it keeps its turn and its card. The old connection of a swapped
    /// worker closing disconnects nothing: the session lives on its new one, its step and its turn stay.
    /// </summary>
    private void SessionDisconnected(long clientId)
    {
        lock (_sessionLock)
        {
            var any = false;
            foreach (var s in _sessions.Values)
            {
                if (s.PrevClientId == clientId) s.PrevClientId = 0;
                else if (s.ClientId == clientId && s.Connected)
                {
                    s.Connected = false; s.PrevClientId = 0; s.LastSeen = DateTime.UtcNow; s.DisconnectedAt = s.LastSeen; any = true;
                }
            }
            if (any) RenameLocked();
            SessionsPublishLocked();
        }
    }

    /// <summary>Record that the current session spoke (called per request; cheap).</summary>
    private void SessionTouch()
    {
        var cid = _reqClientId;
        if (cid == 0) return;
        lock (_sessionLock)
            foreach (var s in _sessions.Values) if ((s.ClientId == cid || s.PrevClientId == cid) && s.Connected) s.LastSeen = DateTime.UtcNow;
    }

    /// <summary>session.touch: the request itself refreshed LastSeen (DrainRequests calls SessionTouch); say whether it is known.</summary>
    private JObject SessionTouchReply() =>
        CurrentSession() is { } me ? new JObject { ["ok"] = true, ["id"] = me.Id } : Err("no_session", "Send session.hello on this connection first.");

    /// <summary>
    /// Whether a session counts as an agent on this HUD now: connected, and for an mcp-http caller also seen in the last
    /// McpHttpIdle (session.touch keeps a long call fresh) or holding a lease (a test across several calls). A stdio server is
    /// one session's own process: connected is present.
    /// </summary>
    private bool PresentLocked(SessionInfo s, DateTime now)
    {
        if (!s.Connected) return false;
        if (s.Kind != "mcp-http" || now - s.LastSeen <= McpHttpIdle) return true;
        foreach (var l in _leases) if (l.Session == s.Id && l.Until > now) return true;
        return false;
    }

    /// <summary>
    /// The session a blocker belongs to: by its session id when it carries one (a lease, the guide step, the floor), else the
    /// connected session with its label (the most recently seen, so a label several sessions share names one, never all).
    /// An id that names no session (a queued step's "queue:&lt;step&gt;", a session forgotten since) falls back to the label.
    /// Null: nobody with that label is connected (HUD-run work, a gone session).
    /// </summary>
    private SessionInfo? OwnerLocked(in Blocker b)
    {
        if (b.Session != null && _sessions.TryGetValue(b.Session, out var known)) return known;
        if (b.Who == null) return null;
        SessionInfo? best = null;
        foreach (var s in _sessions.Values)
            if (s.Connected && s.Label == b.Who && (best == null || s.LastSeen > best.LastSeen)) best = s;
        return best;
    }

    /// <summary>What each session is doing (its blockers' labels, by OwnerLocked), keyed by session id.</summary>
    private Dictionary<string, List<string>> DoingBySessionLocked(List<Blocker> blockers)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var b in blockers)
            if (OwnerLocked(b) is { } o)
            {
                if (!map.TryGetValue(o.Id, out var l)) map[o.Id] = l = new List<string>();
                if (!l.Contains(b.Label)) l.Add(b.Label);
            }
        return map;
    }

    /// <summary>The MCP rollout against the present sessions that reported mcp (a stale caller's old build is not "swapping":
    /// its next call says hello from the new one); null until a hello carried a deployed build.</summary>
    private McpRollout? McpRolloutLocked()
    {
        if (_mcpDeployedSha == null) return null;
        var now = DateTime.UtcNow;
        var total = 0; var on = 0;
        List<string>? behind = null, local = null, unsup = null;
        foreach (var s in _sessions.Values)
        {
            if (s.Mcp == null || !PresentLocked(s, now)) continue;
            total++;
            if (s.Mcp.Local) (local ??= new()).Add(s.Label);
            else if (s.Mcp.Unsupervised) (unsup ??= new()).Add(s.Label);
            else if (s.Mcp.Behind(_mcpDeployedSha)) (behind ??= new()).Add(s.Label);
            else on++;
        }
        return new McpRollout(_mcpDeployedVersion, _mcpDeployedSha, total, on, behind?.ToArray() ?? [], local?.ToArray() ?? [], unsup?.ToArray() ?? []);
    }

    private JObject SessionList()
    {
        var me = CurrentSession();
        // Harmony patches of measurements (Instrumentation.cs): read outside the sessions lock.
        var instrumentation = InstrState(full: false);
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            SessionsExpireLocked(now);
            AttnLoadLocked();
            WorkLoadLocked();
            var blockers = BlockersLocked(me?.Id);
            var doing = DoingBySessionLocked(blockers);
            var holder = AttnHolderLocked();
            return new JObject
            {
                ["ok"] = true,
                ["you"] = me?.Id,
                ["holdSec"] = Settings.RestartHoldSec.Value,
                ["agents"] = _sessions.Values.Count(s => !s.Infra && PresentLocked(s, now)),
                ["sessions"] = new JArray(_sessions.Values.OrderByDescending(s => s.Connected).ThenBy(s => s.HelloAt).Select(s => new JObject
                {
                    ["id"] = s.Id, ["label"] = s.Label, ["name"] = DisplayNameLocked(s), ["hue"] = s.Hue, ["branch"] = s.Branch, ["cwd"] = s.Cwd, ["pid"] = s.Pid, ["kind"] = s.Kind,
                    ["connected"] = s.Connected, ["present"] = PresentLocked(s, now), ["stale"] = s.Connected && !PresentLocked(s, now),
                    ["infra"] = s.Infra, ["idleSec"] = (int)Math.Max(0, (now - s.LastSeen).TotalSeconds),
                    ["helloAt"] = s.HelloAt.ToString("O"), ["lastSeen"] = s.LastSeen.ToString("O"),
                    ["leases"] = _leases.Count(l => l.Session == s.Id),
                    ["doing"] = new JArray(doing.TryGetValue(s.Id, out var d) ? d : []),
                    ["mcp"] = s.Mcp?.Json(),
                    // Its attention items (AttentionQueue.cs): what it waits to show the player, and where it stands.
                    ["queued"] = AttnOfSessionLocked(s.Id, now),
                    // Its work items (Work.cs): its own Task item, the F8 presses it handles, active and recent.
                    ["work"] = WorkOfSessionLocked(s.Id),
                })),
                // Every active work item (F8 presses nobody owns yet included), in board order, and those that ended in the last 10 min.
                ["work"] = WorkActiveJsonLocked(),
                ["recent"] = WorkRecentJsonLocked(),
                ["floor"] = holder == null ? null : new JObject
                {
                    ["itemId"] = holder.Id, ["who"] = holder.Who, ["hue"] = AttnHueLocked(holder.SessionId, holder.Who), ["kind"] = holder.Kind,
                    ["title"] = holder.Title, ["since"] = holder.ShownAt?.ToString("O"),
                },
                ["attentionSeq"] = _attnSeq,
                ["leases"] = new JArray(_leases.Select(LeaseJson)),
                ["blockers"] = new JArray(blockers.Select(BlockerJson)),
                ["restarts"] = new JArray(_restarts.OrderByDescending(r => r.At).Select(RestartJsonLocked)),
                ["mcpRollout"] = McpRolloutLocked()?.Json(),
                ["instrumentation"] = instrumentation,
            };
        }
    }

    private void SessionsExpireLocked(DateTime now)
    {
        _leases.RemoveAll(l => l.Until <= now);
        foreach (var k in _sessions.Where(kv => !kv.Value.Connected && now - kv.Value.LastSeen > SessionForget).Select(kv => kv.Key).ToList())
            _sessions.Remove(k);
        _restarts.RemoveAll(r => r.Status != "waiting" && r.Status != "go" && now - (r.DecidedAt ?? r.At) > RestartKeep);
    }

    // ── Names the player reads ───────────────────────────────────────
    // A session's label is its MCP server's git branch (worktree-aware), its folder name without one, or HEXILE_AGENT.
    // That identifies it for agents and logs (session.list keeps it), but the in-game UI shows a name instead: never a
    // system folder (a server started from C:\Windows\System32 is Claude Desktop's), without a worktree's random
    // "-1a2b3c" tail, and unique among the connected sessions (two servers on main read "main" and "main (shell)").

    private static readonly HashSet<string> SystemFolders = new(StringComparer.OrdinalIgnoreCase)
        { "System32", "SysWOW64", "Windows", "WinSxS", "Program Files", "Program Files (x86)", "ProgramData", "Users", "AppData", "Local", "Roaming", "Temp" };

    /// <summary>The readable name of a label (no uniqueness: RenameLocked adds that among the connected).</summary>
    internal static string BaseDisplayName(string label, string? cwd)
    {
        var l = label.Trim();
        if (l.StartsWith("ps:", StringComparison.Ordinal)) return (l.Length > 3 ? l[3..] : "script") + " (script)";
        // Only a label that is a folder name says nothing; an explicit name (HEXILE_AGENT, a branch) is kept wherever it runs.
        if (l.Length == 0 || SystemFolders.Contains(l) || (l.Length == 2 && l[1] == ':'))
            return cwd != null && IsSystemCwd(cwd) && cwd.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Claude Desktop" : "Claude";
        if (l.StartsWith("detached@", StringComparison.Ordinal)) return "detached checkout";
        // Worktree branches end in a random hex tail ("cranky-wilbur-f2659c"): it tells nothing to the player.
        var dash = l.LastIndexOf('-');
        if (dash > 0 && l.Length - dash - 1 == 6 && l.AsSpan(dash + 1).IndexOfAnyExcept("0123456789abcdef") < 0) l = l[..dash];
        return l;
    }

    /// <summary>A working directory no agent session is about: Windows itself, Program Files, a drive root, the user folder.</summary>
    private static bool IsSystemCwd(string cwd)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(cwd).TrimEnd('\\', '/');
            bool Under(Environment.SpecialFolder f)
            {
                var d = Environment.GetFolderPath(f);
                return d.Length > 0 && (full.Equals(d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || full.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            }
            if (Under(Environment.SpecialFolder.Windows) || Under(Environment.SpecialFolder.ProgramFiles) || Under(Environment.SpecialFolder.ProgramFilesX86)) return true;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
            return full.Length <= 3 || full.Equals(home, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Give every session its display name: base names, then tell the connected ones that read the same apart.</summary>
    private void RenameLocked()
    {
        foreach (var s in _sessions.Values) s.Name = BaseDisplayName(s.Label, s.Cwd);
        foreach (var group in _sessions.Values.Where(s => s.Connected).GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var list = group.OrderBy(s => s.HelloAt).ToList();
            // By kind first (one per client type reads naturally), then by number for what is still the same.
            foreach (var s in list.Skip(1)) s.Name += s.Kind switch { "mcp-http" => " (shell)", _ => "" };
            var n = 1;
            foreach (var s in list.Skip(1).Where(s => list.Count(o => o.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)) > 1)) s.Name += $" {++n}";
        }
    }

    /// <summary>The display name for a label (the connected session holding it, else its base name).</summary>
    internal string SessionDisplayName(string label) { lock (_sessionLock) return DisplayNameOfLocked(label); }

    private string DisplayNameOfLocked(string label) =>
        _sessions.Values.Where(s => s.Label == label).OrderByDescending(s => s.Connected).ThenByDescending(s => s.LastSeen).FirstOrDefault()?.Name is { Length: > 0 } n
            ? n : BaseDisplayName(label, null);

    private static string DisplayNameLocked(SessionInfo s) => s.Name.Length > 0 ? s.Name : BaseDisplayName(s.Label, s.Cwd);

    /// <summary>
    /// Rebuild the in-game view of the agents (SessionsTick, 4 Hz): the connected sessions with something going on,
    /// each with what it asks of the user (pilot blockers: a step, a question, a flow, a recording step) and what else
    /// it does. HUD-run work asking the user under a name no connected session has (a queued step "by Claude") is its
    /// own entry. A blocker belongs to its session by id (OwnerLocked): a label several sessions share (every server started
    /// in the main checkout reads "main") names one of them, never all. Stale callers and infra are not live agents.
    /// </summary>
    private void AgentsViewRebuildLocked(List<Blocker> blockers, DateTime now)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var byId = new Dictionary<string, (string Name, int Hue)>(StringComparer.Ordinal);
        var hues = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in _sessions.Values.OrderBy(s => s.Connected).ThenBy(s => s.LastSeen))   // connected, newest win
        {
            names[s.Label] = DisplayNameLocked(s);
            byId[s.Id] = (DisplayNameLocked(s), s.Hue is >= 0 and < AttnHues ? s.Hue : AttnHueFallback);
            hues[s.Label] = byId[s.Id].Hue;
        }
        var list = new List<AgentView>();
        foreach (var g in blockers.Where(b => b.Who != null || b.Session != null)
                     .Select(b => (b, owner: OwnerLocked(b))).GroupBy(x => x.owner?.Id ?? "label:" + x.b.Who))
        {
            var owner = g.First().owner;
            var label = owner?.Label ?? g.First().b.Who ?? "";
            var live = owner != null && !owner.Infra && PresentLocked(owner, now);
            var asks = g.Where(x => x.b.Kind == "pilot").Select(x => x.b.Label).Distinct().ToArray();
            var doing = g.Where(x => x.b.Kind != "pilot").Select(x => x.b.Label).Distinct().ToArray();
            // A gone session's own leases and measurements end by themselves; only what still asks the user is shown.
            if (!live && asks.Length == 0) continue;
            var name = owner != null ? DisplayNameLocked(owner) : names.TryGetValue(label, out var n) ? n : BaseDisplayName(label, null);
            list.Add(new AgentView(name, label, asks, live ? doing : []));
        }
        // Agents that are asking come first, then by name: the strip and the hover list read in that order.
        var active = list.OrderByDescending(a => a.Asks.Length > 0).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        // Same as last time: keep the instance, so the UI (which recomposes its text when the reference changes) does nothing.
        var old = _agentsView;
        if (SameAgents(old.Active, active) && old.Names.Count == names.Count && names.All(kv => old.Names.TryGetValue(kv.Key, out var v) && v == kv.Value)
            && old.Sessions.Count == byId.Count && byId.All(kv => old.Sessions.TryGetValue(kv.Key, out var v) && v == kv.Value)
            && old.Hues.Count == hues.Count && hues.All(kv => old.Hues.TryGetValue(kv.Key, out var v) && v == kv.Value)) return;
        _agentsView = new AgentsView(active, names, byId, hues);
    }

    private static bool SameAgents(AgentView[] a, AgentView[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i].Name != b[i].Name || a[i].Label != b[i].Label || !a[i].Asks.SequenceEqual(b[i].Asks) || !a[i].Doing.SequenceEqual(b[i].Doing)) return false;
        return true;
    }

    /// <summary>The agents the in-game UI may mention (rebuilt at 4 Hz; no lock).</summary>
    internal AgentsView AgentsSnapshot() => _agentsView;

    // ── Leases ───────────────────────────────────────────────────────

    private JObject LeaseAcquire(JToken? p)
    {
        var me = CurrentSession();
        if (me == null) return Err("no_session", "Send session.hello on this connection first (the MCP server does; scripts pass id and label).");
        var kind = p?["kind"]?.ToString() ?? "other";
        if (!LeaseKinds.Contains(kind)) return Err("bad_kind", $"kind must be one of {string.Join(", ", LeaseKinds)}");
        var label = Clip(p?["label"]?.ToString(), 80);
        if (string.IsNullOrWhiteSpace(label)) return Err("missing_label", "Pass label: what you are doing, e.g. 'perf test of Whats A Route'.");
        var ttl = Math.Clamp(p?["ttlSec"]?.Type == JTokenType.Integer ? p["ttlSec"]!.Value<int>() : LeaseTtlDefault, 5, LeaseTtlMax);
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            SessionsExpireLocked(now);
            // A granted restart is about to happen: starting a test now would lose it. Say who and let the caller wait.
            if (_restarts.FirstOrDefault(r => r.Status == "go") is { } go)
                return Err("restart_pending", $"{go.Who} was granted a HUD restart {(int)(now - go.DecidedAt!.Value).TotalSeconds} s ago" +
                                              (go.Reason != null ? $" ({go.Reason})" : "") + ". Wait for the HUD to come back, then try again.");
            // Restart first (attention queue): a piloted test would hold the restart off, so it waits for the restart instead.
            // Not refused: queued, with where it stands; acquire again once the HUD is back.
            if (kind == "pilot" && _restarts.Where(r => r.Status == "waiting" && r.Session != me.Id).OrderBy(r => r.At).FirstOrDefault() is { } wr)
                return new JObject
                {
                    ["ok"] = true, ["queued"] = true, ["status"] = "queued", ["id"] = null, ["kind"] = kind, ["label"] = label,
                    ["position"] = 2, ["ahead"] = new JArray($"{wr.Who} (HUD restart)"),
                    ["note"] = $"{wr.Who} is waiting to restart the HUD{(wr.Reason != null ? $" ({wr.Reason})" : "")}: restarts go first, so no pilot lease now. " +
                               "Acquire it again once the restart is done (bridge_status shows the HUD back) or the user declined it.",
                };
            var l = new Lease { Id = $"L{++_leaseSeq}", Kind = kind, Label = label!, Session = me.Id, Who = me.Label, Since = now, Until = now.AddSeconds(ttl) };
            _leases.Add(l);
            var waiting = _restarts.Where(r => r.Status == "waiting" && r.Session != me.Id).Select(r => r.Who).Distinct().ToList();
            var o = LeaseJson(l); o["ok"] = true;
            if (waiting.Count > 0) o["note"] = $"{string.Join(", ", waiting)} is waiting to restart the HUD; it goes ahead when you release this lease.";
            return o;
        }
    }

    private JObject LeaseRenew(JToken? p)
    {
        var id = p?["id"]?.ToString();
        var ttl = Math.Clamp(p?["ttlSec"]?.Type == JTokenType.Integer ? p["ttlSec"]!.Value<int>() : LeaseTtlDefault, 5, LeaseTtlMax);
        lock (_sessionLock)
        {
            var l = _leases.FirstOrDefault(x => x.Id == id);
            if (l == null) return Err("unknown_lease", $"No lease '{id}' (expired, released, or the HUD restarted).");
            l.Until = DateTime.UtcNow.AddSeconds(ttl);
            var o = LeaseJson(l); o["ok"] = true; return o;
        }
    }

    private JObject LeaseRelease(JToken? p)
    {
        var id = p?["id"]?.ToString();
        lock (_sessionLock)
        {
            var n = _leases.RemoveAll(x => x.Id == id);
            return new JObject { ["ok"] = true, ["released"] = n > 0, ["id"] = id };
        }
    }

    private static JObject LeaseJson(Lease l) => new()
    {
        ["id"] = l.Id, ["kind"] = l.Kind, ["label"] = l.Label, ["session"] = l.Session, ["who"] = l.Who,
        ["since"] = l.Since.ToString("O"), ["until"] = l.Until.ToString("O"),
        ["secondsLeft"] = Math.Max(0, (int)(l.Until - DateTime.UtcNow).TotalSeconds),
    };

    // ── Blockers ─────────────────────────────────────────────────────

    /// <summary>Everything a restart would interrupt right now, read from the HUD's own state plus the explicit leases.</summary>
    private List<Blocker> BlockersLocked(string? requester)
    {
        var list = new List<Blocker>();
        var requesterLabel = requester != null && _sessions.TryGetValue(requester, out var rs) ? rs.Label : null;
        bool Yours(string? who) => requesterLabel != null && who == requesterLabel;
        lock (_measureLock)
            if (_measure is { } m) list.Add(new Blocker("perf", $"measuring: {m.What}", m.Who, m.EndsAt, Yours(m.Who)));
        // Still patching (before its measured window), or measured window not announced: the patch set blocks too.
        lock (InstrLock)
            foreach (var s in InstrSets.Values.Where(s => !list.Any(b => b.Kind == "perf" && b.Who == s.Who)))
                list.Add(new Blocker("perf", $"patching the HUD for a {s.Kind} ({s.Label})", s.Who, s.ExpiresAt, Yours(s.Who)));
        foreach (var r in _pendingReloads) list.Add(new Blocker("reload", $"compiling {r.Folder}", r.Who, null, Yours(r.Who)));
        if (_reloadCheck is { } rc) list.Add(new Blocker("reload", $"checking {rc.Request.Folder} loaded", rc.Request.Who, null, Yours(rc.Request.Who)));
        if (_isRecording) list.Add(new Blocker("record", "recording a gameplay session", _recordingWho, null, Yours(_recordingWho)));
        var run = _queueRun;
        if (run != null)
        {
            var step = Queue().FirstOrDefault(s => s.Id == run.Id);
            list.Add(new Blocker("pilot", $"recording step '{step?.Label ?? run.Id}' for the user", step?.By, null, Yours(step?.By), Session: step?.Session));
        }
        lock (_flowLock)
            if (_flow.Status == "running") list.Add(new Blocker("pilot", $"guided flow: {_flow.Title ?? "untitled"}", _flow.Who, _flow.EndsAt, Yours(_flow.Who)));
        lock (_guideLock)
            if (_guide.Status is "waiting" or "detected" or "settling" && _guide.Instruction != null)
                list.Add(new Blocker("pilot", $"waiting for the user: {_guide.Instruction}", _guide.Who, null, Yours(_guide.Who), Session: _guide.Session));
        lock (_hlLock)
            foreach (var l in _hlLayers)
                // Held question strips (the attention queue: not this session's turn yet) are not in front of the player: no
                // blocker; nor are expired questions (#76: their strips are gone).
                if (!l.AsksHeld && l.Targets.FirstOrDefault(t => t.Ask != null && t.Answer == null && !t.Expired) is { } asked)
                    list.Add(new Blocker("pilot", $"question for the user: {asked.Ask}" + (l.Pending > 1 ? $" (+{l.Pending - 1})" : ""), l.Who, l.Until, Yours(l.Who), Session: l.Session));
        // The attention queue's floor holder (C8), unless what it shows is listed already (its card, flow, step or question):
        // the same owner, by session id when both know it (two sessions labelled "main" are two owners), else by label.
        if (AttnFloorBlockerLocked(requesterLabel) is { } floor
            && !list.Any(b => b.Kind == "pilot" && (b.Session != null && floor.Session != null ? b.Session == floor.Session : b.Who == floor.Who))) list.Add(floor);
        WorkRouteBlockersLocked(list, requesterLabel);   // an agent route leading the player (Work.cs): the owner's Doing
        foreach (var l in _leases) list.Add(new Blocker(l.Kind, l.Label, l.Who, l.Until, l.Session == requester, l.Id, l.Session));
        return list;
    }

    private static JObject BlockerJson(Blocker b)
    {
        var o = new JObject { ["kind"] = b.Kind, ["label"] = b.Label, ["who"] = b.Who, ["yours"] = b.Yours };
        if (b.Until is DateTime u) { o["until"] = u.ToString("O"); o["secondsLeft"] = Math.Max(0, (int)(u - DateTime.UtcNow).TotalSeconds); }
        if (b.LeaseId != null) o["lease"] = b.LeaseId;
        return o;
    }

    /// <summary>Blockers as the panel shows them this frame (a copy; taken once per frame).</summary>
    internal List<Blocker> BlockersSnapshot() { lock (_sessionLock) return BlockersLocked(null); }

    // ── Restart requests ─────────────────────────────────────────────

    private JObject RestartRequestNew(JToken? p)
    {
        var me = CurrentSession();
        if (me == null) return Err("no_session", "Send session.hello on this connection first (who is asking must be known).");
        var reason = Clip(p?["reason"]?.ToString(), 120);
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            SessionsExpireLocked(now);
            if (_restarts.FirstOrDefault(r => r.Status == "waiting" && r.Session == me.Id) is { } mine)
                return RestartJsonLocked(mine);   // asked twice: the same request
            var req = new RestartRequest { Id = $"R{++_restartSeq}", Session = me.Id, Who = me.Label, Reason = reason, At = now };
            _restarts.Add(req);
            if (_restarts.FirstOrDefault(r => r.Status == "go") is { } go)
            {
                req.Status = "merged"; req.Into = go.Id; req.By = go.Who; req.DecidedAt = now;
            }
            else
            {
                RestartEvaluateLocked(req, now);
                var what = reason != null ? $"{DisplayNameLocked(me)} wants to restart the HUD ({reason})" : $"{DisplayNameLocked(me)} wants to restart the HUD";
                GuideLog(new JObject
                {
                    ["text"] = req.Blockers.Count > 0 ? $"{what}: waiting for {RestartBlockersText(req)}" : $"{what}: in {Settings.RestartHoldSec.Value} s unless you say Not now",
                    ["kind"] = req.Blockers.Count > 0 ? "warn" : "step", ["title"] = "HUD restart",
                });
            }
            SessionsPublishLocked();
            return RestartJsonLocked(req);
        }
    }

    private JObject RestartStatus(string? id)
    {
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            var req = _restarts.FirstOrDefault(r => r.Id == id);
            if (req == null) return Err("unknown_request", $"No restart request '{id}' (the HUD may have restarted since, which is what you asked for).");
            if (req.Status == "waiting") RestartEvaluateLocked(req, now);
            return RestartJsonLocked(req);
        }
    }

    private JObject RestartCancel(string? id)
    {
        lock (_sessionLock)
        {
            var req = _restarts.FirstOrDefault(r => r.Id == id);
            if (req == null) return Err("unknown_request", $"No restart request '{id}'.");
            if (req.Status is "waiting" or "go") { req.Status = "cancelled"; req.By = req.Who; req.DecidedAt = DateTime.UtcNow; SessionsPublishLocked(); }
            return RestartJsonLocked(req);
        }
    }

    /// <summary>
    /// One waiting request against the HUD's state now: blocked (hold reset, blockers listed), or free and holding
    /// (the card's countdown), or past the hold and granted. The countdown starts when the card takes the panel's top
    /// slot: after the floor card that blocked it has shown its answer receipt, faded and left the hand-over gap
    /// (AttnSlotTopAtLocked; attention-queue-ui.md 3), so the player always sees the whole countdown.
    /// </summary>
    private void RestartEvaluateLocked(RestartRequest req, DateTime now)
    {
        req.Blockers = BlockersLocked(req.Session);
        if (req.Blockers.Count > 0) { req.HoldUntil = null; return; }
        req.HoldUntil ??= AttnSlotTopAtLocked(now).AddSeconds(Settings.RestartHoldSec.Value);
        if (now >= req.HoldUntil.Value) RestartGrantLocked(req, "auto", now);
    }

    private void RestartGrantLocked(RestartRequest req, string by, DateTime now)
    {
        req.Status = "go"; req.By = by; req.DecidedAt = now;
        foreach (var o in _restarts.Where(r => r != req && r.Status == "waiting")) { o.Status = "merged"; o.Into = req.Id; o.By = req.Who; o.DecidedAt = now; }
        // Over blockers (the user's Restart now): tell their holders in the log, so the loss has a reason.
        if (req.Blockers.Count > 0)
            GuideLog(new JObject { ["text"] = $"You let {DisplayNameOfLocked(req.Who)} restart the HUD over {RestartBlockersText(req)}", ["kind"] = "warn", ["title"] = "HUD restart" });
        else
            GuideLog(new JObject { ["text"] = $"{DisplayNameOfLocked(req.Who)} is restarting the HUD now" + (req.Reason != null ? $" ({req.Reason})" : ""), ["kind"] = "step", ["title"] = "HUD restart" });
    }

    private string RestartBlockersText(RestartRequest req) =>
        string.Join(", ", req.Blockers.Take(3).Select(b => b.Who != null ? $"{(OwnerLocked(b) is { } o ? DisplayNameLocked(o) : DisplayNameOfLocked(b.Who))}'s {b.Label}" : b.Label)) + (req.Blockers.Count > 3 ? $" (+{req.Blockers.Count - 3})" : "");

    private JObject RestartJsonLocked(RestartRequest r)
    {
        var now = DateTime.UtcNow;
        var o = new JObject
        {
            ["ok"] = true, ["id"] = r.Id, ["status"] = r.Status, ["who"] = r.Who, ["session"] = r.Session, ["reason"] = r.Reason,
            ["at"] = r.At.ToString("O"), ["by"] = r.By, ["into"] = r.Into, ["decidedAt"] = r.DecidedAt?.ToString("O"),
            ["blockers"] = new JArray(r.Blockers.Select(BlockerJson)),
        };
        if (r.HoldUntil is DateTime h) { o["holdUntil"] = h.ToString("O"); o["secondsToGo"] = Math.Max(0, (int)(h - now).TotalSeconds); }
        o["note"] = r.Status switch
        {
            "waiting" when r.Blockers.Count > 0 => "Blocked: poll restart.status until go, or release your own leases. The user sees this on the guide card and can allow or deny it.",
            "waiting" => $"Nothing blocks it: the card shows a {Settings.RestartHoldSec.Value} s countdown with Not now; poll restart.status until go.",
            "go" => "Granted: run the restart now (tools\\restart-hud.ps1). Nobody else is granted until the HUD is back.",
            "merged" => $"{r.By} is restarting the HUD already: wait for the bridge to come back instead of restarting again.",
            "denied" => "The user said Not now. Ask them in chat, or try again later.",
            _ => null,
        };
        return o;
    }

    /// <summary>User's "Restart now" on the card: granted over the blockers.</summary>
    internal void RestartAllow(string id)
    {
        lock (_sessionLock)
        {
            var req = _restarts.FirstOrDefault(r => r.Id == id);
            if (req is { Status: "waiting" }) { req.Blockers = BlockersLocked(req.Session); RestartGrantLocked(req, "user", DateTime.UtcNow); SessionsPublishLocked(); }
        }
    }

    /// <summary>User's "Not now" on the card.</summary>
    internal void RestartDeny(string id)
    {
        lock (_sessionLock)
        {
            var req = _restarts.FirstOrDefault(r => r.Id == id);
            if (req is { Status: "waiting" })
            {
                req.Status = "denied"; req.By = "user"; req.DecidedAt = DateTime.UtcNow;
                GuideLog(new JObject { ["text"] = $"Not now: {DisplayNameOfLocked(req.Who)}'s HUD restart was declined", ["kind"] = "result", ["title"] = "HUD restart" });
                SessionsPublishLocked();
            }
        }
    }

    /// <summary>Per frame from Render (throttled to 4 Hz): expire leases, re-evaluate waiting requests, time out stale grants.</summary>
    private void SessionsTick()
    {
        var now = DateTime.UtcNow;
        if ((now - _sessionsTickAt).TotalMilliseconds < 250) return;
        _sessionsTickAt = now;
        // A waiting step whose agent went quiet (guide.set expiresSec) leaves the card (AgentGuide.cs; its own lock, first).
        GuideExpireTick(now);
        // The attention queue's inputs are read before the sessions lock: game reads, and the guide, flow and highlight
        // locks are only ever taken nested inside the sessions lock, never around it.
        var player = AttnSamplePlayer();
        (string status, string? instruction, string? who, string? session) guide;
        lock (_guideLock) guide = (_guide.Status, _guide.Instruction, _guide.Who, _guide.Session);
        bool flowRunning; string? flowWho;
        lock (_flowLock) { flowRunning = _flow.Status == "running"; flowWho = _flow.Who; }
        var hlPending = HighlightPendingByLayer();
        var hlAsks = HighlightAskTextByLayer();   // the work items' WaitingOn = Player (Work.cs), read before the lock like the rest
        var effects = new List<Action>();
        var workPubs = new List<JObject>();
        lock (_sessionLock)
        {
            if (_sessions.Count > 0 || _leases.Count > 0 || _restarts.Count > 0)
            {
                SessionsExpireLocked(now);
                foreach (var r in _restarts.Where(r => r.Status == "waiting").OrderBy(r => r.At).ToList())
                    if (r.Status == "waiting") RestartEvaluateLocked(r, now);   // a grant merges the others, so re-check the status
                // Granted but the HUD is still here: the requester never restarted (crashed, declined its own UAC prompt).
                foreach (var r in _restarts.Where(r => r.Status == "go" && now - r.DecidedAt!.Value > RestartGoExpiry))
                {
                    r.Status = "expired";
                    GuideLog(new JObject { ["text"] = $"{DisplayNameOfLocked(r.Who)} was granted a HUD restart but didn't restart it", ["kind"] = "warn", ["title"] = "HUD restart" });
                }
            }
            // A fault in the attention queue must not take the sessions down with it (restarts, the chip, the agents view):
            // it is logged once per distinct message and the tick goes on.
            try { AttnTickLocked(now, player, guide, flowRunning, flowWho, hlPending, effects); }
            catch (Exception ex)
            {
                var msg = $"[Attention] tick: {ex.GetType().Name}: {ex.Message}";
                if (msg != _attnTickError) { _attnTickError = msg; LogFault(msg + " " + ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()); }
            }
            // Work items (Work.cs) after the queue moved (their WaitingOn = Player reads its items) and before the blockers
            // (an agent route leading the player is its owner's Doing). Guarded the same way: a fault never stops the tick.
            try { WorkTickLocked(now, guide, hlAsks, workPubs); }
            catch (Exception ex)
            {
                var msg = $"[Work] tick: {ex.GetType().Name}: {ex.Message}";
                if (msg != _workTickError) { _workTickError = msg; LogFault(msg + " " + ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()); }
            }
            // After the queue moved: the floor holder is one of the asks. The blockers are read once per tick, here on the
            // render thread (they enumerate render-owned state such as the step queue), for the agents view and for the
            // per-session doing the published view carries (SessionsPublishLocked also runs on the TCP thread, so it
            // reuses this copy instead of reading the blockers itself).
            var tickBlockers = BlockersLocked(null);
            _doingCache = DoingBySessionLocked(tickBlockers);
            AgentsViewRebuildLocked(tickBlockers, now);
            SessionsPublishLocked();
        }
        AttnRun(effects);
        WorkFlush(workPubs);    // "work" events, outside the lock
        WorkSaveIfChanged();
    }

    /// <summary>What the panel shows about sessions (SessionsView): the copy published last, never taken per frame.</summary>
    internal SessionsView SessionsSnapshot() => _sessionsView;

    /// <summary>
    /// Publish the panel's copy: the sessions (connected first, then by hello), the oldest waiting restart request (the card:
    /// blocked or counting down), the granted one (about to happen), and the MCP rollout. Called under the lock, at 4 Hz from
    /// SessionsTick and on every state change, so the indicator never waits a tick after a click or a hello.
    /// </summary>
    private void SessionsPublishLocked()
    {
        var now = DateTime.UtcNow;
        var doing = _doingCache;   // SessionsTick's copy (at most 250 ms old): this also runs on the TCP thread
        var sessions =_sessions.Values.OrderByDescending(s => s.Connected).ThenBy(s => s.HelloAt)
            .Select(s => new SessionView(s.Id, s.Label, DisplayNameLocked(s), s.Branch, s.Kind, s.Connected, s.LastSeen, s.Mcp,
                PresentLocked(s, now), s.Infra, doing.TryGetValue(s.Id, out var d) ? d.ToArray() : [])).ToArray();
        var agents = 0;
        foreach (var s in sessions) if (s.Present && !s.Infra) agents++;
        var waiting = _restarts.Where(r => r.Status == "waiting").OrderBy(r => r.At).FirstOrDefault();
        var granted = _restarts.FirstOrDefault(r => r.Status == "go");
        _sessionsView = new SessionsView(sessions, agents,RestartCopyLocked(waiting), RestartCopyLocked(granted), McpRolloutLocked());
        AttnPublishLocked();   // and the attention queue's copies (the ask card, the next strip, the dot), when they changed
    }

    /// <summary>
    /// The panel's copy of a request, with the requester's who-dot hue and how many other agents wait for the same restart:
    /// for the waiting one the card shows (the oldest), the other waiting requests (its grant merges them into it); for a
    /// granted one, those merged into it.
    /// </summary>
    private RestartRequest? RestartCopyLocked(RestartRequest? req) => req == null ? null : new RestartRequest
    {
        Id = req.Id, Session = req.Session, Who = req.Who, Reason = req.Reason, At = req.At, Status = req.Status, By = req.By,
        HoldUntil = req.HoldUntil, DecidedAt = req.DecidedAt, Blockers = req.Blockers.ToList(),
        Hue = AttnHueLocked(req.Session, req.Who),
        MergedCount = req.Status == "waiting" ? _restarts.Count(r => r != req && r.Status == "waiting") : _restarts.Count(r => r.Status == "merged" && r.Into == req.Id),
    };
}
