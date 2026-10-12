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
///   hud.bridge_call      invoke one registered PluginBridge delegate by name (scalar arguments only) and return its result
/// Same API on ExileCore and ExileCore2: Core.Current.pluginManager.Plugins (PluginWrapper: Name, IsEnable,
/// Plugin, Tick/RenderDebugInformation), IPlugin._Settings, GameController.PluginBridge (private _methods).
/// Everything is read through reflection and tolerant of members a HUD build lacks. Nothing is written by the
/// introspection methods; hud.bridge_call runs whatever the named plugin method does.
/// </summary>
public partial class WhatsAnAiBridge
{
    private string? ProcessIntrospectionMethod(string method, JToken? p) => method switch
    {
        "hud.plugin_perf" => PluginPerf().ToString(Newtonsoft.Json.Formatting.None),
        "hud.plugin_settings" => PluginSettings(p?["name"]?.Value<string>()).ToString(Newtonsoft.Json.Formatting.None),
        "hud.bridge_methods" => BridgeMethods().ToString(Newtonsoft.Json.Formatting.None),
        "hud.bridge_call" => BridgeCall(p).ToString(Newtonsoft.Json.Formatting.None),
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
    private static readonly Regex SecretName = new("(token|secret|password|passwd|session|cookie|auth|apikey|api_key|key$|poesessid|connection|credential|webhook)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Secrets inside a value under an innocent name (e.g. a connection string's Password=..., a URL's ?token=...).
    private static readonly Regex SecretValue = new(@"(password|pwd|passwd|secret|token|api[_-]?key|access[_-]?key|sessid|bearer)\s*[=:]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when a string setting must not be shown: a secret-looking name, or a secret inside the value.</summary>
    private static bool IsSecret(string name, object? value) => SecretName.IsMatch(name) || (value is string s && SecretValue.IsMatch(s));

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
            return IsSecret(name, v) && v is string ? "[redacted]" : JToken.FromObject(t.IsEnum ? v.ToString()! : v);

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
                : IsSecret(name, inner) && inner is string ? "[redacted]"
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

    /// <summary>PluginBridge's private registry, or null when this HUD build has no _methods dictionary.</summary>
    private IDictionary? BridgeRegistry()
    {
        var bridge = GameController.PluginBridge;
        var field = bridge?.GetType().GetField("_methods", BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(bridge) as IDictionary;
    }

    private static JObject BridgeUnavailable() =>
        new() { ["error"] = "unavailable", ["message"] = "PluginBridge._methods not found in this HUD build (check with hud_type)." };

    private static string DelegateSignature(MethodInfo m) =>
        $"{TypeName(m.ReturnType)} ({string.Join(", ", m.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name}"))})";

