using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace WhatsAnAiBridge;

/// <summary>One serialized member of a bridge DTO, as Newtonsoft writes it.</summary>
public class ContractFieldDto
{
    /// <summary>The JSON name on the wire ([JsonProperty] name, else the CLR name: the bridge sets no naming strategy).</summary>
    [JsonProperty("json")] public string Json { get; set; } = "";
    /// <summary>The C# member name.</summary>
    [JsonProperty("clr")] public string Clr { get; set; } = "";
    /// <summary>Wire shape: string | integer | number | bool | datetime | any | object:&lt;Type&gt; | enum:&lt;Type&gt; |
    /// list&lt;shape&gt; | map&lt;shape&gt; (string keys).</summary>
    [JsonProperty("type")] public string Type { get; set; } = "";
    /// <summary>The C# type as written (int, string, List&lt;EntityDto&gt;...).</summary>
    [JsonProperty("clrType")] public string ClrType { get; set; } = "";
    /// <summary>Null can reach the wire, or the field can be absent (Nullable&lt;T&gt; or a nullable reference).</summary>
    [JsonProperty("nullable")] public bool Nullable { get; set; }
    /// <summary>Left out of the JSON when null (NullValueHandling.Ignore on the member; the bridge's serializer also omits nulls).</summary>
    [JsonProperty("omitNull", NullValueHandling = NullValueHandling.Ignore)] public bool? OmitNull { get; set; }
}

/// <summary>One bridge DTO type (or an enum a DTO uses).</summary>
public class ContractTypeDto
{
    /// <summary>Type name without namespace (nested types as Outer.Inner).</summary>
    [JsonProperty("name")] public string Name { get; set; } = "";
    /// <summary>object | enum.</summary>
    [JsonProperty("kind")] public string Kind { get; set; } = "object";
    /// <summary>[JsonExtensionData]: the type carries fields beyond the declared ones.</summary>
    [JsonProperty("open", NullValueHandling = NullValueHandling.Ignore)] public bool? Open { get; set; }
    [JsonProperty("fields", NullValueHandling = NullValueHandling.Ignore)] public List<ContractFieldDto>? Fields { get; set; }
    /// <summary>Enum member names (serialized as their integer value: the bridge has no StringEnumConverter).</summary>
    [JsonProperty("values", NullValueHandling = NullValueHandling.Ignore)] public List<string>? Values { get; set; }
}

/// <summary>contract.schema: every bridge DTO, reflected once per load.</summary>
public class ContractSchemaDto
{
    /// <summary>Format of this schema (bump when the shape grammar changes).</summary>
    [JsonProperty("version")] public int Version { get; set; }
    /// <summary>SHA-256 (first 16 hex) of the canonical type list: equal hashes = identical contract.</summary>
    [JsonProperty("hash")] public string Hash { get; set; } = "";
    [JsonProperty("typeCount")] public int TypeCount { get; set; }
    /// <summary>How long the reflection took at first use, ms.</summary>
    [JsonProperty("ms")] public double Ms { get; set; }
    /// <summary>Types the walk could not reflect, "Type: why" (they are missing from types).</summary>
    [JsonProperty("errors", NullValueHandling = NullValueHandling.Ignore)] public List<string>? Errors { get; set; }
    [JsonProperty("types", NullValueHandling = NullValueHandling.Ignore)] public List<ContractTypeDto>? Types { get; set; }
}

/// <summary>hello.contract: the cheap drift check (compare the hash; fetch contract.schema only when it differs).</summary>
public class ContractSummaryDto
{
    [JsonProperty("version")] public int Version { get; set; }
    [JsonProperty("hash")] public string Hash { get; set; } = "";
    [JsonProperty("typeCount")] public int TypeCount { get; set; }
    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public string? Error { get; set; }
}

/// <summary>
/// The bridge's DTO contract, reflected from its own types: every class with a [JsonProperty] member, every class or enum
/// those reach, resolved through Newtonsoft's own contract resolver (so names, ignored members and extension data are
/// exactly what <c>Serialize</c> writes). Built once per load (the assembly is new on every reload, so the static cache
/// can't outlive it). The MCP compares its mirrored DTOs against it (hello.contract.hash, then contract.schema).
/// </summary>
public static class ContractSchema
{
    public const int SchemaVersion = 1;
    private static readonly Lazy<ContractSchemaDto> Built = new(Build);

