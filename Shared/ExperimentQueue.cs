using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Queued guided steps: an agent leaves an instruction ("tick Public on a tab") and what to watch; the USER starts it
/// from the in-game guide card when ready - the agent need not be connected, and nothing is captured before Start. The
/// bridge then records like await_change does (baseline, wait for a lasting change, settle, after) for each repeat, and
/// keeps the raw captures until an agent collects them. The queue is saved to &lt;BridgeDirectory&gt;\experiments\queue.json,
/// so it survives HUD restarts and developers can see later what was asked and what was recorded.
///   experiment.queue   {experiment, label, instruction, watch[], repeats?, settleMs?, timeoutMs?, title?, note?, by?}
///   experiment.queued  {all?}               the queue (no captures), newest last
///   experiment.result  {id}                 one step with its captures (raw responses per watch spec)
///   experiment.start   {id}  / experiment.cancel {id} / experiment.collected {id}
/// Watch specs as in the MCP: value:&lt;path&gt; | memory:&lt;path&gt;[:size] | collection:&lt;path&gt;[:Label1,Label2].
/// Captures run on the main thread from Render, every ~120 ms, only while a step runs.
/// </summary>
public partial class WhatsAnAiBridge
{
    internal sealed class QueuedCapture
    {
        public int Repeat;
        public DateTime At;
        public long ChangedAfterMs;
        public int Transients;
        public Dictionary<string, JToken> Before = new();
        public Dictionary<string, JToken> After = new();
    }

    internal sealed class QueuedStep
    {
        public string Id = "";
        public string Experiment = "";
        public string Label = "";
        public string Instruction = "";
        public string? Title;
        public string? Note;
        public string? By;
        public string[] Watch = [];
        public int Repeats = 1;
        public int SettleMs = 500;
        public int TimeoutMs = 120_000;
        public string Status = "queued";   // queued | running | captured | failed | cancelled
        public DateTime QueuedAt;
        public DateTime? StartedAt;
        public DateTime? FinishedAt;
        public string? StartedFrom;        // hud | agent
        public bool Collected;
        public bool Chain;                 // starts by itself when the previous step of the same experiment is captured
        public JArray? Highlight;          // targets shown (guide.highlight) while the step records
        public JObject? Flow;              // guided flow (guide.flow) run from Start until the step ends
        public string? Error;
        public bool UserMarkedDone;        // the user pressed Done on a repeat in which nothing watched changed
        public List<QueuedCapture> Captures = new();
    }

    /// <summary>What Render needs between frames for the running step (not saved).</summary>
    private sealed class QueueRun
    {
        public string Id = "";
        public int Repeat = 1;
        public string Phase = "waiting";   // waiting | detected
        public DateTime PhaseStart;
        public DateTime ChangedAt;
        public DateTime StableSince;
        public DateTime LastPoll = DateTime.MinValue;
        public int Transients;
        public Dictionary<string, JToken> Before = new();
        public Dictionary<string, JToken> Last = new();
        public string BeforeKey = "", LastKey = "";
        public DateTime? DoneAt;           // the user pressed Done and nothing has changed yet: the grace runs from here
        public string GuideStep => $"q:{Id}:{Repeat}";   // this repeat's step id on the card (guide.set stepId)
    }

    private readonly object _queueLock = new();
    private List<QueuedStep>? _queue;
    private QueueRun? _queueRun;
    private const int QueueKeep = 50;
    // After Done, a change still gets this long to show up (the click and the game's update race); then the repeat ends.
    private const int QueueDoneGraceMs = 2000;

    private string QueueFile => Path.Combine(_bridgeDir, "experiments", "queue.json");

    private List<QueuedStep> Queue()
    {
        if (_queue != null) return _queue;
        try { _queue = File.Exists(QueueFile) ? JsonConvert.DeserializeObject<List<QueuedStep>>(File.ReadAllText(QueueFile)) ?? new() : new(); }
        catch (Exception ex) { LogError($"[Queue] {QueueFile} unreadable, starting empty: {ex.Message}"); _queue = new(); }
        // A step that was running when the HUD closed can't resume its baseline: put it back in the queue.
        foreach (var s in _queue.Where(s => s.Status == "running")) { s.Status = "queued"; s.StartedAt = null; s.Captures.Clear(); }
        return _queue;
    }

