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
/// Marks a setting that gives bridge clients power (C# scripts, HUD instrumentation, plugin reload): settings.set
/// refuses to change it without allowPermission. Matched by type name, so another plugin can declare its own copy.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
internal sealed class SettingPermissionAttribute : Attribute { }

/// <summary>
/// Settings of every loaded plugin, described and changed by reflection: no per-plugin code. A node is recognised by
/// shape (ToggleNode, RangeNode&lt;T&gt;, TextNode, ListNode, ColorNode, HotkeyNode*, ButtonNode); labels and tooltips
/// come from the HUD's own [Menu] attribute, the group from a nested settings object's label, else from the property's
/// [MenuTab] (the SettingsMenu renderer's tab), [SettingPermission] marks a permission setting and [MenuReadOnly] a
/// read-only one; the bridge's own settings use the same attributes (Shared\WhatsAnAiBridgeSettings.cs). The
/// renderer's attributes are copied per plugin, so they are matched by type name, never by type.
///   settings.describe {plugin?}                        -> {game, plugins: [{plugin, enabled, settings: [node]}]}
///   settings.set {plugin, path, value, allowPermission?} -> {plugin, setting, previous}
/// Changes run on the main thread (start of Render), like the menu would make them. Permission settings (the switches
/// that give bridge clients power: C# scripts, HUD instrumentation, plugin reload) refuse to change unless the caller
/// passes allowPermission: the MCP server never does from a tool, only from the control center's own route after the
/// user confirms. Text values under secret-looking names are never returned (write-only).
/// </summary>
public partial class WhatsAnAiBridge
{
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
        while (_mainActions.TryDequeue(out var a)) { try { a(); } catch (Exception ex) { LogFault($"[Settings] {ex.Message}"); } }
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
            Walk(settings, "", null, nodes, 0);
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

    /// <summary>Every node under obj, depth-first; nested settings objects become groups named by their label, and a
    /// node outside any (a flat settings class) takes its [MenuTab] as its group.</summary>
    private static void Walk(object obj, string prefix, string? group, JArray into, int depth)
    {
        if (depth > 4) return;
        foreach (var (prop, v) in SettingsMembers(obj))
        {
            if (v == null) continue;
            var path = prefix + prop.Name;
            var menu = MenuOf(prop.Property!);
            if (IsNode(v.GetType()))
            {
                into.Add(Describe(prop.Name, path, v, group ?? menu.Tab, menu));
                continue;
            }
            // A nested settings object (a class holding nodes): its nodes go into a group named after it.
            var t = v.GetType();
            if (t.IsClass && t != typeof(string) && v is not IEnumerable && MemberIndex.Of(t).AllProperties.Any(pp => IsNode(pp.PropertyType)))
                Walk(v, path + ".", menu.Name ?? Words(prop.Name), into, depth + 1);
        }
    }

    /// <summary>
    /// The one walk over a settings object, shared by settings.describe and hud.plugin_settings: its public readable
    /// properties in declaration order with their values (a getter that throws is skipped).
    /// </summary>
    private static IEnumerable<(MemberIndex.Member member, object? value)> SettingsMembers(object obj)
    {
        foreach (var m in MemberIndex.ReadableProperties(obj.GetType()))
        {
            object? v;
            try { v = m.Get(obj); } catch { continue; }
            yield return (m, v);
        }
    }

    /// <summary>A settings node's property (Value, Min, Max, Values...), or null when it has none or the getter throws.</summary>
    private static object? NodeMember(object node, string name) => MemberIndex.TryGet(node, name, kinds: MemberIndex.Kinds.Property);

    private static bool IsNode(Type t) => BaseName(t).EndsWith("Node", StringComparison.Ordinal);
    private static string BaseName(Type t) => t.Name.Contains('`') ? t.Name[..t.Name.IndexOf('`')] : t.Name;

    /// <summary>What a settings property's attributes say about it: label and description, tab, permission, read-only.</summary>
    private readonly record struct MenuInfo(string? Name, string? Tooltip, string? Tab, bool Permission, bool ReadOnly, string? ReadOnlyNote);

    /// <summary>
    /// The attributes on a settings property, by type name: the HUD's [Menu(name, tooltip)] (MenuName / Tooltip on both
    /// HUDs), and SettingsMenu's [MenuTab(tab)] and [MenuReadOnly(note)] plus the bridge's [SettingPermission], which
    /// each plugin declares in its own namespace (copied, never referenced).
    /// </summary>
    private static MenuInfo MenuOf(PropertyInfo p)
    {
        string? name = null, tooltip = null, tab = null, roNote = null;
        bool permission = false, readOnly = false;
        foreach (var a in p.GetCustomAttributes(true))
        {
            switch (a.GetType().Name)
            {
                case "MenuAttribute":
                    name = MemberIndex.TryGet(a, "MenuName") as string;
                    tooltip = MemberIndex.TryGet(a, "Tooltip") as string;
                    break;
                case "MenuTabAttribute": tab = MemberIndex.TryGet(a, "Tab") as string; break;
                case "MenuReadOnlyAttribute": readOnly = true; roNote = MemberIndex.TryGet(a, "Note") as string; break;
                case "SettingPermissionAttribute": permission = true; break;
            }
        }
        return new MenuInfo(name, string.IsNullOrWhiteSpace(tooltip) ? null : tooltip, string.IsNullOrWhiteSpace(tab) ? null : tab.Trim(), permission, readOnly, roNote);
    }

    private static string Words(string name) => System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

