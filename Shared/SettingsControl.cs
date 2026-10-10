using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Settings of every loaded plugin, described and changed by reflection: no per-plugin code. A node is recognised by
/// shape (ToggleNode, RangeNode&lt;T&gt;, TextNode, ListNode, ColorNode, HotkeyNode*, ButtonNode); labels and tooltips
/// come from the HUD's own [Menu] attribute when a plugin uses it, and for this bridge from Meta below. Nested settings
/// objects become groups.
///   settings.describe {plugin?}                        -> {game, plugins: [{plugin, enabled, settings: [node]}]}
///   settings.set {plugin, path, value, allowPermission?} -> {plugin, setting, previous}
/// Changes run on the main thread (start of Render), like the menu would make them. Permission settings (the switches
/// that give bridge clients power: C# scripts, HUD instrumentation, plugin reload) refuse to change unless the caller
/// passes allowPermission: the MCP server never does from a tool, only from the control center's own route after the
/// user confirms. Text values under secret-looking names are never returned (write-only).
/// </summary>
public partial class WhatsAnAiBridge
{
    /// <summary>Labels, groups and descriptions for this bridge's own settings (it has no [Menu] attributes).</summary>
    private static readonly Dictionary<string, (string label, string group, string description, bool permission)> Meta = new()
    {
        ["Enable"] = ("Enabled", "Bridge", "Turn the whole bridge on or off.", false),
        ["BridgeDirectory"] = ("Bridge folder", "Bridge", "Folder (in the HUD folder) for the port and token files, recordings, experiments and the observer journal.", false),
        ["PollIntervalMs"] = ("File request poll (ms)", "Bridge", "How often the legacy file IPC checks for a request.", false),
        ["EnableTcp"] = ("TCP server", "Bridge", "Serve bridge clients (the MCP server) on 127.0.0.1.", false),
        ["TcpPort"] = ("TCP port", "Bridge", "Port of the TCP server (written to bridge-port.txt).", false),
        ["EnableFileIpc"] = ("File IPC (legacy)", "Bridge", "Answer requests written as files.", false),
        ["ShowStatusHud"] = ("Status line", "In game", "The small bridge status line on the HUD.", false),
        ["HudX"] = ("Status line X", "In game", "", false),
        ["HudY"] = ("Status line Y", "In game", "", false),
        ["MaxEntityRange"] = ("Entity range", "Limits", "How far entity queries look (grid units).", false),
        ["MaxDeepStats"] = ("Stats per deep scan", "Limits", "", false),
        ["MaxUiChildren"] = ("UI children per element", "Limits", "", false),
        ["RecordingIntervalMs"] = ("Recording interval (ms)", "Recording", "", false),
        ["RecordingEntityRange"] = ("Recording entity range", "Recording", "", false),
        ["AutoDeepScanBosses"] = ("Deep-scan bosses", "Recording", "Record bosses' full stats automatically.", false),
        ["RecordingMaxDeepStats"] = ("Stats per recorded boss", "Recording", "", false),
        ["AllowPluginReload"] = ("Allow plugin reload", "Permissions", "Lets agents recompile a source plugin in place (the HUD pauses while it compiles).", true),
        ["AllowCSharpScripts"] = ("Allow C# scripts", "Permissions", "Lets agents run arbitrary C# inside the HUD process.", true),
        ["AllowHudInstrumentation"] = ("Allow HUD instrumentation", "Permissions", "Lets traces and profiles patch the HUD's own code for a few seconds (Harmony).", true),
        ["StatsPanelVitalsOpen"] = ("Stats panel: vitals open", "Player stats panel", "", false),
        ["StatsPanelResistsOpen"] = ("Stats panel: resistances open", "Player stats panel", "", false),
        ["StatsPanelPinnedOpen"] = ("Stats panel: pinned open", "Player stats panel", "", false),
        ["StatsPanelShowKeys"] = ("Stats panel: show keys", "Player stats panel", "Show Stats.dat keys instead of the in-game text.", false),
        ["ShowAgentGuide"] = ("Agent guide card", "Agent guide", "The card that shows what the agent asks you to do in game.", false),
        ["GuideLogOpen"] = ("Guide log open", "Agent guide", "", false),
        ["AgentLogHotkey"] = ("Agent log hotkey", "Agent guide", "Toggles the agent log sheet. Set it in game.", false),
        ["ToastAgent"] = ("Toasts: agent activity", "Agent guide", "Show a toast for every agent tool call (the grey ones). Off: they only go to the log.", false),
        ["ToastStep"] = ("Toasts: steps", "Agent guide", "Show a toast when a guided step starts or is queued.", false),
        ["ToastResult"] = ("Toasts: results", "Agent guide", "Show a toast for results and done receipts.", false),
        ["ToastWarn"] = ("Toasts: warnings", "Agent guide", "", false),
        ["ToastError"] = ("Toasts: errors", "Agent guide", "", false),
        ["RestartHoldSec"] = ("HUD restart hold (s)", "Agent guide", "An agent's HUD restart that nothing blocks still waits this long on the card, with Not now. 0 = at once.", false),
    };

