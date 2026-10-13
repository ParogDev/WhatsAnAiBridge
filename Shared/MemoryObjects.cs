using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WhatsAnAiBridge;

/// <summary>
/// What the bridge's memory tools (memory.*, layout.runtime, observe layers) ask of a HUD memory object, on top of
/// <see cref="MemberIndex"/>, one helper each: its Address, the offsets struct it caches, a struct type by name, the
/// struct's size, and the property that names it. Every per-type answer is cached (ConditionalWeakTable, so plugin
/// types stay unloadable).
/// </summary>
internal static class MemoryObjects
{
    /// <summary>The object's public long Address, or null when it has none or the getter throws.</summary>
    internal static long? Address(object? obj)
    {
        if (obj == null || MemberIndex.Find(obj.GetType(), "Address", kinds: MemberIndex.Kinds.Property) is not { CanRead: true } p || p.Type != typeof(long)) return null;
        try { return (long?)p.Get(obj); } catch { return null; }
    }

    // ── Naming properties ────────────────────────────────────────────

    /// <summary>The properties that name an object, best first: explore's preview and the list layer's item text.</summary>
    internal static readonly string[] NamingProperties = ["RenderName", "Name", "Text", "Path", "Id"];

    private static readonly ConditionalWeakTable<Type, MemberIndex.Member[]> _naming = new();

    /// <summary>Of <see cref="NamingProperties"/>, the readable properties a type has, in that order.</summary>
    internal static MemberIndex.Member[] NamingMembers(Type t) => _naming.GetValue(t, type => NamingProperties
        .Select(n => MemberIndex.Find(type, n, kinds: MemberIndex.Kinds.Property)).Where(m => m is { CanRead: true }).ToArray()!);

    // ── The cached offsets struct ────────────────────────────────────

    /// <summary>Where a memory object keeps an offsets struct: a CachedValue&lt;T&gt;/FrameCache&lt;T&gt; field (its Value) or an offsets-typed property.</summary>
    internal sealed record StructSource(MemberIndex.Member Member, Type StructType, string Via, bool ViaCache, bool Game, int Size);

    private static readonly ConditionalWeakTable<Type, StructSource[]> _sources = new();
    private const BindingFlags AnyDeclared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Every place a type caches an offsets struct, best first: GameOffsets types before others, then the largest
    /// (IngameState caches a Vector2 too). Cache fields are generic types named like *Cache* (on the field type or a
    /// base of it) over a non-primitive struct; properties count when typed as a GameOffsets struct.
    /// </summary>
    internal static StructSource[] StructSources(Type type) => _sources.GetValue(type, BuildSources);

    private static StructSource[] BuildSources(Type type)
    {
        var list = new List<StructSource>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(AnyDeclared))
                if (CacheArg(f.FieldType) is { } arg)
                    list.Add(new StructSource(MemberIndex.For(f), arg, $"{t.Name}.{f.Name}.Value", true, IsGameType(arg), MarshalSize(arg)));
        foreach (var p in MemberIndex.Of(type).AllProperties.Concat(type.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)))
            if (p.PropertyType is { IsValueType: true, IsPrimitive: false } pt && IsGameType(pt) && p.GetIndexParameters().Length == 0 && p.GetMethod != null)
                list.Add(new StructSource(MemberIndex.For(p), pt, $"{type.Name}.{p.Name}", false, true, MarshalSize(pt)));
        // Stable: on a tie the order above (fields from the type down to its bases, then properties) wins.
        return list.OrderByDescending(s => s.Game).ThenByDescending(s => s.Size).ToArray();
    }

    /// <summary>T of a CachedValue&lt;T&gt; / FrameCache&lt;T&gt;-like field type (or a base of it) when T is a non-primitive struct.</summary>
    private static Type? CacheArg(Type ft)
    {
        for (var g = ft; g != null && g != typeof(object); g = g.BaseType)
            if (g.IsGenericType && g.Name.Contains("Cache", StringComparison.Ordinal) && g.GetGenericArguments() is [var arg]
                && arg.IsValueType && !arg.IsPrimitive && !arg.IsEnum)
                return arg;
        return null;
    }

    /// <summary>The offsets struct an object caches right now (boxed), from the best source that holds one; nulls when none does.</summary>
    internal static (object? value, string? via) CachedStruct(object obj)
    {
        foreach (var s in StructSources(obj.GetType()))
        {
            try
            {
                var v = s.Member.Get(obj);
                if (s.ViaCache) v = MemberIndex.TryGet(v, "Value", kinds: MemberIndex.Kinds.Property);
                if (v != null) return (v, s.Via);
            }
            catch { }
        }
        return (null, null);
    }

    /// <summary>A type from a GameOffsets* assembly (the HUD's offsets structs; on PoE2 the real obfuscated ones live there too).</summary>
    private static bool IsGameType(Type t) => t.Assembly.GetName().Name?.StartsWith("GameOffsets", StringComparison.Ordinal) == true;

    private static int MarshalSize(Type t) { try { return Marshal.SizeOf(t); } catch { return 0; } }

    // ── Struct types by name and size ────────────────────────────────

    /// <summary>
    /// A loaded struct (non-primitive value type) by full or simple name, case-insensitive (<see cref="MemberIndex.Types"/>):
    /// GameOffsets types first, then the HUD core's (ExileCore*), then any; the first loaded on a tie. The index skips
    /// plugin (collectible) assemblies, so an exact full name is then looked up in those too. Null when none.
    /// </summary>
    internal static Type? StructType(string name)
    {
        static bool IsStruct(Type t) => t.IsValueType && !t.IsPrimitive && !t.IsEnum;
        static int Rank(Type t) => IsGameType(t) ? 2 : t.Assembly.GetName().Name?.StartsWith("ExileCore", StringComparison.Ordinal) == true ? 1 : 0;
        return MemberIndex.Types.Resolve(name, IsStruct, Rank)
               ?? (MemberIndex.RefreshTypes() ? MemberIndex.Types.Resolve(name, IsStruct, Rank) : null)
               ?? AppDomain.CurrentDomain.GetAssemblies().Where(a => a.IsCollectible && !a.IsDynamic)
                   .Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null && IsStruct(t));
    }

    private static readonly ConditionalWeakTable<Type, StrongBox<int>> _sizes = new();
    private static readonly MethodInfo UnsafeSizeOf = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!;

    /// <summary>The managed size of a type (Unsafe.SizeOf&lt;T&gt;, Marshal.SizeOf when that fails, else 8), computed once per type.</summary>
    internal static int SizeOf(Type t) => _sizes.GetValue(t, type =>
    {
        try { return new StrongBox<int>(UnsafeSizeOf.MakeGenericMethod(type).CreateDelegate<Func<int>>()()); }
        catch { try { return new StrongBox<int>(Marshal.SizeOf(type)); } catch { return new StrongBox<int>(8); } }
    }).Value;
}
