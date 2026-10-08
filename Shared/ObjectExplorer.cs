using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// object.explore: one level of the live object model at a walker path, shaped for mapping data out
/// rather than dumping it. For the node and each child: declared/runtime type, a kind (number, string,
/// enum, struct, object, list, dictionary, component...), a one-line preview (structs as "X=1 Y=2"),
/// collection counts, and two ways to reach it: the walker path (for eval_path / watch_object / the
/// next explore call) and null-safe C# to paste into a plugin. Entities also list their components.
/// Collections are paged. Reads run on the main thread under a time budget; members left unread when
/// it runs out are listed in "skipped" (explore them directly). Read-only, public members only.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int ExploreBudgetMs = 60;
    private const int SlowGetterMs = 5;

    private string? ProcessExploreMethod(string method, JToken? p)
    {
        if (method != "object.explore") return null;
        var path = (p?["path"]?.Value<string>() ?? "GameController").Trim();
        return Explore(path, p?["csharp"]?.Value<string>(),
                Math.Max(0, p?["offset"]?.Value<int>() ?? 0), Math.Clamp(p?["limit"]?.Value<int>() ?? 50, 1, 200))
            .ToString(Newtonsoft.Json.Formatting.None);
    }

    private JObject Explore(string path, string? csharp, int offset, int limit)
    {
        var walker = new ExpressionWalker(GameController);
        object? value;
        try
        {
            value = walker.Resolve(path, out var error);
            if (error != null) return new JObject { ["error"] = "resolve_failed", ["message"] = error, ["path"] = path };
        }
        catch (Exception ex)
        {
            return new JObject { ["error"] = "resolve_failed", ["message"] = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message, ["path"] = path };
        }

        csharp ??= ToCSharp(path);
        var node = Describe(value, value?.GetType());
        node.AddFirst(new JProperty("path", path));
        node["csharp"] = csharp;
        if (value == null) return node;

        var type = value.GetType();
        if (type.Namespace != null) node["namespace"] = type.Namespace;
        if (!ExpressionWalker.IsTypeAllowed(type) && !type.IsPrimitive && value is not string)
        {
            node["note"] = "Type is outside the walker's allowed namespaces; its members can't be explored.";
            return node;
        }

        var sw = Stopwatch.StartNew();
        var skipped = new List<string>();
        var children = new JArray();

        if (value is IDictionary dict)
        {
            var keyType = type.IsGenericType ? type.GetGenericArguments().FirstOrDefault() : null;
            var i = 0;
            foreach (DictionaryEntry e in dict)
            {
                if (i++ < offset) continue;
                if (children.Count >= limit) break;
                var key = e.Key?.ToString() ?? "null";
                children.Add(Child($"[{key}]", e.Value, e.Value?.GetType(), $"{path}[\"{key}\"]", $"{csharp}?[{KeyLiteral(e.Key, keyType)}]"));
            }
            node["page"] = new JObject { ["offset"] = offset, ["limit"] = limit, ["total"] = dict.Count };
        }
        else if (value is IList list)
        {
            // Nested indexers (a[0][1]) aren't walker syntax, so items of an indexed path get no path.
            var addressable = !path.EndsWith("]", StringComparison.Ordinal);
            for (int i = offset; i < list.Count && children.Count < limit; i++)
            {
                if (sw.ElapsedMilliseconds > ExploreBudgetMs) { skipped.Add($"[{i}..]"); break; }
                object? item;
                try { item = list[i]; } catch (Exception ex) { children.Add(new JObject { ["name"] = $"[{i}]", ["error"] = Message(ex) }); continue; }
                children.Add(Child($"[{i}]", item, item?.GetType(), addressable ? $"{path}[{i}]" : null, $"{csharp}?[{i}]"));
            }
            node["page"] = new JObject { ["offset"] = offset, ["limit"] = limit, ["total"] = list.Count };
        }
        else if (value is IEnumerable seq and not string)
        {
            var i = 0;
            foreach (var item in seq)
            {
                if (sw.ElapsedMilliseconds > ExploreBudgetMs) { skipped.Add($"[{i}..]"); break; }
                if (i < offset) { i++; continue; }
                if (children.Count >= limit) break;
                children.Add(Child($"[{i}]", item, item?.GetType(), null, $"{csharp}?.ElementAt({i})"));
                i++;
            }
            node["page"] = new JObject { ["offset"] = offset, ["limit"] = limit };
            node["note"] = "Not indexable by the walker: items have C# (System.Linq ElementAt) but no path.";
        }
        else if (!type.IsPrimitive && !type.IsEnum && value is not string)
        {
            var members = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetIndexParameters().Length == 0 && m.GetMethod != null)
                .Select(m => (name: m.Name, declared: m.PropertyType, read: (Func<object?>)(() => m.GetValue(value))))
                .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Select(f => (name: f.Name, declared: f.FieldType, read: (Func<object?>)(() => f.GetValue(value)))))
                .OrderBy(m => m.name, StringComparer.Ordinal);
            foreach (var (name, declared, read) in members)
            {
                var childPath = $"{path}.{name}";
                // Members of a struct take '.': inside a ?. chain the struct isn't nullable, and '?.' on it is CS0023.
                var childCs = type.IsValueType ? $"{csharp}.{name}" : $"{csharp}?.{name}";
                if (sw.ElapsedMilliseconds > ExploreBudgetMs) { skipped.Add(name); continue; }
                if (!ExpressionWalker.IsTypeAllowed(declared) && !declared.IsPrimitive && declared != typeof(string) && !declared.IsEnum)
                {
                    children.Add(new JObject { ["name"] = name, ["type"] = ExpressionWalker.FormatTypeName(declared), ["kind"] = "blocked", ["csharp"] = childCs });
                    continue;
                }
                var t0 = sw.ElapsedMilliseconds;
                object? v;
                try { v = read(); }
                catch (Exception ex)
                {
                    children.Add(new JObject { ["name"] = name, ["type"] = ExpressionWalker.FormatTypeName(declared), ["error"] = Message(ex), ["path"] = childPath, ["csharp"] = childCs });
                    continue;
                }
                var child = Child(name, v, declared, childPath, childCs);
                var ms = sw.ElapsedMilliseconds - t0;
                if (ms >= SlowGetterMs) child["slowMs"] = ms;
                children.Add(child);
            }

            var components = Components(value, path, csharp);
            if (components != null) node["components"] = components;
        }

        node["children"] = children;
        if (skipped.Count > 0)
            node["skipped"] = new JObject { ["reason"] = $"time budget ({ExploreBudgetMs} ms) used up", ["members"] = new JArray(skipped) };
        node["elapsedMs"] = sw.ElapsedMilliseconds;
        return node;
    }

    private static JObject Child(string name, object? value, Type? declared, string? path, string csharp)
    {
        var o = new JObject { ["name"] = name };
        foreach (var p in Describe(value, declared).Properties()) o.Add(p.Name, p.Value);
        if (path != null) o["path"] = path;
        o["csharp"] = csharp;
        return o;
    }

    /// <summary>Type, kind, preview, count and whether the value has anything below it.</summary>
    private static JObject Describe(object? value, Type? declared)
    {
        var runtime = value?.GetType();
        var o = new JObject { ["type"] = ExpressionWalker.FormatTypeName(runtime ?? declared ?? typeof(object)) };
        if (runtime != null && declared != null && runtime != declared && !declared.IsInterface && declared != typeof(object))
            o["declaredType"] = ExpressionWalker.FormatTypeName(declared);

        if (value == null) { o["kind"] = "null"; o["preview"] = "null"; return o; }
        var t = runtime!;
        string kind;
        bool expandable;
        switch (value)
        {
            case string s: kind = "string"; o["preview"] = Quote(s); expandable = false; break;
            case bool b: kind = "bool"; o["preview"] = b ? "true" : "false"; expandable = false; break;
            case Enum e: kind = "enum"; o["preview"] = e.ToString(); expandable = false; break;
            case IDictionary d: kind = "dictionary"; o["count"] = d.Count; o["preview"] = $"Count = {d.Count}"; expandable = d.Count > 0; break;
            case ICollection c: kind = "list"; o["count"] = c.Count; o["preview"] = $"Count = {c.Count}"; expandable = c.Count > 0; break;
            default:
                if (t.IsPrimitive || value is decimal) { kind = "number"; o["preview"] = Number(value); expandable = false; }
                else if (value is IEnumerable) { kind = "sequence"; o["preview"] = t.Name; expandable = true; }
                else if (t.IsValueType) { kind = "struct"; o["preview"] = StructPreview(value, t); expandable = true; }
                else { kind = "object"; o["preview"] = ObjectPreview(value, t); expandable = true; }
                break;
        }
        o["kind"] = kind;
        if (expandable) o["expandable"] = true;
        return o;
    }

    /// <summary>Entity components by name (from the entity's component cache), each reachable with GetComponent&lt;T&gt;().</summary>
    private static JArray? Components(object entity, string path, string csharp)
    {
        if (entity.GetType().Name != "Entity") return null;
        var cache = entity.GetType().GetProperty("CacheComp", BindingFlags.Public | BindingFlags.Instance);
        IDictionary? names;
        try { names = cache?.GetValue(entity) as IDictionary; } catch { return null; }
        if (names == null) return null;
        var rows = new List<JObject>();
        foreach (var key in names.Keys)
        {
            var name = key?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            var known = ExpressionWalker.ResolveComponentType(name) != null;
            var row = new JObject { ["name"] = name, ["kind"] = "component" };
            if (known)
            {
                row["path"] = $"{path}.GetComponent<{name}>()";
                row["csharp"] = $"{csharp}?.GetComponent<{name}>()";
                row["expandable"] = true;
            }
            else row["note"] = "No HUD wrapper type for this component";
            rows.Add(row);
        }
        return new JArray(rows.OrderBy(r => r["path"] == null).ThenBy(r => r["name"]!.ToString(), StringComparer.Ordinal));
    }

    // ── Previews ─────────────────────────────────────────────────────

    private static string Number(object v) => v switch
    {
        float f => float.IsFinite(f) ? f.ToString("0.###", CultureInfo.InvariantCulture) : f.ToString(CultureInfo.InvariantCulture),
        double d => double.IsFinite(d) ? d.ToString("0.###", CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture),
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string Quote(string s) => "\"" + (s.Length > 80 ? s[..77] + "..." : s).Replace("\n", "\\n") + "\"";

    /// <summary>Vector2/3, Color, RectangleF...: "X=1.5 Y=2". Up to 6 public numeric fields or properties.</summary>
    private static string StructPreview(object v, Type t)
    {
        // TimeSpan, DateTime, Guid, CancellationToken...: their own text reads better than their properties.
        // Vector/Point/Color types (System.Numerics, System.Drawing, SharpDX) keep the field view.
        var ns = t.Namespace ?? "";
        if (ns == "System" || ns.StartsWith("System.Threading", StringComparison.Ordinal))
        {
            var text = v.ToString() ?? t.Name;
            return text.Length > 80 ? text[..77] + "..." : text;
        }
        var parts = new List<string>();
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (parts.Count >= 6) break;
            if (f.FieldType.IsPrimitive && f.FieldType != typeof(bool)) parts.Add($"{f.Name}={Number(f.GetValue(v)!)}");
        }
        if (parts.Count == 0)
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (parts.Count >= 6) break;
                if (!p.PropertyType.IsPrimitive || p.PropertyType == typeof(bool) || p.GetIndexParameters().Length > 0) continue;
                try { parts.Add($"{p.Name}={Number(p.GetValue(v)!)}"); } catch { }
            }
        if (parts.Count > 0) return string.Join(" ", parts);
        var s = v.ToString() ?? t.Name;
        return s.Length > 80 ? s[..77] + "..." : s;
    }

    /// <summary>
    /// The type name, plus a naming property (RenderName, Name, Text, Path...) when the object has one,
    /// and "visible"/"hidden" for UI elements (IsVisible).
    /// </summary>
    private static string ObjectPreview(object v, Type t)
    {
        var sb = new StringBuilder(t.Name);
        foreach (var name in new[] { "RenderName", "Name", "Text", "Path", "Id" })
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || p.GetIndexParameters().Length > 0 || !(p.PropertyType == typeof(string) || p.PropertyType.IsPrimitive)) continue;
            try
            {
                var val = p.GetValue(v);
                if (val is string s && s.Length == 0) continue;
                if (val != null) { sb.Append(' ').Append(name).Append('=').Append(val is string str ? Quote(str) : Number(val)); break; }
            }
            catch { }
        }
        if (t.GetProperty("IsVisible", BindingFlags.Public | BindingFlags.Instance) is { } vis && vis.PropertyType == typeof(bool))
            try { sb.Append((bool)vis.GetValue(v)! ? " visible" : " hidden"); } catch { }
        return sb.ToString();
    }

    // ── C# accessors ─────────────────────────────────────────────────

    /// <summary>Key as a C# literal: GameStat.MaximumLife, "text", 42.</summary>
    private static string KeyLiteral(object? key, Type? keyType)
    {
        if (key == null) return "null";
        if (key is Enum) return $"{key.GetType().Name}.{key}";
        if (key is string s) return "\"" + s.Replace("\"", "\\\"") + "\"";
        return Number(key);
    }

    /// <summary>
    /// Walker path to null-safe C# (a plugin has GameController in scope): '.' -> '?.', '[' -> '?['.
    /// Callers that know enum dictionary keys pass their own C# (explore results carry it per child).
    /// </summary>
    private static string ToCSharp(string path)
    {
        var sb = new StringBuilder(path.Length + 8);
        int angle = 0;
        bool quoted = false;
        for (int i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '<') angle++;
            else if (!quoted && c == '>') angle--;
            if (!quoted && angle == 0 && (c == '.' || c == '[') && i > 0 && path[i - 1] != '?') sb.Append('?');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Message(Exception ex) => (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
}
