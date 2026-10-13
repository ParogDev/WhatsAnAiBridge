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
/// Off unless Settings.AllowHudInstrumentation. pipeline.trace {durationMs?, entities?, series?} -> {id}; pipeline.trace_result {id}.
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
        // A previous load of the bridge in this HUD process already put 0Harmony there: reuse that copy. A second copy
        // would have its own detour table and could never undo the patches the first one left (Instrumentation.cs).
        if (System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == "0Harmony") is { } loaded)
        {
            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) => name.Name == "0Harmony" ? loaded : null;
            _harmonyAsm = loaded;
            return null;
        }
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
    // The GC's cumulative pause at frame start (100 ns ticks): the difference to the next frame is the pause it took.
    private static void PreUpdate() => Rec(KUpdateBegin, GC.GetTotalPauseDuration().Ticks);
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
        var memory = GameController.Memory;
        // While wrapped, Memory.DisableCaching() (a type test for PagedMemoryBackend) does nothing: only ground-label and
        // file-table reads use it, never camera or entities, and it lasts the trace (seconds).
        if (memory.CustomBackend != null) { why = "the HUD is showing a memory snapshot (Memory.CustomBackend is set): reads not timed"; return null; }
        if (HudLinks.MemoryBackend.FieldOn(memory.GetType()) is not { } backendField) { why = $"{HudLinks.MemoryBackend.Broken(memory.GetType())} (HUD build changed): reads not timed"; return null; }
        if (backendField.GetValue(memory) is not IMemoryBackend paged) { why = $"{memory.GetType().Name}._backend is null: reads not timed"; return null; }
        var leafField = HudLinks.PagedInner.FieldOn(paged.GetType());
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
        if (_trOn || InstrAnyActive("trace")) return Err("busy", "A trace is already running.");
        var duration = Math.Clamp(p?["durationMs"]?.Value<int>() ?? 3000, 500, 20_000);
        var maxEntities = Math.Clamp(p?["entities"]?.Value<int>() ?? 8, 0, 32);
        _trSeries = p?["series"]?.Value<bool>() == true;

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
        var id = Guid.NewGuid().ToString("N")[..10];
        // Every patch belongs to this set and is removed (original code restored) when it ends, whatever ends it (Instrumentation.cs).
        var set = InstrBegin(id, "trace", "HUD frame timing", duration, CurrentWho());
        Action? unwrap = null;
        try
        {
        TrPluginIdx.Clear(); TrTickIdx.Clear(); TrPluginNames.Clear();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var self = typeof(WhatsAnAiBridge);
        HarmonyMethod H(string name) => new(self.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        void Patch(MethodBase? m, string label, string? pre, string? post, string? plugin = null)
        {
            if (m == null) { ((JArray)report["refused"]!).Add($"{label}: not found in this HUD build"); return; }
            if (TraceIsStub(m)) { ((JArray)report["refused"]!).Add($"{label}: protected (IL stub) - never patched"); return; }
            if (InstrPatch(set, m, pre == null ? null : H(pre), post == null ? null : H(post), plugin) is { } why) ((JArray)report["refused"]!).Add($"{label}: {why}");
            else ((JArray)report["patched"]!).Add(label);
        }

        // The overlay's methods are declared links (HudLinks.cs), preflighted at load; a broken one is refused naming the link.
        void PatchLink(HudLink link, string label, string? pre, string? post)
        {
            if (link.Method is { } m) Patch(m, label, pre, post);
            else ((JArray)report["refused"]!).Add($"{label}: {link.Broken(link.DeclaredType)}");
        }
        PatchLink(HudLinks.RendererUpdate, "ImGuiRenderer.Update (HUD frame)", nameof(PreUpdate), nameof(PostUpdate));
        PatchLink(HudLinks.RendererRender, "ImGuiRenderer.Render (draw data)", nameof(PreRender), nameof(PostRender));
        // Present itself (Vortice IDXGISwapChain.Present) is a calli through the COM vtable, which Harmony cannot copy, and
        // the PoE2 HUD overrides Overlay.PostFrame with a protected stub. Overlay.RunFrameLoop calls ImGuiRenderer.Update ->
        // Render -> Present -> ReplaceFontIfRequired -> PostFrame: Present starts when Render returns and has returned when
        // ReplaceFontIfRequired is entered (also called once before the loop starts; the analysis ignores it).
        PatchLink(HudLinks.OverlayReplaceFont,"Overlay.ReplaceFontIfRequired (Present returned)", nameof(PostPresent), null);

        // Plugin Render and Tick methods (plugins are compiled from source: never protected, but checked anyway). Only the
        // loaded plugins: an earlier load of a reloaded plugin is still in the process, and patching it is pointless (it no
        // longer runs) and used to leave MonoMod's by-name type cache pointing at it (Instrumentation.cs).
        List<(Type type, string name)> pluginTypes;
        try
        {
            pluginTypes = (Core.Current?.pluginManager?.Plugins.ToList() ?? throw new InvalidOperationException("Core.Current.pluginManager is null"))
                .Select(w => (plugin: PluginObject(w), name: w.Name)).Where(x => x.plugin != null)
                .Select(x => (x.plugin!.GetType(), x.name)).DistinctBy(x => x.Item1).ToList();
        }
        catch (Exception ex)
        {
            pluginTypes = [];
            ((JArray)report["refused"]!).Add($"plugin Render/Tick: the HUD's plugin list could not be read ({ex.GetType().Name}: {ex.Message}) - not instrumented");
        }
        foreach (var (t, pluginName) in pluginTypes)
        {
            if (t.IsAbstract) continue;
            var render = t.GetMethod("Render", all, null, Type.EmptyTypes, null);
            if (render == null) continue;
            TrPluginIdx[render] = TrPluginNames.Count;
            TrPluginNames.Add(t.Name);
            Patch(render, $"{t.Name}.Render", nameof(PrePlugin), nameof(PostPlugin), pluginName);
            // Tick too: HealthBars, for one, positions its bars there, not in Render.
            if (t.GetMethod("Tick", all, null, Type.EmptyTypes, null) is { } tick)
            {
                TrTickIdx[tick] = TrPluginNames.Count - 1;
                Patch(tick, $"{t.Name}.Tick", nameof(PreTick), nameof(PostTick), pluginName);
            }
            // Each plugin is its own assembly: a binding to another load of one is refused per assembly (nothing of it
            // measured). Checked right after patching it: InstrBindTo clears MonoMod's caches when patching moves to the
            // next assembly, so a check after the loop would only see the last plugin's resolutions.
            var asm = t.Assembly;
            if (InstrBindingCheck(asm) is { } bad)
            {
                List<MethodBase> ofAsm;
                lock (InstrLock) ofAsm = set.Methods.Where(m => m.DeclaringType?.Assembly == asm).ToList();
                foreach (var m in ofAsm) InstrRelease(set, m);
                ((JArray)report["refused"]!).Add($"{asm.GetName().Name}: patches removed again: {bad}");
            }
        }
        unwrap = WrapMemoryBackends(report, out var whyNoReads);
        if (whyNoReads != null) ((JArray)report["refused"]!).Add(whyNoReads);

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
        MeasureBegin("HUD frame timing", duration);   // guide card + foreground sampling (MeasureFocus.cs)
        InstrArm(set, duration);
        _trOn = true;
        var started = Stopwatch.GetTimestamp();
        System.Threading.Tasks.Task.Run(async () =>
        {
            JObject? foreground = null, ended = null;
            var ranMs = (double)duration;
            try { await System.Threading.Tasks.Task.Delay(duration, set.Cts.Token); }
            catch (OperationCanceledException) { }
            finally
            {
                // Whatever ended it (done, instrument.cancel, a plugin reload, expiry): hooks off, patches out, backends back.
                _trOn = false;
                ranMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                try { foreground = MeasureEnd(); } catch { }
                ended = InstrEnd(set, "done");
                try { unwrap?.Invoke(); } catch { }
            }
            // A copy: the running job (which pipeline.trace_result may be serializing right now) holds report itself.
            var patches = (JObject)report.DeepClone();
            patches["unpatch"] = ended;
            try
            {
            var endedBy = ended["endedBy"]?.ToString() ?? "done";
            var result = AnalyzeTrace(Math.Min(_trIdx, TraceCap), started);
            if (endedBy != "done") { result["endedBy"] = endedBy; result["note"] = $"The trace ended early ({endedBy}): the numbers cover only the time it ran."; }
            result["foreground"] = foreground;
            // Rates over the time it actually ran: a cancelled or cut trace is shorter than its duration.
            var secs = Math.Max(0.001, (endedBy == "done" ? duration : ranMs) / 1000.0);
            var frames = Math.Max(1, result["frames"]?.Value<int>() ?? 1);
            result["gc"] = new JObject
            {
                ["gen0"] = GC.CollectionCount(0) - gc0, ["gen1"] = GC.CollectionCount(1) - gc1, ["gen2"] = GC.CollectionCount(2) - gc2,
                ["pauseMsTotal"] = Math.Round((GC.GetTotalPauseDuration() - gcPause0).TotalMilliseconds, 1),
                ["allocMBPerSecond"] = Math.Round((GC.GetTotalAllocatedBytes() - gcAlloc0) / 1048576.0 / secs, 1),
                // Bytes the HUD fetched from the game (leaf backend). If allocation tracks this, page buffers are not pooled.
                ["fetchedMBPerSecond"] = Math.Round(Interlocked.Read(ref _trFetchBytes) / 1048576.0 / secs, 1),
                ["fetchedPagesPerFrame"] = Math.Round(Interlocked.Read(ref _trFetchBytes) / 4096.0 / frames),
                // How many arrays per size ArrayPool<byte>.Shared keeps (the page cache rents from it): fewer than the pages
                // cycled per frame means most become garbage. Set by DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_* at startup.
                ["sharedArrayPool"] = SharedArrayPoolLimits(),
            };
            JObject Alloc(long[] a) => new(Enumerable.Range(0, Math.Min(a.Length, TrPluginNames.Count)).Where(k => a[k] > 0).OrderByDescending(k => a[k]).Take(12)
                .Select(k => new JProperty(TrPluginNames[k], Math.Round(a[k] / 1024.0 / frames, 1))));
            result["pluginAllocKBPerFrame"] = new JObject { ["render"] = Alloc(TrRenderAlloc), ["tick"] = Alloc(TrTickAlloc) };
            result["id"] = id; result["status"] = "done"; result["durationMs"] = duration; result["patches"] = patches; result["watch"] = job["watch"];
            lock (TraceJobs) TraceJobs[id] = result;
            }
            catch (Exception ex)
            {
                lock (TraceJobs) TraceJobs[id] = new JObject { ["id"] = id, ["status"] = "done", ["error"] = "analysis_failed", ["message"] = $"{ex.GetType().Name}: {ex.Message}", ["patches"] = patches };
            }
        });
        return job;
        }
        catch (Exception ex)
        {
            // Setting up failed after patching began: nothing may stay patched or wrapped.
            _trOn = false;
            try { MeasureEnd(); } catch { }
            InstrEnd(set, $"failed while starting: {ex.GetType().Name}: {ex.Message}");
            try { unwrap?.Invoke(); } catch { }
            throw;
        }
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
        public long GcPause;                   // GC.GetTotalPauseDuration at frame start (TimeSpan ticks)
        public int CamReads, EntReads, Fetches, Cycles;
        public double PluginMs;   // time inside plugin Tick + Render this frame (summed; parallel ticks can overlap)
    }

    private static bool _trSeries;

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
                    cur = new TraceFrame { Begin = t, GcPause = TrArg[i] }; inFrame = true; break;
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
            // Per frame (series=true), for timelines: what each frame took and whether a GC pause landed in it.
            ["series"] = _trSeries ? TraceSeries(full) : null,
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

