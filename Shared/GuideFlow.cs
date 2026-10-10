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
        public string? DoneWhy;                // why the current step is not done (first failing link)
        public string?[] OptionWhy = [];        // per option of the current step: null = available, else why not
        public List<string> Preflight = new();  // static paths that do not resolve: API or layout changes
        public string? Who;                     // the session that started it (Sessions.cs: a restart waits for a running flow)
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
            bool wasRunning;
            lock (_flowLock) { wasRunning = _flow.Status == "running"; if (wasRunning) _flow.Status = "stopped"; _flow.Rev++; }
            HighlightSetLayer(HlFlow, null, null, new JObject { ["clear"] = true });
            if (wasRunning) GuideSet(new JObject { ["clear"] = true });   // the card showed the flow: don't leave its instruction behind
            return FlowStateJson();
        }
        var flow = new FlowState { Title = Clip(p["title"]?.ToString(), 80), Goal = p["goal"], Status = "running", StartedAt = DateTime.UtcNow, Who = CurrentWho() };
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
        flow.Preflight = PreflightFlow(flow);
        lock (_flowLock) { flow.Rev = _flow.Rev + 1; _flow = flow; }
        _flowLastTick = DateTime.MinValue;
        GuideLog(new JObject { ["text"] = $"Guided: {flow.Title ?? flow.Steps[0].Label}", ["kind"] = "step" });
        foreach (var w in flow.Preflight) GuideLog(new JObject { ["text"] = "Guide check: " + w, ["kind"] = "warn" });
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
                ["diagnostics"] = f.Current < 0 ? null : new JObject
                {
                    ["step"] = f.Steps[f.Current].Label,
                    ["notDoneBecause"] = f.DoneWhy,
                    ["options"] = new JArray(f.Steps[f.Current].Options.Select((o, k) => new JObject
                    {
                        ["label"] = o.Label ?? f.Steps[f.Current].Label,
                        ["available"] = k < f.OptionWhy.Length && f.OptionWhy[k] == null,
                        ["why"] = k < f.OptionWhy.Length ? f.OptionWhy[k] : null,
                    })),
                },
                ["preflight"] = f.Preflight.Count == 0 ? null : new JArray(f.Preflight),
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

            var done = new bool[f.Steps.Count];
            string? doneWhy = null;
            int current = -1;
            for (int i = 0; i < f.Steps.Count; i++)
            {
                done[i] = f.Steps[i].Done != null && Eval(f.Steps[i].Done!);
                if (!done[i] && current < 0) { current = i; doneWhy = f.Steps[i].Done == null ? "no done condition (ends with the goal)" : Why; }
            }
            if (current < 0) { FinishFlow(f, "done", "All done"); return; }
            var step = f.Steps[current];
            var optionWhy = new string?[step.Options.Count];
            int option = -1;
            for (int k = 0; k < step.Options.Count; k++)
            {
                var o = step.Options[k];
                var ok = o.When != null ? Eval(o.When) : ResolveTarget(o.Target).Count > 0;
                optionWhy[k] = ok ? null : Why ?? "not available";
                if (ok && option < 0) option = k;
            }
            lock (_flowLock) { f.DoneWhy = doneWhy; f.OptionWhy = optionWhy; }

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
                HighlightSetLayer(HlFlow, null, f.Who, new JObject { ["targets"] = new JArray(target), ["auto"] = false });   // no title pill: the card shows the flow
            }
            else HighlightSetLayer(HlFlow, null, f.Who, new JObject { ["clear"] = true });
            GuideSet(new JObject
            {
                ["title"] = f.Title, ["instruction"] = option >= 0 ? step.Options[option].Label ?? step.Label : step.Label,
                ["status"] = "waiting", ["step"] = current + 1, ["steps"] = f.Steps.Count,
                ["detail"] = option >= 0 ? null : "Not on screen yet - open the panel it is in",
                ["who"] = f.Who,   // the flow runs in the HUD (no session on this call): the card names who started it
            });
        }
        catch (Exception ex) { LogError($"[GuideFlow] {ex.Message}"); }
    }

    private void FinishFlow(FlowState f, string status, string detail)
    {
        lock (_flowLock) { f.Status = status; f.Current = -1; f.Option = -1; f.Rev++; }
        HighlightSetLayer(HlFlow, null, f.Who, new JObject { ["clear"] = true });
        GuideSet(new JObject { ["status"] = status == "done" ? "done" : "failed", ["detail"] = detail, ["step"] = null, ["steps"] = null });
        GuideLog(new JObject { ["text"] = $"{f.Title ?? "Guide"}: {detail}", ["kind"] = status == "done" ? "result" : "warn" });
    }

    // ── Conditions ───────────────────────────────────────────────────

    private bool Eval(JToken c)
    {
        if (c is not JObject o) { Why = "condition is not an object"; return false; }
        try
        {
            if (o["all"] is JArray all) { foreach (var x in all) if (!Eval(x)) return false; return true; }
            if (o["any"] is JArray any) { var reasons = new List<string>(); foreach (var x in any) { if (Eval(x)) return true; reasons.Add(Why ?? "?"); } Why = "none of: " + string.Join(" | ", reasons); return false; }
            if (o["not"] is { } not) { var r = Eval(not); if (r) Why = "'not' failed: the inner condition holds"; return !r; }
            if (o["visible"] is JObject v) return ParseTarget(v) is { } vt ? ResolveTarget(vt).Count > 0 : Fail("visible: target names nothing to find");
            if (o["checked"] is JObject ck) { var s = CheckState(ck); return s == true || Fail(s == null ? $"checked: {Why}" : "checked: the box is unchecked"); }
            if (o["unchecked"] is JObject uk) { var s = CheckState(uk); return s == false || Fail(s == null ? $"unchecked: {Why}" : "unchecked: the box is checked"); }
            if (o["text"] is JObject tx)
            {
                var e = ParseTarget(tx) is { } tt ? ResolveTarget(tt).FirstOrDefault(b => b.e != null).e : null;
                if (e == null) return Fail($"text: {Why}");
                var want = o["equals"]?.ToString();
                return string.Equals(e.Text?.Trim(), want, StringComparison.OrdinalIgnoreCase) || Fail($"text: is '{e.Text?.Trim()}', want '{want}'");
            }
            if (o["eval"]?.ToString() is { } expr)
            {
                var r = JToken.Parse(new ExpressionWalker(GameController).Evaluate(expr));
                if (r["error"] != null) return Fail($"eval {expr}: {r["error"]}");
                return JToken.DeepEquals(Normalize(r["value"]), Normalize(o["equals"])) || Fail($"eval {expr}: is {r["value"]}, want {o["equals"]}");
            }
            if (o["memory"] is JObject m) return MemoryCondition(m);
            return Fail("unknown condition: " + string.Join(",", o.Properties().Select(p => p.Name)));
        }
        catch (Exception ex) { return Fail($"condition threw {ex.GetType().Name}: {ex.Message}"); }
    }

    private bool Fail(string why) { Why = why; return false; }

    // Walker errors that mean the API itself changed (a member, type or indexer is gone) - not "not open right now".
    private static readonly string[] ApiBreak = ["No public property or field", "Could not resolve component type", "Cannot apply indexer", "method found on", "not in the allowed"];

    /// <summary>
    /// At start, resolve every static walker path the flow uses (paths, within/clipTo paths, memory collections). A path
    /// that is merely null now (panel closed) is fine; one whose member/type no longer exists means a HUD or game update
    /// broke the flow - report it at once, naming the path and the broken segment, instead of a step that never shows.
    /// </summary>
    private List<string> PreflightFlow(FlowState f)
    {
        var paths = new HashSet<string>();
        void Target(HighlightTarget t)
        {
            if (t.Path != null) paths.Add(t.Path);
            if (t.ClipTo != null) paths.Add(t.ClipTo);
            if (t.Within?.StartsWith("GameController", StringComparison.Ordinal) == true) paths.Add(t.Within);
        }
        void Cond(JToken? c)
        {
            if (c is JObject o)
            {
                foreach (var key in new[] { "visible", "checked", "unchecked", "text" })
                    if (o[key] is JObject tj && ParseTarget(tj) is { } t) Target(t);
                if (o["memory"]?["collection"]?.ToString() is { } coll) paths.Add(coll);
                if (o["eval"]?.ToString() is { } e) paths.Add(e);
                foreach (var key in new[] { "all", "any" }) foreach (var x in o[key] as JArray ?? []) Cond(x);
                Cond(o["not"]);
            }
        }
        Cond(f.Goal);
        foreach (var s in f.Steps) { Cond(s.Done); foreach (var o in s.Options) { Target(o.Target); Cond(o.When); } }
        var issues = new List<string>();
        foreach (var p in paths)
        {
            new ExpressionWalker(GameController).Resolve(p, out var err);
            if (err != null && ApiBreak.Any(b => err.Contains(b, StringComparison.Ordinal))) issues.Add($"{p}: {err}");
        }
        return issues;
    }

    private static JToken? Normalize(JToken? t) => t?.Type is JTokenType.Integer or JTokenType.Float ? new JValue(t.Value<double>()) : t;

    /// <summary>true/false for a visible checkbox, null when it isn't on screen (can't tell; Why says why).</summary>
    private bool? CheckState(JObject target)
    {
        var e = ParseTarget(target) is { } t ? ResolveTarget(t).FirstOrDefault(b => b.e != null).e : null;
        return e == null ? null : IsChecked(e);
    }

    /// <summary>A value in an item of a collection, e.g. the affinity bits of the stash tab named DUMP.</summary>
    private bool MemoryCondition(JObject m)
    {
        var path = m["collection"]?.ToString() ?? "";
        var coll = new ExpressionWalker(GameController).Resolve(path, out var err);
        if (err != null) return Fail($"memory: collection {path}: {err}");
        if (coll is not IEnumerable items) return Fail($"memory: {path} is a {coll?.GetType().Name ?? "null"}, not a collection");
        var where = m["where"] as JObject;
        int seen = 0;
        foreach (var item in items)
        {
            if (item == null) continue;
            seen++;
            if (where != null && !where.Properties().All(p => string.Equals(LabelValue(item, p.Name)?.ToString(), p.Value.ToString(), StringComparison.OrdinalIgnoreCase)))
                continue;
            if (ReadAddress(item) is not { } addr || addr == 0) return Fail($"memory: matching {item.GetType().Name} has no address");
            var size = m["size"]?.Value<int>() ?? 4;
            var off = m["offset"]?.Value<long>() ?? 0;
            ulong raw = size switch
            {
                1 => GameController.Memory.Read<byte>(addr + off), 2 => GameController.Memory.Read<ushort>(addr + off),
                8 => GameController.Memory.Read<ulong>(addr + off), _ => GameController.Memory.Read<uint>(addr + off),
            };
            var v = m["mask"] != null ? raw & m["mask"]!.Value<ulong>() : raw;
            var want = m["equals"]?.Value<ulong>() ?? 0;
            return v == want || Fail($"memory: +{off} ({size} B) = 0x{raw:X}{(m["mask"] != null ? $" & 0x{m["mask"]!.Value<ulong>():X} = 0x{v:X}" : "")}, want 0x{want:X}");
        }
        return Fail($"memory: no item of {path} ({seen} items) where {where?.ToString(Newtonsoft.Json.Formatting.None) ?? "(any)"}");
    }
}