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
///   outage.set {id, game, phase, reason, by, byLabel?, stopOnly, stopAt?, busy?, ready?, clear?}  shows / updates the
///                 in-game outage card (the MCP calls it after raising the incident); clear: true removes it. Answered on
///                 the listener thread. stopAt (phase down): when the runner stops the HUD; while ahead, the card counts
///                 down to it with Cancel. Kept while the phase stays and stopAt is omitted, cleared by a phase change.
///   outage.state  -> {id, phase, stopAt, answer, answeredAt, busy: [{session, label, doing, lease, until}], ready: [...], ...}.
///                 busy / ready: the bridge's own view (present agents with something going on: a blocker, a lease or an
///                 attention item, as Sessions.cs counts them) unless outage.set passed its own lists.
///   The card (GuidePanelOutage.cs, texts in OutageCardText.cs) sits at the top of the guide panel's stack (restarts go
///   first): announced / holding = waits for busy agents, or asks for the press when none is busy (the runner never stops
///   the HUD on "nothing busy" alone); down with stopAt ahead = the stopping countdown with the steps and Cancel. "Restart
///   now" / "Not now" / "Cancel" publish pub/sub outage.answer {id, answer: now | notNow, at} (from a pool thread, never
///   the render thread). Cancel is a notNow after now, accepted only while the countdown runs.
///   Toasts: the bridge is the single author of outage toasts (cancelled, Restart now over busy agents, down without a
///   countdown, and after the restart HUD IS UP / BACK), so N agents never make N toasts.
///   At load (Initialise) an open outage for this game in phase down | waitingGame on %LOCALAPPDATA%\Hexile\incidents.json
///   (HEXILE_INCIDENT_DIR in tests) becomes a restart lease (kind other, label "user restart &lt;id&gt;") held until the player is
///   in game, so agents' restarts cannot jump in while the user brings the game back; the hud-state tick posts HUD IS UP
///   (menu / loading, retired on the first in-game tick) and BACK (in game) for it.
/// </summary>
public partial class WhatsAnAiBridge
{
    private static readonly HashSet<string> OutagePhases = new(StringComparer.Ordinal) { "announced", "holding", "down", "waitingGame", "back", "cancelled" };
    private static readonly TimeSpan OutageLeaseSpan = TimeSpan.FromMinutes(10);   // renewed by the hud-state tick until in game
    private const double OutageReceiptSec = 60;   // an answered "now" card stays as a receipt at most this long
    private const double OutageStopGraceSec = 30; // the countdown card stays this long past stopAt (the stop is seconds away)

    private readonly object _outLock = new();
    private OutageCardState? _outage;                     // under _outLock
    private volatile OutageView? _outageView;             // the panel's immutable copy
    private string? _outageLeaseId, _outageLeaseOutage;   // the adopted restart lease (in _leases, under _sessionLock)

    // The outage this bridge came back for (adopted at load): its toasts. Initialise, then only the hud-state timer.
    private string? _outageResumeId;
    private DateTime? _outageResumeDownAt;
    private int _outageResumeAgents;
    private bool _outageResumeUpPosted;

    private sealed class OutageCardState
    {
        public string Id = "", Game = "", Phase = "announced";
        public string? Reason, By, ByLabel, Answer;
        public bool StopOnly;
        public DateTime SetAt;
        public DateTime? AnsweredAt, StopAt;
        public List<OutageParty>? Busy, Ready;   // from outage.set (the MCP's view); null = the bridge computes them
        public bool ToastedDown, ToastedCancel;
        public long Rev;
    }

    /// <summary>What the outage card draws (built when the state changes, never per frame).</summary>
    internal sealed record OutageView(string Id, long Rev, string Phase, string? Reason, string? By, string? ByLabel, bool StopOnly, string? Answer,
        DateTime? AnsweredAt, DateTime SetAt, DateTime? StopAt);

    private long _outageRev;

