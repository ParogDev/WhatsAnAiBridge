using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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

    // Game API namespaces come from the per-game partial (ExileCore/GameOffsets[/SharpDX] or ExileCore2/GameOffsets2).
    private static readonly HashSet<string> AllowedNamespaces = new(
        WhatsAnAiBridge.ApiNamespaces.Concat(["System.Collections.Generic", "System"]),
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal)
    {
        "GetComponent",
        "GetChildAtIndex",
        "ToString",
        "HasComponent",
    };

    private readonly GameController _gc;

    public ExpressionWalker(GameController gc)
    {
        _gc = gc;
    }

    /// <summary>
    /// Evaluate a dotted-path expression and return the result as JSON.
    /// </summary>
    public string Evaluate(string expression)
    {
        try
        {
            var current = Resolve(expression, out var error);
            return error != null ? Error(error) : SerializeResult(current, expression);
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
    /// Resolve a path to its live object (null is a valid result). Members and indexers on the path are
    /// read with the same rules as Evaluate; getters may throw (callers catch).
    /// </summary>
    internal object? Resolve(string expression, out string? error)
    {
        error = null;
        // The first GetComponent<T> builds the type index (every loaded assembly's types); that one-off
        // cost must not count against the path's deadline (it timed out the first call after a HUD start).
        if (expression.Contains("Component<", StringComparison.Ordinal))
            _ = MemberIndex.Types;
        var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
        var segments = Parsed(expression);
        if (segments.Count == 0) { error = "Empty expression"; return null; }
        // Root must be GameController
        if (segments[0].Name != "GameController") { error = "Expression must start with 'GameController'"; return null; }
        if (segments[0].HasIndexer || segments[0].IsMethodCall) { error = "GameController can't be indexed or called"; return null; }

        object? current = _gc;
        for (int i = 1; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (DateTime.UtcNow > deadline) { error = $"Evaluation timed out at segment '{seg.Name}'"; return null; }
            if (current == null) { error = $"Null reference at segment '{seg.Name}' (after '{segments[i - 1].Name}')"; return null; }

            var currentType = current.GetType();
            if (!IsTypeAllowed(currentType)) { error = $"Type '{currentType.FullName}' is not in the allowed namespace list"; return null; }

            if (seg.IsMethodCall)
            {
                current = InvokeMethod(current, currentType, seg, out error);
                if (error != null) return null;
            }
            else
            {
                // Most-derived property (never an indexer), else field; ambiguity-safe (NormalInventoryItem.Item vs this[int]).
                if (MemberIndex.Find(currentType, seg.Name) is { CanRead: true } member) current = member.Get(current);
                else { error = $"No public property or field '{seg.Name}' on type '{currentType.Name}'"; return null; }
            }

            if (seg.HasIndexer)
            {
                if (current == null) { error = $"'{seg.Name}' returned null"; return null; }
                current = ApplyIndexer(current, seg.IndexerValue!, out error);
                if (error != null) return null;
            }
        }
        return current;
    }

    /// <summary>
    /// Describe the public members of the type at a given path expression.
    /// </summary>
    public string DescribeType(string expression)
    {
        try
        {
            var current = Resolve(expression, out var error);
            if (error != null) return Error(error);
            if (current == null) return Error("Terminal value is null");
            return DescribeTypeMembers(current.GetType(), expression);
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
        /// <summary>GetComponent/HasComponent: the resolved component type, once found (a miss is looked up again next time).
        /// Resolve caches only ever hold types of non-collectible assemblies (the HUD's own), and ClearCaches drops them on unload.</summary>
        public volatile Type? ComponentType;
        /// <summary>GetComponent/HasComponent: the last target type and its closed method (one entry: a path's targets rarely vary).</summary>
        public volatile Tuple<Type, MethodInfo>? LastMethod;
    }

    // Parsed paths, cached by text: observer layers, flows and experiment watches resolve the same paths several times a
    // second. Segments are never changed after parsing, except their resolve caches above (each a single reference write).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<PathSegment>> _parsed = new(StringComparer.Ordinal);
    private const int MaxParsedPaths = 2048;

    private static List<PathSegment> Parsed(string expr)
    {
        if (_parsed.TryGetValue(expr, out var hit)) return hit;
        var segs = ParseExpression(expr);
        if (_parsed.Count >= MaxParsedPaths) _parsed.Clear();   // one-off paths from agents must not grow it forever
        return _parsed.GetOrAdd(expr, segs);
    }

    /// <summary>Paths parsed and cached right now (bridge.self_perf / checks).</summary>
    internal static int ParsedCount => _parsed.Count;

    /// <summary>
    /// Drops every parsed path and its resolve caches. Called when the bridge unloads (hot reload or HUD close), so
    /// nothing resolved under one load is handed to the next. The resolve caches never hold a type of a collectible
    /// (hot-reloadable plugin) assembly in the first place.
    /// </summary>
    internal static void ClearCaches() => _parsed.Clear();

    /// <summary>
    /// The walker segment for a dictionary key: <c>["key"]</c> with <c>\"</c> and <c>\\</c> escaped, so a key holding
    /// dots, brackets or quotes reads back as the same key (ParseExpression undoes it).
    /// </summary>
    internal static string KeySegment(string key)
        => "[\"" + (key.IndexOfAny(['"', '\\']) < 0 ? key : key.Replace("\\", "\\\\").Replace("\"", "\\\"")) + "\"]";

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
                if (pos < expr.Length && expr[pos] == '"')
                {
                    // Quoted key (KeySegment writes these): \" and \\ are escapes, so keys holding ] . or quotes round-trip.
                    // Any other backslash stays literal (older paths with a backslash in a key read as before).
                    var key = new StringBuilder();
                    pos++; // skip "
                    while (pos < expr.Length && expr[pos] != '"')
                    {
                        if (expr[pos] == '\\' && pos + 1 < expr.Length && expr[pos + 1] is '"' or '\\') pos++;
                        key.Append(expr[pos++]);
                    }
                    while (pos < expr.Length && expr[pos] != ']') pos++;   // past the closing quote
                    seg.IndexerValue = key.ToString();
                }
                else
                {
                    var idxStart = pos;
                    while (pos < expr.Length && expr[pos] != ']') pos++;
                    seg.IndexerValue = expr[idxStart..pos].Trim('"', '\'');
                }
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

        if (seg.Name is "GetComponent" or "HasComponent" && seg.GenericArg != null)
        {
            // Per segment: the last target type's closed method (a compiled call after a few uses), else the full lookup.
            if (seg.LastMethod is { } last && last.Item1 == targetType) return MemberIndex.Call(last.Item2, target);
            var compType = seg.ComponentType ??= ResolveComponentType(seg.GenericArg);
            if (compType == null)
            {
                var ti = MemberIndex.Types;
                var other = ti.Candidates(seg.GenericArg).Length;   // same name outside the HUD namespaces
                error = $"Could not resolve component type '{seg.GenericArg}' (looked in ExileCore*/GameOffsets* types of the HUD's own "
                        + $"{ti.AssemblyCount} assemblies, type index build {ti.Build}; plugin assemblies are not indexed"
                        + (other > 0 ? $"; {other} type(s) of that name exist outside those namespaces" : "") + ")";
                return null;
            }

            // Closed generic method cached per (type, component); throws on a constraint violation like MakeGenericMethod.
            var generic = MemberIndex.GenericMethod(targetType, seg.Name, compType);
            if (generic == null)
            {
                error = $"No {seg.Name} method found on {targetType.Name}";
                return null;
            }

            // The parse cache is shared and outlives calls: never pin a type of a collectible (hot-reloadable) assembly in it.
            if (!targetType.Assembly.IsCollectible && !compType.Assembly.IsCollectible) seg.LastMethod = Tuple.Create(targetType, generic);
            return MemberIndex.Call(generic, target);
        }

        if (seg.Name == "GetChildAtIndex" && seg.MethodArg != null)
        {
            if (!int.TryParse(seg.MethodArg, out var index))
            {
                error = "GetChildAtIndex requires an integer argument";
                return null;
            }

            var method = MemberIndex.Method(targetType, "GetChildAtIndex", typeof(int));
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

    /// <summary>
    /// A HUD type (ExileCore*/GameOffsets* namespaces) by simple or full name. Several types share a simple name
    /// (e.g. "Player", "Buffs"): a component (a subclass of the HUD's Component) wins, then a class in a
    /// *.Components namespace; the old cache kept whichever loaded last.
    /// </summary>
    internal static Type? ResolveComponentType(string typeName)
        => MemberIndex.Types.Resolve(typeName, IsHudType, ComponentRank)
           // A miss may be a HUD assembly loaded after the index was built: rebuild once per assembly-count change.
           ?? (MemberIndex.RefreshTypes() ? MemberIndex.Types.Resolve(typeName, IsHudType, ComponentRank) : null);

    private static bool IsHudType(Type t)
        => t.Namespace?.StartsWith("ExileCore", StringComparison.Ordinal) == true
           || t.Namespace?.StartsWith("GameOffsets", StringComparison.Ordinal) == true;

    private static int ComponentRank(Type t)
    {
        var rank = 0;
        for (var b = t.BaseType; b != null; b = b.BaseType)
            if (b.Name == "Component" && IsHudType(b)) { rank += 2; break; }
        if (t.Namespace?.EndsWith(".Components", StringComparison.Ordinal) == true) rank++;
        return rank;
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
            var indexer = MemberIndex.Indexer(type, typeof(int));
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

        // Enum-keyed indexers (generic dictionaries, InventoryElement[InventoryIndex]); a type may have several
        // indexers (InventoryElement: this[InventoryIndex] and Element's this[int]), most-derived first.
        // Two passes: a defined value first (Enum.TryParse also accepts any number, which would bind [1] to whichever
        // enum indexer comes first), then any value that parses, as before.
        List<string>? enumKeys = null;
        for (var pass = 0; pass < 2; pass++)
            foreach (var indexerProp in MemberIndex.Indexers(type))
            {
                var ps = indexerProp.GetIndexParameters();
                if (ps.Length != 1 || !indexerProp.CanRead) continue;
                var keyType = ps[0].ParameterType;
                if (!keyType.IsEnum) continue;
                if (Enum.TryParse(keyType, indexValue, true, out var enumKey) && (pass == 1 || Enum.IsDefined(keyType, enumKey!)))
                    return indexerProp.GetValue(collection, [enumKey]);
                if (pass == 1) (enumKeys ??= []).Add(keyType.Name);
            }

        // Name the link: the type has an enum indexer, the key just isn't one of its values (e.g. a PoE1 name on PoE2).
        error = enumKeys != null
            ? $"Cannot apply indexer [{indexValue}] to type {type.Name}: '{indexValue}' is not a {string.Join(" or ", enumKeys)} value"
            : $"Cannot apply indexer [{indexValue}] to type {type.Name}";
        return null;
    }

    // ── Type safety ────────────────────────────────────────────────────

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, object> _allowedCache = new();
    private static readonly object Allowed = true, NotAllowed = false;

    /// <summary>Whether the walker may read members of this type (cached per type).</summary>
    internal static bool IsTypeAllowed(Type type)
        => (bool)_allowedCache.GetValue(type, t => ComputeTypeAllowed(t) ? Allowed : NotAllowed);

    private static bool ComputeTypeAllowed(Type type)
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
        // Compiler-generated anonymous types (script results) have unreadable names; omit them.
        var result = type.Name.Contains("AnonymousType") ? new JObject() : new JObject { ["_type"] = type.Name };

        // Reflection's own list and order (cached), so the first 50 are the same as ever; reads use compiled getters.
        var props = MemberIndex.Of(type).AllProperties;
        foreach (var prop in props.Take(50))
        {
            try
            {
                if (prop.GetIndexParameters().Length > 0) continue; // Skip indexed properties
                var val = MemberIndex.For(prop).Get(obj);
                result[prop.Name] = val == null ? JValue.CreateNull() : SerializeValue(val, depth + 1);
            }
            catch (Exception ex)
            {
                result[prop.Name] = $"[error: {ex.InnerException?.Message ?? ex.Message}]";
            }
        }

        return result;
    }

    /// <summary>Any value as JSON with the walker's depth and size limits (used for script results too).</summary>
    internal static JToken ToJson(object? value) => value == null ? JValue.CreateNull() : SerializeValue(value, 0);

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
        foreach (var prop in MemberIndex.Of(type).AllProperties.OrderBy(p => p.Name))
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
        foreach (var field in MemberIndex.Of(type).AllFields.OrderBy(f => f.Name))
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

    internal static string FormatTypeName(Type type)
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
