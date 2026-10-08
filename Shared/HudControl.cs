using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
/// work; Render (the main thread, where the menu button runs it too) performs it. The HUD freezes
/// while the plugin compiles (a few seconds). The watchdog keeps answering hud.reload_status meanwhile.
/// </summary>
public partial class WhatsAnAiBridge
{
    private readonly ConcurrentQueue<ReloadRequest> _pendingReloads = new();
    private volatile ReloadResultDto? _lastReload;

    private sealed record ReloadRequest(string Folder, string? CompiledPath, string? FailedSourceDir, DateTime QueuedAt);

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

        _pendingReloads.Enqueue(new ReloadRequest(folder, loaded?.CompiledPath, failedDir, DateTime.UtcNow));
        return new ReloadQueuedResponse
        {
            Queued = true,
            Plugin = folder,
            Message = "Queued for the HUD's next frame. The HUD pauses while it compiles (usually a few seconds). " +
                      "Poll hud.reload_status until last.plugin matches and finishedAt is set.",
        };
    }

    /// <summary>Called at the start of Render: performs at most one queued reload per frame.</summary>
    private void RunPendingReload()
    {
        if (!_pendingReloads.TryDequeue(out var r)) return;
        var result = new ReloadResultDto { Plugin = r.Folder, QueuedAt = r.QueuedAt, StartedAt = DateTime.UtcNow };
        _lastReload = result;
        var sw = Stopwatch.StartNew();
        try
        {
            var pm = Core.Current?.pluginManager ?? throw new InvalidOperationException("plugin manager unavailable");
            if (r.CompiledPath != null)
            {
                var reload = pm.GetType().GetMethod("ReloadSourcePlugin", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                             ?? throw new MissingMethodException("PluginManager.ReloadSourcePlugin not found in this HUD build");
                reload.Invoke(pm, new object?[] { r.CompiledPath, null });
            }
            else
            {
                pm.LoadFailedSourcePlugin(r.FailedSourceDir!);
            }
            // A failed compile is recorded in FailedSourcePlugins (and Errors.txt) rather than thrown.
            var failure = pm.FailedSourcePlugins.FirstOrDefault(kv => string.Equals(Path.GetFileName(kv.Key.TrimEnd('\\', '/')), r.Folder, StringComparison.OrdinalIgnoreCase));
            result.Ok = failure.Key == null;
            if (failure.Key != null) result.Error = failure.Value.Length > 4000 ? failure.Value[..4000] + " ..." : failure.Value;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            result.Ok = false;
            result.Error = inner.Message;
        }
        result.DurationMs = sw.ElapsedMilliseconds;
        result.FinishedAt = DateTime.UtcNow;
        _lastReload = result;
        if (result.Ok == true) LogMessage($"[Reload] {r.Folder} recompiled and reloaded in {result.DurationMs} ms (requested over the bridge)");
        else LogError($"[Reload] {r.Folder} failed: {result.Error}");
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
    /// <summary>null while running.</summary>
    [JsonProperty("ok", NullValueHandling = NullValueHandling.Ignore)] public bool? Ok { get; set; }
    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public string? Error { get; set; }
}
