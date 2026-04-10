using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ExileCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Walks the ExileApi object graph via reflection, evaluating dotted-path
/// expressions like "GameController.Player.GetComponent&lt;Life&gt;().CurHP".
/// Read-only, public members only, with namespace allowlist and timeout.
/// </summary>
public class ExpressionWalker
{
    private const int MaxSerializationDepth = 2;
    private const int MaxResultSizeBytes = 65536;
    private const int TimeoutMs = 150;

    private static readonly HashSet<string> AllowedNamespaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExileCore",
        "GameOffsets",
        "System.Collections.Generic",
        "System",
        "SharpDX",
    };

    private static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal)
    {
        "GetComponent",
        "GetChildAtIndex",
        "ToString",
        "HasComponent",
    };

    private readonly GameController _gc;

    // Cache of component type lookups
    private static Dictionary<string, Type>? _componentTypeCache;

    public ExpressionWalker(GameController gc)
    {
        _gc = gc;
    }

    /// <summary>
    /// Evaluate a dotted-path expression and return the result as JSON.
    /// </summary>
    public string Evaluate(string expression)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);

        try
        {
            var segments = ParseExpression(expression);
            if (segments.Count == 0)
                return Error("Empty expression");

            // Root must be GameController
            if (segments[0].Name != "GameController")
                return Error("Expression must start with 'GameController'");

            object? current = _gc;
            var currentType = _gc.GetType();

            for (int i = 1; i < segments.Count; i++)
            {
                if (DateTime.UtcNow > deadline)
                    return Error($"Evaluation timed out at segment '{segments[i].Name}'");

                if (current == null)
                    return Error($"Null reference at segment '{segments[i].Name}' (after '{segments[i - 1].Name}')");

                var seg = segments[i];
                currentType = current.GetType();

                if (!IsTypeAllowed(currentType))
                    return Error($"Type '{currentType.FullName}' is not in the allowed namespace list");

                if (seg.IsMethodCall)
                {
                    current = InvokeMethod(current, currentType, seg, out var error);
                    if (error != null) return Error(error);
                }
                else if (seg.HasIndexer)
                {
                    // First resolve the property, then index into it
                    var prop = currentType.GetProperty(seg.Name, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null)
                    {
                        current = prop.GetValue(current);
                        if (current == null)
                            return Error($"Property '{seg.Name}' returned null");
                    }
                    else
                    {
                        var field = currentType.GetField(seg.Name, BindingFlags.Public | BindingFlags.Instance);
                        if (field != null)
                            current = field.GetValue(current);
                        else
                            return Error($"No public property or field '{seg.Name}' on type '{currentType.Name}'");
                    }

                    current = ApplyIndexer(current, seg.IndexerValue!, out var indexError);
                    if (indexError != null) return Error(indexError);
                }
                else
                {
                    var prop = currentType.GetProperty(seg.Name, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null)
                    {
                        current = prop.GetValue(current);
                    }
                    else
                    {
                        var field = currentType.GetField(seg.Name, BindingFlags.Public | BindingFlags.Instance);
                        if (field != null)
                            current = field.GetValue(current);
                        else
                            return Error($"No public property or field '{seg.Name}' on type '{currentType.Name}'");
                    }
                }
            }

            return SerializeResult(current, expression);
        }
        catch (TargetInvocationException ex)
        {
            return Error($"Invocation error: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            return Error($"Evaluation error: {ex.Message}");
        }
    }

    /// <summary>
    /// Describe the public members of the type at a given path expression.
    /// </summary>
    public string DescribeType(string expression)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);

        try
        {
            Type targetType;

            if (expression == "GameController")
            {
                targetType = _gc.GetType();
            }
            else
            {
                var segments = ParseExpression(expression);
                if (segments.Count == 0)
                    return Error("Empty expression");

                if (segments[0].Name != "GameController")
                    return Error("Expression must start with 'GameController'");

                object? current = _gc;

                for (int i = 1; i < segments.Count; i++)
                {
                    if (DateTime.UtcNow > deadline)
                        return Error("Timed out resolving path");

                    if (current == null)
                        return Error($"Null reference at '{segments[i].Name}'");

                    var seg = segments[i];
                    var currentType = current.GetType();

                    if (seg.IsMethodCall)
                    {
                        current = InvokeMethod(current, currentType, seg, out var error);
                        if (error != null) return Error(error);
                    }
                    else
                    {
                        var prop = currentType.GetProperty(seg.Name, BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null)
                            current = prop.GetValue(current);
                        else
                        {
                            var field = currentType.GetField(seg.Name, BindingFlags.Public | BindingFlags.Instance);
                            if (field != null)
                                current = field.GetValue(current);
                            else
                                return Error($"No public member '{seg.Name}' on '{currentType.Name}'");
                        }
                    }
                }

                if (current == null)
                    return Error("Terminal value is null");

                targetType = current.GetType();
            }

            return DescribeTypeMembers(targetType, expression);
        }
        catch (Exception ex)
        {
            return Error($"Describe error: {ex.Message}");
        }
    }

    // ── Parsing ────────────────────────────────────────────────────────

    private class PathSegment
    {
        public string Name { get; set; } = "";
        public bool IsMethodCall { get; set; }
        public string? GenericArg { get; set; }
        public string? MethodArg { get; set; }
        public bool HasIndexer { get; set; }
        public string? IndexerValue { get; set; }
    }

    private static List<PathSegment> ParseExpression(string expr)
    {
        var segments = new List<PathSegment>();
        int pos = 0;

        while (pos < expr.Length)
        {
            // Skip dots
            if (expr[pos] == '.')
            {
                pos++;
                continue;
            }

            var seg = new PathSegment();

            // Read name
            var nameStart = pos;
            while (pos < expr.Length && expr[pos] != '.' && expr[pos] != '<' && expr[pos] != '(' && expr[pos] != '[')
                pos++;
            seg.Name = expr[nameStart..pos];

            // Check for generic <T>
            if (pos < expr.Length && expr[pos] == '<')
            {
                pos++; // skip <
                var genericStart = pos;
                var depth = 1;
                while (pos < expr.Length && depth > 0)
                {
                    if (expr[pos] == '<') depth++;
                    else if (expr[pos] == '>') depth--;
                    if (depth > 0) pos++;
                }
                seg.GenericArg = expr[genericStart..pos];
                pos++; // skip >
                seg.IsMethodCall = true;

                // Skip ()
                if (pos < expr.Length && expr[pos] == '(')
                {
                    pos++; // skip (
                    var argStart = pos;
                    while (pos < expr.Length && expr[pos] != ')') pos++;
                    if (pos > argStart) seg.MethodArg = expr[argStart..pos];
                    pos++; // skip )
                }
            }
            // Check for method call ()
            else if (pos < expr.Length && expr[pos] == '(')
            {
                seg.IsMethodCall = true;
                pos++; // skip (
                var argStart = pos;
                while (pos < expr.Length && expr[pos] != ')') pos++;
                if (pos > argStart) seg.MethodArg = expr[argStart..pos];
                pos++; // skip )
            }

            // Check for indexer [N] or ["key"]
            if (pos < expr.Length && expr[pos] == '[')
            {
                pos++; // skip [
                var idxStart = pos;
                while (pos < expr.Length && expr[pos] != ']') pos++;
                seg.IndexerValue = expr[idxStart..pos].Trim('"', '\'');
                seg.HasIndexer = true;
                pos++; // skip ]
            }

            if (!string.IsNullOrEmpty(seg.Name))
                segments.Add(seg);
        }

        return segments;
    }

    // ── Method invocation ──────────────────────────────────────────────

    private object? InvokeMethod(object target, Type targetType, PathSegment seg, out string? error)
    {
        error = null;

        if (!AllowedMethods.Contains(seg.Name))
        {
            error = $"Method '{seg.Name}' is not in the allowed methods list. Allowed: {string.Join(", ", AllowedMethods)}";
            return null;
        }

        if (seg.Name == "GetComponent" && seg.GenericArg != null)
        {
            var compType = ResolveComponentType(seg.GenericArg);
            if (compType == null)
            {
                error = $"Could not resolve component type '{seg.GenericArg}'";
                return null;
            }

            var method = targetType.GetMethod("GetComponent", Type.EmptyTypes);
            if (method == null)
            {
                error = $"No GetComponent method found on {targetType.Name}";
                return null;
            }

            var generic = method.MakeGenericMethod(compType);
            return generic.Invoke(target, null);
        }

        if (seg.Name == "HasComponent" && seg.GenericArg != null)
        {
            var compType = ResolveComponentType(seg.GenericArg);
            if (compType == null)
            {
                error = $"Could not resolve component type '{seg.GenericArg}'";
                return null;
            }

            var method = targetType.GetMethod("HasComponent", Type.EmptyTypes);
            if (method == null)
            {
                error = $"No HasComponent method found on {targetType.Name}";
                return null;
            }

            var generic = method.MakeGenericMethod(compType);
            return generic.Invoke(target, null);
        }

        if (seg.Name == "GetChildAtIndex" && seg.MethodArg != null)
        {
            if (!int.TryParse(seg.MethodArg, out var index))
            {
                error = "GetChildAtIndex requires an integer argument";
                return null;
            }

            var method = targetType.GetMethod("GetChildAtIndex", [typeof(int)]);
            if (method == null)
            {
                error = $"No GetChildAtIndex method on {targetType.Name}";
                return null;
            }

            return method.Invoke(target, [index]);
        }

        if (seg.Name == "ToString")
        {
            return target.ToString();
        }

        error = $"Unhandled method invocation: {seg.Name}";
        return null;
    }

    private static Type? ResolveComponentType(string typeName)
    {
        if (_componentTypeCache == null)
        {
            _componentTypeCache = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Namespace?.StartsWith("ExileCore") == true
                            || type.Namespace?.StartsWith("GameOffsets") == true)
                        {
                            _componentTypeCache[type.Name] = type;
                            if (type.FullName != null)
                                _componentTypeCache[type.FullName] = type;
                        }
                    }
                }
                catch { }
            }
        }

        _componentTypeCache.TryGetValue(typeName, out var result);
        return result;
    }

    // ── Indexer application ────────────────────────────────────────────

    private static object? ApplyIndexer(object? collection, string indexValue, out string? error)
    {
        error = null;
        if (collection == null)
        {
            error = "Cannot index into null";
            return null;
        }

        var type = collection.GetType();

        // Integer index
        if (int.TryParse(indexValue, out var intIndex))
        {
            if (collection is IList list)
                return intIndex < list.Count ? list[intIndex] : throw new IndexOutOfRangeException($"Index {intIndex} out of range (count: {list.Count})");

            // Try indexer property
            var indexer = type.GetProperty("Item", [typeof(int)]);
            if (indexer != null)
                return indexer.GetValue(collection, [intIndex]);
        }

        // String key (dictionary)
        if (collection is IDictionary dict)
        {
            // Try string key first
            if (dict.Contains(indexValue))
                return dict[indexValue];

            // Try enum key (common for GameStat dictionaries)
            foreach (var key in dict.Keys)
            {
                if (key?.ToString() == indexValue)
                    return dict[key];
            }

            error = $"Key '{indexValue}' not found in dictionary";
            return null;
        }

        // Try generic IDictionary with enum keys
        var indexerProp = type.GetProperty("Item");
        if (indexerProp != null)
        {
            var keyType = indexerProp.GetIndexParameters().FirstOrDefault()?.ParameterType;
            if (keyType?.IsEnum == true && Enum.TryParse(keyType, indexValue, true, out var enumKey))
                return indexerProp.GetValue(collection, [enumKey]);
        }

        error = $"Cannot apply indexer [{indexValue}] to type {type.Name}";
        return null;
    }

    // ── Type safety ────────────────────────────────────────────────────

    private static bool IsTypeAllowed(Type type)
    {
        var ns = type.Namespace;
        if (ns == null) return false;

        // Block dangerous namespaces explicitly
        if (ns.StartsWith("System.IO") || ns.StartsWith("System.Net")
            || ns.StartsWith("System.Diagnostics") || ns.StartsWith("System.Reflection"))
            return false;

        return AllowedNamespaces.Any(allowed => ns.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));
    }

    // ── Serialization ──────────────────────────────────────────────────

    private static string SerializeResult(object? value, string expression)
    {
        var result = new JObject
        {
            ["expression"] = expression,
        };

        if (value == null)
        {
            result["value"] = JValue.CreateNull();
            result["type"] = "null";
        }
        else if (value is string s)
        {
            result["value"] = s;
            result["type"] = "string";
        }
        else if (value.GetType().IsPrimitive || value is decimal)
        {
            result["value"] = JToken.FromObject(value);
            result["type"] = value.GetType().Name;
        }
        else if (value.GetType().IsEnum)
        {
            result["value"] = value.ToString();
            result["type"] = value.GetType().Name;
            result["numericValue"] = Convert.ToInt32(value);
        }
        else
        {
            result["type"] = value.GetType().FullName;
            result["value"] = SerializeObject(value, 0);
        }

        var json = result.ToString(Formatting.None);
        if (json.Length > MaxResultSizeBytes)
        {
            result["value"] = "[Result truncated - exceeded 64KB limit]";
            result["truncated"] = true;
            json = result.ToString(Formatting.None);
        }

        return json;
    }

    private static JToken SerializeObject(object obj, int depth)
    {
        if (depth > MaxSerializationDepth)
            return $"[depth limit: {obj.GetType().Name}]";

        if (obj is IDictionary dict)
        {
            var jObj = new JObject();
            int count = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (count++ > 100)
                {
                    jObj["_truncated"] = $"{dict.Count} total entries";
                    break;
                }
                var key = entry.Key?.ToString() ?? "null";
                jObj[key] = entry.Value == null ? JValue.CreateNull() : SerializeValue(entry.Value, depth + 1);
            }
            return jObj;
        }

        if (obj is IEnumerable enumerable and not string)
        {
            var jArr = new JArray();
            int count = 0;
            foreach (var item in enumerable)
            {
                if (count++ > 50)
                {
                    jArr.Add("[... more items]");
                    break;
                }
                jArr.Add(item == null ? JValue.CreateNull() : SerializeValue(item, depth + 1));
            }
            return jArr;
        }

        // Object: enumerate public properties
        var type = obj.GetType();
        var result = new JObject { ["_type"] = type.Name };

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in props.Take(50))
        {
            try
            {
                if (prop.GetIndexParameters().Length > 0) continue; // Skip indexed properties
                var val = prop.GetValue(obj);
                result[prop.Name] = val == null ? JValue.CreateNull() : SerializeValue(val, depth + 1);
            }
            catch (Exception ex)
            {
                result[prop.Name] = $"[error: {ex.InnerException?.Message ?? ex.Message}]";
            }
        }

        return result;
    }

    private static JToken SerializeValue(object value, int depth)
    {
        if (value is string s) return s;
        if (value is bool b) return b;
        if (value is int i) return i;
        if (value is long l) return l;
        if (value is float f) return float.IsFinite(f) ? f : 0f;
        if (value is double d) return double.IsFinite(d) ? d : 0d;
        if (value is uint u) return u;
        if (value is byte by) return by;
        if (value is short sh) return sh;
        if (value is ushort us) return us;
        if (value.GetType().IsEnum) return value.ToString()!;
        if (value.GetType().IsPrimitive) return JToken.FromObject(value);

        if (depth > MaxSerializationDepth)
            return $"[{value.GetType().Name}]";

        return SerializeObject(value, depth);
    }

    private static string DescribeTypeMembers(Type type, string expression)
    {
        var result = new JObject
        {
            ["expression"] = expression,
            ["type"] = type.FullName,
        };

        var props = new JArray();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        {
            if (prop.GetIndexParameters().Length > 0) continue; // Skip indexed
            props.Add(new JObject
            {
                ["name"] = prop.Name,
                ["type"] = FormatTypeName(prop.PropertyType),
                ["canRead"] = prop.CanRead,
            });
        }
        result["properties"] = props;

        var methods = new JArray();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName) // Skip property getters/setters
            .OrderBy(m => m.Name)
            .Take(30))
        {
            var mObj = new JObject
            {
                ["name"] = method.Name,
                ["returnType"] = FormatTypeName(method.ReturnType),
            };

            var paramList = method.GetParameters();
            if (paramList.Length > 0)
            {
                var pArr = new JArray();
                foreach (var p in paramList)
                    pArr.Add(new JObject { ["name"] = p.Name, ["type"] = FormatTypeName(p.ParameterType) });
                mObj["parameters"] = pArr;
            }

            if (method.IsGenericMethodDefinition)
                mObj["isGeneric"] = true;

            methods.Add(mObj);
        }
        result["methods"] = methods;

        var fields = new JArray();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name))
        {
            fields.Add(new JObject
            {
                ["name"] = field.Name,
                ["type"] = FormatTypeName(field.FieldType),
            });
        }
        if (fields.Count > 0)
            result["fields"] = fields;

        return result.ToString(Formatting.None);
    }

    private static string FormatTypeName(Type type)
    {
        if (type.IsGenericType)
        {
            var name = type.Name;
            var backtick = name.IndexOf('`');
            if (backtick >= 0) name = name[..backtick];
            var args = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
            return $"{name}<{args}>";
        }
        return type.Name;
    }

    private static string Error(string message)
    {
        return new JObject
        {
            ["error"] = message,
        }.ToString(Formatting.None);
    }
}
