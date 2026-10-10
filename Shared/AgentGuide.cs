using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// The in-game agent guide: what the agent wants the user to do right now (a sticky instruction with step and status)
/// and a short log of what the agent is doing - so the user never has to watch the chat to know what to do in game.
///   guide.set   {title?, instruction?, step?, steps?, status?, detail?, clear?}
///               status: waiting (do it now) | detected (change seen) | settling | captured | failed | info | done
///   guide.log   {text, kind?: agent|step|result|warn|error, title?}
///               title: the toast's caps title (up to 40 chars, e.g. "Low life"); without one the kind is the title
///   guide.state {}
///   guide.user  {stepId}  the user's answer to a step that offered Done: {current, mark: null|done|cancelled, markAgoMs,
///               unseen, status}. current false: the card shows another step now (replaced, cleared, expired).
/// A step that reads the answer passes stepId and offerDone on guide.set (await_change and the queue runner do): the
/// card then has a Done button beside its x, and says "I can't see that change yet" once the status stayed waiting for
/// unseenAfterSec with no Done. Done is a mark the agent reads (guide.user) and checks against what it watches;
/// "Not done yet" takes it back; the x on such a card marks it cancelled. A step's card never outlives its asker:
/// guide.set {clear, ifStep} clears only that step, expiresSec clears it when the agent goes quiet, and a closed
/// connection clears the step its session left waiting (GuideOwnerGone, from Sessions.cs).
/// The state is in memory only (not saved); the panel is drawn by GuidePanel.cs. Written from the TCP thread, read in
/// Render, so access goes through the lock.
/// </summary>
public partial class WhatsAnAiBridge
{
    internal sealed class GuideLogEntry
    {
        public DateTime At;
        public string Kind = "agent";
        public string Text = "";
        public string? Title;   // the toast's title line; null = the kind as a word
    }

    internal sealed class GuideState
    {
        public string? Title;
        public string? Instruction;
        public int? Step;
        public int? Steps;
        public string Status = "idle";
        public string? Detail;
        public DateTime UpdatedAt = DateTime.MinValue;
        public DateTime? InstructionSince;
        public string? Who;     // the session that set the instruction or status (Sessions.cs); shown when several agents are active
        public string? Session; // that session's id: its waiting step is cleared when it disconnects (GuideOwnerGone)
        // A step that offers Done (stepId + offerDone on guide.set): the user's mark and the "can't see it yet" clock.
        public string? StepId;
        public bool OfferDone;
        public string? Mark;            // null | done | cancelled
        public DateTime? MarkAt;
        public DateTime? WaitingSince;  // the status last became waiting (or Not done yet restarted the clock)
        public double UnseenAfterSec = GuideUnseenDefaultSec;
        public DateTime? ExpiresAt;     // a waiting step whose agent went quiet is cleared then (GuideExpireTick)
        public int Rev;
        public readonly List<GuideLogEntry> Log = new();
    }

    private readonly object _guideLock = new();
    private readonly GuideState _guide = new();
    private const int GuideLogMax = 40;
    private const double GuideUnseenDefaultSec = 20;

    /// <summary>
    /// A step that offers Done as the card draws it this frame: whether the Done button shows, the user's mark (done:
    /// the agent is checking; cancelled), how long ago it was made, whether the "I can't see that change yet" line is
    /// due (waiting, no mark, unseenAfterSec passed), and how long the waiting phase has lasted.
    /// </summary>
    internal readonly record struct GuideAskView(string? StepId, bool OfferDone, string? Mark, double MarkAgoSec, bool Unseen, double WaitingSec);

    /// <summary>Statuses in which the user is acting on the card (a restart blocker; cleared with their asker).</summary>
    private static bool GuideActive(string status) => status is "waiting" or "detected" or "settling";

    // unseen: the user said done, but the agent saw none of what it watches change (it will look elsewhere).
    private static readonly HashSet<string> GuideStatuses = new(StringComparer.Ordinal)
        { "idle", "waiting", "detected", "settling", "captured", "failed", "unseen", "info", "done" };

    private string? ProcessGuideMethod(string method, JToken? p) => method switch
    {
        "guide.set" => GuideSet(p).ToString(Newtonsoft.Json.Formatting.None),
        "guide.log" => GuideLog(p).ToString(Newtonsoft.Json.Formatting.None),
        "guide.state" => GuideStateJson().ToString(Newtonsoft.Json.Formatting.None),
        "guide.user" => GuideUser(p?["stepId"]?.ToString()).ToString(Newtonsoft.Json.Formatting.None),
        _ => null,
    };

