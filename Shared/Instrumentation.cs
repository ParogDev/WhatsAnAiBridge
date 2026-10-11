using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// The one owner of every Harmony patch the bridge applies (profile.plugin, pipeline.trace and the profiler's calibration).
/// A patch set lives exactly as long as its measurement:
///   - Every patch goes through InstrPatch, registered to a set (one Harmony owner id per set, "whatsanaibridge.kind.id").
///     A method already instrumented by another set, or patched by anyone else, is refused (never stacked).
///   - InstrEnd removes a set's patches and puts the ORIGINAL code back (HarmonyRestore). Harmony's own Unpatch never
///     does: it re-detours the method to a fresh copy of its IL with zero patches (Name_PatchN), which stays until the
///     plugin reloads. Measurements call InstrEnd in a finally, so it runs on done, cancel, error and cut.
///   - A 1 s sweep (timer, so it runs while the overlay is hidden) ends sets that outlived their duration + 15 s, sets
///     whose plugin was reloaded or unloaded (the HUD menu's Reload too), and every 5 s restores methods still patched by a
///     bridge owner that no live set owns (orphans of a previous bridge load) or left on a zero-patch Harmony copy.
///   - hud.reload_plugin ends the sets that touch the plugin before it reloads; a bridge reload ends all of them.
///   - instrument.state lists active sets, leftovers and the cleanup events (also in session.list and hud.plugins);
///     instrument.cancel {id?|all} ends sets now.
/// Why the copy can be broken (2026-10-11, Whats A Route: EntryPointNotFoundException in IEnumerable`1.GetEnumerator
/// inside BuildState_Patch3): Harmony's MonoMod resolves the types a patched copy references BY ASSEMBLY NAME and caches
/// the result (MonoMod.Utils.ReflectionHelper). A plugin reloaded into a new load context keeps its assembly name, so the
/// copy of the new load's method bound to the FIRST load's types, and interface calls on the new load's objects failed.
/// InstrBindTo clears those caches whenever patching moves to another assembly, and InstrBindingCheck verifies after
/// patching that nothing of the patched assembly resolved to another load (else the set is ended and the result says so).
/// </summary>
public partial class WhatsAnAiBridge
{
    internal const string InstrOwnerPrefix = "whatsanaibridge.";
    private const int InstrMarginMs = 15_000;

    private sealed class InstrSet
    {
        public string Id = "", Kind = "", Label = "", Owner = "";
        public string? Who;
        public DateTime StartedAt = DateTime.UtcNow, ExpiresAt;
        public readonly List<MethodBase> Methods = new();
        public readonly HashSet<Assembly> Assemblies = new();
        /// <summary>Plugin assemblies among Assemblies, with the plugin's name: a reload or unload of one ends the set.</summary>
        public readonly Dictionary<Assembly, string> Plugins = new();
        public readonly CancellationTokenSource Cts = new();
        public string? EndedBy;
        public JObject? EndReport;
    }

    private static readonly object InstrLock = new();
    private static readonly Dictionary<string, InstrSet> InstrSets = new();
    private static readonly Dictionary<MethodBase, InstrSet> InstrOwners = new();
    private static readonly List<JObject> InstrEvents = new();
    private static int _instrEventSeq;
    private static Timer? _instrTimer;
    private static int _instrSweeping;
    private static DateTime _instrLastOrphanSweep;
    private static Assembly? _instrResolvedFor;
    private static string? _instrCacheBroken, _instrRestoreBroken;
    /// <summary>Leftovers the orphan sweep could not restore (under InstrLock): reported once, then skipped by the sweep.</summary>
    private static readonly HashSet<MethodBase> InstrUnrestorable = new();

    private string? ProcessInstrumentMethod(string method, JToken? p) => method switch
    {
        "instrument.state" => SafeMemory(() => InstrState(full: true)),
        "instrument.cancel" => SafeMemory(() => InstrCancel(p)),
        _ => null,
    };

    // ── Sets ─────────────────────────────────────────────────────────

