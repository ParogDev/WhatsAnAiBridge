using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Read-only reflection over the HUD itself, for agents developing plugins:
///   hud.plugin_perf      per-plugin Tick/Render cost from the HUD's own DebugInformation counters
///   hud.plugin_settings  a plugin's live settings (ISettings nodes), secrets redacted
///   hud.bridge_methods   everything plugins registered on PluginBridge (the cross-plugin API), with signatures
/// Same API on ExileCore and ExileCore2: Core.Current.pluginManager.Plugins (PluginWrapper: Name, IsEnable,
/// Plugin, Tick/RenderDebugInformation), IPlugin._Settings, GameController.PluginBridge (private _methods).
/// Everything is read through reflection and tolerant of members a HUD build lacks. Nothing is written.
/// </summary>
public partial class WhatsAnAiBridge
{
    private string? ProcessIntrospectionMethod(string method, JToken? p) => method switch
    {
        "hud.plugin_perf" => PluginPerf().ToString(Newtonsoft.Json.Formatting.None),
        "hud.plugin_settings" => PluginSettings(p?["name"]?.Value<string>()).ToString(Newtonsoft.Json.Formatting.None),
        "hud.bridge_methods" => BridgeMethods().ToString(Newtonsoft.Json.Formatting.None),
        _ => null,
    };

    // ── Per-plugin cost ──────────────────────────────────────────────

    private JObject PluginPerf()
    {
        var pm = Core.Current?.pluginManager;
        if (pm == null) return new JObject { ["error"] = "plugin_manager_unavailable" };
        var rows = new List<JObject>();
        foreach (var w in pm.Plugins.ToList())
        {
            var row = new JObject { ["name"] = w.Name, ["enabled"] = w.IsEnable };
            row["tick"] = Counter(Prop(w, "TickDebugInformation"));
            row["render"] = Counter(Prop(w, "RenderDebugInformation"));
            rows.Add(row);
        }
        double Cost(JObject r) => (r["tick"]?["avgMs"]?.Value<double?>() ?? 0) + (r["render"]?["avgMs"]?.Value<double?>() ?? 0);
        return new JObject
        {
            ["note"] = "avgMs/maxMs come from the HUD's own per-plugin DebugInformation counters (recent window). " +
                       "Tick runs game logic, Render draws; their sum is the plugin's frame cost.",
            ["plugins"] = new JArray(rows.OrderByDescending(Cost)),
        };
    }

    private static JObject? Counter(object? info)
    {
        if (info == null) return null;
        var o = new JObject();
        void Add(string key, string member)
        {
            if (Prop(info, member) is IConvertible c)
                try { o[key] = Math.Round(Convert.ToDouble(c), 4); } catch (FormatException) { } catch (InvalidCastException) { }
        }
        Add("avgMs", "TickAverage");
        Add("maxMs", "TickMax");
        Add("lastMs", "Tick");
        Add("totalAvgMs", "TotalAverage");
        return o;
    }

    // ── Live settings ────────────────────────────────────────────────