    private JObject BridgeMethods()
    {
        if (BridgeRegistry() is not { } dict) return BridgeUnavailable();
        var rows = new List<JObject>();
        var live = LivePluginAssemblies();
        foreach (DictionaryEntry e in dict)
        {
            var row = new JObject { ["name"] = e.Key?.ToString() };
            if (e.Value is Delegate d)
            {
                var m = d.Method;
                row["signature"] = DelegateSignature(m);
                row["delegateType"] = TypeName(d.GetType());
                row["declaredIn"] = m.DeclaringType?.FullName;
                if (StaleDelegateNote(d, live) is { } stale) row["stale"] = stale;
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

    // ── Calling a PluginBridge method ────────────────────────────────

    /// <summary>
    /// hud.bridge_call {name, args?: [scalars]}: invoke one delegate another plugin registered on PluginBridge and return
    /// what it gave back. Every link that can break is named: the registry (unavailable), the method (method_missing,
    /// with what is registered), the value (not_a_delegate), the arguments (arity_mismatch / unsupported_parameter /
    /// bad_argument) and the call itself (invoke_failed, with the exception). A string result that parses as JSON comes
    /// back as `result` with resultType "json"; other strings as "text", scalars as "value", null as "null".
    /// </summary>
    private JObject BridgeCall(JToken? p)
    {
        var name = p?["name"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(name))
            return new JObject { ["error"] = "missing_name", ["message"] = "Pass name: a PluginBridge method as hud.bridge_methods lists it, e.g. WhatsARoute.GetState." };
        if (BridgeRegistry() is not { } dict) return BridgeUnavailable();

        if (!dict.Contains(name))
        {
            var registered = dict.Keys.Cast<object?>().Select(k => k?.ToString() ?? "").OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            var prefix = name.Contains('.') ? name[..name.IndexOf('.')] : name;
            var samePlugin = registered.Where(r => r.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)).ToList();
            return new JObject
            {
                ["error"] = "method_missing",
                ["message"] = samePlugin.Count > 0
                    ? $"PluginBridge has no '{name}'. '{prefix}' registers: {string.Join(", ", samePlugin)} (an older build of that plugin?)."
                    : $"PluginBridge has no '{name}' and nothing under '{prefix}.': the plugin that registers it is not loaded (hud.plugins lists loaded and failed plugins). Registered: {(registered.Count == 0 ? "nothing" : string.Join(", ", registered))}.",
                ["registered"] = new JArray(registered),
            };
        }
        var value = dict[name];
        if (value is not Delegate d)
            return new JObject { ["error"] = "not_a_delegate", ["message"] = $"'{name}' is registered as {value?.GetType().FullName ?? "null"}, not a delegate: it cannot be called." };

        var ps = d.Method.GetParameters();
        var signature = DelegateSignature(d.Method);
        var argsToken = p?["args"];
        if (argsToken != null && argsToken.Type != JTokenType.Null && argsToken is not JArray)
            return new JObject { ["error"] = "bad_argument", ["message"] = $"args must be an array of scalars ({signature}); got a {argsToken.Type}.", ["signature"] = signature };
        var argTokens = argsToken as JArray ?? new JArray();
        if (argTokens.Count != ps.Length)
            return new JObject { ["error"] = "arity_mismatch", ["message"] = $"'{name}' takes {ps.Length} argument(s) ({signature}); {argTokens.Count} given.", ["signature"] = signature };
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var t = ps[i].ParameterType;
            var scalar = t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);
            if (!scalar)
                return new JObject { ["error"] = "unsupported_parameter", ["message"] = $"'{name}' parameter {i} ({ps[i].Name}) is a {TypeName(t)}: hud.bridge_call passes scalars only (numbers, strings, bools, enums). Call it from run_csharp instead.", ["signature"] = signature };
            if (argTokens[i].Type == JTokenType.Null && t.IsValueType)
                return new JObject { ["error"] = "bad_argument", ["message"] = $"'{name}' parameter {i} ({ps[i].Name}) is a {TypeName(t)}: null is not a value for it.", ["signature"] = signature };
            try { args[i] = argTokens[i].Type == JTokenType.Null ? null : argTokens[i].ToObject(t); }
            catch (Exception ex) { return new JObject { ["error"] = "bad_argument", ["message"] = $"'{name}' parameter {i} ({ps[i].Name}, {TypeName(t)}): {ex.Message}", ["signature"] = signature }; }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        object? result;
        try { result = d.DynamicInvoke(args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            return new JObject { ["error"] = "invoke_failed", ["message"] = $"'{name}' threw {tie.InnerException.GetType().Name}: {tie.InnerException.Message}", ["signature"] = signature, ["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2) };
        }
        catch (Exception ex)
        {
            return new JObject { ["error"] = "invoke_failed", ["message"] = $"'{name}' could not be invoked: {ex.GetType().Name}: {ex.Message}", ["signature"] = signature, ["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2) };
        }
        var reply = new JObject { ["ok"] = true, ["name"] = name, ["signature"] = signature, ["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2) };
        if (StaleDelegateNote(d, LivePluginAssemblies()) is { } stale) reply["stale"] = $"'{name}' is {stale}";
        switch (result)
        {
            case null:
                reply["resultType"] = "null"; reply["result"] = JValue.CreateNull(); break;
            case string s:
                var trimmed = s.TrimStart();
                JToken? parsed = null;
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                    try { parsed = JToken.Parse(s); } catch (Newtonsoft.Json.JsonException) { parsed = null; }
                if (parsed != null) { reply["resultType"] = "json"; reply["result"] = parsed; }
                else { reply["resultType"] = "text"; reply["result"] = s; }
                break;
            case IConvertible c when result.GetType().IsPrimitive || result is decimal || result is Enum:
                reply["resultType"] = "value"; reply["result"] = result is Enum ? result.ToString() : JToken.FromObject(c); break;
            default:
                reply["resultType"] = "value";
                try { reply["result"] = JToken.FromObject(result); }
                catch (Exception ex) { reply["result"] = result.ToString(); reply["note"] = $"result of type {result.GetType().FullName} could not be serialized ({ex.Message}); its ToString() is given"; }
                break;
        }
        return reply;
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
