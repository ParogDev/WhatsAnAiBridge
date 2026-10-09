using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Render-pipeline tracing for the render-fidelity work (scaffolding research/render-fidelity.md): how long after the
/// HUD reads the camera and entity positions does its frame reach the screen, how steady are frames, what each plugin's
/// Render costs. For the trace's duration only:
///   - Harmony patches UNPROTECTED code: ClickableTransparentOverlay ImGuiRenderer.Update (the HUD frame: core Tick and
///     plugins) and ImGuiRenderer.Render, Overlay.ReplaceFontIfRequired (Present has returned), every plugin's Render.
///   - Memory reads are timed by wrapping the HUD's IMemoryBackend (see WrapMemoryBackends), filtered to the camera struct
///     and the Render components of the nearest players.
/// Never patches a protected method. ExileCore2 ships IL stubs whose real body only the JIT gets (patching one would
/// replace it with the stub and hang the HUD): TraceIsStub refuses them. Its obfuscated methods fail Harmony's IL copy
/// before anything is applied ("invalid program"). Either way the target is reported by name in patches.refused.
/// Off unless Settings.AllowHudInstrumentation. pipeline.trace {durationMs?, entities?} -> {id}; pipeline.trace_result {id}.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int TraceCap = 400_000;
    private static readonly long[] TrT = new long[TraceCap];
    private static readonly byte[] TrKind = new byte[TraceCap];
    private static readonly long[] TrArg = new long[TraceCap];
    private static int _trIdx;
    private static volatile bool _trOn;
    private static (long start, long end, bool isEntity, int id)[] _trRanges = [];
    private static readonly Dictionary<MethodBase, int> TrPluginIdx = new();
    private static readonly List<string> TrPluginNames = new();

    // Camera/entity kinds come in pairs (entity = camera + 1): MatchRead relies on it.
    private const byte KUpdateBegin = 1, KUpdateEnd = 2, KRenderBegin = 3, KRenderEnd = 4, KPresentEnd = 6,
        KReadCamera = 7, KReadEntity = 8, KPluginBegin = 9, KPluginEnd = 10, KFetchCamera = 11, KFetchEntity = 12, KCacheCycle = 13, KTickBegin = 14, KTickEnd = 15;

    private static readonly Dictionary<string, JObject> TraceJobs = new();
    private Harmony? _harmony;

    private string? ProcessTraceMethod(string method, JToken? p) => method switch
    {
        "pipeline.trace" => SafeMemory(() => HarmonyLoadError() is { } e ? Err("harmony_unavailable", e) : TraceStart(p)),
        "pipeline.trace_result" => SafeMemory(() => { lock (TraceJobs) return TraceJobs.TryGetValue(p?["id"]?.ToString() ?? "", out var j) ? j : Err("unknown_id", "No such trace."); }),
        _ => null,
    };

    private static System.Reflection.Assembly? _harmonyAsm;
    private static string? _harmonyError;

    /// <summary>
    /// 0Harmony cannot load into the HUD's collectible plugin load context ("Operation is not supported": it emits code).
    /// Load it once into the default context, from bytes so the file in the plugin's output folder is never locked, and
    /// hand it to the plugin context through Default.Resolving. Must run before any method touching Harmony is JIT-compiled.
    /// </summary>
    private string? HarmonyLoadError()
    {
        if (_harmonyAsm != null) return null;
        if (_harmonyError != null) return _harmonyError;
        var dir = System.IO.Path.GetFileName(DirectoryFullName.TrimEnd('\\', '/'));
        var candidates = new[] { "Temp", "Compiled" }
            .Select(sub => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", sub, dir, "0Harmony.dll")).ToList();
        var path = candidates.FirstOrDefault(System.IO.File.Exists);
        if (path == null)
            return _harmonyError = $"0Harmony.dll not found next to the compiled bridge (looked in {string.Join(", ", candidates.Select(c => System.IO.Path.GetRelativePath(AppDomain.CurrentDomain.BaseDirectory, c)))}). " +
                                   "The .csproj copies it from the Lib.Harmony package on build: restart the HUD to recompile.";
        try
        {
            using var s = System.IO.File.OpenRead(path);
            var asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(s);
            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) => name.Name == "0Harmony" ? asm : null;
            _harmonyAsm = asm;
            return null;
        }
        catch (Exception ex) { return _harmonyError = $"Loading 0Harmony into the default load context failed: {ex.GetType().Name}: {ex.Message}"; }
    }

    private static void Rec(byte kind, long arg)
    {
        if (!_trOn) return;
        var i = Interlocked.Increment(ref _trIdx) - 1;
        if (i >= TraceCap) return;
        TrT[i] = Stopwatch.GetTimestamp();
        TrKind[i] = kind;
        TrArg[i] = arg;
    }

    // ── Patch bodies (static, allocation-free except __args) ──
    private static void PreUpdate() => Rec(KUpdateBegin, 0);
    private static void PostUpdate() => Rec(KUpdateEnd, 0);
    private static void PreRender() => Rec(KRenderBegin, 0);
    private static void PostRender() => Rec(KRenderEnd, 0);
    private static void PostPresent() => Rec(KPresentEnd, 0);
    // Allocation per plugin: bytes this thread allocated between entry and exit (GC.GetAllocatedBytesForCurrentThread
    // is a cheap counter read). Garbage per frame is what drives the HUD's GC pauses (frame-time spikes).
    private static readonly long[] TrRenderAlloc = new long[512], TrTickAlloc = new long[512];
    private static void PrePlugin(MethodBase __originalMethod, out long __state)
    {
        __state = GC.GetAllocatedBytesForCurrentThread();
        if (_trOn && TrPluginIdx.TryGetValue(__originalMethod, out var i)) Rec(KPluginBegin, i);
    }
    private static void PostPlugin(MethodBase __originalMethod, long __state)
    {
        if (!_trOn || !TrPluginIdx.TryGetValue(__originalMethod, out var i)) return;
        Rec(KPluginEnd, i);
        if (i < TrRenderAlloc.Length) Interlocked.Add(ref TrRenderAlloc[i], GC.GetAllocatedBytesForCurrentThread() - __state);
    }
    private static readonly Dictionary<MethodBase, int> TrTickIdx = new();
    private static void PreTick(MethodBase __originalMethod, out long __state)
    {
        __state = GC.GetAllocatedBytesForCurrentThread();
        if (_trOn && TrTickIdx.TryGetValue(__originalMethod, out var i)) Rec(KTickBegin, i);
    }
    private static void PostTick(MethodBase __originalMethod, long __state)
    {
        if (!_trOn || !TrTickIdx.TryGetValue(__originalMethod, out var i)) return;
        Rec(KTickEnd, i);
        if (i < TrTickAlloc.Length) Interlocked.Add(ref TrTickAlloc[i], GC.GetAllocatedBytesForCurrentThread() - __state);
    }

    // Memory reads. Both HUDs read through an IMemoryBackend: a caching PagedMemoryBackend (pages cached per frame, last
    // frame's pages prefetched at NotifyFrame) over a leaf that reads the game. Its methods are obfuscated on PoE2 (Harmony
    // cannot copy them), so reads are timed by wrapping instead of patching: Memory.CustomBackend - the HUD's own hook
    // (its snapshot viewer uses it) - gets a wrapper around the paged backend (when a plugin consumes a value), and the
    // paged backend's leaf field gets one too (when that data was taken from the game). Both are put back when the trace
    // ends. A read matches a watched range on overlap (the leaf reads whole pages).
    private static long _trLogicalAll, _trFetchAll, _trFetchBytes;

    private sealed class TimingBackend(IMemoryBackend inner, bool fetch) : IMemoryBackend
    {
        public readonly IMemoryBackend Inner = inner;
        public bool TryReadMemory(IntPtr address, Span<byte> target)
        {
            if (fetch)
            {
                var ok = Inner.TryReadMemory(address, target);   // data is the game's as of the end of the read
                if (_trOn) Interlocked.Add(ref _trFetchBytes, target.Length);
                MatchRead(address.ToInt64(), target.Length, KFetchCamera, ref _trFetchAll);
                return ok;
            }
            // Record after the inner read: a cache miss fetches inside it, and that fetch is the data this read returns.
            // (Recording first credited misses to the previous frame's fetch: a false "camera one frame old".)
            var read = Inner.TryReadMemory(address, target);
            MatchRead(address.ToInt64(), target.Length, KReadCamera, ref _trLogicalAll);
            return read;
        }
        public void NotifyFrame() { if (!fetch) Rec(KCacheCycle, 0); Inner.NotifyFrame(); }
        public void Dispose() { }   // the HUD owns the wrapped backend
    }

    private static void MatchRead(long addr, int len, byte cameraKind, ref long counter)
    {
        if (!_trOn) return;
        Interlocked.Increment(ref counter);
        long end = addr + Math.Max(1, len);
        foreach (var (start, rEnd, isEntity, id) in _trRanges)
            if (addr < rEnd && end > start) { Rec((byte)(cameraKind + (isEntity ? 1 : 0)), id); return; }
    }

    /// <summary>Wraps the memory backends for the trace; returns the undo, or null with the reason in <paramref name="why"/>.</summary>
    private Action? WrapMemoryBackends(JObject report, out string? why)
    {
        why = null;
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var memory = GameController.Memory;
        // While wrapped, Memory.DisableCaching() (a type test for PagedMemoryBackend) does nothing: only ground-label and
        // file-table reads use it, never camera or entities, and it lasts the trace (seconds).
        if (memory.CustomBackend != null) { why = "the HUD is showing a memory snapshot (Memory.CustomBackend is set): reads not timed"; return null; }
        var paged = memory.GetType().GetField("_backend", inst)?.GetValue(memory) as IMemoryBackend;
        if (paged == null) { why = $"{memory.GetType().Name}._backend not found (HUD build changed): reads not timed"; return null; }
        var leafField = paged.GetType().GetFields(inst).FirstOrDefault(f => f.FieldType == typeof(IMemoryBackend));
        var leaf = leafField?.GetValue(paged) as IMemoryBackend;
        if (leafField != null && leaf != null) leafField.SetValue(paged, new TimingBackend(leaf, fetch: true));
        else ((JArray)report["refused"]!).Add($"{paged.GetType().Name}: no inner IMemoryBackend field - fetches from the game not timed, only cached reads");
        var outer = new TimingBackend(paged, fetch: false);
        memory.CustomBackend = outer;
        ((JArray)report["patched"]!).Add($"Memory.CustomBackend = timing wrapper over {paged.GetType().Name}" + (leaf != null ? $" (and over its {leaf.GetType().Name})" : ""));
        return () =>
        {
            if (ReferenceEquals(memory.CustomBackend, outer)) memory.CustomBackend = null;
            if (leafField != null && leaf != null && leafField.GetValue(paged) is TimingBackend) leafField.SetValue(paged, leaf);
        };
    }

    private JObject TraceStart(JToken? p)
    {
        if (!Settings.AllowHudInstrumentation.Value)
            return Err("instrumentation_disabled", "Enable 'Allow HUD Instrumentation' in the bridge settings (Dev Loop) to trace the render pipeline.");
        if (_trOn) return Err("busy", "A trace is already running.");
        var duration = Math.Clamp(p?["durationMs"]?.Value<int>() ?? 3000, 500, 20_000);
        var maxEntities = Math.Clamp(p?["entities"]?.Value<int>() ?? 8, 0, 32);

        // What to watch: the camera struct and the Render components of the nearest players (moving targets).
        var ranges = new List<(long, long, bool, int)>();
        var entityNames = new List<string>();
        try
        {
            var cam = GameController.IngameState.Camera.Address;
            if (cam != 0) ranges.Add((cam, cam + 0x400, false, 0));
        }
        catch { }
        try
        {
            var players = GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Player, out var list) ? list : [];
            foreach (var e in players.OrderBy(e => e.DistancePlayer).Take(maxEntities))
            {
                var r = e.GetComponent<Render>();
                if (r == null || r.Address == 0) continue;
                ranges.Add((r.Address, r.Address + 0x200, true, entityNames.Count));
                entityNames.Add(e.RenderName ?? e.Path ?? "?");
            }
        }
        catch { }
        _trRanges = ranges.ToArray();

        var report = new JObject { ["patched"] = new JArray(), ["refused"] = new JArray() };
        _harmony ??= new Harmony("whatsanaibridge.pipeline-trace");
        TrPluginIdx.Clear(); TrTickIdx.Clear(); TrPluginNames.Clear();
        var asms = AppDomain.CurrentDomain.GetAssemblies();
        Type? T(string asmName, string typeName) => asms.FirstOrDefault(a => a.GetName().Name == asmName)?.GetType(typeName);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var self = typeof(WhatsAnAiBridge);
        HarmonyMethod H(string name) => new(self.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        void Patch(MethodBase? m, string label, string? pre, string? post)
        {
            if (m == null) { ((JArray)report["refused"]!).Add($"{label}: not found in this HUD build"); return; }
            if (TraceIsStub(m)) { ((JArray)report["refused"]!).Add($"{label}: protected (IL stub) - never patched"); return; }
            try
            {
                _harmony.Patch(m, prefix: pre == null ? null : H(pre), postfix: post == null ? null : H(post));
                ((JArray)report["patched"]!).Add(label);
            }
            catch (Exception ex) { ((JArray)report["refused"]!).Add($"{label}: patch failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        var renderer = T("ClickableTransparentOverlay", "ClickableTransparentOverlay.ImGuiRenderer");
        Patch(renderer?.GetMethods(all).FirstOrDefault(m => m.Name == "Update"), "ImGuiRenderer.Update (HUD frame)", nameof(PreUpdate), nameof(PostUpdate));
        Patch(renderer?.GetMethods(all).FirstOrDefault(m => m.Name == "Render"), "ImGuiRenderer.Render (draw data)", nameof(PreRender), nameof(PostRender));
        // Present itself (Vortice IDXGISwapChain.Present) is a calli through the COM vtable, which Harmony cannot copy, and
        // the PoE2 HUD overrides Overlay.PostFrame with a protected stub. Overlay.RunFrameLoop calls ImGuiRenderer.Update ->
        // Render -> Present -> ReplaceFontIfRequired -> PostFrame: Present starts when Render returns and has returned when
        // ReplaceFontIfRequired is entered (also called once before the loop starts; the analysis ignores it).
        var overlay = T("ClickableTransparentOverlay", "ClickableTransparentOverlay.Overlay");
        Patch(overlay?.GetMethod("ReplaceFontIfRequired", all), "Overlay.ReplaceFontIfRequired (Present returned)", nameof(PostPresent), null);

        // Plugin Render methods (plugins are compiled from source: never protected, but checked anyway).
        foreach (var a in asms)
        {
            Type[] types;
            try { types = a.GetTypes(); } catch { continue; }
            foreach (var t in types)
            {
                if (t.IsAbstract || t.BaseType is not { IsGenericType: true } bt || !bt.Name.StartsWith("BaseSettingsPlugin", StringComparison.Ordinal)) continue;
                var render = t.GetMethod("Render", all, null, Type.EmptyTypes, null);
                if (render == null) continue;
                TrPluginIdx[render] = TrPluginNames.Count;
                TrPluginNames.Add(t.Name);
                Patch(render, $"{t.Name}.Render", nameof(PrePlugin), nameof(PostPlugin));
                // Tick too: HealthBars, for one, positions its bars there, not in Render.
                if (t.GetMethod("Tick", all, null, Type.EmptyTypes, null) is { } tick)
                {
                    TrTickIdx[tick] = TrPluginNames.Count - 1;
                    Patch(tick, $"{t.Name}.Tick", nameof(PreTick), nameof(PostTick));
                }
            }
        }
        var unwrap = WrapMemoryBackends(report, out var whyNoReads);
        if (whyNoReads != null) ((JArray)report["refused"]!).Add(whyNoReads);

        var id = Guid.NewGuid().ToString("N")[..10];
        var job = new JObject { ["id"] = id, ["status"] = "running", ["durationMs"] = duration, ["watch"] = new JObject { ["camera"] = ranges.Any(r => !r.Item3), ["entities"] = new JArray(entityNames) }, ["patches"] = report };
        lock (TraceJobs)
        {
            if (TraceJobs.Count >= 10) TraceJobs.Remove(TraceJobs.Keys.First());
            TraceJobs[id] = job;
        }
        _trIdx = 0;
        Array.Clear(TrRenderAlloc); Array.Clear(TrTickAlloc);
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        var gcPause0 = GC.GetTotalPauseDuration(); var gcAlloc0 = GC.GetTotalAllocatedBytes();
        _trLogicalAll = 0; _trFetchAll = 0; _trFetchBytes = 0;
        _trOn = true;
        var started = Stopwatch.GetTimestamp();
        System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(duration);
            _trOn = false;
            try { _harmony.UnpatchAll(_harmony.Id); } catch { }
            try { unwrap?.Invoke(); } catch { }
            var result = AnalyzeTrace(Math.Min(_trIdx, TraceCap), started);
            var secs = duration / 1000.0;
            var frames = Math.Max(1, result["frames"]?.Value<int>() ?? 1);
            result["gc"] = new JObject
            {
                ["gen0"] = GC.CollectionCount(0) - gc0, ["gen1"] = GC.CollectionCount(1) - gc1, ["gen2"] = GC.CollectionCount(2) - gc2,
                ["pauseMsTotal"] = Math.Round((GC.GetTotalPauseDuration() - gcPause0).TotalMilliseconds, 1),
                ["allocMBPerSecond"] = Math.Round((GC.GetTotalAllocatedBytes() - gcAlloc0) / 1048576.0 / secs, 1),
                // Bytes the HUD fetched from the game (leaf backend). If allocation tracks this, page buffers are not pooled.
                ["fetchedMBPerSecond"] = Math.Round(Interlocked.Read(ref _trFetchBytes) / 1048576.0 / secs, 1),
            };
            JObject Alloc(long[] a) => new(Enumerable.Range(0, Math.Min(a.Length, TrPluginNames.Count)).Where(k => a[k] > 0).OrderByDescending(k => a[k]).Take(12)
                .Select(k => new JProperty(TrPluginNames[k], Math.Round(a[k] / 1024.0 / frames, 1))));
            result["pluginAllocKBPerFrame"] = new JObject { ["render"] = Alloc(TrRenderAlloc), ["tick"] = Alloc(TrTickAlloc) };
            result["id"] = id; result["status"] = "done"; result["durationMs"] = duration; result["patches"] = report; result["watch"] = job["watch"];
            lock (TraceJobs) TraceJobs[id] = result;
        });
        return job;
    }

    /// <summary>A protector stub (tiny body, backward branch, no member reference): must never be patched.</summary>
    private static bool TraceIsStub(MethodBase m)
    {
        byte[]? il;
        try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { return false; }
        if (il == null) return false;   // extern / P/Invoke: patchable via its managed wrapper only - Harmony refuses itself
        return il.Length <= 24 && il.Length >= 3 && il.Any(b => b is 0x2B or 0x2C or 0x2D) &&
               !il.Any(b => b is 0x28 or 0x6F or 0x7B or 0x7C or 0x7D or 0x73) &&
               il.Select((b, i) => (b, i)).Any(x => x.b is 0x2B or 0x2C or 0x2D && x.i + 1 < il.Length && (sbyte)il[x.i + 1] < 0);
    }

    private sealed class TraceFrame
    {
        public long Begin, UpdEnd, RBegin, REnd, PEnd;
        public long CamRead, EntRead;          // last consumed read of the camera / a watched entity in this frame
        public long CamData, EntData;          // when the data those reads returned was fetched from the game (0 = before the trace)
        public long Cycle;                     // the cache cycle (NotifyFrame) in this frame
        public int CamReads, EntReads, Fetches, Cycles;
        public double PluginMs;   // time inside plugin Tick + Render this frame (summed; parallel ticks can overlap)
    }

    private JObject AnalyzeTrace(int n, long started)
    {
        double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var frames = new List<TraceFrame>();
        var plugin = new Dictionary<int, List<double>>();
        var pluginOpen = new Dictionary<int, long>();
        var tick = new Dictionary<int, List<double>>();
        var tickOpen = new Dictionary<int, long>();
        var cur = new TraceFrame();
        bool inFrame = false;
        long lastCamFetch = 0;
        var lastEntFetch = new Dictionary<long, long>();
        var order = Enumerable.Range(0, n).OrderBy(i => TrT[i]).ToArray();
        var counts = new int[16];
        foreach (var i in order) counts[TrKind[i] & 15]++;
        foreach (var i in order)
        {
            var t = TrT[i];
            switch (TrKind[i])
            {
                case KUpdateBegin:
                    if (inFrame) frames.Add(cur);
                    cur = new TraceFrame { Begin = t }; inFrame = true; break;
                case KUpdateEnd: cur.UpdEnd = t; break;
                case KRenderBegin: cur.RBegin = t; break;
                case KRenderEnd: cur.REnd = t; break;   // RunFrameLoop calls Present right after Render
                case KPresentEnd: cur.PEnd = t; break;
                case KFetchCamera: lastCamFetch = t; cur.Fetches++; break;
                case KFetchEntity: lastEntFetch[TrArg[i]] = t; cur.Fetches++; break;
                case KReadCamera: cur.CamRead = t; cur.CamData = lastCamFetch; cur.CamReads++; break;
                case KReadEntity: cur.EntRead = t; cur.EntData = lastEntFetch.GetValueOrDefault(TrArg[i]); cur.EntReads++; break;
                case KCacheCycle: if (cur.Cycle == 0) cur.Cycle = t; cur.Cycles++; break;
                case KTickBegin: tickOpen[(int)TrArg[i]] = t; break;
                case KTickEnd:
                    if (tickOpen.Remove((int)TrArg[i], out var tb)) { (tick.TryGetValue((int)TrArg[i], out var tl) ? tl : tick[(int)TrArg[i]] = new()).Add(ms(t - tb)); cur.PluginMs += ms(t - tb); }
                    break;
                case KPluginBegin: pluginOpen[(int)TrArg[i]] = t; break;
                case KPluginEnd:
                    if (pluginOpen.Remove((int)TrArg[i], out var b)) { (plugin.TryGetValue((int)TrArg[i], out var l) ? l : plugin[(int)TrArg[i]] = new()).Add(ms(t - b)); cur.PluginMs += ms(t - b); }
                    break;
            }
        }
        if (inFrame && cur.PEnd != 0) frames.Add(cur);
        static JObject Stats(IEnumerable<double> xs)
        {
            var a = xs.Where(x => x >= 0).OrderBy(x => x).ToArray();
            if (a.Length == 0) return new JObject { ["n"] = 0 };
            double avg = a.Average(), sd = Math.Sqrt(a.Select(x => (x - avg) * (x - avg)).Average());
            return new JObject { ["n"] = a.Length, ["avg"] = Math.Round(avg, 3), ["p50"] = Math.Round(a[a.Length / 2], 3), ["p95"] = Math.Round(a[(int)(a.Length * 0.95)], 3), ["max"] = Math.Round(a[^1], 3), ["sd"] = Math.Round(sd, 3) };
        }
        static JObject SignedStats(IEnumerable<double> xs)
        {
            var a = xs.OrderBy(x => x).ToArray();
            if (a.Length == 0) return new JObject { ["n"] = 0 };
            return new JObject { ["n"] = a.Length, ["avg"] = Math.Round(a.Average(), 3), ["p5"] = Math.Round(a[(int)(a.Length * 0.05)], 3), ["p50"] = Math.Round(a[a.Length / 2], 3), ["p95"] = Math.Round(a[(int)(a.Length * 0.95)], 3) };
        }
        var full = frames.Where(f => f.PEnd > f.Begin).ToList();
        double PerFrame(Func<TraceFrame, int> f) => full.Count == 0 ? 0 : Math.Round(full.Average(x => (double)f(x)), 2);
        return new JObject
        {
            ["events"] = n, ["eventsDropped"] = Math.Max(0, _trIdx - TraceCap), ["frames"] = full.Count,
            // Patched but never hit = that link of the pipeline is not where we think it is (fail at the broken link).
            ["calls"] = new JObject
            {
                ["frameStart"] = counts[KUpdateBegin], ["draw"] = counts[KRenderBegin], ["presentReturned"] = counts[KPresentEnd],
                ["pluginRenders"] = counts[KPluginEnd], ["cacheCycles"] = counts[KCacheCycle],
                ["readsAll"] = Interlocked.Read(ref _trLogicalAll), ["fetchesAll"] = Interlocked.Read(ref _trFetchAll),
                ["cameraReads"] = counts[KReadCamera], ["cameraFetches"] = counts[KFetchCamera],
                ["entityReads"] = counts[KReadEntity], ["entityFetches"] = counts[KFetchEntity],
            },
            ["hudFps"] = full.Count > 1 ? Math.Round(1000.0 * (full.Count - 1) / ms(full[^1].Begin - full[0].Begin), 1) : null,
            ["frameIntervalMs"] = Stats(full.Zip(full.Skip(1), (a, b) => ms(b.Begin - a.Begin))),
            ["updateMs"] = Stats(full.Where(f => f.UpdEnd > 0).Select(f => ms(f.UpdEnd - f.Begin))),
            // Split of the frame work: plugins (Tick + Render) vs the rest (the HUD core: game controller, entity parsing,
            // its own UI). Approximate when plugin Ticks run on worker threads in parallel.
            ["pluginsMs"] = Stats(full.Where(f => f.UpdEnd > 0).Select(f => f.PluginMs)),
            ["coreMs"] = Stats(full.Where(f => f.UpdEnd > 0).Select(f => Math.Max(0, ms(f.UpdEnd - f.Begin) - f.PluginMs))),
            ["drawMs"] = Stats(full.Where(f => f.REnd > 0).Select(f => ms(f.REnd - f.RBegin))),
            ["presentCallMs"] = Stats(full.Where(f => f.REnd > 0).Select(f => ms(f.PEnd - f.REnd))),
            // Read = when the HUD used the value; data = when that value was taken from the game. Data age at Present is
            // how stale what the user sees is the moment it reaches the screen (before the compositor and the display).
            ["cameraReadToPresentMs"] = Stats(full.Where(f => f.CamRead > 0).Select(f => ms(f.PEnd - f.CamRead))),
            ["entityReadToPresentMs"] = Stats(full.Where(f => f.EntRead > 0).Select(f => ms(f.PEnd - f.EntRead))),
            ["cameraDataAgeAtPresentMs"] = Stats(full.Where(f => f.CamData > 0).Select(f => ms(f.PEnd - f.CamData))),
            ["entityDataAgeAtPresentMs"] = Stats(full.Where(f => f.EntData > 0).Select(f => ms(f.PEnd - f.EntData))),
            // Camera and entity data from different moments: a box drawn with this frame's camera around last frame's position.
            ["cameraEntityDataSkewMs"] = Stats(full.Where(f => f.CamData > 0 && f.EntData > 0).Select(f => Math.Abs(ms(f.EntData - f.CamData)))),
            ["dataFetchedBeforeFrameStart"] = new JObject
            {
                ["camera"] = full.Count(f => f.CamData > 0 && f.CamData < f.Begin), ["entity"] = full.Count(f => f.EntData > 0 && f.EntData < f.Begin),
            },
            // Where in the frame things happen (negative = during the previous frame).
            ["timelineMs"] = new JObject
            {
                ["cacheCycleAfterFrameStart"] = Stats(full.Where(f => f.Cycle > 0).Select(f => ms(f.Cycle - f.Begin))),
                ["cameraDataFetchedAfterFrameStart"] = SignedStats(full.Where(f => f.CamData > 0).Select(f => ms(f.CamData - f.Begin))),
                ["entityDataFetchedAfterFrameStart"] = SignedStats(full.Where(f => f.EntData > 0).Select(f => ms(f.EntData - f.Begin))),
                ["cameraReadAfterFrameStart"] = Stats(full.Where(f => f.CamRead > 0).Select(f => ms(f.CamRead - f.Begin))),
            },
            ["perFrame"] = new JObject
            {
                ["cameraReads"] = PerFrame(f => f.CamReads), ["entityReads"] = PerFrame(f => f.EntReads),
                ["watchedFetches"] = PerFrame(f => f.Fetches), ["cacheCycles"] = PerFrame(f => f.Cycles),
            },
            ["pluginTickMs"] = new JObject(tick.OrderByDescending(kv => kv.Value.Average()).Take(12)
                .Select(kv => new JProperty(kv.Key < TrPluginNames.Count ? TrPluginNames[kv.Key] : $"#{kv.Key}", Stats(kv.Value)))),
            ["pluginRenderMs"] = new JObject(plugin.OrderByDescending(kv => kv.Value.Average()).Take(12)
                .Select(kv => new JProperty(kv.Key < TrPluginNames.Count ? TrPluginNames[kv.Key] : $"#{kv.Key}", Stats(kv.Value)))),
        };
    }
}
