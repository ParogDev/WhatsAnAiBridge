using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Runtime reflection over the HUD's offsets structs: the layout the CLR actually uses, which is the truth even where the
/// metadata [FieldOffset] is a decoy (PoE2 GameOffsets2) or the getters are protected stubs the IL map cannot read.
///   layout.runtime {type?|path?}: for each field of the struct (nested game structs flattened as a.b), set that field alone to 0xAB bytes in a fresh boxed
///   instance and see which bytes changed: that is its offset and size. Reported next to the metadata offset ("decoy"
///   where they differ).
///   With a walker path to a memory object (e.g. GameController.Player.GetComponent&lt;Render&gt;()), it also finds the
///   object's cached offsets struct, maps each of its public properties to the struct field holding the same value
///   (unique non-zero matches only), and verifies every mapped field against fresh game memory at Address + offset.
/// Read-only. Fails naming the link (path, struct cache not found, non-blittable struct).
/// </summary>
public partial class WhatsAnAiBridge
{
    private const BindingFlags LayoutFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private string? ProcessLayoutMethod(string method, JToken? p) => method switch
    {
        "layout.runtime" => SafeMemory(() => RuntimeLayout(p)),
        _ => null,
    };

    private JObject RuntimeLayout(JToken? p)
    {
        var path = p?["path"]?.Value<string>();
        var typeName = p?["type"]?.Value<string>();
        object? obj = null;
        object? structValue = null;
        Type? structType = null;
        string? via = null;

        if (!string.IsNullOrWhiteSpace(path))
        {
            obj = new ExpressionWalker(GameController).Resolve(path, out var error);
            if (error != null) return Err("resolve_failed", $"{path}: {error}");
            if (obj == null) return Err("null", $"{path} is null right now");
            (structValue, via) = FindOffsetsStruct(obj);
            if (structValue == null)
                return Err("no_struct_cache", $"{obj.GetType().Name} holds no cached offsets struct (no CachedValue<T>/FrameCache<T> field or offsets-typed property)");
            structType = structValue.GetType();
        }
        else if (!string.IsNullOrWhiteSpace(typeName))
        {
            structType = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name is { } n && (n.StartsWith("GameOffsets", StringComparison.Ordinal) || n.StartsWith("ExileCore", StringComparison.Ordinal)))
                .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
                .FirstOrDefault(t => t.IsValueType && (t.FullName == typeName || t.Name == typeName));
            if (structType == null) return Err("type_not_found", $"No struct '{typeName}' in GameOffsets*/ExileCore*");
        }
        else return Err("bad_request", "Pass path (a memory object) or type (an offsets struct).");

        int size;
        try { size = Marshal.SizeOf(structType); }
        catch (Exception ex) { return Err("not_blittable", $"{structType.FullName}: {ex.Message}"); }

        var failed = new JArray();
        var fields = MeasureFields(structType, size, 0, "", [], 0, failed);
        fields.Sort((a, b) => a.Off != b.Off ? a.Off.CompareTo(b.Off) : a.Name.Length.CompareTo(b.Name.Length));

        var o = new JObject
        {
            ["struct"] = structType.FullName, ["size"] = $"0x{size:X}",
            ["fields"] = new JArray(fields.Select(x => new JObject
            {
                ["name"] = x.Name, ["type"] = x.Type.Name, ["offset"] = $"0x{x.Off:X}", ["size"] = x.Len,
                ["metadataOffset"] = x.Meta is { } m ? $"0x{m:X}" : null,
                ["decoy"] = x.Meta is { } mm && mm != x.Off ? true : null,
                ["value"] = structValue == null || IsGameStruct(x.Type) ? null : Short(ChainGet(x.Chain, structValue)),
            })),
        };
        if (failed.Count > 0) o["unmeasured"] = failed;
        if (fields.Count(x => x.Meta is { } m && m != x.Off) is var decoys and > 0) o["decoyOffsets"] = decoys;
        if (obj == null) return o;