public partial class WhatsAnAiBridge
{
    private static JObject? _poolLimits;

    /// <summary>ArrayPool&lt;byte&gt;.Shared limits, read once from the runtime's SharedArrayPoolStatics (null fields when that internal type moves).</summary>
    private static JObject SharedArrayPoolLimits()
    {
        if (_poolLimits != null)
        {
            // gc.pool may have changed the 4 KB bucket since: report what it holds now.
            var live = PoolCapacity(4096);
            if (live != null) _poolLimits["arraysPerSize"] = live;
            return _poolLimits;
        }
        static int? Get(HudLink link) => link.Field?.GetValue(null) is int v ? v : null;
        var partitions = Get(HudLinks.PoolPartitionCount);
        var perPartition = Get(HudLinks.PoolMaxPerPartition);
        return _poolLimits = new JObject
        {
            ["partitions"] = partitions, ["maxArraysPerPartition"] = perPartition,
            ["arraysPerSize"] = PoolCapacity(4096) ?? partitions * perPartition,
            ["envMaxArraysPerPartition"] = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXARRAYSPERPARTITION"),
            ["broken"] = partitions == null ? HudLinks.PoolPartitionCount.Broken(HudLinks.PoolPartitionCount.DeclaredType)
                : perPartition == null ? HudLinks.PoolMaxPerPartition.Broken(HudLinks.PoolMaxPerPartition.DeclaredType) : null,
        };
    }
}