    private JObject GuideSet(JToken? p)
    {
        // Resolved before the lock: CurrentSession takes the sessions lock, which BlockersLocked (Sessions.cs) holds while it
        // reads the guide state under this lock. Never nest them the other way round.
        var me = p?["instruction"] != null || p?["status"] != null ? CurrentSession() : null;
        lock (_guideLock)
        {
            var now = DateTime.UtcNow;
            // ifStep: change the card only while it still shows that step, so an agent cleaning up after itself never
            // wipes the step another agent (or the queue) put up since.
            if (p?["ifStep"]?.ToString() is { } ifStep && ifStep != _guide.StepId)
                return new JObject
                {
                    ["ok"] = true, ["rev"] = _guide.Rev, ["skipped"] = true, ["stepId"] = _guide.StepId,
                    ["note"] = _guide.StepId == null ? "The card shows no step now; nothing changed." : $"The card shows another step now (by {_guide.Who ?? "unknown"}); nothing changed.",
                };
            var prevStatus = _guide.Status;
            if (p?["clear"]?.Value<bool>() == true)
            {
                _guide.Title = _guide.Instruction = _guide.Detail = null;
                _guide.Step = _guide.Steps = null;
                _guide.Status = "idle";
                _guide.InstructionSince = null;
                _guide.Who = _guide.Session = null;
                GuideStepResetLocked(null);
            }
            if (me != null) { _guide.Who = me.Label; _guide.Session = me.Id; }
            // The HUD's own steps (the queue runner, from Render: no session) name who queued them, and belong to no
            // connection, so no disconnect clears them.
            else if (p?["instruction"] != null && _reqClientId == 0) { _guide.Who = Clip(p["who"]?.ToString(), 40); _guide.Session = null; }
            if (p?["title"] is { } t) _guide.Title = Clip(t.Type == JTokenType.Null ? null : t.ToString(), 80);
            if (p?["instruction"] is { } i)
            {
                var text = Clip(i.Type == JTokenType.Null ? null : i.ToString(), 200);
                if (text != _guide.Instruction) _guide.InstructionSince = text == null ? null : now;
                _guide.Instruction = text;
                // A new instruction without a step id is not a step that offers Done (nobody would read the button).
                if (p["stepId"] == null) GuideStepResetLocked(null);
            }
            if (p?["stepId"] is { } sid)
            {
                var id = Clip(sid.Type == JTokenType.Null ? null : sid.ToString(), 64);
                if (id != _guide.StepId) GuideStepResetLocked(id);
            }
            if (p?["offerDone"] is { } od) _guide.OfferDone = od.Type == JTokenType.Boolean && od.Value<bool>() && _guide.StepId != null;
            if (p?["unseenAfterSec"] is { Type: JTokenType.Integer or JTokenType.Float } ua) _guide.UnseenAfterSec = Math.Clamp(ua.Value<double>(), 3, 600);
            if (p?["expiresSec"] is { } ex)
                _guide.ExpiresAt = ex.Type is JTokenType.Integer or JTokenType.Float ? now.AddSeconds(Math.Clamp(ex.Value<double>(), 5, 3600)) : null;
            if (p?["step"] is { } s) _guide.Step = s.Type == JTokenType.Integer ? s.Value<int>() : null;
            if (p?["steps"] is { } n) _guide.Steps = n.Type == JTokenType.Integer ? n.Value<int>() : null;
            if (p?["detail"] is { } d) _guide.Detail = Clip(d.Type == JTokenType.Null ? null : d.ToString(), 300);
            if (p?["status"]?.ToString() is { } st)
            {
                if (!GuideStatuses.Contains(st)) return new JObject { ["error"] = "bad_status", ["message"] = $"status must be one of {string.Join(", ", GuideStatuses)}" };
                _guide.Status = st;
                // A step that is over offers nothing more: the button goes, and a late Done can't land on a finished step.
                if (!GuideActive(st)) { _guide.OfferDone = false; _guide.ExpiresAt = null; }
            }
            if (_guide.Status != "waiting") _guide.WaitingSince = null;
            else if (prevStatus != "waiting" || _guide.WaitingSince == null || p?["instruction"] != null) _guide.WaitingSince = now;
            _guide.UpdatedAt = now;
            _guide.Rev++;
            return GuideStateJsonLocked();
        }
    }

    /// <summary>A new step on the card (or none): its id, no Done offered yet, no mark, no expiry.</summary>
    private void GuideStepResetLocked(string? stepId)
    {
        _guide.StepId = stepId;
        _guide.OfferDone = false;
        _guide.Mark = null;
        _guide.MarkAt = null;
        _guide.UnseenAfterSec = GuideUnseenDefaultSec;
        _guide.ExpiresAt = null;
    }