    // Setting names that may hold credentials or personal data (e.g. a PoE session id for stash APIs).
    private static readonly Regex SecretName = new("(token|secret|password|passwd|session|cookie|auth|apikey|api_key|key$|poesessid)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private JObject PluginSettings(string? name)
    {
        var pm = Core.Current?.pluginManager;
        if (pm == null) return new JObject { ["error"] = "plugin_manager_unavailable" };
        if (string.IsNullOrWhiteSpace(name)) return new JObject { ["error"] = "missing_name", ["message"] = "Pass name (see hud.plugin_perf for names)." };
        var w = pm.Plugins.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? pm.Plugins.FirstOrDefault(x => x.Name.Replace(" ", "").Contains(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        if (w == null) return new JObject { ["error"] = "unknown_plugin", ["message"] = $"No loaded plugin '{name}'." };
        var settings = Prop(Prop(w, "Plugin"), "_Settings");
        if (settings == null) return new JObject { ["error"] = "no_settings", ["plugin"] = w.Name };
        return new JObject
        {
            ["plugin"] = w.Name,
            ["type"] = settings.GetType().FullName,
            ["settings"] = SettingsNode(settings, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)),
        };
    }

    private static JToken SettingsNode(object obj, int depth, HashSet<object> seen)
    {
        if (depth > 4 || !seen.Add(obj)) return "[...]";
        var o = new JObject();
        foreach (var prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0 || o.Count >= 150) continue;
            object? v;
            try { v = prop.GetValue(obj); } catch { continue; }
            if (v == null) { o[prop.Name] = JValue.CreateNull(); continue; }
            // SettingValue redacts string values under secret-looking names (bools, numbers, enums stay).
            o[prop.Name] = SettingValue(prop.Name, v, depth, seen);
        }
        return o;
    }

    /// <summary>Settings nodes (ToggleNode, RangeNode&lt;T&gt;, TextNode, ListNode, ColorNode, HotkeyNode...) by shape, not by type.</summary>
    private static JToken SettingValue(string name, object v, int depth, HashSet<object> seen)
    {
        var t = v.GetType();
        if (t.IsPrimitive || v is string || v is decimal || t.IsEnum)
            return SecretName.IsMatch(name) && v is string ? "[redacted]" : JToken.FromObject(t.IsEnum ? v.ToString()! : v);

        var baseName = t.Name.Contains('`') ? t.Name[..t.Name.IndexOf('`')] : t.Name; // RangeNode`1 -> RangeNode
        var valueProp = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (baseName.EndsWith("Node", StringComparison.Ordinal) && (valueProp == null || valueProp.GetIndexParameters().Length > 0))
            return new JObject { ["node"] = baseName }; // ButtonNode and other value-less nodes
        if (valueProp != null && valueProp.GetIndexParameters().Length == 0 && baseName.EndsWith("Node", StringComparison.Ordinal))
        {
            object? inner;
            try { inner = valueProp.GetValue(v); } catch { inner = null; }
            var node = new JObject { ["node"] = baseName };
            node["value"] = inner == null ? JValue.CreateNull()
                : SecretName.IsMatch(name) && inner is string ? "[redacted]"
                : inner is IConvertible || inner is Enum ? JToken.FromObject(inner is Enum ? inner.ToString()! : inner)
                : inner.ToString();
            foreach (var extra in new[] { "Min", "Max", "Values" })
            {
                var ep = t.GetProperty(extra, BindingFlags.Public | BindingFlags.Instance);
                if (ep == null) continue;
                try
                {
                    var ev = ep.GetValue(v);
                    if (ev is IEnumerable e && ev is not string) node[extra.ToLowerInvariant()] = new JArray(e.Cast<object>().Take(50).Select(x => x?.ToString()));
                    else if (ev != null) node[extra.ToLowerInvariant()] = JToken.FromObject(ev is IConvertible ? ev : ev.ToString()!);
                }
                catch { }
            }
            return node;
        }
        // Structs and framework types (DateTime, TimeSpan, Vector2, Color, Guid...) read best as text;
        // walking their properties produces pages of noise.
        if (t.IsValueType || (t.Namespace ?? "").StartsWith("System", StringComparison.Ordinal) && v is not IEnumerable)
            return v.ToString() ?? "";
        if (v is IEnumerable list && v is not string)
            return new JArray(list.Cast<object?>().Take(20).Select(x => x == null ? JValue.CreateNull() : (JToken)SettingValue(name, x, depth + 1, seen)));
        // Nested settings groups (submenus are plain objects of nodes).
        return SettingsNode(v, depth + 1, seen);
    }

    // ── PluginBridge registry ────────────────────────────────────────

    private JObject BridgeMethods()
    {
        var bridge = GameController.PluginBridge;
        var field = bridge?.GetType().GetField("_methods", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(bridge) is not IDictionary dict)
            return new JObject { ["error"] = "unavailable", ["message"] = "PluginBridge._methods not found in this HUD build (check with hud_type)." };
        var rows = new List<JObject>();
        foreach (DictionaryEntry e in dict)
        {
            var row = new JObject { ["name"] = e.Key?.ToString() };
            if (e.Value is Delegate d)
            {
                var m = d.Method;
                row["signature"] = $"{TypeName(m.ReturnType)} ({string.Join(", ", m.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name}"))})";
                row["delegateType"] = TypeName(d.GetType());
                row["declaredIn"] = m.DeclaringType?.FullName;
            }
            else row["valueType"] = e.Value?.GetType().FullName;
            rows.Add(row);
        }
        return new JObject
        {
            ["note"] = "Call from another plugin with GameController.PluginBridge.GetMethod<TDelegate>(name). " +
                       "Agents can invoke them with run_csharp (dynamic) - inspect side effects first.",
            ["methods"] = new JArray(rows.OrderBy(r => r["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)),
        };
    }

    private static string TypeName(Type t)
    {
        if (!t.IsGenericType) return t.Name;
        var n = t.Name[..t.Name.IndexOf('`')];
        return $"{n}<{string.Join(", ", t.GetGenericArguments().Select(TypeName))}>";
    }

    private static object? Prop(object? obj, string name)
    {
        if (obj == null) return null;
        try { return obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(obj); }
        catch { return null; }
    }
}
