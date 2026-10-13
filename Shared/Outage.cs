using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// The outage incident's bridge side (contract section 3): the user restarts game + HUD by hand (a patch, a crash) and the
/// agents learn it is planned and when it is back.
///   outage.set {id, game, phase, reason, by, stopOnly, busy?, ready?, clear?}  shows / updates the in-game outage card (the
///                 MCP calls it after raising the incident); clear: true removes it. Answered on the listener thread.
///   outage.state  -> {id, phase, answer, answeredAt, busy: [{session, label, doing, lease}], ready: [{session, label}], ...}.
///                 busy / ready: the bridge's own view (present agents with something going on: a blocker, a lease or an
///                 attention item, as Sessions.cs counts them) unless outage.set passed its own lists.
///   The card (GuidePanelOutage.cs, texts in OutageCardText.cs) sits at the top of the guide panel's stack (restarts go
///   first) while the phase is announced or holding; "Restart now" / "Not now" publish pub/sub outage.answer {id, answer:
///   now | notNow, at} (from a pool thread, never the render thread).
///   At load (Initialise) an open outage for this game in phase down | waitingGame on %LOCALAPPDATA%\Hexile\incidents.json
///   (HEXILE_INCIDENT_DIR in tests) becomes a restart lease (kind other, label "user restart &lt;id&gt;") held until the player is
///   in game, so agents' restarts cannot jump in while the user brings the game back.
/// </summary>
public partial class WhatsAnAiBridge
{
    private static readonly HashSet<string> OutagePhases = new(StringComparer.Ordinal) { "announced", "holding", "down", "waitingGame", "back", "cancelled" };
    private static readonly TimeSpan OutageLeaseSpan = TimeSpan.FromMinutes(10);   // renewed by the hud-state tick until in game
    private const double OutageReceiptSec = 60;   // an answered "now" card stays as a receipt at most this long

    private readonly object _outLock = new();
    private OutageCardState? _outage;                     // under _outLock
    private volatile OutageView? _outageView;             // the panel's immutable copy
    private string? _outageLeaseId, _outageLeaseOutage;   // the adopted restart lease (in _leases, under _sessionLock)

    private sealed class OutageCardState
    {
        public string Id = "", Game = "", Phase = "announced";
        public string? Reason, By, Answer;
        public bool StopOnly;
        public DateTime SetAt;
        public DateTime? AnsweredAt;
        public List<OutageParty>? Busy, Ready;   // from outage.set (the MCP's view); null = the bridge computes them
        public long Rev;
    }

    /// <summary>What the outage card draws (built when the state changes, never per frame).</summary>
    internal sealed record OutageView(string Id, long Rev, string Phase, string? Reason, string? By, bool StopOnly, string? Answer, DateTime? AnsweredAt, DateTime SetAt);

    private long _outageRev;

    private string OutageSet(JToken? p)
    {
        OutageSetParams? q;
        try { q = p?.ToObject<OutageSetParams>(); }
        catch (Exception ex) { return Serialize(new JObject { ["error"] = "bad_params", ["message"] = $"outage.set params: {ex.Message}" }); }
        if (q == null) return Serialize(new JObject { ["error"] = "missing_params", ["message"] = "outage.set needs {id, game, phase, reason, by, stopOnly} or {clear: true}." });
        lock (_outLock)
        {
            if (q.Clear)
            {
                if (q.Id != null && _outage != null && _outage.Id != q.Id)
                    return Serialize(new JObject { ["error"] = "other_outage", ["message"] = $"clear asked for {q.Id}, but the card shows {_outage.Id}; nothing cleared." });
                var had = _outage != null;
                _outage = null;
                OutagePublishLocked();
                var cleared = OutageStateNow();
                cleared.Cleared = had;
                return JsonConvert.SerializeObject(cleared);
            }
            if (string.IsNullOrWhiteSpace(q.Id)) return Serialize(new JObject { ["error"] = "missing_id", ["message"] = "outage.set needs id (the incident id)." });
            var game = q.Game ?? GameId;
            if (game != GameId)
                return Serialize(new JObject { ["error"] = "wrong_game", ["message"] = $"outage {q.Id} is for {game}, this bridge is {GameId}." });
            var phase = q.Phase ?? _outage?.Phase ?? "announced";
            if (!OutagePhases.Contains(phase))
                return Serialize(new JObject { ["error"] = "bad_phase", ["message"] = $"phase '{phase}' is not one of {string.Join(", ", OutagePhases)}." });
            if (_outage == null || _outage.Id != q.Id)
                _outage = new OutageCardState { Id = q.Id!, SetAt = DateTime.UtcNow };
            var o = _outage;
            o.Game = game;
            o.Phase = phase;
            o.Reason = q.Reason ?? o.Reason;
            o.By = q.By ?? o.By;
            o.StopOnly = q.StopOnly ?? o.StopOnly;
            if (q.Busy != null) o.Busy = q.Busy;
            if (q.Ready != null) o.Ready = q.Ready;
            OutagePublishLocked();
        }
        return JsonConvert.SerializeObject(OutageStateNow());
    }

