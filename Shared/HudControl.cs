using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Plugin dev loop from the outside: list the HUD's source plugins and recompile one in place,
/// exactly like its Reload button in the HUD menu, so an agent can edit -> reload -> verify without
/// restarting the HUD. The HUD only compiles source plugins at startup or on that button (its file
/// watcher reloads compiled DLLs only).
///
/// Same API on ExileCore and ExileCore2: Core.Current.pluginManager, its public Plugins list and
/// FailedSourcePlugins, internal ReloadSourcePlugin(compiledPath, beforeReload) and public
/// LoadFailedSourcePlugin(sourceDir). ReloadSourcePlugin is internal, hence reflection.
///
/// Threading: requests may be served by the watchdog thread, so hud.reload_plugin only queues the
/// work; Render hands it to the HUD's next-frame hook (NextFrameTask), which runs on the main thread at the
/// start of Core.Tick, before the plugin loops. Never reload from inside Render itself: Core.Tick calls each
/// plugin's Render from a foreach over pluginManager.Plugins, and a reload removes the old wrapper from that
/// list ("Collection was modified" in Core.Tick, and the rest of that frame's plugins skipped). The menu's
/// Reload button runs outside those loops too. The HUD freezes
/// while the plugin compiles (a few seconds). The watchdog keeps answering hud.reload_status meanwhile.
/// </summary>
public partial class WhatsAnAiBridge
{
    private readonly ConcurrentQueue<ReloadRequest> _pendingReloads = new();
    private volatile ReloadResultDto? _lastReload;
    private int _reloadScheduled;   // 1 while a reload waits for the HUD's next frame start

    private sealed record ReloadRequest(string Folder, string? CompiledPath, string? FailedSourceDir, DateTime QueuedAt, string? Who = null);

    /// <summary>Routes "hud.*" JSON-RPC methods. Returns null for anything else.</summary>
    private string? ProcessHudMethod(string method, JToken? p)
    {
        switch (method)
        {
            case "hud.plugins":
                return Serialize(ListSourcePlugins());
            case "hud.reload_plugin":
                return Serialize(QueueReload(p?["name"]?.Value<string>(), p?["force"]?.Value<bool>() == true));
            case "hud.reload_status":
                return Serialize(new ReloadStatusResponse
                {
                    Pending = _pendingReloads.Select(r => r.Folder).ToList(),
                    Last = _lastReload,
                });
            default:
                return null;
        }
    }

    // ── Reading the plugin manager ───────────────────────────────────

    private sealed record SourcePluginInfo(string Folder, string Name, string CompiledPath, bool Enabled);