    private readonly ConcurrentQueue<Action> _mainActions = new();

    private string? ProcessSettingsMethod(string method, JToken? p) => method switch
    {
        "settings.describe" => SafeMemory(() => SettingsDescribe(p?["plugin"]?.ToString())),
        "settings.set" => SafeMemory(() => SettingsSet(p)),
        _ => null,
    };

    /// <summary>Runs queued actions on the main thread (called at the start of Render).</summary>
    private void RunMainActions()
    {
        while (_mainActions.TryDequeue(out var a)) { try { a(); } catch (Exception ex) { LogError($"[Settings] {ex.Message}"); } }
    }

    private JObject SettingsDescribe(string? only)
    {
        var pm = Core.Current?.pluginManager;
        if (pm == null) return Err("plugin_manager_unavailable", "The HUD's plugin manager isn't reachable (Core.Current.pluginManager).");
        var plugins = new JArray();
        foreach (var w in pm.Plugins)
        {
            if (only != null && !SamePlugin(w.Name, only)) continue;
            var settings = Prop(Prop(w, "Plugin"), "_Settings");
            if (settings == null) continue;
            var nodes = new JArray();
            Walk(settings, "", null, ReferenceEquals(Prop(w, "Plugin"), this), nodes, 0);
            plugins.Add(new JObject
            {
                ["plugin"] = w.Name,
                ["enabled"] = (Prop(settings, "Enable") is { } en ? Prop(en, "Value") as bool? : null) ?? true,
                ["settings"] = nodes,
            });
        }
        if (only != null && plugins.Count == 0) return Err("unknown_plugin", $"No loaded plugin '{only}' with settings. Loaded: {string.Join(", ", pm.Plugins.Select(x => x.Name))}");
        return new JObject { ["ok"] = true, ["game"] = GameId, ["plugins"] = plugins };
    }