    private void OutagePublishLocked()
    {
        var o = _outage;
        if (o == null) { _outageView = null; return; }
        o.Rev = ++_outageRev;
        _outageView = new OutageView(o.Id, o.Rev, o.Phase, o.Reason, o.By, o.StopOnly, o.Answer, o.AnsweredAt, o.SetAt);
    }

    /// <summary>The card is on screen: phase announced / holding, not answered "notNow", an answered "now" only as a short receipt.</summary>
    private static bool OutageCardShown(OutageCardState o) => OutageCardShown(o.Phase, o.Answer, o.AnsweredAt);

    private static bool OutageCardShown(string phase, string? answer, DateTime? answeredAt) =>
        phase is "announced" or "holding" && answer != "notNow"
        && !(answer == "now" && answeredAt is DateTime at && (DateTime.UtcNow - at).TotalSeconds > OutageReceiptSec);

    /// <summary>For the panel: the card to draw this frame, or null.</summary>
    internal OutageView? OutageCardSnapshot()
    {
        var v = _outageView;
        if (v == null) return null;
        return OutageCardShown(v.Phase, v.Answer, v.AnsweredAt) ? v : null;   // lock-free: the view is immutable
    }

    private OutageStateDto OutageStateNow()
    {
        OutageStateDto r;
        List<OutageParty>? busy, ready;
        lock (_outLock)
        {
            var o = _outage;
            r = new OutageStateDto
            {
                Id = o?.Id, Game = o?.Game ?? GameId, Phase = o?.Phase, Reason = o?.Reason, By = o?.By, StopOnly = o?.StopOnly ?? false,
                Answer = o?.Answer, AnsweredAt = o?.AnsweredAt, CardShown = o != null && OutageCardShown(o),
            };
            busy = o?.Busy; ready = o?.Ready;
        }
        if (busy != null || ready != null)
        {
            r.Busy = busy ?? new(); r.Ready = ready ?? new(); r.PartiesFrom = "mcp";
        }
        else
        {
            var (b, rd) = OutageParties();
            r.Busy = b; r.Ready = rd; r.PartiesFrom = "bridge";
        }
        lock (_sessionLock) r.Lease = _outageLeaseId;
        return r;
    }