    private void SaveQueue()
    {
        try
        {
            var q = Queue();
            // Keep every open step and the newest finished ones.
            var finished = q.Where(s => s.Status is not ("queued" or "running")).ToList();
            if (finished.Count > QueueKeep) foreach (var s in finished.Take(finished.Count - QueueKeep)) q.Remove(s);
            Directory.CreateDirectory(Path.GetDirectoryName(QueueFile)!);
            var tmp = QueueFile + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(q, Formatting.Indented));
            File.Move(tmp, QueueFile, true);
        }
        catch (Exception ex) { LogError($"[Queue] save failed: {ex.Message}"); }
    }

    private string? ProcessExperimentMethod(string method, JToken? p) => method switch
    {
        "experiment.queue" => SafeMemory(() => QueueAdd(p)),
        "experiment.queued" => SafeMemory(() => QueueList(p)),
        "experiment.result" => SafeMemory(() => QueueResult(p)),
        "experiment.start" => SafeMemory(() => QueueStart(p?["id"]?.ToString(), "agent")),
        "experiment.cancel" => SafeMemory(() => QueueCancel(p?["id"]?.ToString())),
        "experiment.collected" => SafeMemory(() => QueueCollected(p?["id"]?.ToString())),
        _ => null,
    };

    private JObject QueueAdd(JToken? p)
    {
        var experiment = p?["experiment"]?.ToString() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(experiment, @"^[\w.-]{1,64}$")) return Err("bad_experiment", "experiment: letters, digits, '-', '_' or '.', up to 64 characters.");
        var watch = (p?["watch"] as JArray)?.Select(w => w.ToString()).Where(w => w.Length > 0).ToArray() ?? [];
        if (watch.Length == 0) return Err("missing_watch", "Pass watch: at least one spec (value:|memory:|collection:<path>).");
        foreach (var w in watch) if (ParseWatch(w) == null) return Err("bad_watch", $"Watch spec '{w}': expected value:|memory:|collection: prefix.");
        var instruction = Clip(p?["instruction"]?.ToString(), 200);
        if (string.IsNullOrWhiteSpace(instruction)) return Err("missing_instruction", "Pass instruction: what the user should do, in game words.");
        var step = new QueuedStep
        {
            Id = Guid.NewGuid().ToString("N")[..10], Experiment = experiment, Label = Clip(p?["label"]?.ToString(), 40) ?? "step",
            Instruction = instruction!, Title = Clip(p?["title"]?.ToString(), 80), Note = Clip(p?["note"]?.ToString(), 300),
            By = Clip(p?["by"]?.ToString(), 40) ?? CurrentWho(), Watch = watch, Chain = p?["chain"]?.Value<bool>() == true, Highlight = p?["highlight"] as JArray, Flow = p?["flow"] as JObject,
            Repeats = Math.Clamp(p?["repeats"]?.Value<int>() ?? 1, 1, 10),
            SettleMs = Math.Clamp(p?["settleMs"]?.Value<int>() ?? 500, 100, 5000),
            TimeoutMs = Math.Clamp(p?["timeoutMs"]?.Value<int>() ?? 120_000, 5_000, 600_000),
            QueuedAt = DateTime.UtcNow,
        };
        lock (_queueLock)
        {
            Queue().Add(step);
            SaveQueue();
        }
        GuideLog(new JObject { ["text"] = $"Queued for you: {step.Instruction}", ["kind"] = "step" });
        TouchGuide();
        return new JObject { ["ok"] = true, ["id"] = step.Id, ["position"] = QueueSnapshot().Count(s => s.Status == "queued"), ["file"] = QueueFile };
    }

    private JObject QueueList(JToken? p)
    {
        var all = p?["all"]?.Value<bool>() == true;
        lock (_queueLock)
        {
            var items = Queue().Where(s => all || !s.Collected || s.Status is "queued" or "running").Select(StepSummary);
            return new JObject { ["ok"] = true, ["file"] = QueueFile, ["steps"] = new JArray(items), ["running"] = _queueRun?.Id };
        }
    }

    private static JObject StepSummary(QueuedStep s) => new()
    {
        ["id"] = s.Id, ["experiment"] = s.Experiment, ["label"] = s.Label, ["instruction"] = s.Instruction, ["title"] = s.Title,
        ["note"] = s.Note, ["by"] = s.By, ["chain"] = s.Chain, ["watch"] = new JArray(s.Watch), ["repeats"] = s.Repeats, ["status"] = s.Status,
        ["captured"] = s.Captures.Count, ["queuedAt"] = s.QueuedAt.ToString("O"), ["startedAt"] = s.StartedAt?.ToString("O"),
        ["finishedAt"] = s.FinishedAt?.ToString("O"), ["startedFrom"] = s.StartedFrom, ["collected"] = s.Collected, ["error"] = s.Error,
        ["userMarkedDone"] = s.UserMarkedDone ? true : null,
    };

    private JObject QueueResult(JToken? p)
    {
        var id = p?["id"]?.ToString();
        lock (_queueLock)
        {
            var s = Queue().FirstOrDefault(x => x.Id == id);
            if (s == null) return Err("unknown_id", "No queued step with that id (experiment.queued lists them).");
            var o = StepSummary(s);
            o["settleMs"] = s.SettleMs;
            o["timeoutMs"] = s.TimeoutMs;
            o["captures"] = JArray.FromObject(s.Captures);
            return o;
        }
    }

    internal JObject QueueStart(string? id, string from)
    {
        lock (_queueLock)
        {
            if (_queueRun != null) return Err("busy", "Another queued step is running; it must finish or be cancelled first.");
            var s = id == null ? Queue().FirstOrDefault(x => x.Status == "queued") : Queue().FirstOrDefault(x => x.Id == id);
            if (s == null) return Err("unknown_id", "No such queued step.");
            if (s.Status != "queued") return Err("not_queued", $"Step is {s.Status}.");
            s.Status = "running"; s.StartedAt = DateTime.UtcNow; s.StartedFrom = from; s.Captures.Clear(); s.Error = null; s.UserMarkedDone = false;
            _queueRun = new QueueRun { Id = s.Id, PhaseStart = DateTime.UtcNow };
            SaveQueue();
            return new JObject { ["ok"] = true, ["id"] = s.Id };
        }
    }

    internal JObject QueueCancel(string? id)
    {
        lock (_queueLock)
        {
            var s = Queue().FirstOrDefault(x => x.Id == id);
            if (s == null) return Err("unknown_id", "No such queued step.");
            if (s.Status is not ("queued" or "running")) return Err("finished", $"Step is already {s.Status}.");
            if (_queueRun?.Id == s.Id) _queueRun = null;
            s.Status = "cancelled"; s.FinishedAt = DateTime.UtcNow;
            SaveQueue();
        }
        GuideSet(new JObject { ["status"] = "info", ["detail"] = "Step skipped" });
        return new JObject { ["ok"] = true, ["id"] = id };
    }

    private JObject QueueCollected(string? id)
    {
        lock (_queueLock)
        {
            var s = Queue().FirstOrDefault(x => x.Id == id);
            if (s == null) return Err("unknown_id", "No such queued step.");
            s.Collected = true;
            SaveQueue();
            return new JObject { ["ok"] = true, ["id"] = id };
        }
    }

    /// <summary>A copy of the queue for drawing (open steps first, in order).</summary>
    internal List<QueuedStep> QueueSnapshot()
    {
        lock (_queueLock) return Queue().Where(s => s.Status is "queued" or "running").ToList();
    }

    /// <summary>Bump the guide's rev so the panel shows itself for queue changes.</summary>
    private void TouchGuide() { lock (_guideLock) { _guide.UpdatedAt = DateTime.UtcNow; _guide.Rev++; } }

    // ── Runner (main thread, from Render) ────────────────────────────

    private void RunQueuedStep()
    {
        QueueRun? run;
        QueuedStep? step;
        lock (_queueLock)
        {
            run = _queueRun;
            step = run == null ? null : Queue().FirstOrDefault(s => s.Id == run.Id);
            if (run != null && step == null) _queueRun = run = null;
        }
        if (run == null || step == null) return;
        var now = DateTime.UtcNow;
        if ((now - run.LastPoll).TotalMilliseconds < 120) return;
        run.LastPoll = now;
        try
        {
            if (run.BeforeKey == "")
            {
                if (step.Highlight != null) HighlightSetLayer(HlQueue, null, step.By, new JObject { ["targets"] = step.Highlight.DeepClone(), ["title"] = step.Title });
                if (step.Flow != null && run.Repeat == 1) FlowSet(step.Flow.DeepClone());
                run.Before = CaptureWatch(step.Watch);
                run.BeforeKey = run.LastKey = Fingerprint(run.Before);
                run.Last = run.Before;
                run.PhaseStart = now;
                GuideSet(new JObject
                {
                    ["title"] = step.Title ?? $"Experiment: {step.Experiment}", ["instruction"] = step.Instruction, ["status"] = "waiting",
                    ["step"] = step.Repeats > 1 ? run.Repeat : null, ["steps"] = step.Repeats > 1 ? step.Repeats : null,
                    ["detail"] = run.Repeat > 1 ? $"Again ({run.Repeat} of {step.Repeats}) - recording" : "Recording - do it now",
                    ["stepId"] = run.GuideStep, ["offerDone"] = true, ["who"] = step.By,
                });
                return;
            }
            var current = CaptureWatch(step.Watch);
            var key = Fingerprint(current);
            // The user's Done on the card (AgentGuide.cs): a change seen is captured now instead of after the settle; with
            // nothing changed, a change still gets QueueDoneGraceMs, then the repeat ends saying the watch saw nothing.
            // "Not done yet" clears the mark and the repeat goes on as before.
            var mark = GuideMarkFor(run.GuideStep);
            if (mark == "done") run.DoneAt ??= now;
            else run.DoneAt = null;
            if (run.Phase == "waiting")
            {
                if (key != run.BeforeKey)
                {
                    run.Phase = "detected"; run.ChangedAt = run.StableSince = now; run.Last = current; run.LastKey = key;
                    GuideSet(new JObject { ["status"] = "detected", ["detail"] = "Change seen - hold still" });
                }
                else if (run.DoneAt is DateTime doneAt && (now - doneAt).TotalMilliseconds > QueueDoneGraceMs) FinishQueuedUnseen(step, run);
                else if ((now - run.PhaseStart).TotalMilliseconds > step.TimeoutMs) FinishQueued(step, "failed", "No lasting change before the time limit");
                return;
            }
            if (key != run.LastKey) { run.StableSince = now; run.Last = current; run.LastKey = key; if (run.DoneAt == null) return; }
            if (run.DoneAt == null && (now - run.StableSince).TotalMilliseconds < step.SettleMs) return;
            if (run.LastKey == run.BeforeKey)
            {
                // Went back to the baseline (hover, animation): not the action.
                run.Transients++; run.Phase = "waiting";
                GuideSet(new JObject { ["status"] = "waiting", ["detail"] = "That changed back - still waiting for the action" });
                return;
            }
            lock (_queueLock)
            {
                step.Captures.Add(new QueuedCapture
                {
                    Repeat = run.Repeat, At = now, ChangedAfterMs = (long)(run.ChangedAt - run.PhaseStart).TotalMilliseconds,
                    Transients = run.Transients, Before = run.Before, After = run.Last,
                });
                SaveQueue();
            }
            var changed = CountChanged(run.Before, run.Last);
            GuideLog(new JObject { ["text"] = $"Recorded '{step.Label}' {run.Repeat}/{step.Repeats}: {changed} value(s) changed", ["kind"] = "result" });
            if (run.Repeat < step.Repeats)
            {
                // Next repeat starts from the state the last one left (a toggle goes back, a move continues).
                var next = run.Repeat + 1;
                run.Repeat = next; run.Phase = "waiting"; run.Transients = 0; run.DoneAt = null;
                run.Before = run.Last; run.BeforeKey = run.LastKey; run.PhaseStart = now;
                // A new step id per repeat: the last repeat's Done must not end this one.
                GuideSet(new JObject { ["status"] = "waiting", ["step"] = next, ["steps"] = step.Repeats, ["detail"] = $"Again ({next} of {step.Repeats}) - recording",
                    ["stepId"] = run.GuideStep, ["offerDone"] = true });
                return;
            }
            FinishQueued(step, "captured", $"Recorded {step.Repeats}x - Claude can read it any time");
        }
        catch (Exception ex)
        {
            step.Error = ex.Message;
            FinishQueued(step, "failed", "Recording error: " + Clip(ex.Message, 120));
        }
    }

    /// <summary>
    /// The user pressed Done but nothing watched changed within the grace: the step ends failed (repeats already
    /// recorded are kept for collection) and says, on the card and in its error, that the watch specs probably don't
    /// follow the action, naming them, so whoever queued it watches something else next time.
    /// </summary>
    private void FinishQueuedUnseen(QueuedStep step, QueueRun run)
    {
        lock (_queueLock)
        {
            step.UserMarkedDone = true;
            step.Error = $"The user pressed Done on repeat {run.Repeat}, but nothing watched changed: the watch spec is probably wrong " +
                         $"({string.Join("; ", step.Watch)})" + (step.Captures.Count > 0 ? $". {step.Captures.Count} earlier repeat(s) were recorded." : ".");
        }
        FinishQueued(step, "failed", $"You said done, but none of the {step.Watch.Length} watched value(s) changed - the agent will watch something else", "unseen");
    }

    private void FinishQueued(QueuedStep step, string status, string detail, string? guideStatus = null)
    {
        lock (_queueLock)
        {
            step.Status = status; step.FinishedAt = DateTime.UtcNow;
            _queueRun = null;
            SaveQueue();
        }
        if (step.Highlight != null) HighlightSetLayer(HlQueue, null, step.By, new JObject { ["clear"] = true });
        if (step.Flow != null) FlowSet(new JObject { ["stop"] = true });
        GuideSet(new JObject { ["status"] = guideStatus ?? (status == "captured" ? "captured" : "failed"), ["detail"] = detail });
        if (status != "captured") GuideLog(new JObject { ["text"] = $"'{step.Label}': {detail}", ["kind"] = "warn" });
        if (status != "captured") return;
        // A chained follow-up of the same experiment starts at once: the user pressed Start once for the whole series.
        QueuedStep? next;
        lock (_queueLock) next = Queue().FirstOrDefault(s => s.Status == "queued");
        if (next is { Chain: true } && next.Experiment == step.Experiment) QueueStart(next.Id, "chain");
    }

    /// <summary>One capture of every watch spec: the same responses the MCP gets for eval / memory.read / memory.collect.</summary>
    private Dictionary<string, JToken> CaptureWatch(string[] watch)
    {
        var d = new Dictionary<string, JToken>();
        foreach (var raw in watch)
        {
            var w = ParseWatch(raw)!.Value;
            JToken r = w.kind switch
            {
                "value" => JToken.Parse(new ExpressionWalker(GameController).Evaluate(w.path)),
                "memory" => MemoryRead(new JObject { ["path"] = w.path, ["size"] = w.size > 0 ? w.size : 256, ["classify"] = false }),
                _ => MemoryCollect(new JObject { ["path"] = w.path, ["size"] = w.size, ["labels"] = new JArray(w.labels), ["limit"] = 500 }),
            };
            d[raw] = r;
        }
        return d;
    }

    private static (string kind, string path, int size, string[] labels)? ParseWatch(string raw)
    {
        var i = raw.IndexOf(':');
        if (i < 1) return null;
        var kind = raw[..i].Trim().ToLowerInvariant();
        if (kind is not ("value" or "memory" or "collection")) return null;
        var rest = raw[(i + 1)..].Trim();
        int size = 0; string[] labels = [];
        var j = rest.LastIndexOf(':');
        if (j > 0 && kind == "memory" && int.TryParse(rest[(j + 1)..], out var n)) { size = Math.Clamp(n, 8, 1024); rest = rest[..j]; }
        else if (j > 0 && kind == "collection") { labels = rest[(j + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); rest = rest[..j]; }
        return (kind, rest, size, labels);
    }

    /// <summary>What counts as "changed": values, bytes and labels - not timings or truncation notes.</summary>
    private static string Fingerprint(Dictionary<string, JToken> capture) =>
        string.Join("\n", capture.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + Essence(kv.Value)));

    private static string Essence(JToken r)
    {
        if (r["error"] != null) return "error:" + r["error"];
        if (r["items"] is JArray items)
            return string.Join(";", items.Select(x => $"{x["address"]}:{x["data"]}:{x["labels"]?.ToString(Formatting.None)}"));
        if (r["data"] != null) return $"{r["address"]}:{r["data"]}";
        return r["value"]?.ToString(Formatting.None) ?? r.ToString(Formatting.None);
    }

    private static int CountChanged(Dictionary<string, JToken> a, Dictionary<string, JToken> b) =>
        a.Count(kv => b.TryGetValue(kv.Key, out var v) && Essence(kv.Value) != Essence(v));
}
