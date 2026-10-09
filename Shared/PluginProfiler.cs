using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Method-level profiler for one HUD plugin: where its frame time goes. For durationMs, Harmony wraps every method
/// declared in the plugin's assembly (bodies over 8 IL bytes; not generic definitions, not methods of generic types,
/// not abstract; never the protected IL stubs - plugins are compiled from source, but checked anyway) with a
/// prefix/postfix that keeps a per-thread call stack, so each method gets calls, inclusive and self time (inclusive
/// minus its profiled callees). Then everything is unpatched. Overhead: roughly 0.1-0.3 µs per call, reported.
/// Off unless Settings.AllowHudInstrumentation. profile.plugin {name | assembly+filter, durationMs?, maxMethods?} -> {id};
/// profile.result {id}. assembly= profiles part of a HUD assembly (e.g. ExileCore2, filter=EntityListWrapper).
/// </summary>
public partial class WhatsAnAiBridge
{
    private sealed class ProfStat { public long Calls, Inclusive, Self, AllocInclusive, AllocSelf; }

    private static volatile bool _profOn;
    private static readonly ConcurrentDictionary<MethodBase, ProfStat> ProfStats = new();
    // Per frame on the stack: callee time and callee allocation accumulated so far, and this call's allocation start.
    [ThreadStatic] private static Stack<(long childTime, long childAlloc, long allocStart)>? _profChild;
    private static readonly Dictionary<string, JObject> ProfJobs = new();
    private Harmony? _profHarmony;

    private string? ProcessProfileMethod(string method, JToken? p) => method switch
    {
        "profile.plugin" => SafeMemory(() => HarmonyLoadError() is { } e ? Err("harmony_unavailable", e) : ProfileStart(p)),
        "profile.result" => SafeMemory(() => { lock (ProfJobs) return ProfJobs.TryGetValue(p?["id"]?.ToString() ?? "", out var j) ? j : Err("unknown_id", "No such profile."); }),
        _ => null,
    };

    private static void ProfPre(out long __state)
    {
        // __state = start timestamp when this call pushed a frame, 0 when profiling was off at entry (nothing to pop).
        if (!_profOn) { __state = 0; return; }
        (_profChild ??= new Stack<(long, long, long)>()).Push((0, 0, GC.GetAllocatedBytesForCurrentThread()));
        __state = Stopwatch.GetTimestamp();
    }

    private static void ProfPost(MethodBase __originalMethod, long __state)
    {
        if (__state == 0 || _profChild is not { Count: > 0 } stack) return;
        var incl = Stopwatch.GetTimestamp() - __state;
        var (childTime, childAlloc, allocStart) = stack.Pop();   // always pop what the prefix pushed, even if profiling just ended
        var alloc = GC.GetAllocatedBytesForCurrentThread() - allocStart;
        if (stack.Count > 0) { var c = stack.Pop(); stack.Push((c.childTime + incl, c.childAlloc + alloc, c.allocStart)); }   // ours counts as our caller's callee
        if (!_profOn) return;
        var s = ProfStats.GetOrAdd(__originalMethod, _ => new ProfStat());
        Interlocked.Increment(ref s.Calls);
        Interlocked.Add(ref s.Inclusive, incl);
        Interlocked.Add(ref s.Self, incl - childTime);
        Interlocked.Add(ref s.AllocInclusive, alloc);
        Interlocked.Add(ref s.AllocSelf, alloc - childAlloc);
    }