    /// <summary>
    /// Busy vs ready from the bridge's own state: present, non-infra sessions; busy = doing something (its blockers: a
    /// measurement, a step, the floor...), holding a lease or owning an attention item; every other present one is ready
    /// (readiness never waits on silence).
    /// </summary>
    private (List<OutageParty> busy, List<OutageParty> ready) OutageParties()
    {
        var busy = new List<OutageParty>();
        var ready = new List<OutageParty>();
        var view = _sessionsView;
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            foreach (var s in view.Sessions)
            {
                if (!s.Present || s.Infra) continue;
                var lease = _leases.FirstOrDefault(l => l.Session == s.Id && l.Until > now && l.Id != _outageLeaseId);
                var queued = 0;
                try { queued = AttnOfSessionLocked(s.Id, now).Count; } catch { }
                if (s.Doing.Length > 0 || lease != null || queued > 0)
                {
                    var doing = s.Doing.Length > 0 ? string.Join("; ", s.Doing)
                        : lease != null ? lease.Label
                        : $"{queued} item(s) waiting to show the player";
                    busy.Add(new OutageParty { Session = s.Id, Label = s.Label, Doing = doing, Lease = lease?.Id });
                }
                else ready.Add(new OutageParty { Session = s.Id, Label = s.Label });
            }
        }
        return (busy, ready);
    }

    /// <summary>The card's buttons (render thread): record the answer, publish outage.answer from a pool thread, toast.</summary>
    private void OutageAnswer(string id, string answer)
    {
        DateTime at;
        lock (_outLock)
        {
            if (_outage == null || _outage.Id != id || _outage.Answer != null) return;
            _outage.Answer = answer;
            _outage.AnsweredAt = at = DateTime.UtcNow;
            OutagePublishLocked();
        }
        var ev = new OutageAnswerEvent { Id = id, Answer = answer, At = at };
        ThreadPool.QueueUserWorkItem(_ => HudStatePublish("outage.answer", ev));
        try
        {
            GuideLog(new JObject
            {
                ["text"] = answer == "now" ? OutageCardText.ToastNowText : OutageCardText.ToastNotNowText,
                ["kind"] = answer == "now" ? "result" : "agent",
                ["title"] = OutageCardText.ToastTitle,
            });
        }
        catch { }
    }

    // ── The incident board ───────────────────────────────────────────

    private static string IncidentBoardPath =>
        Path.Combine(Environment.GetEnvironmentVariable("HEXILE_INCIDENT_DIR") is { Length: > 0 } dir
            ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexile"), "incidents.json");

    /// <summary>An open outage incident for this game on the board: its id and outage phase.</summary>
    private bool OutageOpenOnBoard(out string? id, out string? phase)
    {
        id = null; phase = null;
        try
        {
            var path = IncidentBoardPath;
            if (!File.Exists(path)) return false;
            var board = JsonConvert.DeserializeObject<IncidentBoardDto>(HsReadShared(path));
            var inc = board?.Incidents?.LastOrDefault(i => i.Kind == "outage" && i.State != "resolved"
                                                          && (i.Scope == GameId || i.Outage?.Game == GameId || i.Key == $"outage:{GameId}"));
            if (inc == null) return false;
            id = inc.Id; phase = inc.Outage?.Phase;
            return true;
        }
        catch (Exception ex) { _hsReadError = $"reading the incident board {IncidentBoardPath}: {ex.GetType().Name}: {ex.Message}"; return false; }
    }

    /// <summary>Initialise: an open outage for this game in down | waitingGame becomes a restart lease held until in game.</summary>
    private void OutageAdoptFromBoard()
    {
        if (!OutageOpenOnBoard(out var id, out var phase) || id == null || phase is not ("down" or "waitingGame")) return;
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            _outageLeaseId = $"L-outage-{id}";
            _outageLeaseOutage = id;
            _leases.RemoveAll(l => l.Id == _outageLeaseId);
            _leases.Add(new Lease
            {
                Id = _outageLeaseId, Kind = "other", Label = $"user restart {id}", Session = $"outage:{id}", Who = "user restart",
                Since = now, Until = now + OutageLeaseSpan,
            });
        }
        LogMessage($"[Outage] {id} ({phase}): holding agents' HUD restarts until the player is in game");
    }

    /// <summary>From the hud-state tick: renew the adopted lease while not in game, release it once ready.</summary>
    private void OutageLeaseTick(string phase)
    {
        if (_outageLeaseId == null) return;
        string? released = null;
        lock (_sessionLock)
        {
            var l = _leases.FirstOrDefault(x => x.Id == _outageLeaseId);
            if (phase == "ready" || l == null)
            {
                _leases.RemoveAll(x => x.Id == _outageLeaseId);
                released = _outageLeaseOutage;
                _outageLeaseId = null;
                _outageLeaseOutage = null;
            }
            else l.Until = DateTime.UtcNow + OutageLeaseSpan;
        }
        if (released != null) LogMessage($"[Outage] {released}: the player is in game, agents' HUD restarts are free again");
    }
}
