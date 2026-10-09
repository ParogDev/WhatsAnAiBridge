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
/// Off unless Settings.AllowHudInstrumentation. profile.plugin {name | assembly+filter, method?, durationMs?, maxMethods?} -> {id};
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

    private static bool IsInstrumentationCode(MethodBase m)
    {
        static bool Hit(string? s) => s != null && (s.Contains("Prof", StringComparison.Ordinal) || s.Contains("Trace", StringComparison.Ordinal)
            || s.Contains("Harmony", StringComparison.Ordinal) || s.Contains("SelfPerf", StringComparison.Ordinal));
        // Closures and state machines carry the outer method's name in their own (<ProfileStart>b__0, <>c__DisplayClass).
        return Hit(m.Name) || Hit(m.DeclaringType?.Name) || Hit(m.DeclaringType?.DeclaringType?.Name);
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
        var methodFilter = p?["method"]?.ToString();
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
            object? PluginOf(object w) => w.GetType().GetProperty("Plugin", any)?.GetValue(w) ?? w.GetType().GetField("_plugin", any)?.GetValue(w);
            // The HUD's display name, or the plugin's type name (what pipeline.trace reports, e.g. SkillDpsCore for "Skill DPS").
            var wrapper = Core.Current?.pluginManager?.Plugins.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase))
                          ?? Core.Current?.pluginManager?.Plugins.FirstOrDefault(w => string.Equals(PluginOf(w)?.GetType().Name, name, StringComparison.OrdinalIgnoreCase));
            if (wrapper == null)
                return Err("not_found", $"No loaded plugin named '{name}'. Loaded: {string.Join(", ", Core.Current?.pluginManager?.Plugins.Select(w => w.Name) ?? [])}");
            var plugin = PluginOf(wrapper);
            asm = plugin?.GetType().Assembly;
            if (asm == null) return Err("not_found", $"Could not reach {name}'s plugin object (PluginWrapper.Plugin)");
            label = wrapper.Name;
        }
        if (!string.IsNullOrWhiteSpace(methodFilter)) label += $" (methods ~{methodFilter})";
        var self = asm == typeof(WhatsAnAiBridge).Assembly;
        // The bridge profiles a filtered part of itself only, never the profiler/trace code that runs the patching
        // (patching the method that applies the patches, or the hooks themselves, would recurse or deadlock).
        if (self && string.IsNullOrWhiteSpace(filter) && string.IsNullOrWhiteSpace(methodFilter))
            return Err("bad_request", "Profiling the bridge needs method= (a method name substring, e.g. Stats) or filter= (a type name substring).");

        const BindingFlags decl = any | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var targets = new List<MethodBase>();
        Type[] types;
        try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
        foreach (var t in types)
        {
            if (t.ContainsGenericParameters) continue;
            if (filter != null && t.FullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) != true) continue;
            // Instance constructors too: per-frame object construction (e.g. a state rebuilt each frame) is otherwise the caller's self time.
            foreach (var m in t.GetMethods(decl).Cast<MethodBase>().Concat(t.GetConstructors(any | BindingFlags.DeclaredOnly)))
            {
                if (m.IsAbstract || m.IsGenericMethodDefinition || m.ContainsGenericParameters) continue;
                if (methodFilter != null && !m.Name.Contains(methodFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (self && IsInstrumentationCode(m)) continue;
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
            var (ohTicks, ohBytes) = ProfCalibrate(harmony, pre, post);
            ProfStats.Clear();
            _profOn = true;
            await System.Threading.Tasks.Task.Delay(duration);
            _profOn = false;
            // Remove the hooks' own cost (measured on an empty method) from every call's self numbers.
            foreach (var st in ProfStats.Values)
            {
                st.Self = Math.Max(0, st.Self - (long)(st.Calls * ohTicks));
                st.Inclusive = Math.Max(0, st.Inclusive - (long)(st.Calls * ohTicks));
                st.AllocSelf = Math.Max(0, st.AllocSelf - (long)(st.Calls * ohBytes));
                st.AllocInclusive = Math.Max(0, st.AllocInclusive - (long)(st.Calls * ohBytes));
            }
            try { harmony.UnpatchAll(harmony.Id); } catch { }
            double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            var stats = ProfStats.ToArray();
            var totalCalls = stats.Sum(kv => kv.Value.Calls);
            var result = new JObject
            {
                ["id"] = id, ["status"] = "done", ["plugin"] = label, ["durationMs"] = duration,
                ["methodsPatched"] = patched, ["methodsCapped"] = targets.Count >= max, ["patchMs"] = patchMs, ["refused"] = refused, ["calls"] = totalCalls,
                ["selfTotalMsPerSecond"] = Math.Round(stats.Sum(kv => Ms(kv.Value.Self)) * 1000.0 / duration, 3),
                ["hookOverhead"] = new JObject { ["usPerCall"] = Math.Round(ohTicks * 1e6 / Stopwatch.Frequency, 3), ["bytesPerCall"] = Math.Round(ohBytes, 1) },
                ["note"] = "self = inclusive minus profiled callees (BCL/HUD calls count as self). The hooks' own cost (hookOverhead, measured on an empty method) is subtracted per call.",
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

public partial class WhatsAnAiBridge
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int ProfCalibrationTarget(int x) => x + 1;

    /// <summary>The hooks' own time and allocation per call: an empty method patched like the targets, called 2000 times.
    /// Without this, every profiled call carries the prefix/postfix cost (Harmony loads __originalMethod inside the window).</summary>
    private static (double ticks, double bytes) ProfCalibrate(Harmony harmony, HarmonyMethod pre, HarmonyMethod post)
    {
        var m = typeof(WhatsAnAiBridge).GetMethod(nameof(ProfCalibrationTarget), BindingFlags.Static | BindingFlags.NonPublic);
        if (m == null) return (0, 0);
        try { harmony.Patch(m, prefix: pre, postfix: post); } catch { return (0, 0); }
        ProfStats.Clear();
        _profOn = true;
        var x = 0;
        for (var i = 0; i < 2000; i++) x = ProfCalibrationTarget(x);
        _profOn = false;
        try { harmony.Unpatch(m, HarmonyPatchType.All, harmony.Id); } catch { }
        return ProfStats.TryGetValue(m, out var s) && s.Calls > 0 && x > 0 ? ((double)s.Self / s.Calls, (double)s.AllocSelf / s.Calls) : (0, 0);
    }
}