    public static ContractSchemaDto Get() => Built.Value;

    public static ContractSummaryDto Summary()
    {
        try
        {
            var s = Get();
            return new ContractSummaryDto { Version = s.Version, Hash = s.Hash, TypeCount = s.TypeCount,
                Error = s.Errors is { Count: > 0 } e ? $"{e.Count} type(s) not reflected: {e[0]}" : null };
        }
        catch (Exception ex) { return new ContractSummaryDto { Version = SchemaVersion, Error = $"{ex.GetType().Name}: {ex.Message}" }; }
    }

    private static ContractSchemaDto Build()
    {
        var sw = Stopwatch.StartNew();
        var asm = typeof(ContractSchema).Assembly;
        var resolver = new DefaultContractResolver();
        var nullability = new NullabilityInfoContext();
        var errors = new List<string>();
        Type[] all;
        try { all = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { all = ex.Types.Where(t => t != null).ToArray()!; errors.Add($"assembly: {ex.LoaderExceptions.FirstOrDefault()?.Message}"); }

        const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var queue = new Queue<Type>(all.Where(t => t.IsClass && !t.IsGenericTypeDefinition && !IsCompilerGenerated(t)
            && t.GetMembers(Members).Any(m => m.IsDefined(typeof(JsonPropertyAttribute), false))));
        var seen = new HashSet<Type>();
        var types = new List<ContractTypeDto>();

        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!seen.Add(t)) continue;
            try
            {
                if (t.IsEnum)
                {
                    types.Add(new ContractTypeDto { Name = NameOf(t), Kind = "enum", Values = Enum.GetNames(t).ToList() });
                    continue;
                }
                if (resolver.ResolveContract(t) is not JsonObjectContract oc) continue;
                var dto = new ContractTypeDto { Name = NameOf(t), Fields = new() };
                if (oc.ExtensionDataSetter != null || oc.ExtensionDataGetter != null) dto.Open = true;
                foreach (var p in oc.Properties)
                {
                    if (p.Ignored || !p.Readable || p.PropertyType == null) continue;
                    var member = t.GetMember(p.UnderlyingName ?? "", MemberTypes.Property | MemberTypes.Field,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault();
                    var shape = Shape(p.PropertyType, queue, asm);
                    dto.Fields.Add(new ContractFieldDto
                    {
                        Json = p.PropertyName ?? p.UnderlyingName ?? "",
                        Clr = p.UnderlyingName ?? "",
                        Type = shape,
                        ClrType = ClrName(p.PropertyType),
                        Nullable = IsNullable(p.PropertyType, member, nullability),
                        OmitNull = p.NullValueHandling == NullValueHandling.Ignore ? true : null,
                    });
                }
                dto.Fields = dto.Fields.OrderBy(f => f.Json, StringComparer.Ordinal).ToList();
                types.Add(dto);
            }
            catch (Exception ex) { errors.Add($"{NameOf(t)}: {ex.GetType().Name}: {ex.Message}"); }
        }

        types = types.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        // Only what reaches the wire is hashed: renaming a C# member behind the same JSON name changes nothing for a reader.
        var canonical = JsonConvert.SerializeObject(types.Select(x => new object?[]
        {
            x.Name, x.Kind, x.Open, x.Values,
            x.Fields?.Select(f => new object?[] { f.Json, f.Type, f.Nullable, f.OmitNull }),
        }), Formatting.None);
        string hash;
        using (var sha = SHA256.Create())
            hash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)).Take(8).Select(b => b.ToString("x2")));
        return new ContractSchemaDto
        {
            Version = SchemaVersion, Hash = hash, TypeCount = types.Count, Types = types,
            Ms = Math.Round(sw.Elapsed.TotalMilliseconds, 2), Errors = errors.Count > 0 ? errors : null,
        };
    }

    /// <summary>The wire shape of a CLR type; bridge classes and enums it reaches are queued for their own entry.</summary>
    private static string Shape(Type t, Queue<Type> queue, Assembly asm)
    {
        t = System.Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string) || t == typeof(char) || t == typeof(Guid) || t == typeof(TimeSpan)) return "string";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "datetime";
        if (t.IsEnum)
        {
            if (t.Assembly == asm) queue.Enqueue(t);
            return $"enum:{NameOf(t)}";
        }
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte)
            || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort)) return "integer";
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "number";
        if (t == typeof(object) || typeof(JToken).IsAssignableFrom(t)) return "any";
        if (t.IsArray) return $"list<{Shape(t.GetElementType()!, queue, asm)}>";
        if (DictionaryValue(t) is { } dv) return $"map<{Shape(dv, queue, asm)}>";
        if (t != typeof(string) && typeof(IEnumerable).IsAssignableFrom(t))
        {
            var el = t.GetInterfaces().Concat(new[] { t })
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
            return $"list<{(el == null ? "any" : Shape(el, queue, asm))}>";
        }
        if (t.Assembly == asm && (t.IsClass || t.IsValueType) && !t.IsPrimitive)
        {
            queue.Enqueue(t);
            return $"object:{NameOf(t)}";
        }
        // A HUD / BCL type serialized by Newtonsoft's defaults (Vector2, a record from the HUD...): no contract of ours.
        return $"external:{ClrName(t)}";
    }

    private static Type? DictionaryValue(Type t)
    {
        var d = t.GetInterfaces().Concat(new[] { t })
            .FirstOrDefault(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        return d?.GetGenericArguments()[1];
    }

    private static bool IsNullable(Type t, MemberInfo? member, NullabilityInfoContext ctx)
    {
        if (System.Nullable.GetUnderlyingType(t) != null) return true;
        if (t.IsValueType) return false;
        try
        {
            return member switch
            {
                PropertyInfo pi => ctx.Create(pi).ReadState != NullabilityState.NotNull,
                FieldInfo fi => ctx.Create(fi).ReadState != NullabilityState.NotNull,
                _ => true,
            };
        }
        catch { return true; }
    }

    private static bool IsCompilerGenerated(Type t) => t.Name.Contains('<') || t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false);

    private static string NameOf(Type t) => t.DeclaringType is { } outer ? $"{NameOf(outer)}.{t.Name}" : t.Name;

    private static string ClrName(Type t)
    {
        if (System.Nullable.GetUnderlyingType(t) is { } u) return ClrName(u) + "?";
        if (!t.IsGenericType) return t.Name;
        var name = t.Name.Substring(0, t.Name.IndexOf('`'));
        return $"{name}<{string.Join(",", t.GetGenericArguments().Select(ClrName))}>";
    }
}