    /// <summary>guide.user: the user's answer to a step that offered Done (the agent reads it while it waits).</summary>
    private JObject GuideUser(string? stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId)) return new JObject { ["error"] = "missing_step", ["message"] = "Pass stepId: the id you gave guide.set." };
        lock (_guideLock)
        {
            var o = new JObject { ["ok"] = true, ["stepId"] = stepId, ["current"] = _guide.StepId == stepId, ["rev"] = _guide.Rev };
            if (_guide.StepId != stepId)
            {
                o["note"] = _guide.StepId == null ? "The card shows no step now (cleared or expired)." : $"The card shows another step now (by {_guide.Who ?? "unknown"}).";
                return o;
            }
            var a = GuideAskLocked(DateTime.UtcNow);
            o["mark"] = a.Mark;
            if (a.Mark != null) o["markAgoMs"] = (long)(a.MarkAgoSec * 1000);
            o["offerDone"] = a.OfferDone;
            o["unseen"] = a.Unseen;
            o["status"] = _guide.Status;
            return o;
        }
    }

    private GuideAskView GuideAskLocked(DateTime now)
    {
        var waiting = _guide.WaitingSince is DateTime ws ? (now - ws).TotalSeconds : 0;
        var markAgo = _guide.MarkAt is DateTime ma ? (now - ma).TotalSeconds : 0;
        var unseen = _guide.OfferDone && _guide.Status == "waiting" && _guide.Mark == null && _guide.WaitingSince != null && waiting >= _guide.UnseenAfterSec;
        return new GuideAskView(_guide.StepId, _guide.OfferDone, _guide.Mark, markAgo, unseen, waiting);
    }

    /// <summary>The Done side of the card as the panel draws it (taken once per frame).</summary>
    internal GuideAskView GuideAskSnapshot() { lock (_guideLock) return GuideAskLocked(DateTime.UtcNow); }

    /// <summary>The card's Done button: the user says they did it. The agent reads the mark and checks what it watches.</summary>
    internal void GuideMarkDone()
    {
        lock (_guideLock)
        {
            if (!_guide.OfferDone || _guide.Mark != null || !GuideActive(_guide.Status)) return;
            _guide.Mark = "done";
            _guide.MarkAt = DateTime.UtcNow;
            _guide.UpdatedAt = DateTime.UtcNow;
            _guide.Rev++;
        }
    }

    /// <summary>"Not done yet": take the Done back; the step keeps waiting and the "can't see it" clock starts over.</summary>
    internal void GuideUnmarkDone()
    {
        lock (_guideLock)
        {
            if (_guide.Mark != "done" || !_guide.OfferDone || !GuideActive(_guide.Status)) return;
            _guide.Mark = null;
            _guide.MarkAt = null;
            if (_guide.Status == "waiting") _guide.WaitingSince = DateTime.UtcNow;
            _guide.UpdatedAt = DateTime.UtcNow;
            _guide.Rev++;
        }
    }

    /// <summary>
    /// A step whose agent went quiet (expiresSec passed while the user was still asked to act) is cleared, so the card
    /// and the "waiting for the user" blocker don't outlive it. From SessionsTick (Render, 4 Hz).
    /// </summary>
    private void GuideExpireTick(DateTime now)
    {
        string? who, what;
        lock (_guideLock)
        {
            if (_guide.ExpiresAt is not DateTime until || now < until || !GuideActive(_guide.Status)) return;
            who = _guide.Who; what = _guide.Instruction;
            GuideClearStepLocked();
        }
        GuideLog(new JObject { ["text"] = $"{GuideWhoText(who)} stopped waiting for: {what ?? "the step"}", ["kind"] = "warn", ["title"] = "Step ended" });
    }

    /// <summary>
    /// The session behind the card disconnected (Sessions.cs, called outside the sessions lock): a step it left asking
    /// the user to act is cleared. A finished card (captured, failed...) stays as a receipt.
    /// </summary>
    private void GuideOwnerGone(string sessionId)
    {
        string? who, what;
        lock (_guideLock)
        {
            if (_guide.Session != sessionId || !GuideActive(_guide.Status)) return;
            who = _guide.Who; what = _guide.Instruction;
            GuideClearStepLocked();
        }
        GuideLog(new JObject { ["text"] = $"{GuideWhoText(who)} left - no need for: {what ?? "the step"}", ["kind"] = "warn", ["title"] = "Step ended" });
    }

    private void GuideClearStepLocked()
    {
        _guide.Instruction = _guide.Detail = null;
        _guide.Step = _guide.Steps = null;
        _guide.Status = "idle";
        _guide.InstructionSince = null;
        _guide.WaitingSince = null;
        // The step id and its mark stay: an agent that comes back and asks guide.user learns its card is gone.
        _guide.OfferDone = false;
        _guide.ExpiresAt = null;
        _guide.UpdatedAt = DateTime.UtcNow;
        _guide.Rev++;
    }

    private string GuideWhoText(string? label) => label == null ? "The agent" : SessionDisplayName(label);

    private JObject GuideLog(JToken? p)
    {
        var text = Clip(p?["text"]?.ToString(), 200);
        if (string.IsNullOrWhiteSpace(text)) return new JObject { ["error"] = "missing_text" };
        var kind = p?["kind"]?.ToString() is "step" or "result" or "warn" or "error" ? p["kind"]!.ToString() : "agent";
        var title = Clip(p?["title"]?.ToString(), 40);
        lock (_guideLock)
        {
            _guide.Log.Add(new GuideLogEntry { At = DateTime.UtcNow, Kind = kind, Text = text!, Title = string.IsNullOrWhiteSpace(title) ? null : title });
            if (_guide.Log.Count > GuideLogMax) _guide.Log.RemoveRange(0, _guide.Log.Count - GuideLogMax);
            // No UpdatedAt here: the quiet clock counts what the user saw (a toast that showed, GuideToastFeed.cs), not every
            // line; agent lines for each tool call are off by default and would keep the quiet card awake forever.
            _guide.Rev++;
            return new JObject { ["ok"] = true, ["rev"] = _guide.Rev };
        }
    }

    private JObject GuideStateJson() { lock (_guideLock) return GuideStateJsonLocked(); }

    private JObject GuideStateJsonLocked() => new()
    {
        ["ok"] = true, ["rev"] = _guide.Rev, ["title"] = _guide.Title, ["instruction"] = _guide.Instruction,
        ["step"] = _guide.Step, ["steps"] = _guide.Steps, ["status"] = _guide.Status, ["detail"] = _guide.Detail, ["who"] = _guide.Who,
        ["stepId"] = _guide.StepId, ["offerDone"] = _guide.OfferDone, ["mark"] = _guide.Mark, ["unseen"] = GuideAskLocked(DateTime.UtcNow).Unseen,
        ["log"] = new JArray(_guide.Log.TakeLast(10).Select(e => new JObject { ["at"] = e.At.ToString("HH:mm:ss"), ["kind"] = e.Kind, ["text"] = e.Text, ["title"] = e.Title })),
        // What the panel's attention decided last frame (GuideToastFeed.cs): combat hold, held toasts, quiet dot.
        ["attention"] = AttentionJson(),
    };

    /// <summary>A consistent copy for drawing (taken once per frame).</summary>
    internal (string? title, string? instruction, int? step, int? steps, string status, string? detail, DateTime updatedAt, DateTime? instructionSince, int rev, GuideLogEntry[] log, string? who) GuideSnapshot()
    {
        lock (_guideLock)
            return (_guide.Title, _guide.Instruction, _guide.Step, _guide.Steps, _guide.Status, _guide.Detail, _guide.UpdatedAt,
                _guide.InstructionSince, _guide.Rev, _guide.Log.ToArray(), _guide.Who);
    }

    /// <summary>
    /// Clear the sticky instruction from the panel itself (the user's x). On a step that offers Done it is the user's
    /// Cancel: the mark tells the waiting agent, which stops instead of waiting out its timeout.
    /// </summary>
    internal void GuideDismiss()
    {
        lock (_guideLock)
        {
            if (_guide.OfferDone && GuideActive(_guide.Status) && _guide.StepId != null) { _guide.Mark = "cancelled"; _guide.MarkAt = DateTime.UtcNow; }
            _guide.Instruction = null;
            _guide.Status = "idle";
            _guide.InstructionSince = null;
            _guide.WaitingSince = null;
            _guide.OfferDone = false;
            _guide.ExpiresAt = null;
            _guide.Rev++;
        }
    }

    /// <summary>The user's mark on the queue runner's own step (ExperimentQueue.cs), read without the JSON round trip.</summary>
    internal string? GuideMarkFor(string stepId) { lock (_guideLock) return _guide.StepId == stepId ? _guide.Mark : null; }

    // ASCII tail: the HUD font has no ellipsis glyph.
    private static string? Clip(string? s, int max) => s == null ? null : s.Length > max ? s[..(max - 2)] + ".." : s;
}
