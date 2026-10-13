using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

// "HUD temporarily unavailable is normal": the HUD's live phase (hud-state.json, hud.state, status), the listener's early
// replies while the HUD is on a load screen, the pub/sub events of a phase change, and the outage (planned user restart)
// card. Contract: scaffolding scratch contract.md sections 1-3. Field names here are the contract: never rename one.

/// <summary>What the TCP listener asks the plugin without queueing for the main thread (TcpBridgeServer.Live).</summary>
internal interface IBridgeLiveInfo
{
    /// <summary>A method answered on the listener thread (hud.state, outage.set, outage.state): its result JSON, else null.</summary>
    string? TryLocal(string method, JToken? p, int pending);

    /// <summary>Adds phase, phaseSinceMs and tickAgeMs to the listener's status reply.</summary>
    void AddStatus(JObject status);

    /// <summary>A queued request has waited this long: an error to answer it with now (the HUD is loading), else null.</summary>
    BridgeRpcError? EarlyReply(TimeSpan waited, int pending);

    /// <summary>The 10 s timeout's message and data (frozen vs loading vs busy).</summary>
    BridgeRpcError Timeout(int seconds, int pending);
}

/// <summary>A JSON-RPC error the listener sends (code, message, data).</summary>
internal sealed record BridgeRpcError(int Code, string Message, object Data);