public partial class WhatsAnAiBridge
{
    /// <summary>contract.schema {type?}: the whole schema, or one type (and the types it reaches) by name.</summary>
    private string? ProcessContractMethod(string method, JToken? p)
    {
        if (method != "contract.schema") return null;
        var s = ContractSchema.Get();
        var only = p?["type"]?.Value<string>();
        if (string.IsNullOrEmpty(only)) return Serialize(s);
        var byName = s.Types!.ToDictionary(t => t.Name, StringComparer.Ordinal);
        if (!byName.ContainsKey(only!))
        {
            var near = s.Types!.Where(t => t.Name.IndexOf(only!, StringComparison.OrdinalIgnoreCase) >= 0).Select(t => t.Name).Take(10).ToList();
            return Serialize(Err("unknown_type", $"No bridge DTO '{only}' in the contract ({s.TypeCount} types){(near.Count > 0 ? "; close: " + string.Join(", ", near) : "")}."));
        }
        // The type plus everything it reaches, so a caller can compare one family without the rest.
        var picked = new List<ContractTypeDto>();
        var stack = new Stack<string>(new[] { only! });
        var done = new HashSet<string>();
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!done.Add(n) || !byName.TryGetValue(n, out var t)) continue;
            picked.Add(t);
            foreach (var f in t.Fields ?? new())
                foreach (var r in ReferencedTypes(f.Type)) stack.Push(r);
        }
        return Serialize(new ContractSchemaDto { Version = s.Version, Hash = s.Hash, TypeCount = s.TypeCount, Ms = s.Ms, Errors = s.Errors,
            Types = picked.OrderBy(t => t.Name, StringComparer.Ordinal).ToList() });
    }

    private static IEnumerable<string> ReferencedTypes(string shape)
    {
        foreach (var prefix in new[] { "object:", "enum:" })
        {
            var i = shape.IndexOf(prefix, StringComparison.Ordinal);
            if (i < 0) continue;
            var rest = shape.Substring(i + prefix.Length);
            var end = rest.IndexOf('>');
            yield return end < 0 ? rest : rest.Substring(0, end);
        }
    }
}
