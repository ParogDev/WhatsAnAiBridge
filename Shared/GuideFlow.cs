using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Guided flows: the user does a multi-step task in game (e.g. "remove the Ritual affinity from DUMP") and the HUD
/// shows the right next step from the game's state, not from a fixed sequence:
///   - goal: when it holds, the flow is done (whatever order the user took).
///   - steps: each has a 'done' condition; the current step is the first one not done, re-evaluated every 100 ms, so
///     navigating away (closing a dialog) moves back to the step that brings it back.
///   - options per step, in priority: the first whose 'when' holds (default: its target is on screen) is highlighted,
///     e.g. right-click the tab if it's in the tab row, else click it in the tab list, else open the tab list.
/// The bridge owns the state (any client can read it); the MCP expands reusable recipes into flows.
///   guide.flow {title, goal?, steps:[{label, done?, options:[{target, label?, when?}]}], timeoutSec?} | {stop:true}
///   guide.flow_state {}
/// Conditions: {all:[..]} {any:[..]} {not:c} {visible:target} {checked:target} {unchecked:target}
///   {text:target, equals:"..."} {eval:"walker expr", equals:value}
///   {memory:{collection:"walker path", where:{Prop:value}, offset, size:1|2|4|8, mask?, equals}}
/// </summary>
public partial class WhatsAnAiBridge
{
    private sealed class FlowOption { public HighlightTarget Target = new(); public string? Label; public JToken? When; }
    private sealed class FlowStep { public string Label = ""; public JToken? Done; public List<FlowOption> Options = new(); }

    private sealed class FlowState
    {
        public string? Title;
        public JToken? Goal;
        public List<FlowStep> Steps = new();
        public int Current = -1;                 // index of the current step; -1 when none
        public int Option = -1;                  // index of the option shown for it; -1 = none on screen
        public bool[] DoneNow = [];
        public string Status = "idle";           // idle | running | done | stopped | timeout
        public DateTime StartedAt;
        public DateTime? EndsAt;
        public int Rev;
    }

    private readonly object _flowLock = new();
    private FlowState _flow = new();
    private DateTime _flowLastTick = DateTime.MinValue;

    private string? ProcessFlowMethod(string method, JToken? p) => method switch
    {
        "guide.flow" => SafeMemory(() => FlowSet(p)),
        "guide.flow_state" => SafeMemory(FlowStateJson),
        _ => null,
    };

    internal JObject FlowSet(JToken? p)
    {
        if (p?["stop"]?.Value<bool>() == true || p?["steps"] is not JArray steps)
        {
            lock (_flowLock) { if (_flow.Status == "running") _flow.Status = "stopped"; _flow.Rev++; }
            HighlightSet(new JObject { ["clear"] = true });
            return FlowStateJson();
        }
        var flow = new FlowState { Title = Clip(p["title"]?.ToString(), 80), Goal = p["goal"], Status = "running", StartedAt = DateTime.UtcNow };
        var timeout = p["timeoutSec"]?.Value<double>();
        if (timeout is > 0) flow.EndsAt = DateTime.UtcNow.AddSeconds(Math.Min(timeout.Value, 3600));
        foreach (var s in steps.OfType<JObject>().Take(20))
        {
            var step = new FlowStep { Label = Clip(s["label"]?.ToString(), 80) ?? "Step", Done = s["done"] };
            foreach (var o in (s["options"] as JArray ?? []).OfType<JObject>().Take(8))
            {
                if (o["target"] is not JObject tj || ParseTarget(tj) is not { } target)
                    return Err("bad_option", $"Step '{step.Label}': each option needs a target (item | text | path | panel | rect).");
                step.Options.Add(new FlowOption { Target = target, Label = Clip(o["label"]?.ToString(), 60), When = o["when"] });
            }
            flow.Steps.Add(step);
        }
        if (flow.Steps.Count == 0) return Err("no_steps", "Pass steps: [{label, done, options:[{target, label?, when?}]}].");
        flow.DoneNow = new bool[flow.Steps.Count];
        lock (_flowLock) { flow.Rev = _flow.Rev + 1; _flow = flow; }
        _flowLastTick = DateTime.MinValue;
        GuideLog(new JObject { ["text"] = $"Guided: {flow.Title ?? flow.Steps[0].Label}", ["kind"] = "step" });
        return FlowStateJson();
    }