/// <summary>hud-state.json's startup block: when this HUD process started and when the bridge got up.</summary>
public sealed class HudStartupDto
{
    [JsonProperty("processStartedAt")] public DateTime? ProcessStartedAt { get; set; }
    [JsonProperty("initAt")] public DateTime? InitAt { get; set; }
    [JsonProperty("listenerAt")] public DateTime? ListenerAt { get; set; }
    [JsonProperty("firstReadyAt")] public DateTime? FirstReadyAt { get; set; }
    /// <summary>Process start to listener up, seconds (null until the listener is up).</summary>
    [JsonProperty("sec")] public double? Sec { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

/// <summary>
/// hud-state.json (contract section 1): written atomically by the bridge on every phase change and as a heartbeat every 2 s
/// from a worker timer. ISO UTC times; nulls written as null.
/// </summary>
public class HudStateDto
{
    [JsonProperty("schema")] public int Schema { get; set; } = 1;
    [JsonProperty("game")] public string Game { get; set; } = "";
    /// <summary>starting | menu | loading | ready | restarting | user-restart | closed</summary>
    [JsonProperty("phase")] public string Phase { get; set; } = "starting";
    [JsonProperty("prevPhase")] public string? PrevPhase { get; set; }
    [JsonProperty("since")] public DateTime Since { get; set; }
    /// <summary>+1 on every phase change (not on heartbeats), carried over from the previous file.</summary>
    [JsonProperty("seq")] public long Seq { get; set; }
    [JsonProperty("heartbeat")] public DateTime Heartbeat { get; set; }
    /// <summary>Last Tick/Render on the main thread (main thread frozen: old). Null before the first.</summary>
    [JsonProperty("tickAt")] public DateTime? TickAt { get; set; }
    [JsonProperty("inGame")] public bool InGame { get; set; }
    [JsonProperty("loading")] public bool Loading { get; set; }
    /// <summary>The area before the current load, or the current area when ready.</summary>
    [JsonProperty("lastArea")] public string? LastArea { get; set; }
    /// <summary>The current area when ready, else null.</summary>
    [JsonProperty("area")] public string? Area { get; set; }
    [JsonProperty("areaChangedAt")] public DateTime? AreaChangedAt { get; set; }
    [JsonProperty("lastLoadMs")] public long? LastLoadMs { get; set; }
    [JsonProperty("gamePid")] public int? GamePid { get; set; }
    [JsonProperty("hudPid")] public int HudPid { get; set; }
    [JsonProperty("listenerUp")] public bool ListenerUp { get; set; }
    [JsonProperty("port")] public int? Port { get; set; }
    [JsonProperty("startup")] public HudStartupDto? Startup { get; set; }
    /// <summary>The last 10 measured startups (process start to listener up), carried over between HUD runs.</summary>
    [JsonProperty("recentStartupSec")] public List<double> RecentStartupSec { get; set; } = new();
    /// <summary>Median of RecentStartupSec, null when none.</summary>
    [JsonProperty("typicalStartupSec")] public double? TypicalStartupSec { get; set; }
    /// <summary>On restarting | user-restart | closed: agent-restart | user-restart | unknown.</summary>
    [JsonProperty("closeReason")] public string? CloseReason { get; set; }
    /// <summary>The last off-thread read that failed, naming the link (null while reads work).</summary>
    [JsonProperty("error")] public string? Error { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

/// <summary>hud.state (answered on the listener thread): the file's object plus the live main-thread age and the queue.</summary>
public sealed class HudStateReplyDto : HudStateDto
{
    [JsonProperty("ok")] public bool Ok { get; set; } = true;
    /// <summary>ms since the last main-thread Tick/Render; null before the first.</summary>
    [JsonProperty("tickAgeMs")] public long? TickAgeMs { get; set; }
    /// <summary>Requests queued for the main thread.</summary>
    [JsonProperty("pending")] public int Pending { get; set; }
    [JsonProperty("phaseSinceMs")] public long PhaseSinceMs { get; set; }
}

/// <summary>restart-state.json as tools\restart-hud.ps1 writes it (read in OnClose: an agent/script restart in progress).</summary>
public sealed class RestartStateFileDto
{
    [JsonProperty("phase")] public string? Phase { get; set; }
    [JsonProperty("at")] public DateTime? At { get; set; }
    [JsonProperty("who")] public string? Who { get; set; }
    [JsonProperty("reason")] public string? Reason { get; set; }
    [JsonProperty("game")] public string? Game { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

// ── Listener error data (contract section 2) ───────────────────────────

/// <summary>-32010 data: the request was answered from the listener because the HUD is on a load screen (or starting).</summary>
public sealed class HudLoadingErrorData
{
    [JsonProperty("reason")] public string Reason { get; set; } = "hud_loading";
    [JsonProperty("phase")] public string Phase { get; set; } = "";
    [JsonProperty("since")] public DateTime Since { get; set; }
    [JsonProperty("sinceMs")] public long SinceMs { get; set; }
    [JsonProperty("lastArea")] public string? LastArea { get; set; }
    [JsonProperty("lastLoadMs")] public long? LastLoadMs { get; set; }
    [JsonProperty("retryAfterMs")] public long RetryAfterMs { get; set; }
}

/// <summary>-32002 data: the 10 s timeout, with what the HUD was doing.</summary>
public sealed class HudTimeoutErrorData
{
    [JsonProperty("reason")] public string Reason { get; set; } = "hud_timeout";
    [JsonProperty("phase")] public string Phase { get; set; } = "";
    [JsonProperty("sinceMs")] public long SinceMs { get; set; }
    [JsonProperty("tickAgeMs")] public long? TickAgeMs { get; set; }
    [JsonProperty("lastArea")] public string? LastArea { get; set; }
    [JsonProperty("pending")] public int Pending { get; set; }
}

// ── Pub/sub events (source "bridge") ────────────────────────────────────

public sealed class AreaLoadingEvent
{
    [JsonProperty("from")] public string? From { get; set; }
    [JsonProperty("at")] public DateTime At { get; set; }
}

public sealed class AreaReadyEvent
{
    [JsonProperty("area")] public string? Area { get; set; }
    [JsonProperty("from")] public string? From { get; set; }
    /// <summary>The load's duration; null when no load screen was seen (first area after login, a load under 250 ms).</summary>
    [JsonProperty("loadMs")] public long? LoadMs { get; set; }
}

public sealed class HudPhaseEvent
{
    [JsonProperty("phase")] public string Phase { get; set; } = "";
    [JsonProperty("prev")] public string? Prev { get; set; }
    [JsonProperty("since")] public DateTime Since { get; set; }
    [JsonProperty("inGame")] public bool InGame { get; set; }
    [JsonProperty("area")] public string? Area { get; set; }
}

public sealed class OutageAnswerEvent
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    /// <summary>now | notNow</summary>
    [JsonProperty("answer")] public string Answer { get; set; } = "";
    [JsonProperty("at")] public DateTime At { get; set; }
}

// ── Outage (contract section 3) ─────────────────────────────────────────

/// <summary>A session in outage.state's busy / ready lists.</summary>
public sealed class OutageParty
{
    [JsonProperty("session")] public string? Session { get; set; }
    [JsonProperty("label")] public string? Label { get; set; }
    /// <summary>busy only: what it is doing (its blockers' labels).</summary>
    [JsonProperty("doing", NullValueHandling = NullValueHandling.Ignore)] public string? Doing { get; set; }
    /// <summary>busy only: a lease it holds.</summary>
    [JsonProperty("lease", NullValueHandling = NullValueHandling.Ignore)] public string? Lease { get; set; }
    /// <summary>busy only, optional: when its work ends (a lease's TTL, a step's deadline); the card shows "(2m 00s left)".</summary>
    [JsonProperty("until", NullValueHandling = NullValueHandling.Ignore)] public DateTime? Until { get; set; }
    /// <summary>busy only, optional: since when it is busy; the card shows "(for 1m 40s)" when no until is known.</summary>
    [JsonProperty("since", NullValueHandling = NullValueHandling.Ignore)] public DateTime? Since { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

/// <summary>outage.set params: shows / updates the in-game card; clear removes it. busy / ready: the MCP's view, used
/// instead of the bridge's own when given.</summary>
public sealed class OutageSetParams
{
    [JsonProperty("id")] public string? Id { get; set; }
    [JsonProperty("game")] public string? Game { get; set; }
    [JsonProperty("phase")] public string? Phase { get; set; }
    [JsonProperty("reason")] public string? Reason { get; set; }
    [JsonProperty("by")] public string? By { get; set; }
    /// <summary>The requester's readable label (requestedByLabel): the name shown when `by` is not a connected session.</summary>
    [JsonProperty("byLabel")] public string? ByLabel { get; set; }
    [JsonProperty("stopOnly")] public bool? StopOnly { get; set; }
    /// <summary>Phase down: when the runner stops the HUD (ISO UTC). While in the future the card counts down to it, with
    /// Cancel (outage.answer notNow). Kept while the phase stays the same and stopAt is omitted; a phase change without
    /// it clears it.</summary>
    [JsonProperty("stopAt")] public DateTime? StopAt { get; set; }
    [JsonProperty("clear")] public bool Clear { get; set; }
    [JsonProperty("busy")] public List<OutageParty>? Busy { get; set; }
    [JsonProperty("ready")] public List<OutageParty>? Ready { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

/// <summary>outage.state (and outage.set's reply).</summary>
public sealed class OutageStateDto
{
    [JsonProperty("ok")] public bool Ok { get; set; } = true;
    /// <summary>The outage on the card; null when none.</summary>
    [JsonProperty("id")] public string? Id { get; set; }
    [JsonProperty("game")] public string? Game { get; set; }
    [JsonProperty("phase")] public string? Phase { get; set; }
    [JsonProperty("reason")] public string? Reason { get; set; }
    [JsonProperty("by")] public string? By { get; set; }
    [JsonProperty("stopOnly")] public bool StopOnly { get; set; }
    [JsonProperty("byLabel", NullValueHandling = NullValueHandling.Ignore)] public string? ByLabel { get; set; }
    /// <summary>Phase down: when the runner stops the HUD (the card's countdown), else null.</summary>
    [JsonProperty("stopAt")] public DateTime? StopAt { get; set; }
    /// <summary>The card's answer: null | now | notNow.</summary>
    [JsonProperty("answer")] public string? Answer { get; set; }
    [JsonProperty("answeredAt")] public DateTime? AnsweredAt { get; set; }
    /// <summary>Whether the card is on screen now (phase announced / holding, not answered "notNow").</summary>
    [JsonProperty("cardShown")] public bool CardShown { get; set; }
    [JsonProperty("busy")] public List<OutageParty> Busy { get; set; } = new();
    [JsonProperty("ready")] public List<OutageParty> Ready { get; set; } = new();
    /// <summary>Where busy / ready came from: bridge (its sessions, leases and attention items) | mcp (outage.set).</summary>
    [JsonProperty("partiesFrom")] public string PartiesFrom { get; set; } = "bridge";
    /// <summary>The restart lease adopted from the incident board at load (user restart held until in game), if any.</summary>
    [JsonProperty("lease", NullValueHandling = NullValueHandling.Ignore)] public string? Lease { get; set; }
    [JsonProperty("cleared", NullValueHandling = NullValueHandling.Ignore)] public bool? Cleared { get; set; }
}

/// <summary>The incident board (%LOCALAPPDATA%\Hexile\incidents.json), only the fields the bridge reads.</summary>
public sealed class IncidentBoardDto
{
    [JsonProperty("rev")] public long Rev { get; set; }
    [JsonProperty("incidents")] public List<IncidentDto>? Incidents { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

public sealed class IncidentDto
{
    [JsonProperty("id")] public string? Id { get; set; }
    [JsonProperty("kind")] public string? Kind { get; set; }
    [JsonProperty("scope")] public string? Scope { get; set; }
    [JsonProperty("state")] public string? State { get; set; }
    [JsonProperty("key")] public string? Key { get; set; }
    [JsonProperty("outage")] public IncidentOutageDto? Outage { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

public sealed class IncidentOutageDto
{
    [JsonProperty("game")] public string? Game { get; set; }
    /// <summary>announced | holding | down | waitingGame | back | cancelled</summary>
    [JsonProperty("phase")] public string? Phase { get; set; }
    /// <summary>When the phase was entered; for phase down the HUD stop, which the back toast's elapsed time counts from.</summary>
    [JsonProperty("phaseAt")] public DateTime? PhaseAt { get; set; }
    [JsonProperty("reason")] public string? Reason { get; set; }
    /// <summary>Who takes part, by session id; the toasts count every one not gone.</summary>
    [JsonProperty("participants")] public Dictionary<string, IncidentParticipantDto>? Participants { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

public sealed class IncidentParticipantDto
{
    [JsonProperty("label")] public string? Label { get; set; }
    /// <summary>busy | ready | idle | gone</summary>
    [JsonProperty("state")] public string? State { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}