    private string OutageSet(JToken? p)
    {
        OutageSetParams? q;
        try { q = p?.ToObject<OutageSetParams>(); }
        catch (Exception ex) { return Serialize(new JObject { ["error"] = "bad_params", ["message"] = $"outage.set params: {ex.Message}" }); }
        if (q == null) return Serialize(new JObject { ["error"] = "missing_params", ["message"] = "outage.set needs {id, game, phase, reason, by, stopOnly} or {clear: true}." });
        bool toastDown = false, toastCancel = false;
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
            var samePhase = o.Phase == phase;
            o.Game = game;
            o.Phase = phase;
            o.Reason = q.Reason ?? o.Reason;
            o.By = q.By ?? o.By;
            o.ByLabel = q.ByLabel ?? o.ByLabel;
            o.StopOnly = q.StopOnly ?? o.StopOnly;
            o.StopAt = q.StopAt?.ToUniversalTime() ?? (samePhase ? o.StopAt : null);
            if (q.Busy != null) o.Busy = q.Busy;
            if (q.Ready != null) o.Ready = q.Ready;
            // The HUD stops without a countdown on the card: one toast carries the instruction.
            if (phase == "down" && !o.ToastedDown && !(o.StopAt > DateTime.UtcNow) && o.Answer != "notNow") { o.ToastedDown = toastDown = true; }
            // Cancelled from elsewhere (the requester): the card's own Not now / Cancel toasted already.
            if (phase == "cancelled" && !o.ToastedCancel && o.Answer != "notNow") { o.ToastedCancel = toastCancel = true; }
            OutagePublishLocked();
        }
        var state = OutageStateNow();
        if (toastDown)
        {
            var t = state.Busy.Count + state.Ready.Count;
            OutageToast("step", OutageCardText.ToastDownTitle,
                t > 0 ? string.Format(OutageCardText.ToastDownText, OutageCardText.AgentsWait(t)) : OutageCardText.ToastDownTextNone, OutageCardText.ToastDownHold);
        }
        if (toastCancel) OutageToast("result", OutageCardText.ToastCancelledTitle, OutageCardText.ToastCancelledText, OutageCardText.ToastCancelledHold);
        return JsonConvert.SerializeObject(state);
    }

    private void OutagePublishLocked()
    {
        var o = _outage;
        if (o == null) { _outageView = null; return; }
        o.Rev = ++_outageRev;
        _outageView = new OutageView(o.Id, o.Rev, o.Phase, o.Reason, o.By, o.ByLabel, o.StopOnly, o.Answer, o.AnsweredAt, o.SetAt, o.StopAt);
    }

    /// <summary>The card is on screen: phase announced / holding, not answered "notNow", an answered "now" only as a short
    /// receipt; phase down only while its countdown runs (and briefly past it, until the HUD stops).</summary>
    private static bool OutageCardShown(OutageCardState o) => OutageCardShown(o.Phase, o.Answer, o.AnsweredAt, o.StopAt);

    private static bool OutageCardShown(string phase, string? answer, DateTime? answeredAt, DateTime? stopAt)
    {
        if (answer == "notNow") return false;
        var now = DateTime.UtcNow;
        if (phase == "down") return stopAt is DateTime s && (now - s).TotalSeconds < OutageStopGraceSec;
        return phase is "announced" or "holding"
               && !(answer == "now" && answeredAt is DateTime at && (now - at).TotalSeconds > OutageReceiptSec);
    }

    /// <summary>For the panel: the card to draw this frame, or null.</summary>
    internal OutageView? OutageCardSnapshot()
    {
        var v = _outageView;
        if (v == null) return null;
        return OutageCardShown(v.Phase, v.Answer, v.AnsweredAt, v.StopAt) ? v : null;   // lock-free: the view is immutable
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
                Id = o?.Id, Game = o?.Game ?? GameId, Phase = o?.Phase, Reason = o?.Reason, By = o?.By, ByLabel = o?.ByLabel, StopOnly = o?.StopOnly ?? false,
                StopAt = o?.StopAt, Answer = o?.Answer, AnsweredAt = o?.AnsweredAt, CardShown = o != null && OutageCardShown(o),
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
                    busy.Add(new OutageParty { Session = s.Id, Label = s.Label, Doing = doing, Lease = lease?.Id, Until = lease?.Until });
                }
                else ready.Add(new OutageParty { Session = s.Id, Label = s.Label });
            }
        }
        return (busy, ready);
    }

    /// <summary>
    /// The card's buttons (render thread): record the answer, publish outage.answer from a pool thread, toast. A second
    /// answer is refused, except Cancel (notNow) while the stopping countdown runs. <paramref name="busy"/>: busy agents the
    /// card listed (Restart now over them toasts that they are told).
    /// </summary>
    private void OutageAnswer(string id, string answer, int busy)
    {
        DateTime at;
        lock (_outLock)
        {
            var o = _outage;
            if (o == null || o.Id != id) return;
            var cancelInCountdown = answer == "notNow" && o.Phase == "down" && o.StopAt > DateTime.UtcNow;
            if (o.Answer != null && !cancelInCountdown) return;
            o.Answer = answer;
            o.AnsweredAt = at = DateTime.UtcNow;
            if (answer == "notNow") o.ToastedCancel = true;
            OutagePublishLocked();
        }
        var ev = new OutageAnswerEvent { Id = id, Answer = answer, At = at };
        ThreadPool.QueueUserWorkItem(_ => HudStatePublish("outage.answer", ev));
        if (answer == "notNow")
            OutageToast("result", OutageCardText.ToastCancelledTitle, OutageCardText.ToastCancelledText, OutageCardText.ToastCancelledHold);
        else if (busy > 0)
            OutageToast("step", OutageCardText.ToastNowTitle, string.Format(OutageCardText.ToastNowText, OutageCardText.Agents(busy)), OutageCardText.ToastNowHold);
    }

    /// <summary>One outage toast through the guide log (the HUD's own line: no session, never an agent's).</summary>
    private void OutageToast(string kind, string title, string text, double holdSec, string? tag = null)
    {
        try { GuideLog(new JObject { ["text"] = text, ["kind"] = kind, ["title"] = title, ["holdSec"] = holdSec }, tag); }
        catch (Exception ex) { LogError($"[Outage] toast '{title}': {ex.GetType().Name}: {ex.Message}"); }
    }

    // ── The incident board ───────────────────────────────────────────

    private static string IncidentBoardPath =>
        Path.Combine(Environment.GetEnvironmentVariable("HEXILE_INCIDENT_DIR") is { Length: > 0 } dir
            ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexile"), "incidents.json");

    /// <summary>An open outage incident for this game on the board: its id and outage phase.</summary>
    private bool OutageOpenOnBoard(out string? id, out string? phase)
    {
        var inc = OutageIncidentOnBoard();
        id = inc?.Id; phase = inc?.Outage?.Phase;
        return inc != null;
    }

    /// <summary>The open outage incident for this game on the board, or null (none, no board, or a read that failed: named in
    /// hud-state's error).</summary>
    private IncidentDto? OutageIncidentOnBoard()
    {
        try
        {
            var path = IncidentBoardPath;
            if (!File.Exists(path)) return null;
            var board = JsonConvert.DeserializeObject<IncidentBoardDto>(HsReadShared(path));
            return board?.Incidents?.LastOrDefault(i => i.Kind == "outage" && i.State != "resolved"
                                                       && (i.Scope == GameId || i.Outage?.Game == GameId || i.Key == $"outage:{GameId}"));
        }
        catch (Exception ex) { _hsReadError = $"reading the incident board {IncidentBoardPath}: {ex.GetType().Name}: {ex.Message}"; return null; }
    }

    /// <summary>Initialise: an open outage for this game in down | waitingGame becomes a restart lease held until in game,
    /// and the HUD IS UP / BACK toasts are armed for it.</summary>
    private void OutageAdoptFromBoard()
    {
        var inc = OutageIncidentOnBoard();
        var id = inc?.Id;
        var phase = inc?.Outage?.Phase;
        if (id == null || phase is not ("down" or "waitingGame")) return;
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
        _outageResumeId = id;
        // The stop time is known only while the board still says down (phaseAt = the HUD stop); after waitingGame, BACK
        // says no elapsed time rather than a wrong one.
        _outageResumeDownAt = phase == "down" ? inc!.Outage!.PhaseAt?.ToUniversalTime() : null;
        _outageResumeAgents = inc!.Outage!.Participants?.Values.Count(x => x != null && x.State is "busy" or "ready" or "idle") ?? 0;
        _outageResumeUpPosted = false;
        LogMessage($"[Outage] {id} ({phase}): holding agents' HUD restarts until the player is in game");
    }

    /// <summary>From the hud-state tick: renew the adopted lease while not in game, release it once ready; the resume toasts.</summary>
    private void OutageLeaseTick(string phase)
    {
        OutageResumeTick(phase);
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

    /// <summary>
    /// The outage this bridge came back for: HUD IS UP once at menu / loading (tagged, so a toast the overlay never got to
    /// show over the login screens cannot pop stale in game), then on the first in-game tick that one retired and BACK.
    /// A HUD-only restart (game still in) goes straight to BACK.
    /// </summary>
    private void OutageResumeTick(string phase)
    {
        if (_outageResumeId == null) return;
        var t = _outageResumeAgents;
        if (phase is "menu" or "loading")
        {
            if (_outageResumeUpPosted) return;
            _outageResumeUpPosted = true;
            OutageToast("step", OutageCardText.ToastUpTitle,
                t > 0 ? string.Format(OutageCardText.ToastUpText, OutageCardText.AgentsResume(t)) : OutageCardText.ToastUpTextNone,
                OutageCardText.ToastUpHold, OutageCardText.ToastUpTag);
        }
        else if (phase == "ready")
        {
            _outageResumeId = null;
            GuideToastRetire(OutageCardText.ToastUpTag);
            string text;
            if (_outageResumeDownAt is DateTime down)
            {
                var elapsed = GuideElapsed(DateTime.UtcNow - down);
                text = t > 0 ? string.Format(OutageCardText.ToastBackText, OutageCardText.AgentsResume(t), elapsed)
                             : string.Format(OutageCardText.ToastBackTextNone, elapsed);
            }
            else text = t > 0 ? string.Format(OutageCardText.ToastBackTextNoTime, OutageCardText.AgentsResume(t)) : OutageCardText.ToastBackTextNoneNoTime;
            OutageToast("result", OutageCardText.ToastBackTitle, text, OutageCardText.ToastBackHold);
        }
    }
}