    private JObject ProfileStart(JToken? p)
    {
        if (!Settings.AllowHudInstrumentation.Value)
            return Err("instrumentation_disabled", "Enable 'Allow HUD Instrumentation' in the bridge settings (Dev Loop) to profile plugins.");
        if (_profOn) return Err("busy", "A profile is already running.");
        var name = p?["name"]?.ToString() ?? "";
        var duration = Math.Clamp(p?["durationMs"]?.Value<int>() ?? 3000, 500, 20_000);
        var asmName = p?["assembly"]?.ToString();
        var filter = p?["filter"]?.ToString();
        var max = Math.Clamp(p?["maxMethods"]?.Value<int>() ?? 600, 10, 3000);
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Assembly? asm;
        string label;
        if (!string.IsNullOrWhiteSpace(asmName))
        {
            // A HUD assembly (e.g. ExileCore2): profile its core. Only a filtered part at a time (type name substring),
            // so the patch set stays small; protected stubs are skipped and uncopyable methods fail without effect.
            asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, asmName, StringComparison.OrdinalIgnoreCase));
            if (asm == null) return Err("not_found", $"No loaded assembly '{asmName}'.");
            if (asmName.StartsWith("System", StringComparison.OrdinalIgnoreCase) || asmName is "0Harmony" or "mscorlib")
                return Err("refused", "Runtime and Harmony assemblies are not profiled.");
            if (string.IsNullOrWhiteSpace(filter)) return Err("bad_request", "Profiling a whole HUD assembly is refused: pass filter (a type name substring, e.g. EntityListWrapper).");
            label = $"{asm.GetName().Name} [{filter}]";
        }
        else
        {
            var wrapper = Core.Current?.pluginManager?.Plugins.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
            if (wrapper == null)
                return Err("not_found", $"No loaded plugin named '{name}'. Loaded: {string.Join(", ", Core.Current?.pluginManager?.Plugins.Select(w => w.Name) ?? [])}");
            var plugin = wrapper.GetType().GetProperty("Plugin", any)?.GetValue(wrapper) ?? wrapper.GetType().GetField("_plugin", any)?.GetValue(wrapper);
            asm = plugin?.GetType().Assembly;
            if (asm == null) return Err("not_found", $"Could not reach {name}'s plugin object (PluginWrapper.Plugin)");
            label = wrapper.Name;
        }
        if (asm == typeof(WhatsAnAiBridge).Assembly) return Err("refused", "The bridge does not profile itself.");

        const BindingFlags decl = any | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var targets = new List<MethodBase>();
        Type[] types;
        try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
        foreach (var t in types)
        {
            if (t.ContainsGenericParameters) continue;
            if (filter != null && t.FullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) != true) continue;
            foreach (var m in t.GetMethods(decl).Cast<MethodBase>())
            {
                if (m.IsAbstract || m.IsGenericMethodDefinition || m.ContainsGenericParameters) continue;
                byte[]? il;
                try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { continue; }
                if (il == null || il.Length <= 8 || TraceIsStub(m)) continue;
                if (targets.Count >= max) break;
                targets.Add(m);
            }
        }
        ProfStats.Clear();
        _profHarmony ??= new Harmony("whatsanaibridge.plugin-profiler");
        var pre = new HarmonyMethod(typeof(WhatsAnAiBridge).GetMethod(nameof(ProfPre), BindingFlags.Static | BindingFlags.NonPublic));
        var post = new HarmonyMethod(typeof(WhatsAnAiBridge).GetMethod(nameof(ProfPost), BindingFlags.Static | BindingFlags.NonPublic));
        var id = Guid.NewGuid().ToString("N")[..10];
        var job = new JObject { ["id"] = id, ["status"] = "patching", ["plugin"] = label, ["methods"] = targets.Count, ["durationMs"] = duration };
        lock (ProfJobs) { if (ProfJobs.Count >= 10) ProfJobs.Remove(ProfJobs.Keys.First()); ProfJobs[id] = job; }
        var harmony = _profHarmony;
        System.Threading.Tasks.Task.Run(async () =>
        {
            var refused = new JArray();
            var patched = 0;
            var sw = Stopwatch.StartNew();
            foreach (var m in targets)
            {
                try { harmony.Patch(m, prefix: pre, postfix: post); patched++; }
                catch (Exception ex) { if (refused.Count < 20) refused.Add($"{m.DeclaringType?.Name}.{m.Name}: {ex.GetType().Name}"); }
            }
            var patchMs = sw.ElapsedMilliseconds;
            _profOn = true;
            await System.Threading.Tasks.Task.Delay(duration);
            _profOn = false;
            try { harmony.UnpatchAll(harmony.Id); } catch { }
            double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            var stats = ProfStats.ToArray();
            var totalCalls = stats.Sum(kv => kv.Value.Calls);
            var result = new JObject
            {
                ["id"] = id, ["status"] = "done", ["plugin"] = label, ["durationMs"] = duration,
                ["methodsPatched"] = patched, ["methodsCapped"] = targets.Count >= max, ["patchMs"] = patchMs, ["refused"] = refused, ["calls"] = totalCalls,
                ["selfTotalMsPerSecond"] = Math.Round(stats.Sum(kv => Ms(kv.Value.Self)) * 1000.0 / duration, 3),
                ["note"] = "self = inclusive minus profiled callees (BCL/HUD calls count as self). Per-call overhead ~0.1-0.3 us inflates tiny hot methods.",
                ["top"] = new JArray(stats.OrderByDescending(kv => kv.Value.Self).Take(25).Select(kv => new JObject
                {
                    ["method"] = $"{kv.Key.DeclaringType?.Name}.{kv.Key.Name}",
                    ["calls"] = kv.Value.Calls,
                    ["selfMsPerSecond"] = Math.Round(Ms(kv.Value.Self) * 1000.0 / duration, 3),
                    ["inclMsPerSecond"] = Math.Round(Ms(kv.Value.Inclusive) * 1000.0 / duration, 3),
                    ["selfUsPerCall"] = Math.Round(Ms(kv.Value.Self) * 1000.0 / Math.Max(1, kv.Value.Calls), 2),
                    ["allocSelfKBPerSecond"] = Math.Round(kv.Value.AllocSelf / 1024.0 * 1000.0 / duration, 1),
                    ["allocInclKBPerSecond"] = Math.Round(kv.Value.AllocInclusive / 1024.0 * 1000.0 / duration, 1),
                })),
                // Where the garbage comes from (GC pauses at high fps): self = bytes allocated in the method minus its
                // profiled callees, so BCL work it calls (LINQ, string building) counts as its own.
                ["allocTotalKBPerSecond"] = Math.Round(stats.Sum(kv => kv.Value.AllocSelf) / 1024.0 * 1000.0 / duration, 1),
                ["topAlloc"] = new JArray(stats.Where(kv => kv.Value.AllocSelf > 0).OrderByDescending(kv => kv.Value.AllocSelf).Take(15).Select(kv => new JObject
                {
                    ["method"] = $"{kv.Key.DeclaringType?.Name}.{kv.Key.Name}",
                    ["calls"] = kv.Value.Calls,
                    ["allocSelfKBPerSecond"] = Math.Round(kv.Value.AllocSelf / 1024.0 * 1000.0 / duration, 1),
                    ["bytesPerCall"] = kv.Value.Calls == 0 ? 0 : kv.Value.AllocSelf / kv.Value.Calls,
                })),
            };
            lock (ProfJobs) ProfJobs[id] = result;
        });
        return job;
    }
}
