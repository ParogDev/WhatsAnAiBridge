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
        public int Rev;
        public readonly List<GuideLogEntry> Log = new();
    }

    private readonly object _guideLock = new();
    private readonly GuideState _guide = new();
    private const int GuideLogMax = 40;

    private static readonly HashSet<string> GuideStatuses = new(StringComparer.Ordinal)
        { "idle", "waiting", "detected", "settling", "captured", "failed", "info", "done" };

    private string? ProcessGuideMethod(string method, JToken? p) => method switch
    {
        "guide.set" => GuideSet(p).ToString(Newtonsoft.Json.Formatting.None),
        "guide.log" => GuideLog(p).ToString(Newtonsoft.Json.Formatting.None),
        "guide.state" => GuideStateJson().ToString(Newtonsoft.Json.Formatting.None),
        _ => null,
    };

    private JObject GuideSet(JToken? p)
    {
        lock (_guideLock)
        {
            if (p?["clear"]?.Value<bool>() == true)
            {
                _guide.Title = _guide.Instruction = _guide.Detail = null;
                _guide.Step = _guide.Steps = null;
                _guide.Status = "idle";
                _guide.InstructionSince = null;
            }
            if (p?["title"] is { } t) _guide.Title = Clip(t.Type == JTokenType.Null ? null : t.ToString(), 80);
            if (p?["instruction"] is { } i)
            {
                var text = Clip(i.Type == JTokenType.Null ? null : i.ToString(), 200);
                if (text != _guide.Instruction) _guide.InstructionSince = text == null ? null : DateTime.UtcNow;
                _guide.Instruction = text;
            }
            if (p?["step"] is { } s) _guide.Step = s.Type == JTokenType.Integer ? s.Value<int>() : null;
            if (p?["steps"] is { } n) _guide.Steps = n.Type == JTokenType.Integer ? n.Value<int>() : null;
            if (p?["detail"] is { } d) _guide.Detail = Clip(d.Type == JTokenType.Null ? null : d.ToString(), 300);
            if (p?["status"]?.ToString() is { } st)
            {
                if (!GuideStatuses.Contains(st)) return new JObject { ["error"] = "bad_status", ["message"] = $"status must be one of {string.Join(", ", GuideStatuses)}" };
                _guide.Status = st;
            }
            _guide.UpdatedAt = DateTime.UtcNow;
            _guide.Rev++;
            return GuideStateJsonLocked();
        }
    }

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
            _guide.UpdatedAt = DateTime.UtcNow;
            _guide.Rev++;
            return new JObject { ["ok"] = true, ["rev"] = _guide.Rev };
        }
    }

    private JObject GuideStateJson() { lock (_guideLock) return GuideStateJsonLocked(); }

    private JObject GuideStateJsonLocked() => new()
    {
        ["ok"] = true, ["rev"] = _guide.Rev, ["title"] = _guide.Title, ["instruction"] = _guide.Instruction,
        ["step"] = _guide.Step, ["steps"] = _guide.Steps, ["status"] = _guide.Status, ["detail"] = _guide.Detail,
        ["log"] = new JArray(_guide.Log.TakeLast(10).Select(e => new JObject { ["at"] = e.At.ToString("HH:mm:ss"), ["kind"] = e.Kind, ["text"] = e.Text, ["title"] = e.Title })),
    };

    /// <summary>A consistent copy for drawing (taken once per frame).</summary>
    internal (string? title, string? instruction, int? step, int? steps, string status, string? detail, DateTime updatedAt, DateTime? instructionSince, int rev, GuideLogEntry[] log) GuideSnapshot()
    {
        lock (_guideLock)
            return (_guide.Title, _guide.Instruction, _guide.Step, _guide.Steps, _guide.Status, _guide.Detail, _guide.UpdatedAt,
                _guide.InstructionSince, _guide.Rev, _guide.Log.ToArray());
    }

    /// <summary>Clear the sticky instruction from the panel itself (the user's "dismiss").</summary>
    internal void GuideDismiss()
    {
        lock (_guideLock)
        {
            _guide.Instruction = null;
            _guide.Status = "idle";
            _guide.InstructionSince = null;
            _guide.Rev++;
        }
    }

    // ASCII tail: the HUD font has no ellipsis glyph.
    private static string? Clip(string? s, int max) => s == null ? null : s.Length > max ? s[..(max - 2)] + ".." : s;
}