    private JObject FlowStateJson()
    {
        lock (_flowLock)
        {
            var f = _flow;
            return new JObject
            {
                ["ok"] = true, ["status"] = f.Status, ["title"] = f.Title, ["rev"] = f.Rev,
                ["current"] = f.Current >= 0 ? f.Current + 1 : null,
                ["steps"] = new JArray(f.Steps.Select((s, i) => new JObject
                {
                    ["n"] = i + 1, ["label"] = s.Label,
                    ["state"] = f.Status == "done" || (i < f.DoneNow.Length && f.DoneNow[i]) ? "done" : i == f.Current ? "current" : "todo",
                    ["showing"] = i == f.Current && f.Option >= 0 ? s.Options[f.Option].Label ?? s.Label : null,
                })),
                ["startedAt"] = f.StartedAt == default ? null : f.StartedAt.ToString("O"),
                ["plan"] = new JArray(PlanLocked(f)),
            };
        }
    }

    /// <summary>For the guide card: the checklist (label, state) and the current option's instruction.</summary>
    internal (string? title, (string label, string state)[] steps, string? showing, string status, int rev) FlowSnapshot()
    {
        lock (_flowLock)
        {
            var f = _flow;
            var steps = f.Steps.Select((s, i) => (s.Label, f.Status == "done" || (i < f.DoneNow.Length && f.DoneNow[i]) ? "done" : i == f.Current ? "current" : "todo")).ToArray();
            var showing = f.Current >= 0 && f.Option >= 0 ? f.Steps[f.Current].Options[f.Option].Label ?? f.Steps[f.Current].Label : null;
            return (f.Title, steps, showing, f.Status, f.Rev);
        }
    }

    /// <summary>
    /// The current best order of actions from where the user is now: options are ordered best-first, so the ones above
    /// the option shown are what it unlocks ("open the tab list" -> "click DUMP in the list" -> "right-click DUMP"),
    /// then the label of every later step not done yet. Recomputed with the flow, so it re-plans as the user acts.
    /// </summary>
    private static List<string> PlanLocked(FlowState f)
    {
        var plan = new List<string>();
        if (f.Status != "running" || f.Current < 0) return plan;
        var step = f.Steps[f.Current];
        if (f.Option >= 0) for (int k = f.Option; k >= 0; k--) plan.Add(step.Options[k].Label ?? step.Label);
        else plan.Add(step.Label);
        for (int i = f.Current + 1; i < f.Steps.Count; i++)
            if (!(i < f.DoneNow.Length && f.DoneNow[i])) plan.Add(f.Steps[i].Label);
        return plan;
    }

    /// <summary>For the guide card: the live plan (first entry = what to do now).</summary>
    internal string[] FlowPlan() { lock (_flowLock) return PlanLocked(_flow).ToArray(); }

    // ── Tick (main thread, from Render) ──────────────────────────────

    private void FlowTick()
    {
        FlowState f;
        lock (_flowLock) f = _flow;
        if (f.Status != "running") return;
        var now = DateTime.UtcNow;
        if ((now - _flowLastTick).TotalMilliseconds < 100) return;
        _flowLastTick = now;
        try
        {
            if (f.EndsAt is { } end && now > end) { FinishFlow(f, "timeout", "Guide timed out - ask Claude to restart it"); return; }
            if (f.Goal != null && Eval(f.Goal)) { FinishFlow(f, "done", "All done"); return; }

            var done = f.Steps.Select(s => s.Done != null && Eval(s.Done)).ToArray();
            var current = Array.FindIndex(done, d => !d);
            if (current < 0) { FinishFlow(f, "done", "All done"); return; }
            var step = f.Steps[current];
            var option = step.Options.FindIndex(o => o.When != null ? Eval(o.When) : ResolveTarget(o.Target).Count > 0);

            bool changed;
            lock (_flowLock)
            {
                changed = current != f.Current || option != f.Option;
                f.DoneNow = done;
                if (changed) { f.Current = current; f.Option = option; f.Rev++; }
            }
            if (!changed) return;

            // Show only the option that applies now, numbered as its step.
            if (option >= 0)
            {
                var o = step.Options[option];
                var t = o.Target;
                var target = new JObject
                {
                    ["tier"] = "primary", ["order"] = current + 1, ["label"] = o.Label ?? step.Label, ["action"] = t.Action,
                    ["item"] = t.Item, ["path"] = t.Path, ["panel"] = t.Panel, ["text"] = t.Text, ["within"] = t.Within,
                    ["clipTo"] = t.ClipTo, ["rel"] = t.Rel == null ? null : new JArray(t.Rel), ["child"] = t.Child == null ? null : new JArray(t.Child),
                    ["rect"] = t.Rect == null ? null : new JArray(t.Rect),
                };
                foreach (var prop in target.Properties().Where(x => x.Value.Type == JTokenType.Null).ToList()) prop.Remove();
                HighlightSet(new JObject { ["targets"] = new JArray(target), ["auto"] = false });   // no title pill: the card shows the flow
            }
            else HighlightSet(new JObject { ["clear"] = true });
            GuideSet(new JObject
            {
                ["title"] = f.Title, ["instruction"] = option >= 0 ? step.Options[option].Label ?? step.Label : step.Label,
                ["status"] = "waiting", ["step"] = current + 1, ["steps"] = f.Steps.Count,
                ["detail"] = option >= 0 ? null : "Not on screen yet - open the panel it is in",
            });
        }
        catch (Exception ex) { LogError($"[GuideFlow] {ex.Message}"); }
    }