public partial class WhatsAnAiBridge
{
    /// <summary>
    /// One entry per complete frame, as parallel arrays (compact JSON): ms since the first frame, the interval to the
    /// next frame, the work (update) time, the plugins' share of it, and the GC pause that landed between this frame's
    /// start and the next (from GC.GetTotalPauseDuration, recorded at each frame start).
    /// </summary>
    private static JObject TraceSeries(List<TraceFrame> full)
    {
        double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var t = new JArray(); var interval = new JArray(); var work = new JArray(); var plugins = new JArray(); var gc = new JArray();
        for (var k = 0; k < full.Count; k++)
        {
            var f = full[k];
            var next = k + 1 < full.Count ? full[k + 1] : null;
            t.Add(Math.Round(ms(f.Begin - full[0].Begin), 2));
            interval.Add(next == null ? null : Math.Round(ms(next.Begin - f.Begin), 2));
            work.Add(f.UpdEnd > 0 ? Math.Round(ms(f.UpdEnd - f.Begin), 3) : null);
            plugins.Add(Math.Round(f.PluginMs, 3));
            gc.Add(next == null ? 0 : Math.Round((next.GcPause - f.GcPause) / (double)TimeSpan.TicksPerMillisecond, 2));
        }
        return new JObject { ["tMs"] = t, ["intervalMs"] = interval, ["workMs"] = work, ["pluginsMs"] = plugins, ["gcPauseMs"] = gc };
    }
}