    private static JObject Describe(string name, string path, object node, string? group, MenuInfo menu)
    {
        var t = node.GetType();
        var baseName = BaseName(t);
        var kind = baseName switch
        {
            "ToggleNode" => "toggle", "RangeNode" => "range", "TextNode" => "text", "ListNode" => "list", "ColorNode" => "color",
            "ButtonNode" => "button", _ when baseName.StartsWith("Hotkey", StringComparison.Ordinal) => "hotkey", _ => "unknown",
        };
        var description = menu.Tooltip;
        if (menu.ReadOnly)
            description = ((description ?? "") + " (read-only: " + (string.IsNullOrWhiteSpace(menu.ReadOnlyNote) ? "the plugin sets it itself" : menu.ReadOnlyNote) + ")").Trim();
        var o = new JObject
        {
            ["path"] = path,
            ["label"] = menu.Name ?? Words(name),
            ["group"] = group,
            ["description"] = description,
            ["kind"] = kind,
            ["permission"] = menu.Permission,
            ["readOnly"] = menu.ReadOnly || kind is "hotkey" or "button" or "unknown",
        };
        var value = NodeMember(node, "Value");
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
            try { o["min"] = Num(NodeMember(node, "Min")); } catch { }
            try { o["max"] = Num(NodeMember(node, "Max")); } catch { }
        }
        if (kind == "list" && NodeMember(node, "Values") is IEnumerable vals)
            o["options"] = new JArray(vals.Cast<object>().Take(200).Select(x => x?.ToString()));
        if (kind == "text" && IsSecret(name, value)) o["description"] = ((o["description"]?.ToString() ?? "") + " (write-only: the value is never shown)").Trim();
        return o;
    }

    /// <summary>A range limit as an integer when it is one (RangeNode<int>), else a double.</summary>
    private static JToken Num(object? v) => v is int or long or short or byte ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : Convert.ToDouble(v, CultureInfo.InvariantCulture);

    /// <summary>#RRGGBBAA from System.Drawing.Color (PoE2) or SharpDX.Color (PoE1): both expose R, G, B, A.</summary>
    private static string ColorHex(object c)
    {
        int B(string n) => Convert.ToInt32(MemberIndex.TryGet(c, n) ?? 0, CultureInfo.InvariantCulture);
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
        object? owner = Prop(Prop(w, "Plugin"), "_Settings");
        MemberIndex.Member? prop = null;
        var parts = path.Split('.');
        if (owner == null) return Err("unknown_setting", $"{w.Name} has no settings object (_Settings is null).");
        for (var i = 0; i < parts.Length; i++)
        {
            prop = MemberIndex.Find(owner.GetType(), parts[i], kinds: MemberIndex.Kinds.Property);
            if (prop is not { CanRead: true })
                return Err("unknown_setting", $"'{parts[i]}' is not a setting of {w.Name} ({(i == 0 ? "at the settings root" : $"under {string.Join('.', parts.Take(i))}")}, type {owner.GetType().Name}).");
            if (i == parts.Length - 1) break;
            try { owner = prop.Get(owner); }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } ie } ? ie : ex;
                return Err("unknown_setting", $"reading '{string.Join('.', parts.Take(i + 1))}' of {w.Name} threw {inner.GetType().Name}: {inner.Message} (segment {i + 1} of {parts.Length}).");
            }
            if (owner == null) return Err("unknown_setting", $"'{string.Join('.', parts.Take(i + 1))}' of {w.Name} is null (segment {i + 1} of {parts.Length}), so '{parts[i + 1]}' can't be reached.");
        }
        var node = prop!.Get(owner);
        if (node == null) return Err("unknown_setting", $"{path} of {w.Name} is null.");
        if (!IsNode(node.GetType())) return Err("unknown_setting", $"{path} of {w.Name} is a {node.GetType().Name}, not a settings node.");
        var menu = MenuOf(prop!.Property!);
        var before = Describe(prop.Name, path, node, parts.Length == 1 ? menu.Tab : null, menu);
        if (before["permission"]?.Value<bool>() == true && !allowPermission)
            return Err("permission_setting", $"{before["label"]} gives bridge clients power, so it can't be changed through MCP tools. Change it in game or in the Hexile control center (it asks you to confirm).");
        if (before["readOnly"]?.Value<bool>() == true)
            return Err("read_only", menu.ReadOnly ? $"{before["label"]} is read-only: {menu.ReadOnlyNote ?? "the plugin sets it itself"}." : $"{before["label"]} ({before["kind"]}) can only be changed in game.");

        var vp = MemberIndex.Property(node.GetType(), "Value");
        if (vp == null || !vp.CanWrite) return Err("read_only", $"{path} has no writable Value.");
        object? v;
        try { v = Coerce(before["kind"]!.ToString(), value, vp.PropertyType, node); }
        catch (Exception ex) { return Err("bad_value", $"{before["label"]}: {ex.Message}"); }
        vp.SetValue(node, v);
        return new JObject
        {
            ["ok"] = true, ["plugin"] = w.Name, ["previous"] = before["value"],
            ["setting"] = Describe(prop.Name, path, node, before["group"]?.ToString(), menu),
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
                double Lim(string n) => Convert.ToDouble(NodeMember(node, n) ?? throw new InvalidOperationException($"{node.GetType().Name}.{n} can't be read, so the range can't be checked"), CultureInfo.InvariantCulture);
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
                if (NodeMember(node, "Values") is IEnumerable vals && !vals.Cast<object>().Any(x => x?.ToString() == s))
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
        var fromArgb = MemberIndex.StaticMethod(target, "FromArgb", typeof(int), typeof(int), typeof(int), typeof(int));
        if (fromArgb != null) return fromArgb.Invoke(null, [(int)a, (int)r, (int)g, (int)b])!;
        var ctor = target.GetConstructor([typeof(byte), typeof(byte), typeof(byte), typeof(byte)]);
        if (ctor != null) return ctor.Invoke([r, g, b, a]);
        throw new NotSupportedException($"don't know how to make a {target.Name}");
    }
}