    private List<SourcePluginInfo> LoadedSourcePlugins()
    {
        var pm = Core.Current?.pluginManager;
        if (pm == null) return new();
        var sourceRoot = Path.GetFullPath(pm.SourcePluginDirectoryPath);
        var list = new List<SourcePluginInfo>();
        foreach (var w in pm.Plugins.ToList())
        {
            // Source plugins are compiled into <HUD>\...\<Folder>\<Assembly>.dll under the HUD's temp
            // plugin directory; the folder name is the source folder's name.
            var compiled = w.PathOnDisk;
            if (string.IsNullOrEmpty(compiled)) continue;
            var folder = Path.GetFileName(Path.GetDirectoryName(compiled) ?? "");
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(Path.Combine(sourceRoot, folder))) continue;
            if (list.Any(x => x.CompiledPath == compiled)) continue; // one assembly can hold several plugins
            list.Add(new SourcePluginInfo(folder, w.Name, compiled, w.IsEnable));
        }
        return list;
    }

    private HudPluginsResponse ListSourcePlugins()
    {
        var pm = Core.Current?.pluginManager;
        if (pm == null) return new HudPluginsResponse { Error = "plugin_manager_unavailable" };
        return new HudPluginsResponse
        {
            Loaded = LoadedSourcePlugins().Select(p => new HudPluginDto { Folder = p.Folder, Name = p.Name, Enabled = p.Enabled }).ToList(),
            Failed = pm.FailedSourcePlugins.Select(kv => new HudFailedPluginDto
            {
                Folder = Path.GetFileName(kv.Key.TrimEnd('\\', '/')),
                Error = kv.Value.Length > 4000 ? kv.Value[..4000] + " ..." : kv.Value,
            }).ToList(),
            ReloadEnabled = Settings.AllowPluginReload.Value,
            AvoidLockingDllFiles = AvoidLockingDllFiles,
            Instrumentation = InstrState(full: false),
        };
    }

    /// <summary>
    /// HUD core setting "Avoid locking plugin dlls" (Core > Plugin Settings, default off on both HUDs). Off: plugin DLLs are loaded from
    /// their file, which stays locked, so recompiling changed code can't replace it - and the HUD has
    /// already unloaded the plugin by then. On: loaded from a memory stream, reloads always work.
    /// It applies to plugins loaded after it is turned on.
    /// </summary>
    private bool AvoidLockingDllFiles => GameController.Settings.CoreSettings.PluginSettings.AvoidLockingDllFiles.Value;

    // ── Reloading ────────────────────────────────────────────────────

    private ReloadQueuedResponse QueueReload(string? name, bool force)
    {
        if (!Settings.AllowPluginReload.Value)
            return new ReloadQueuedResponse { Error = "reload_disabled", Message = "Enable 'Allow plugin reload requests' in the bridge settings." };
        if (string.IsNullOrWhiteSpace(name))
            return new ReloadQueuedResponse { Error = "missing_name", Message = "Pass name: the plugin's folder or display name." };
        var pm = Core.Current?.pluginManager;
        if (pm == null)
            return new ReloadQueuedResponse { Error = "plugin_manager_unavailable" };

        static bool Same(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

        var loaded = LoadedSourcePlugins().FirstOrDefault(p => Same(p.Folder, name) || Same(p.Name, name));
        string? failedDir = null;
        if (loaded == null)
            failedDir = pm.FailedSourcePlugins.Keys.FirstOrDefault(k => Same(Path.GetFileName(k.TrimEnd('\\', '/')), name));
        if (loaded == null && failedDir == null)
            return new ReloadQueuedResponse { Error = "unknown_plugin", Message = $"No source plugin '{name}' is loaded or failed in this HUD (see hud.plugins). A brand-new plugin folder needs a HUD restart." };

        var folder = loaded?.Folder ?? Path.GetFileName(failedDir!.TrimEnd('\\', '/'));
        if (string.Equals(Path.GetFullPath(DirectoryFullName).TrimEnd('\\'), Path.GetFullPath(Path.Combine(pm.SourcePluginDirectoryPath, folder)).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            || Same(folder, Name))
            return new ReloadQueuedResponse { Error = "cannot_reload_self", Message = "The bridge can't reload itself while serving the request; restart the HUD instead." };
        if (loaded != null && !AvoidLockingDllFiles && !force)
            return new ReloadQueuedResponse
            {
                Error = "dll_locked",
                Plugin = folder,
                Message = "The HUD setting Core > Plugin Settings > 'Avoid locking plugin dlls' is off, so this plugin's DLL is locked: " +
                          "reloading changed code would fail and leave the plugin unloaded until a HUD restart. Turn the setting on " +
                          "and restart the HUD once (it applies to plugins loaded afterwards), or restart the HUD to pick up the edit. " +
                          "force=true reloads anyway (fine when the code is unchanged).",
            };
        if (_pendingReloads.Any(r => r.Folder == folder))
            return new ReloadQueuedResponse { Queued = true, Plugin = folder, Message = "Already queued." };

        _pendingReloads.Enqueue(new ReloadRequest(folder, loaded?.CompiledPath, failedDir, DateTime.UtcNow, CurrentWho()));
        return new ReloadQueuedResponse
        {
            Queued = true,
            Plugin = folder,
            Message = "Queued for the HUD's next frame. The HUD pauses while it compiles (usually a few seconds). " +
                      "Poll hud.reload_status until last.plugin matches and finishedAt is set.",
        };
    }

    /// <summary>Called from Render: hands at most one queued reload to the next frame's start, outside the
    /// HUD's plugin loops (see the class summary).</summary>
    private void SchedulePendingReload()
    {
        // The last reload isn't final until its plugin is seen running (CheckReloadedPlugin only reads the plugin list).
        if (_reloadCheck is { } c)
        {
            // Runs every frame while checking: a throw must end the check once, never repeat per frame.
            try { CheckReloadedPlugin(); }
            catch (Exception ex)
            {
                _reloadCheck = null;
                c.Result.Ok = false;
                c.Result.Error = "check_failed";
                c.Result.Message = $"{c.Request.Folder} compiled, but checking that it loaded threw {ex.GetType().Name}: {ex.Message}";
                try { FinishReload(c); } catch { /* the result above is already set */ }
            }
            return;
        }
        if (_pendingReloads.IsEmpty || Interlocked.Exchange(ref _reloadScheduled, 1) == 1) return;
        new NextFrameTask().GetAwaiter().OnCompleted(() =>
        {
            try { RunPendingReload(); }
            finally { Volatile.Write(ref _reloadScheduled, 0); }
        });
    }

    /// <summary>
    /// Performs at most one queued reload. Runs from the HUD's next-frame hook, never from Render. A compile that
    /// succeeded is checked over the next frames (CheckReloadedPlugin) before the result is final.
    /// </summary>
    private void RunPendingReload()
    {
        if (_reloadCheck != null || !_pendingReloads.TryDequeue(out var r)) return;
        var result = new ReloadResultDto { Plugin = r.Folder, QueuedAt = r.QueuedAt, StartedAt = DateTime.UtcNow };
        _lastReload = result;
        var sw = Stopwatch.StartNew();
        var old = PluginWrappersOf(r.Folder);
        var check = new ReloadCheck(r, result, sw,
            old.Select(w => (object)w.Plugin).ToHashSet(ReferenceEqualityComparer.Instance),
            old.Select(w => w.Plugin.GetType().Assembly).ToHashSet(),
            old.Count > 0 ? old.Any(w => w.IsEnable) : null);
        try
        {
            var pm = Core.Current?.pluginManager ?? throw new InvalidOperationException("plugin manager unavailable");
            if (r.CompiledPath != null)
            {
                // A measurement patching this plugin ends first: the old load's patches go, and nothing is measured on a
                // load that is about to stop running (Instrumentation.cs).
                var asms = pm.Plugins.Where(w => string.Equals(w.PathOnDisk, r.CompiledPath, StringComparison.OrdinalIgnoreCase))
                    .Select(PluginObject).Where(o => o != null).Select(o => o!.GetType().Assembly).ToHashSet();
                InstrDropPluginAssemblies(asms, $"cut: {r.Folder} was reloaded (hud.reload_plugin) mid-measurement");
                Reload(pm).Invoke(pm, new object?[] { r.CompiledPath, null });
            }
            else
            {
                // A plugin that failed to compile. The HUD's own rebuild (LoadFailedSourcePlugin, the menu's "Try to
                // rebuild") adds the new plugin to the list but never calls Initialise/AreaChange/EntityAdded on it
                // (seen on PoE2 2026-10-11, and so in ExileCore's source): it shows as loaded and never ticks.
                // ReloadSourcePlugin runs the full load, so use it with the path the plugin compiles to
                // (<temp plugins>\<folder>\x.dll; the HUD only uses the folder part and validates the rest).
                result.Via = "rebuild";
                var tempRoot = LoadedSourcePlugins().Select(p => Path.GetDirectoryName(Path.GetDirectoryName(p.CompiledPath))).FirstOrDefault(d => d != null);
                var rebuilt = false;
                if (tempRoot != null)
                {
                    pm.FailedSourcePlugins.TryRemove(r.FailedSourceDir!, out var previous);
                    try
                    {
                        Reload(pm).Invoke(pm, new object?[] { Path.Combine(tempRoot, r.Folder, r.Folder + ".dll"), null });
                        rebuilt = true;
                    }
                    catch (TargetInvocationException) when (FailureOf(pm, r.Folder) == null)
                    {
                        // Refused before compiling (a temp path the HUD doesn't accept): put the entry back, use the HUD's rebuild.
                        if (previous != null) pm.FailedSourcePlugins.TryAdd(r.FailedSourceDir!, previous);
                    }
                }
                if (!rebuilt)
                {
                    result.Via = "rebuild_failed_plugin";
                    pm.LoadFailedSourcePlugin(r.FailedSourceDir!);
                }
            }
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            result.Ok = false;
            result.Error = inner.Message;
        }
        // A failed compile is recorded in FailedSourcePlugins (and Errors.txt); ReloadSourcePlugin then also throws a
        // bare "Unable to recompile", so the compiler's message wins.
        var pmNow = Core.Current?.pluginManager;
        if (pmNow != null && FailureOf(pmNow, r.Folder) is { } failure)
        {
            result.Ok = false;
            result.Error = failure.Length > 4000 ? failure[..4000] + " ..." : failure;
        }
        if (result.Ok == false)
        {
            // The old instance is unloaded either way, but its PluginBridge methods still answer.
            result.StaleBridgeMethods = StaleBridgeMethodNames(check.OldAssemblies);
            FinishReload(check);
            return;
        }
        check.CompiledAt = DateTime.UtcNow;
        _reloadCheck = check;
    }

    private static MethodInfo Reload(PluginManager pm) =>
        pm.GetType().GetMethod("ReloadSourcePlugin", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingMethodException("PluginManager.ReloadSourcePlugin not found in this HUD build");

    private static string? FailureOf(PluginManager pm, string folder) =>
        pm.FailedSourcePlugins.FirstOrDefault(kv => string.Equals(Path.GetFileName(kv.Key.TrimEnd('\\', '/')), folder, StringComparison.OrdinalIgnoreCase)).Value;

    private static List<PluginWrapper> PluginWrappersOf(string folder) =>
        (Core.Current?.pluginManager?.Plugins.ToList() ?? new())
        .Where(w => w.Plugin != null && !string.IsNullOrEmpty(w.PathOnDisk)
                    && string.Equals(Path.GetFileName(Path.GetDirectoryName(w.PathOnDisk)), folder, StringComparison.OrdinalIgnoreCase))
        .ToList();

    private sealed class ReloadCheck(ReloadRequest request, ReloadResultDto result, Stopwatch sw, HashSet<object> oldInstances, HashSet<Assembly> oldAssemblies, bool? wasEnabled)
    {
        public ReloadRequest Request { get; } = request;
        public ReloadResultDto Result { get; } = result;
        public Stopwatch Sw { get; } = sw;
        public HashSet<object> OldInstances { get; } = oldInstances;
        public HashSet<Assembly> OldAssemblies { get; } = oldAssemblies;
        /// <summary>null: the plugin wasn't loaded before (it had failed to compile).</summary>
        public bool? WasEnabled { get; } = wasEnabled;
        public DateTime CompiledAt { get; set; }
        public DateTime? SeenAt { get; set; }
    }

    private volatile ReloadCheck? _reloadCheck;
    private static readonly TimeSpan ReloadLoadTimeout = TimeSpan.FromSeconds(10), ReloadInitGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A compile that succeeded isn't a running plugin yet: the HUD adds the new instance about 1 s later from a
    /// coroutine, and one of its paths adds it without initialising it. Each frame until it settles: is there a new
    /// instance, and is every enabled one Initialized? Reports the first link that broke instead of "ok".
    /// </summary>
    private void CheckReloadedPlugin()
    {
        var c = _reloadCheck!;
        var r = c.Result;
        var now = DateTime.UtcNow;
        var fresh = PluginWrappersOf(c.Request.Folder).Where(w => !c.OldInstances.Contains(w.Plugin)).ToList();
        if (fresh.Count == 0)
        {
            if (now - c.CompiledAt < ReloadLoadTimeout) return;
            r.Ok = false;
            r.Error = "not_loaded";
            r.Message = $"{c.Request.Folder} compiled, but no new instance of it was loaded within {ReloadLoadTimeout.TotalSeconds:0} s: " +
                        "the HUD's load step (its reload coroutine) didn't run, or found no IPlugin/ISettings class. Check hud_log; reload again or restart the HUD.";
        }
        else
        {
            c.SeenAt ??= now;
            var uninitialised = fresh.Where(w => w.IsEnable && !w.Plugin.Initialized).ToList();
            if (uninitialised.Count > 0 && now - c.SeenAt < ReloadInitGrace) return;
            r.Instances = fresh.Count;
            r.Initialized = fresh.All(w => w.Plugin.Initialized);
            if (uninitialised.Count > 0)
            {
                r.Ok = false;
                r.Error = "not_initialised";
                r.Message = $"{c.Request.Folder} compiled and loaded, but the HUD never called Initialise on {string.Join(", ", uninitialised.Select(w => w.Name))}: " +
                            "it is enabled yet won't Tick or Render" + (r.Via == "rebuild_failed_plugin"
                                ? " (the HUD's rebuild of a plugin that had failed to compile skips Initialise). Call reload_plugin again: a reload of a loaded plugin initialises it."
                                : ". Call reload_plugin again, or restart the HUD.");
            }
            else if (c.WasEnabled == true && fresh.All(w => !w.IsEnable && !w.Plugin.Initialized))
            {
                r.Ok = false;
                r.Error = "init_failed";
                r.Message = $"{c.Request.Folder} compiled and loaded, but its Initialise() returned false or threw, so the HUD turned it off. " +
                            "See what it logged (hud_log), fix it, reload, and enable it again in the HUD menu.";
            }
            else
            {
                r.Ok = true;
                if (fresh.All(w => !w.IsEnable)) r.Message = "Loaded but disabled in the HUD menu: it isn't initialised until it is enabled.";
            }
        }
        // Methods the old instance registered and the new one didn't overwrite still point into the unloaded plugin.
        r.StaleBridgeMethods = StaleBridgeMethodNames(c.OldAssemblies);
        _reloadCheck = null;
        FinishReload(c);
    }

    private void FinishReload(ReloadCheck c)
    {
        var result = c.Result;
        var r = c.Request;
        if (result.StaleBridgeMethods is { Count: 0 }) result.StaleBridgeMethods = null;
        if (result.StaleBridgeMethods != null)
            result.Warnings.Add($"PluginBridge methods registered by the unloaded instance still answer, with its stale state: {string.Join(", ", result.StaleBridgeMethods)}. " +
                                (result.Ok == true
                                    ? "The new instance doesn't register them (renamed or removed?): callers get the old plugin's delegates until a HUD restart."
                                    : "Callers get the old plugin's delegates until the plugin loads again (it re-registers them) or the HUD restarts."));
        result.DurationMs = c.Sw.ElapsedMilliseconds;
        result.FinishedAt = DateTime.UtcNow;
        _lastReload = result;
        ObsHud(new JObject { ["cause"] = "reload", ["plugin"] = r.Folder, ["ok"] = result.Ok, ["durationMs"] = result.DurationMs });
        if (result.Ok == true) LogMessage($"[Reload] {r.Folder} recompiled, reloaded and initialised in {result.DurationMs} ms (requested over the bridge)");
        else LogError($"[Reload] {r.Folder} failed: {result.Message ?? result.Error}");
        foreach (var w in result.Warnings) LogError($"[Reload] {r.Folder}: {w}");
    }

    /// <summary>PluginBridge entries whose delegate lives in one of these plugin assemblies while no loaded plugin uses it.</summary>
    private List<string> StaleBridgeMethodNames(HashSet<Assembly> assemblies)
    {
        if (assemblies.Count == 0) return new();
        var bridge = GameController.PluginBridge;
        if (bridge?.GetType().GetField("_methods", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(bridge) is not IDictionary dict) return new();
        var live = LivePluginAssemblies() ?? new();
        // foreach over IDictionary uses its IDictionaryEnumerator; Cast<DictionaryEntry>() would go through the plain
        // IEnumerable, which yields KeyValuePairs for the generic dictionary behind PluginBridge (InvalidCastException).
        var names = new List<string>();
        foreach (DictionaryEntry e in dict)
            if (e.Value is Delegate d && DelegateAssembly(d) is { } a && assemblies.Contains(a) && !live.Contains(a))
                names.Add(e.Key?.ToString() ?? "");
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private static Assembly? DelegateAssembly(Delegate d) => d.Target?.GetType().Assembly ?? d.Method.DeclaringType?.Assembly;

    /// <summary>
    /// Why a PluginBridge delegate is stale, or null: it lives in a plugin assembly no loaded plugin uses any more (an
    /// unloaded or reloaded instance), so it still answers, from that instance's state. HUD core delegates (the default
    /// load context) never count; null too when the plugin list can't be read right now.
    /// </summary>
    private static string? StaleDelegateNote(Delegate d, HashSet<Assembly>? live)
    {
        if (live == null || DelegateAssembly(d) is not { } a || live.Contains(a)) return null;
        if (System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(a) is not { } ctx || ctx == System.Runtime.Loader.AssemblyLoadContext.Default) return null;
        return $"registered by an instance of {a.GetName().Name} that is no longer loaded (it was reloaded or failed to compile): " +
               "it answers from that old instance's state. The plugin re-registers it when it loads again; until then, or if it no longer registers it, it stays stale until a HUD restart.";
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────

public class HudPluginsResponse
{
    [JsonProperty("loaded")] public List<HudPluginDto> Loaded { get; set; } = new();
    [JsonProperty("failed")] public List<HudFailedPluginDto> Failed { get; set; } = new();
    [JsonProperty("reloadEnabled")] public bool ReloadEnabled { get; set; }
    /// <summary>HUD core setting; reloads of changed code need it on (see WhatsAnAiBridge.AvoidLockingDllFiles).</summary>
    [JsonProperty("avoidLockingDllFiles")] public bool AvoidLockingDllFiles { get; set; }
    /// <summary>Harmony patches of running measurements, leftovers and cleanup events (instrument.state, Instrumentation.cs).</summary>
    [JsonProperty("instrumentation", NullValueHandling = NullValueHandling.Ignore)] public Newtonsoft.Json.Linq.JObject? Instrumentation { get; set; }
    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public string? Error { get; set; }
}

public class HudPluginDto
{
    [JsonProperty("folder")] public string Folder { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("enabled")] public bool Enabled { get; set; }
}

public class HudFailedPluginDto
{
    [JsonProperty("folder")] public string Folder { get; set; } = "";
    [JsonProperty("error")] public string Error { get; set; } = "";
}

public class ReloadQueuedResponse
{
    [JsonProperty("queued")] public bool Queued { get; set; }
    [JsonProperty("plugin", NullValueHandling = NullValueHandling.Ignore)] public string? Plugin { get; set; }
    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public string? Error { get; set; }
    [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)] public string? Message { get; set; }
}

public class ReloadStatusResponse
{
    [JsonProperty("pending")] public List<string> Pending { get; set; } = new();
    [JsonProperty("last", NullValueHandling = NullValueHandling.Ignore)] public ReloadResultDto? Last { get; set; }
}

public class ReloadResultDto
{
    [JsonProperty("plugin")] public string Plugin { get; set; } = "";
    [JsonProperty("queuedAt")] public DateTime QueuedAt { get; set; }
    [JsonProperty("startedAt")] public DateTime StartedAt { get; set; }
    [JsonProperty("finishedAt", NullValueHandling = NullValueHandling.Ignore)] public DateTime? FinishedAt { get; set; }
    [JsonProperty("durationMs", NullValueHandling = NullValueHandling.Ignore)] public long? DurationMs { get; set; }
    /// <summary>null while running (compiling, then checking that the new instance loaded and initialised).</summary>
    [JsonProperty("ok", NullValueHandling = NullValueHandling.Ignore)] public bool? Ok { get; set; }
    /// <summary>The compiler output, or after a good compile: not_loaded | not_initialised | init_failed (Message says what it means).</summary>
    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public string? Error { get; set; }
    [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)] public string? Message { get; set; }
    /// <summary>For a plugin that had failed to compile: "rebuild" (through ReloadSourcePlugin) or "rebuild_failed_plugin" (the HUD's LoadFailedSourcePlugin, which skips Initialise).</summary>
    [JsonProperty("via", NullValueHandling = NullValueHandling.Ignore)] public string? Via { get; set; }
    /// <summary>New plugin instances found after the reload (one assembly can hold several plugins).</summary>
    [JsonProperty("instances", NullValueHandling = NullValueHandling.Ignore)] public int? Instances { get; set; }
    /// <summary>Every new instance's IPlugin.Initialized; false with ok=true means it is disabled in the HUD menu.</summary>
    [JsonProperty("initialized", NullValueHandling = NullValueHandling.Ignore)] public bool? Initialized { get; set; }
    /// <summary>PluginBridge methods that still point into the unloaded instance (stale delegates answering with its state).</summary>
    [JsonProperty("staleBridgeMethods", NullValueHandling = NullValueHandling.Ignore)] public List<string>? StaleBridgeMethods { get; set; }
    [JsonProperty("warnings")] public List<string> Warnings { get; set; } = new();
}