    private static bool SamePlugin(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    /// <summary>Every node under obj, depth-first; nested settings objects become groups named by their label.</summary>
    private static void Walk(object obj, string prefix, string? group, bool isBridge, JArray into, int depth)
    {
        if (depth > 4) return;
        foreach (var prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            object? v;
            try { v = prop.GetValue(obj); } catch { continue; }
            if (v == null) continue;
            var path = prefix + prop.Name;
            var (menuName, tooltip) = MenuOf(prop);
            if (IsNode(v.GetType()))
            {
                into.Add(Describe(prop.Name, path, v, group, menuName, tooltip, isBridge));
                continue;
            }
            // A nested settings object (a class holding nodes): its nodes go into a group named after it.
            var t = v.GetType();
            if (t.IsClass && t != typeof(string) && v is not IEnumerable && t.GetProperties().Any(pp => IsNode(pp.PropertyType)))
                Walk(v, path + ".", menuName ?? Words(prop.Name), isBridge, into, depth + 1);
        }
    }

    private static bool IsNode(Type t) => BaseName(t).EndsWith("Node", StringComparison.Ordinal);
    private static string BaseName(Type t) => t.Name.Contains('`') ? t.Name[..t.Name.IndexOf('`')] : t.Name;

    /// <summary>The HUD's [Menu(name, tooltip)] on a settings property (fields MenuName / Tooltip on both HUDs).</summary>
    private static (string? name, string? tooltip) MenuOf(PropertyInfo p)
    {
        var a = p.GetCustomAttributes(true).FirstOrDefault(x => x.GetType().Name == "MenuAttribute");
        if (a == null) return (null, null);
        string? F(string n) => a.GetType().GetField(n)?.GetValue(a) as string ?? a.GetType().GetProperty(n)?.GetValue(a) as string;
        return (F("MenuName"), F("Tooltip"));
    }

    private static string Words(string name) => System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

    private static JObject Describe(string name, string path, object node, string? group, string? menuName, string? tooltip, bool isBridge)
    {
        var t = node.GetType();
        var baseName = BaseName(t);
        var kind = baseName switch
        {
            "ToggleNode" => "toggle", "RangeNode" => "range", "TextNode" => "text", "ListNode" => "list", "ColorNode" => "color",
            "ButtonNode" => "button", _ when baseName.StartsWith("Hotkey", StringComparison.Ordinal) => "hotkey", _ => "unknown",
        };
        var meta = isBridge && Meta.TryGetValue(path, out var m) ? m : default;
        var o = new JObject
        {
            ["path"] = path,
            ["label"] = meta.label ?? menuName ?? Words(name),
            ["group"] = meta.group ?? group,
            ["description"] = string.IsNullOrEmpty(meta.description) ? tooltip : meta.description,
            ["kind"] = kind,
            ["permission"] = meta.permission,
            ["readOnly"] = kind is "hotkey" or "button" or "unknown",
        };
        object? value = null;
        try { value = t.GetProperty("Value")?.GetValue(node); } catch { }
        o["value"] = value switch
        {
            null => JValue.CreateNull(),
            string s when IsSecret(name, s) => "[redacted]",
            bool b => b,
            int or long or short or byte or uint or ushort or sbyte => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            IConvertible c when value is not string && !value.GetType().IsEnum => Convert.ToDouble(c, CultureInfo.InvariantCulture),
            _ when kind == "color" => ColorHex(value),
            _ => value.ToString(),
        };
        if (kind == "range")
        {
            try { o["min"] = Num(t.GetProperty("Min")?.GetValue(node)); } catch { }
            try { o["max"] = Num(t.GetProperty("Max")?.GetValue(node)); } catch { }
        }
        if (kind == "list" && t.GetProperty("Values")?.GetValue(node) is IEnumerable vals)
            o["options"] = new JArray(vals.Cast<object>().Take(200).Select(x => x?.ToString()));
        if (kind == "text" && IsSecret(name, value)) o["description"] = ((o["description"]?.ToString() ?? "") + " (write-only: the value is never shown)").Trim();
        return o;
    }

    /// <summary>A range limit as an integer when it is one (RangeNode<int>), else a double.</summary>
    private static JToken Num(object? v) => v is int or long or short or byte ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : Convert.ToDouble(v, CultureInfo.InvariantCulture);

    /// <summary>#RRGGBBAA from System.Drawing.Color (PoE2) or SharpDX.Color (PoE1): both expose R, G, B, A.</summary>
    private static string ColorHex(object c)
    {
        int B(string n) => Convert.ToInt32(c.GetType().GetProperty(n)?.GetValue(c) ?? c.GetType().GetField(n)?.GetValue(c) ?? 0, CultureInfo.InvariantCulture);
        return $"#{B("R"):X2}{B("G"):X2}{B("B"):X2}{B("A"):X2}";
    }

    private JObject SettingsSet(JToken? p)
    {
        var plugin = p?["plugin"]?.ToString();
        var path = p?["path"]?.ToString();
        var value = p?["value"];
        if (string.IsNullOrWhiteSpace(plugin) || string.IsNullOrWhiteSpace(path) || value == null)
            return Err("bad_request", "Pass plugin, path and value (settings.describe lists them).");
        var allowPermission = p?["allowPermission"]?.Value<bool>() == true;

        // Prefer the main thread (the next Render), as the menu would. While the game isn't the foreground window the HUD
        // hides its overlay and calls no plugin Render, and that's exactly when the control center is used: then set it
        // here. A node's Value setter is a plain assignment (plus the plugin's change handler, if any). Claimed once.
        var claimed = 0;
        var done = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mainActions.Enqueue(() => { if (System.Threading.Interlocked.Exchange(ref claimed, 1) == 0) done.TrySetResult(ApplySetting(plugin, path, value, allowPermission)); });
        if (done.Task.Wait(TimeSpan.FromMilliseconds(300))) return done.Task.Result;
        if (System.Threading.Interlocked.Exchange(ref claimed, 1) != 0) return done.Task.Wait(TimeSpan.FromSeconds(2)) ? done.Task.Result : Err("not_applied", "The change is still running on the HUD's main thread.");
        var r = ApplySetting(plugin, path, value, allowPermission);
        if (r["ok"] != null) r["appliedOn"] = "bridge thread (the HUD wasn't rendering: overlay hidden or paused)";
        return r;
    }

    /// <summary>Main thread. Resolves path on the plugin's settings, checks kind and permission, sets the node's Value.</summary>
    private JObject ApplySetting(string plugin, string path, JToken value, bool allowPermission)
    {
        var pm = Core.Current?.pluginManager;
        var w = pm?.Plugins.FirstOrDefault(x => SamePlugin(x.Name, plugin));
        if (w == null) return Err("unknown_plugin", $"No loaded plugin '{plugin}'.");
        var isBridge = ReferenceEquals(Prop(w, "Plugin"), this);
        object? owner = Prop(Prop(w, "Plugin"), "_Settings");
        PropertyInfo? prop = null;
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length && owner != null; i++)
        {
            prop = owner.GetType().GetProperty(parts[i], BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return Err("unknown_setting", $"'{parts[i]}' is not a setting of {w.Name} (at {string.Join('.', parts.Take(i))}).");
            if (i < parts.Length - 1) owner = prop.GetValue(owner);
        }
        var node = prop?.GetValue(owner);
        if (node == null || !IsNode(node.GetType())) return Err("unknown_setting", $"{path} of {w.Name} is not a settings node.");
        var (menuName, tooltip) = MenuOf(prop!);
        var before = Describe(prop!.Name, path, node, null, menuName, tooltip, isBridge);
        if (before["permission"]?.Value<bool>() == true && !allowPermission)
            return Err("permission_setting", $"{before["label"]} gives bridge clients power, so it can't be changed through MCP tools. Change it in game or in the Hexile control center (it asks you to confirm).");
        if (before["readOnly"]?.Value<bool>() == true)
            return Err("read_only", $"{before["label"]} ({before["kind"]}) can only be changed in game.");

        var vp = node.GetType().GetProperty("Value");
        if (vp == null || !vp.CanWrite) return Err("read_only", $"{path} has no writable Value.");
        object? v;
        try { v = Coerce(before["kind"]!.ToString(), value, vp.PropertyType, node); }
        catch (Exception ex) { return Err("bad_value", $"{before["label"]}: {ex.Message}"); }
        vp.SetValue(node, v);
        return new JObject
        {
            ["ok"] = true, ["plugin"] = w.Name, ["previous"] = before["value"],
            ["setting"] = Describe(prop.Name, path, node, before["group"]?.ToString(), menuName, tooltip, isBridge),
        };
    }

    private static object? Coerce(string kind, JToken value, Type target, object node)
    {
        switch (kind)
        {
            case "toggle": return value.Type == JTokenType.Boolean ? value.Value<bool>() : bool.Parse(value.ToString());
            case "range":
            {
                var d = value.Value<double>();
                var t = node.GetType();
                double Lim(string n) => Convert.ToDouble(t.GetProperty(n)?.GetValue(node), CultureInfo.InvariantCulture);
                if (d < Lim("Min") || d > Lim("Max")) throw new ArgumentOutOfRangeException(nameof(value), $"{d} is outside {Lim("Min")}-{Lim("Max")}");
                return Convert.ChangeType(target == typeof(int) ? Math.Round(d) : d, target, CultureInfo.InvariantCulture);
            }
            case "text":
                // "[redacted]" is what describe shows for a hidden value: sending it back would overwrite the secret with it.
                if (value.ToString() == "[redacted]") throw new ArgumentException("'[redacted]' stands for a hidden value; type the new value instead");
                return value.ToString();
            case "list":
            {
                var s = value.ToString();
                if (node.GetType().GetProperty("Values")?.GetValue(node) is IEnumerable vals && !vals.Cast<object>().Any(x => x?.ToString() == s))
                    throw new ArgumentException($"'{s}' is not one of the choices");
                return s;
            }
            case "color": return ParseColor(value.ToString(), target);
            default: throw new InvalidOperationException($"{kind} settings can't be set from outside the game");
        }
    }

    /// <summary>#RRGGBB or #RRGGBBAA into the HUD's colour type: System.Drawing.Color.FromArgb (PoE2) or a (r, g, b, a) constructor (SharpDX, PoE1).</summary>
    private static object ParseColor(string hex, Type target)
    {
        var h = hex.TrimStart('#');
        if (h.Length is not (6 or 8)) throw new FormatException("colour must be #RRGGBB or #RRGGBBAA");
        byte P(int i) => byte.Parse(h.Substring(i, 2), NumberStyles.HexNumber);
        byte r = P(0), g = P(2), b = P(4), a = h.Length == 8 ? P(6) : (byte)255;
        var fromArgb = target.GetMethod("FromArgb", [typeof(int), typeof(int), typeof(int), typeof(int)]);
        if (fromArgb != null) return fromArgb.Invoke(null, [(int)a, (int)r, (int)g, (int)b])!;
        var ctor = target.GetConstructor([typeof(byte), typeof(byte), typeof(byte), typeof(byte)]);
        if (ctor != null) return ctor.Invoke([r, g, b, a]);
        throw new NotSupportedException($"don't know how to make a {target.Name}");
    }
}