    private InstrSet InstrBegin(string id, string kind, string label, int durationMs, string? who)
    {
        // Patching hundreds of methods takes seconds: a generous expiry until InstrArm, when the measured window starts.
        var s = new InstrSet { Id = id, Kind = kind, Label = label, Who = who, Owner = $"{InstrOwnerPrefix}{kind}.{id}", ExpiresAt = DateTime.UtcNow.AddMilliseconds(durationMs + 60_000) };
        lock (InstrLock) InstrSets[s.Id] = s;
        InstrEnsureTimer();
        return s;
    }

    /// <summary>The measured window starts now: the set expires durationMs + 15 s from here.</summary>
    private static void InstrArm(InstrSet s, int durationMs)
    {
        lock (InstrLock) s.ExpiresAt = DateTime.UtcNow.AddMilliseconds(durationMs + InstrMarginMs);
    }

    private static bool InstrAnyActive(string kind)
    {
        lock (InstrLock) return InstrSets.Values.Any(s => s.Kind == kind);
    }

    private static string MethodName(MethodBase m) => $"{m.DeclaringType?.Name}.{m.Name}";

    /// <summary>Patch one method for the set: null when patched, else why not (the measurement lists it under refused).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? InstrPatch(InstrSet s, MethodBase m, HarmonyMethod? pre, HarmonyMethod? post, string? plugin = null)
    {
        lock (InstrLock)
        {
            if (s.EndedBy != null) return $"not patched: the measurement ended ({s.EndedBy})";
            if (InstrOwners.TryGetValue(m, out var other)) return $"already instrumented by {other.Kind} {other.Id} ({other.Label}): never patched twice";
            var info = Harmony.GetPatchInfo(m);
            if (info != null && info.Owners.Count > 0) return $"already patched by {string.Join(", ", info.Owners)}: never stacked";
            var asm = m.DeclaringType?.Assembly;
            if (asm != null && InstrBindTo(asm) is { } why) return why;
            InstrOwners[m] = s;
            InstrUnrestorable.Remove(m);   // a set owns it again: if it is left over after this one, the sweep retries it
            s.Methods.Add(m);
            if (asm != null) { s.Assemblies.Add(asm); if (plugin != null) s.Plugins[asm] = plugin; }
            try
            {
                new Harmony(s.Owner).Patch(m, prefix: pre, postfix: post);
                return null;
            }
            catch (Exception ex)
            {
                InstrOwners.Remove(m);
                s.Methods.Remove(m);
                try { InstrRestoreLocked(m); } catch { }   // a failed patch can still have left a detour
                return $"patch failed: {ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    /// <summary>Take one method out of a running set (the profiler's calibration target) and restore its original code.</summary>
    private void InstrRelease(InstrSet s, MethodBase m)
    {
        lock (InstrLock)
        {
            if (!InstrOwners.TryGetValue(m, out var o) || o != s) return;
            InstrOwners.Remove(m);
            s.Methods.Remove(m);
            try { InstrRestoreLocked(m); } catch (Exception ex) { InstrEvent("restore_failed", $"{MethodName(m)}: {ex.GetType().Name}: {ex.Message}", s); }
        }
    }

    /// <summary>
    /// End a set: remove every patch it applied and restore the original code, whatever ended it. Idempotent: a second call
    /// returns the first report. endedBy "done" is the normal end; anything else is recorded as an event.
    /// </summary>
    private JObject InstrEnd(InstrSet s, string endedBy)
    {
        JObject report;
        lock (InstrLock)
        {
            if (s.EndReport != null) return s.EndReport;
            s.EndedBy = endedBy;
            report = s.Methods.Count == 0 ? InstrEmptyReport(endedBy) : InstrRemoveAllLocked(s, endedBy);
            InstrSets.Remove(s.Id);
            s.EndReport = report;
            if (endedBy != "done")
                InstrEvent(endedBy.StartsWith("cancel", StringComparison.Ordinal) ? "cancelled" : endedBy.StartsWith("expired", StringComparison.Ordinal) ? "expired" : "cut",
                    $"{s.Kind} {s.Id} ({s.Label}) ended: {endedBy}; {report["restored"]} of {s.Methods.Count} methods restored", s);
        }
        // Cancel only after the report is set and outside InstrLock: the measurement's await continuation runs inline on
        // this thread, calls InstrEnd(s, "done") (which must get this report, not overwrite endedBy) and then MeasureEnd,
        // whose guide card takes _sessionLock (BlockersLocked takes InstrLock under _sessionLock: a lock inversion).
        try { s.Cts.Cancel(); } catch { }
        return report;
    }

    private static JObject InstrEmptyReport(string endedBy) => new()
    {
        ["endedBy"] = endedBy, ["methods"] = 0, ["restored"] = 0, ["unpatchedOnly"] = 0, ["failed"] = new JArray(), ["stillPatchedCount"] = 0,
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JObject InstrRemoveAllLocked(InstrSet s, string endedBy)
    {
        int restored = 0, unpatched = 0;
        var failed = new JArray();
        foreach (var m in s.Methods)
        {
            try { if (InstrRestoreLocked(m)) restored++; else unpatched++; }
            catch (Exception ex) { if (failed.Count < 20) failed.Add($"{MethodName(m)}: {ex.GetType().Name}: {ex.Message}"); }
            InstrOwners.Remove(m);
        }
        // Verify, never assume: anything still patched or detoured is named (and raised by the MCP as an incident).
        var left = s.Methods.Where(InstrStillDetoured).ToList();
        if (left.Count > 0 || failed.Count > 0)
            InstrEvent("restore_failed", $"{s.Kind} {s.Id} ({s.Label}): {left.Count} methods still patched after the end ({string.Join(", ", left.Take(5).Select(MethodName))})" +
                                         (_instrRestoreBroken != null ? $"; restore link broken: {_instrRestoreBroken}" : ""), s);
        return new JObject
        {
            ["endedBy"] = endedBy, ["methods"] = s.Methods.Count, ["restored"] = restored, ["unpatchedOnly"] = unpatched, ["failed"] = failed,
            ["stillPatchedCount"] = left.Count, ["stillPatched"] = new JArray(left.Take(20).Select(MethodName)),
            ["restoreBroken"] = _instrRestoreBroken,
        };
    }

    /// <summary>End every set touching one of these assemblies (a plugin about to reload), or every set (assemblies null).</summary>
    private void InstrEndWhere(Func<InstrSet, bool> which, string endedBy)
    {
        List<InstrSet> hit;
        lock (InstrLock) hit = InstrSets.Values.Where(which).ToList();
        foreach (var s in hit) InstrEnd(s, endedBy);
    }

    private void InstrDropPluginAssemblies(IReadOnlyCollection<Assembly> asms, string why)
    {
        if (asms.Count == 0) return;
        InstrEndWhere(s => s.Assemblies.Overlaps(asms), why);
    }

    // ── Binding: the patched copy must use the patched load's own types ──

    private static readonly string[] MonoModCaches = ["AssemblyCache", "AssembliesCache", "ResolveReflectionCache"];

    private static IEnumerable<IDictionary> MonoModCacheDicts(out string? broken)
    {
        broken = null;
        var rh = typeof(Harmony).Assembly.GetType("MonoMod.Utils.ReflectionHelper");
        if (rh == null) { broken = "MonoMod.Utils.ReflectionHelper not found in 0Harmony"; return []; }
        var list = new List<IDictionary>();
        foreach (var n in MonoModCaches)
        {
            if (rh.GetField(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) is IDictionary d) list.Add(d);
            else { broken = $"MonoMod.Utils.ReflectionHelper.{n} not found (0Harmony changed)"; return []; }
        }
        return list;
    }

    /// <summary>
    /// Before Harmony builds a copy of a method of asm: forget MonoMod's by-name resolutions, so the copy's type references
    /// resolve to this load of the assembly, not to an earlier load with the same name. Null = go, else why not.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? InstrBindTo(Assembly asm)
    {
        if (asm == _instrResolvedFor) return null;
        var dicts = MonoModCacheDicts(out var broken).ToList();
        if (broken != null)
        {
            _instrCacheBroken = broken;
            // Without the caches cleared, the copy binds by name: safe only while no other load of the assembly exists.
            var name = asm.FullName;
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a != asm && a.FullName == name))
                return $"not patched: another load of {asm.GetName().Name} is in the process and {broken}, so the patched copy could bind to the other load's types (EntryPointNotFoundException)";
        }
        foreach (var d in dicts) lock (d) d.Clear();
        _instrResolvedFor = asm;
        return null;
    }

    /// <summary>After patching asm's methods: did MonoMod resolve any reference of asm to another load of it? Null = no.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? InstrBindingCheck(Assembly asm)
    {
        var name = asm.FullName;
        foreach (var d in MonoModCacheDicts(out _))
        {
            lock (d)
                foreach (DictionaryEntry e in d)
                {
                    var target = e.Value is WeakReference w ? w.Target : e.Value is WeakReference[] ws ? ws.FirstOrDefault()?.Target : null;
                    var a = target switch { Assembly x => x, MemberInfo mi => mi.Module.Assembly, _ => null };
                    if (a != null && a != asm && a.FullName == name)
                        return $"MonoMod resolved {e.Key} to another load of {asm.GetName().Name} (the patched copy would call that load's types)";
                }
        }
        return null;
    }

    // ── Restoring the original code ──────────────────────────────────

    private sealed record HarmonyInternals(IDictionary Detours, IDictionary State, IDictionary Originals, object Locker);
    private static HarmonyInternals? _harmonyInternals;

    /// <summary>Harmony keeps the detour per original (PatchTools.detours) and the patch state (HarmonySharedState): both are needed to restore.</summary>
    private static HarmonyInternals? HarmonyInternalsOrBroken()
    {
        if (_harmonyInternals != null) return _harmonyInternals;
        const BindingFlags sf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var h = typeof(Harmony).Assembly;
        object? F(string type, string field)
        {
            var t = h.GetType(type);
            if (t == null) { _instrRestoreBroken = $"{type} not found in 0Harmony"; return null; }
            var v = t.GetField(field, sf)?.GetValue(null);
            if (v == null) _instrRestoreBroken = $"{type}.{field} not found (0Harmony changed)";
            return v;
        }
        if (F("HarmonyLib.PatchTools", "detours") is not IDictionary detours) return null;
        if (F("HarmonyLib.HarmonySharedState", "state") is not IDictionary state) return null;
        if (F("HarmonyLib.HarmonySharedState", "originals") is not IDictionary originals) return null;
        if (F("HarmonyLib.PatchProcessor", "locker") is not { } locker) return null;
        _instrRestoreBroken = null;
        return _harmonyInternals = new HarmonyInternals(detours, state, originals, locker);
    }

    /// <summary>Remove the detour and Harmony's record of the method: its original code runs again. False = link broken.</summary>
    private static bool HarmonyRestore(MethodBase m)
    {
        var hi = HarmonyInternalsOrBroken();
        if (hi == null) return false;
        lock (hi.Locker)
        {
            lock (hi.Detours)
            {
                if (hi.Detours.Contains(m))
                {
                    (hi.Detours[m] as IDisposable)?.Dispose();
                    hi.Detours.Remove(m);
                }
            }
            lock (hi.State) hi.State.Remove(m);
            lock (hi.Originals)
                foreach (var k in hi.Originals.Keys.Cast<object>().Where(k => ReferenceEquals(hi.Originals[k], m)).ToList())
                    hi.Originals.Remove(k);
        }
        return true;
    }

    /// <summary>
    /// Put m's original code back when only bridge owners patch it (true). With a foreign owner, or the restore link
    /// broken, only the bridge's own patches are removed (false: Harmony then leaves m on a regenerated copy).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool InstrRestoreLocked(MethodBase m)
    {
        var owners = Harmony.GetPatchInfo(m)?.Owners ?? (IReadOnlyCollection<string>)[];
        if (owners.All(o => o.StartsWith(InstrOwnerPrefix, StringComparison.Ordinal)) && HarmonyRestore(m)) return true;
        if (m.DeclaringType?.Assembly is { } asm) InstrBindTo(asm);
        foreach (var o in owners.Where(o => o.StartsWith(InstrOwnerPrefix, StringComparison.Ordinal)).ToList())
            new Harmony(o).Unpatch(m, HarmonyPatchType.All, o);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool InstrStillDetoured(MethodBase m)
    {
        if (Harmony.GetPatchInfo(m) is { } info && info.Owners.Count > 0) return true;
        var hi = HarmonyInternalsOrBroken();
        if (hi == null) return Harmony.GetPatchInfo(m) != null;
        lock (hi.Detours) return hi.Detours.Contains(m);
    }

    // ── Sweep: expired sets, reloaded plugins, orphans ───────────────

    private void InstrEnsureTimer()
    {
        lock (InstrLock)
            _instrTimer ??= new Timer(_ => InstrTick(), null, 1000, 1000);
    }

    private void InstrStopTimer()
    {
        lock (InstrLock) { _instrTimer?.Dispose(); _instrTimer = null; }
    }

    /// <summary>
    /// At bridge start: a previous load of the bridge in this HUD process may have left Harmony patches (a bridge reload
    /// mid-measurement). If 0Harmony is already in the default load context, reuse it and sweep right away.
    /// </summary>
    private void InstrStartup()
    {
        try
        {
            if (System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == "0Harmony") && HarmonyLoadError() == null)
            {
                InstrEnsureTimer();
                _instrLastOrphanSweep = DateTime.MinValue;
            }
        }
        catch (Exception ex) { LogError($"[Instrumentation] startup sweep: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void InstrTick()
    {
        if (_harmonyAsm == null) return;
        if (Interlocked.Exchange(ref _instrSweeping, 1) == 1) return;
        try { InstrSweep(); }
        catch (Exception ex) { InstrEvent("sweep_failed", $"{ex.GetType().Name}: {ex.Message}", null); }
        finally { Volatile.Write(ref _instrSweeping, 0); }
    }

    private void InstrSweep()
    {
        List<InstrSet> sets;
        lock (InstrLock) sets = InstrSets.Values.ToList();
        var now = DateTime.UtcNow;
        foreach (var s in sets.Where(s => now > s.ExpiresAt))
            InstrEnd(s, $"expired: still patched {(now - s.StartedAt).TotalSeconds:F0} s after it started (its duration + 15 s)");
        var withPlugins = sets.Where(s => s.EndedBy == null && s.Plugins.Count > 0).ToList();
        if (withPlugins.Count > 0 && LivePluginAssemblies() is { } live)
            foreach (var s in withPlugins)
            {
                List<string> gone;
                lock (InstrLock) gone = s.Plugins.Where(kv => !live.Contains(kv.Key)).Select(kv => kv.Value).Distinct().ToList();
                if (gone.Count > 0) InstrEnd(s, $"cut: {string.Join(", ", gone)} was reloaded or unloaded mid-measurement");
            }
        if (now - _instrLastOrphanSweep >= TimeSpan.FromSeconds(5))
        {
            _instrLastOrphanSweep = now;
            InstrSweepOrphans();
        }
    }

    /// <summary>The assemblies of the plugins loaded right now, or null when the plugin list can't be read this tick.</summary>
    private static HashSet<Assembly>? LivePluginAssemblies()
    {
        try
        {
            var pm = Core.Current?.pluginManager;
            if (pm == null) return null;
            return pm.Plugins.ToList().Select(PluginObject).Where(o => o != null).Select(o => o!.GetType().Assembly).ToHashSet();
        }
        catch { return null; }   // the HUD changed the list while we read it (a reload): next tick
    }

    /// <summary>A PluginWrapper's plugin object (property Plugin, or field _plugin).</summary>
    private static object? PluginObject(object w)
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return w.GetType().GetProperty("Plugin", any)?.GetValue(w) ?? w.GetType().GetField("_plugin", any)?.GetValue(w);
    }

    /// <summary>
    /// Methods Harmony still patches for a bridge owner that no live set holds (orphans: a previous bridge load, or a set
    /// whose end failed), and methods left detoured to a zero-patch copy (residue of Harmony's own Unpatch): restore them.
    /// Patches of other owners are left alone and counted.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InstrSweepOrphans()
    {
        lock (InstrLock)
        {
            var orphans = new List<string>();
            var residue = new List<string>();
            var failed = new List<string>();
            var owners = new HashSet<string>();
            foreach (var m in Harmony.GetAllPatchedMethods().ToList())
            {
                if (InstrOwners.ContainsKey(m)) continue;
                // Tried and failed before: reported once (still counted in leftover), not a new event every 5 s.
                if (InstrUnrestorable.Contains(m)) continue;
                var info = Harmony.GetPatchInfo(m);
                var o = info?.Owners ?? (IReadOnlyCollection<string>)[];
                if (o.Any(x => !x.StartsWith(InstrOwnerPrefix, StringComparison.Ordinal))) continue;   // someone else's
                string? fail = null;
                try
                {
                    InstrRestoreLocked(m);
                    if (InstrStillDetoured(m)) fail = MethodName(m);
                }
                catch (Exception ex) { fail = $"{MethodName(m)} ({ex.GetType().Name})"; }
                if (fail != null) { failed.Add(fail); InstrUnrestorable.Add(m); continue; }
                if (o.Count > 0) { orphans.Add(MethodName(m)); owners.UnionWith(o); }
                else residue.Add(MethodName(m));
            }
            if (orphans.Count > 0)
                InstrEvent("orphaned", $"{orphans.Count} methods were still patched by {string.Join(", ", owners)} with no measurement running: restored ({string.Join(", ", orphans.Take(6))}{(orphans.Count > 6 ? ", ..." : "")})", null,
                    new JObject { ["methods"] = new JArray(orphans.Take(40)), ["owners"] = new JArray(owners) });
            if (residue.Count > 0)
                InstrEvent("residue", $"{residue.Count} methods ran a zero-patch Harmony copy left by an earlier unpatch: original code restored ({string.Join(", ", residue.Take(6))}{(residue.Count > 6 ? ", ..." : "")})", null,
                    new JObject { ["methods"] = new JArray(residue.Take(40)) });
            if (failed.Count > 0)
                InstrEvent("restore_failed", $"{failed.Count} leftover methods could not be restored ({string.Join(", ", failed.Take(6))}): reload their plugin" +
                                             (_instrRestoreBroken != null ? $"; restore link broken: {_instrRestoreBroken}" : ""), null,
                    new JObject { ["methods"] = new JArray(failed.Take(40)) });
        }
    }

    // ── Events, state, cancel ────────────────────────────────────────

    private static void InstrEvent(string kind, string text, InstrSet? s, JObject? extra = null)
    {
        var e = new JObject { ["seq"] = 0, ["at"] = DateTime.UtcNow.ToString("O"), ["kind"] = kind, ["text"] = text };
        if (s != null) { e["set"] = s.Id; e["measurement"] = s.Kind; e["label"] = s.Label; e["who"] = s.Who; }
        if (extra != null) e.Merge(extra);
        lock (InstrEvents)
        {
            e["seq"] = ++_instrEventSeq;
            InstrEvents.Add(e);
            if (InstrEvents.Count > 30) InstrEvents.RemoveAt(0);
        }
    }

    // A cut (plugin reloaded) or a cancel is handled as designed; these mean patches outlived their owner or could not go.
    private static readonly string[] InstrProblemKinds = ["orphaned", "residue", "expired", "restore_failed", "sweep_failed"];

    /// <summary>
    /// Active patch sets, what is still patched without one (leftover: should always be empty), and the cleanup events.
    /// clean = nothing patched outside a running measurement. Cheap when Harmony was never loaded (nothing to list).
    /// </summary>
    private JObject InstrState(bool full)
    {
        var now = DateTime.UtcNow;
        JArray active;
        lock (InstrLock)
            active = new JArray(InstrSets.Values.Select(s => new JObject
            {
                ["id"] = s.Id, ["kind"] = s.Kind, ["label"] = s.Label, ["who"] = s.Who, ["owner"] = s.Owner,
                ["startedAt"] = s.StartedAt.ToString("O"), ["expiresAt"] = s.ExpiresAt.ToString("O"),
                ["secondsLeft"] = Math.Max(0, (int)(s.ExpiresAt - now).TotalSeconds), ["methods"] = s.Methods.Count,
                ["plugins"] = new JArray(s.Plugins.Values.Distinct()),
            }));
        var leftover = _harmonyAsm == null ? new JObject { ["count"] = 0, ["methods"] = new JArray(), ["foreign"] = 0 } : InstrLeftover();
        JObject[] events;
        lock (InstrEvents) events = InstrEvents.ToArray();
        var problems = events.Where(e => InstrProblemKinds.Contains(e["kind"]?.ToString())).ToList();
        return new JObject
        {
            ["ok"] = true,
            ["harmonyLoaded"] = _harmonyAsm != null,
            ["clean"] = leftover["count"]!.Value<int>() == 0,
            ["active"] = active,
            ["leftover"] = leftover,
            ["lastProblem"] = problems.LastOrDefault()?.DeepClone(),
            ["problemSeq"] = problems.LastOrDefault()?["seq"]?.Value<int>() ?? 0,
            ["events"] = new JArray(events.Skip(full ? 0 : Math.Max(0, events.Length - 5)).Select(e => e.DeepClone())),
            ["broken"] = _instrRestoreBroken == null && _instrCacheBroken == null ? null : new JObject { ["restore"] = _instrRestoreBroken, ["bindingCaches"] = _instrCacheBroken },
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JObject InstrLeftover()
    {
        var mine = new List<string>();
        var foreign = 0;
        lock (InstrLock)
            foreach (var m in Harmony.GetAllPatchedMethods().ToList())
            {
                if (InstrOwners.ContainsKey(m)) continue;
                var o = Harmony.GetPatchInfo(m)?.Owners ?? (IReadOnlyCollection<string>)[];
                if (o.Any(x => !x.StartsWith(InstrOwnerPrefix, StringComparison.Ordinal))) { foreign++; continue; }
                mine.Add(o.Count == 0 ? $"{MethodName(m)} (zero-patch copy)" : $"{MethodName(m)} ({string.Join(", ", o)})");
            }
        return new JObject { ["count"] = mine.Count, ["methods"] = new JArray(mine.Take(40)), ["foreign"] = foreign };
    }

    private JObject InstrCancel(JToken? p)
    {
        var id = p?["id"]?.ToString();
        var all = p?["all"]?.Value<bool>() == true;
        if (string.IsNullOrEmpty(id) && !all) return Err("bad_request", "Pass id (a profile or trace id) or all=true.");
        var who = CurrentWho();
        List<InstrSet> hit;
        lock (InstrLock) hit = InstrSets.Values.Where(s => all || s.Id == id).ToList();
        var ended = new JArray(hit.Select(s => new JObject { ["id"] = s.Id, ["kind"] = s.Kind, ["label"] = s.Label, ["report"] = InstrEnd(s, $"cancelled by {who ?? "a client"}") }));
        return new JObject { ["ok"] = true, ["cancelled"] = ended, ["message"] = hit.Count == 0 ? (all ? "Nothing was running." : $"No running measurement {id} (already done?).") : null };
    }
}