    private void FinishFlow(FlowState f, string status, string detail)
    {
        lock (_flowLock) { f.Status = status; f.Current = -1; f.Option = -1; f.Rev++; }
        HighlightSet(new JObject { ["clear"] = true });
        GuideSet(new JObject { ["status"] = status == "done" ? "done" : "failed", ["detail"] = detail, ["step"] = null, ["steps"] = null });
        GuideLog(new JObject { ["text"] = $"{f.Title ?? "Guide"}: {detail}", ["kind"] = status == "done" ? "result" : "warn" });
    }

    // ── Conditions ───────────────────────────────────────────────────

    private bool Eval(JToken c)
    {
        if (c is not JObject o) return false;
        try
        {
            if (o["all"] is JArray all) return all.All(Eval);
            if (o["any"] is JArray any) return any.Any(Eval);
            if (o["not"] is { } not) return !Eval(not);
            if (o["visible"] is JObject v) return ParseTarget(v) is { } vt && ResolveTarget(vt).Count > 0;
            if (o["checked"] is JObject ck) return CheckState(ck) == true;
            if (o["unchecked"] is JObject uk) return CheckState(uk) == false;
            if (o["text"] is JObject tx)
            {
                var e = ParseTarget(tx) is { } tt ? ResolveTarget(tt).FirstOrDefault(b => b.e != null).e : null;
                return e != null && string.Equals(e.Text?.Trim(), o["equals"]?.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            if (o["eval"]?.ToString() is { } expr)
            {
                var r = JToken.Parse(new ExpressionWalker(GameController).Evaluate(expr));
                return r["error"] == null && JToken.DeepEquals(Normalize(r["value"]), Normalize(o["equals"]));
            }
            if (o["memory"] is JObject m) return MemoryCondition(m);
        }
        catch { }
        return false;
    }

    private static JToken? Normalize(JToken? t) => t?.Type is JTokenType.Integer or JTokenType.Float ? new JValue(t.Value<double>()) : t;

    /// <summary>true/false for a visible checkbox, null when it isn't on screen (can't tell).</summary>
    private bool? CheckState(JObject target)
    {
        var e = ParseTarget(target) is { } t ? ResolveTarget(t).FirstOrDefault(b => b.e != null).e : null;
        return e == null ? null : IsChecked(e);
    }

    /// <summary>A value in an item of a collection, e.g. the affinity bits of the stash tab named DUMP.</summary>
    private bool MemoryCondition(JObject m)
    {
        var coll = new ExpressionWalker(GameController).Resolve(m["collection"]?.ToString() ?? "", out _);
        if (coll is not IEnumerable items) return false;
        var where = m["where"] as JObject;
        foreach (var item in items)
        {
            if (item == null) continue;
            if (where != null && !where.Properties().All(p => string.Equals(LabelValue(item, p.Name)?.ToString(), p.Value.ToString(), StringComparison.OrdinalIgnoreCase)))
                continue;
            if (ReadAddress(item) is not { } addr || addr == 0) return false;
            var size = m["size"]?.Value<int>() ?? 4;
            var a = addr + (m["offset"]?.Value<long>() ?? 0);
            ulong v = size switch
            {
                1 => GameController.Memory.Read<byte>(a), 2 => GameController.Memory.Read<ushort>(a),
                8 => GameController.Memory.Read<ulong>(a), _ => GameController.Memory.Read<uint>(a),
            };
            if (m["mask"] != null) v &= m["mask"]!.Value<ulong>();
            return v == (m["equals"]?.Value<ulong>() ?? 0);
        }
        return false;
    }
}