        o["path"] = path; o["object"] = obj.GetType().Name; o["structVia"] = via;
        // Is the struct a copy of the memory at Address? Compare the whole struct with fresh bytes.
        var address = obj.GetType().GetProperty("Address")?.GetValue(obj) as long? ?? 0;
        byte[]? live = null;
        if (address != 0)
        {
            using (GameController.Memory.DisableCaching()) live = GameController.Memory.ReadBytes(address, size);
            var cached = BoxedBytes(structValue!, size);
            if (live is { Length: > 0 } && live.Length == size)
                o["structMatchesMemoryAtAddress"] = $"{Enumerable.Range(0, size).Count(i => cached[i] == live[i]) * 100 / size}% of bytes";
        }
        // Property -> field by value (unique, non-zero, equal), then field -> fresh memory.
        var props = new JArray();
        foreach (var prop in obj.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (prop.GetIndexParameters().Length > 0 || !IsComparable(prop.PropertyType) || prop.Name == "Address") continue;
            object? pv;
            try { pv = prop.GetValue(obj); } catch { continue; }
            if (pv == null || IsZero(pv)) continue;
            var hits = fields.Where(x => x.Type == prop.PropertyType && Equals(ChainGet(x.Chain, structValue!), pv)).ToList();
            if (hits.Count == 0) continue;
            var e = new JObject { ["property"] = prop.Name, ["type"] = prop.PropertyType.Name };
            // Overlapping fields at one offset (obfuscators add decoy fields over real ones) are the same memory: not ambiguous.
            if (hits.Select(h => h.Off).Distinct().Count() > 1) { e["ambiguous"] = new JArray(hits.Select(h => $"{h.Name}@0x{h.Off:X}")); props.Add(e); continue; }
            var hit = hits.OrderBy(h => h.Name.Length).First();
            e["field"] = hits.Count == 1 ? hit.Name : string.Join(" | ", hits.Select(h => h.Name)); e["offset"] = $"0x{hit.Off:X}";
            if (live is { } l && hit.Off + hit.Len <= l.Length)
                e["memoryNow"] = BoxedBytes(structValue!, size).AsSpan(hit.Off, hit.Len).SequenceEqual(l.AsSpan(hit.Off, hit.Len)) ? "same" : "changed since cached (live value)";
            props.Add(e);
        }
        o["properties"] = props;
        return o;
    }

    /// <summary>The offsets struct a memory object caches: a CachedValue&lt;T&gt;/FrameCache&lt;T&gt; field's Value, or a property typed as a GameOffsets struct.</summary>
    private static (object? value, string? via) FindOffsetsStruct(object obj)
    {
        // Candidates: CachedValue<T>/FrameCache<T> field values and GameOffsets-typed properties. An object can cache
        // several (IngameState caches a Vector2 too): prefer GameOffsets types, then the largest struct.
        var found = new List<(object v, string via, bool game, int size)>();
        static int Size(Type t) { try { return Marshal.SizeOf(t); } catch { return 0; } }
        static bool Game(Type t) => t.Assembly.GetName().Name?.StartsWith("GameOffsets", StringComparison.Ordinal) == true;
        for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(LayoutFlags | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType is not { IsGenericType: true } ft || !ft.Name.Contains("Cache", StringComparison.Ordinal)) continue;
                var arg = ft.GetGenericArguments()[0];
                if (!arg.IsValueType || arg.IsPrimitive) continue;
                try
                {
                    var cache = f.GetValue(obj);
                    if (cache?.GetType().GetProperty("Value")?.GetValue(cache) is { } v) found.Add((v, $"{t.Name}.{f.Name}.Value", Game(arg), Size(arg)));
                }
                catch { }
            }
        foreach (var prop in obj.GetType().GetProperties(LayoutFlags))
            if (prop.PropertyType is { IsValueType: true, IsPrimitive: false } pt && Game(pt) && prop.GetIndexParameters().Length == 0)
                try { if (prop.GetValue(obj) is { } v) found.Add((v, $"{obj.GetType().Name}.{prop.Name}", true, Size(pt))); } catch { }
        var best = found.OrderByDescending(x => x.game).ThenByDescending(x => x.size).FirstOrDefault();
        return best.v == null ? (null, null) : (best.v, best.via);
    }

    private sealed record LayoutField(string Name, Type Type, int Off, int Len, int? Meta, FieldInfo[] Chain);

    /// <summary>
    /// Each field's runtime offset: set it alone to 0xAB bytes in a fresh boxed instance and see which bytes changed.
    /// Game structs nested inside (VitalStruct, Vector3 wrappers...) are measured the same way and flattened as a.b.
    /// </summary>
    private static List<LayoutField> MeasureFields(Type t, int size, int baseOff, string prefix, FieldInfo[] chain, int depth, JArray failed)
    {
        var list = new List<LayoutField>();
        foreach (var f in t.GetFields(LayoutFlags))
        {
            var name = prefix + f.Name;
            var meta = f.GetCustomAttribute<FieldOffsetAttribute>()?.Value;
            try
            {
                var box = Activator.CreateInstance(t)!;
                f.SetValue(box, FilledValue(f.FieldType));
                var bytes = BoxedBytes(box, size);
                int first = Array.IndexOf(bytes, (byte)0xAB), last = Array.LastIndexOf(bytes, (byte)0xAB);
                if (first < 0) { failed.Add($"{name}: no byte changed"); continue; }
                var c = chain.Append(f).ToArray();
                list.Add(new LayoutField(name, f.FieldType, baseOff + first, last - first + 1, meta is { } m ? baseOff + m : null, c));
                if (depth < 3 && IsGameStruct(f.FieldType) && Marshal.SizeOf(f.FieldType) is var inner and > 0)
                    list.AddRange(MeasureFields(f.FieldType, inner, baseOff + first, name + ".", c, depth + 1, failed));
            }
            catch (Exception ex) { failed.Add($"{name} ({f.FieldType.Name}): {ex.GetType().Name}"); }
        }
        return list;
    }

    private static bool IsGameStruct(Type t) => t is { IsValueType: true, IsPrimitive: false, IsEnum: false } &&
                                               t.Assembly.GetName().Name?.StartsWith("GameOffsets", StringComparison.Ordinal) == true;

    private static object? ChainGet(FieldInfo[] chain, object root)
    {
        object? v = root;
        foreach (var f in chain) { if (v == null) return null; v = SafeGet(f, v); }
        return v;
    }

    private static object FilledValue(Type t)
    {
        var u = t.IsEnum ? Enum.GetUnderlyingType(t) : t;
        var n = u == typeof(bool) ? 1 : u == typeof(char) ? 2 : Marshal.SizeOf(u);
        var bytes = Enumerable.Repeat((byte)0xAB, n).ToArray();
        var h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var v = u == typeof(bool) ? true : u == typeof(char) ? (object)(char)0xABAB : Marshal.PtrToStructure(h.AddrOfPinnedObject(), u)!;
            return t.IsEnum ? Enum.ToObject(t, v) : v;
        }
        finally { h.Free(); }
    }

    private static byte[] BoxedBytes(object boxed, int size)
    {
        var h = GCHandle.Alloc(boxed, GCHandleType.Pinned);
        try { var b = new byte[size]; Marshal.Copy(h.AddrOfPinnedObject(), b, 0, size); return b; }
        finally { h.Free(); }
    }

    private static object? SafeGet(FieldInfo f, object o) { try { return f.GetValue(o); } catch { return null; } }

    private static bool IsComparable(Type t) => t.IsPrimitive || t.IsEnum || t.Name is "Vector2" or "Vector3" or "Vector4" or "Vector2i";

    private static bool IsZero(object v) => v switch
    {
        bool b => !b, byte x => x == 0, short x => x == 0, ushort x => x == 0, int x => x == 0, uint x => x == 0,
        long x => x == 0, ulong x => x == 0, float x => x == 0, double x => x == 0,
        _ => v.Equals(Activator.CreateInstance(v.GetType())),
    };

    private static string? Short(object? v)
    {
        var s = v?.ToString();
        return s is { Length: > 60 } ? s[..60] + "..." : s;
    }
}
