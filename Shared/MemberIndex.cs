using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace WhatsAnAiBridge;

/// <summary>
/// The bridge's one reflection core: member lookup by (type, name), compiled getters, per-type member lists and a
/// type-by-name index. Every walker (eval, explore, observe layers, memory views, settings...) should look members
/// up here instead of calling Type.GetProperty / GetField itself.
///
/// - **Ambiguity-safe.** Type.GetProperty(name) throws AmbiguousMatchException when a name is declared more than once
///   in the hierarchy (a `new` property hiding a base one, or a property named like an indexer: NormalInventoryItem.Item
///   next to Element's this[int], whose metadata name is also "Item"). Lookups here never throw: indexers are never
///   returned by name (use <see cref="Indexer"/> / <see cref="Indexers"/>), and of several candidates the one declared
///   on the most-derived type wins (a property before a field on the same type).
/// - **Cached per type** in a ConditionalWeakTable, so a type from a plugin's collectible load context is never kept
///   alive by the cache (reloading that plugin still unloads it). Misses are cached too.
/// - **Compiled getters.** <see cref="Member.Get"/> reads through PropertyInfo/FieldInfo.GetValue for the first
///   <see cref="CompileAfterReads"/> reads of a member, then compiles an expression-tree getter (so one-off reads in explore never
///   pay for a compile). A getter that fails to compile stays on reflection for good (<see cref="Member.CompileError"/>
///   says why). Exceptions thrown by a compiled getter are wrapped in TargetInvocationException, exactly like
///   PropertyInfo.GetValue, so callers' error texts don't change.
/// - **Types by name** (<see cref="Types"/>): one index over the non-collectible loaded assemblies, built once, by full
///   name and by simple name (case-insensitive), keeping every candidate: two types named "Player" are both listed and
///   the caller chooses (ExpressionWalker.ResolveComponentType prefers the Component subclass).
///
/// TODO(A2): move MemoryInspector, ObserveLayers/ObserveEachRaw, SettingsControl, RuntimeLayout and HudIntrospection onto
/// this (Find/Property/Field with NonPublic flags, Readable for "public scalar props", Types for type-by-name scans).
/// </summary>
internal static class MemberIndex
{
    internal const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
    internal const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// Reads through reflection before a member's getter is compiled. Compiling costs ~0.1-1 ms once and saves well
    /// under 1 us per read (the runtime's own GetValue is already emit-backed after a few calls), so only members read
    /// over and over (observer layers, repeated evals) are worth it: one-off explores never compile.
    /// </summary>
    internal const int CompileAfterReads = 64;

    [Flags]
    internal enum Kinds { Property = 1, Field = 2, Both = Property | Field }

    /// <summary>A readable property (never an indexer) or field, with a cached getter.</summary>
    internal sealed class Member
    {
        public readonly string Name;
        /// <summary>The declared type of the property or field.</summary>
        public readonly Type Type;
        public readonly MemberInfo Info;
        public readonly Type DeclaringType;
        public PropertyInfo? Property => Info as PropertyInfo;
        public FieldInfo? Field => Info as FieldInfo;
        public bool IsField => Info is FieldInfo;
        /// <summary>Property with a getter, or a field.</summary>
        public readonly bool CanRead;

        private Func<object, object?>? _compiled;
        private int _reads;
        private volatile bool _noCompile;
        /// <summary>Why the getter could not be compiled (reads stay on reflection), else null.</summary>
        public string? CompileError { get; private set; }
        public bool Compiled => _compiled != null;

        internal Member(MemberInfo info)
        {
            Info = info;
            Name = info.Name;
            DeclaringType = info.DeclaringType!;
            if (info is PropertyInfo p) { Type = p.PropertyType; CanRead = p.GetMethod != null; }
            else { Type = ((FieldInfo)info).FieldType; CanRead = true; }
            // Nothing to compile: no getter, static, or a type that can't be boxed (pointers, ref structs, byrefs).
            if (!CanRead || DeclaringType.ContainsGenericParameters || Type.IsPointer || Type.IsByRef || Type.IsByRefLike
                || (info is FieldInfo { IsStatic: true }) || (info is PropertyInfo { GetMethod.IsStatic: true }))
                _noCompile = true;
        }

        /// <summary>The value on <paramref name="target"/>; throws like PropertyInfo.GetValue (getter exceptions as TargetInvocationException).</summary>
        public object? Get(object target)
        {
            var c = _compiled;
            if (c == null && !_noCompile && Interlocked.Increment(ref _reads) > CompileAfterReads)
                c = Compile();
            if (c == null)
                return Info is PropertyInfo p ? p.GetValue(target) : ((FieldInfo)Info).GetValue(target);
            try { return c(target); }
            catch (Exception ex) { throw new TargetInvocationException(ex); }
        }

        private Func<object, object?>? Compile()
        {
            lock (this)
            {
                if (_compiled != null || _noCompile) return _compiled;
                try
                {
                    var o = Expression.Parameter(typeof(object), "o");
                    var inst = Expression.Convert(o, DeclaringType);   // unboxes a struct
                    Expression body = Info is PropertyInfo p ? Expression.Property(inst, p) : Expression.Field(inst, (FieldInfo)Info);
                    _compiled = Expression.Lambda<Func<object, object?>>(Expression.Convert(body, typeof(object)), o).Compile();
                    Interlocked.Increment(ref _compiledCount);
                }
                catch (Exception ex)
                {
                    CompileError = $"{ex.GetType().Name}: {ex.Message}";
                    _noCompile = true;
                    Interlocked.Increment(ref _compileFailures);
                    _lastCompileError = $"{this}: {CompileError}";
                }
                return _compiled;
            }
        }

        public override string ToString() => $"{DeclaringType.Name}.{Name}";
    }

    /// <summary>What reflection says about one type, computed on first use.</summary>
    internal sealed class TypeMembers
    {
        public readonly Type Type;
        private PropertyInfo[]? _allProperties;
        private FieldInfo[]? _allFields;
        private Member[]? _readable;
        private PropertyInfo[]? _indexers;
        internal readonly ConcurrentDictionary<(string name, BindingFlags flags, Kinds kinds), Member?> ByName = new();
        internal readonly ConcurrentDictionary<(string name, Type? typeArg, string signature), MethodInfo?> Methods = new();

        internal TypeMembers(Type type) => Type = type;

        /// <summary>Type.GetProperties(Public | Instance) as reflection returns it (indexers and hidden duplicates included).</summary>
        public PropertyInfo[] AllProperties => _allProperties ??= Type.GetProperties(PublicInstance);

        /// <summary>Type.GetFields(Public | Instance) as reflection returns it.</summary>
        public FieldInfo[] AllFields => _allFields ??= Type.GetFields(PublicInstance);

        /// <summary>
        /// Public instance properties with a getter (no indexers) and public instance fields, one per name (the
        /// most-derived declaration), sorted by name (ordinal).
        /// </summary>
        public Member[] Readable => _readable ??= BuildReadable();

        /// <summary>Public instance indexers (any parameter count), most-derived first.</summary>
        public PropertyInfo[] Indexers => _indexers ??= AllProperties.Where(p => p.GetIndexParameters().Length > 0)
            .OrderByDescending(p => Depth(p.DeclaringType)).ToArray();

        private Member[] BuildReadable()
        {
            var names = AllProperties.Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod != null).Select(p => p.Name)
                .Concat(AllFields.Select(f => f.Name)).Distinct(StringComparer.Ordinal);
            var list = new List<Member>();
            foreach (var name in names)
                if (Find(Type, name) is { CanRead: true } m) list.Add(m);
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list.ToArray();
        }
    }

    private static readonly ConditionalWeakTable<Type, TypeMembers> _types = new();
    private static int _compiledCount, _compileFailures;
    private static string? _lastCompileError;

    /// <summary>For bridge.self_perf: compiled getters, compile failures (the last named), the type index once built.</summary>
    internal static Newtonsoft.Json.Linq.JObject Status()
    {
        var o = new Newtonsoft.Json.Linq.JObject { ["compiledGetters"] = _compiledCount, ["compileFailures"] = _compileFailures };
        if (_lastCompileError != null) o["lastCompileError"] = _lastCompileError;
        if (_typeIndex.IsValueCreated)
        {
            var ti = _typeIndex.Value;
            o["typeIndex"] = new Newtonsoft.Json.Linq.JObject { ["types"] = ti.TypeCount, ["buildMs"] = ti.BuildMs, ["partialAssemblies"] = new Newtonsoft.Json.Linq.JArray(ti.Partial) };
        }
        return o;
    }

    /// <summary>The cached member lists of a type.</summary>
    internal static TypeMembers Of(Type type) => _types.GetValue(type, t => new TypeMembers(t));

    /// <summary>
    /// The property (never an indexer) or field called <paramref name="name"/>, or null. Several declarations of the
    /// name: the most-derived wins, a property before a field on the same type. Never throws AmbiguousMatchException.
    /// </summary>
    internal static Member? Find(Type type, string name, BindingFlags flags = PublicInstance, Kinds kinds = Kinds.Both)
    {
        var tm = Of(type);
        var key = (name, flags, kinds);
        if (tm.ByName.TryGetValue(key, out var hit)) return hit;
        return tm.ByName.GetOrAdd(key, static (k, t) => Lookup(t, k.name, k.flags, k.kinds), type);   // no closure on the hit path
    }

    /// <summary>The non-indexer property called <paramref name="name"/> (most-derived), or null.</summary>
    internal static PropertyInfo? Property(Type type, string name, BindingFlags flags = PublicInstance)
        => Find(type, name, flags, Kinds.Property)?.Property;

    /// <summary>The field called <paramref name="name"/> (most-derived), or null.</summary>
    internal static FieldInfo? Field(Type type, string name, BindingFlags flags = PublicInstance)
        => Find(type, name, flags, Kinds.Field)?.Field;

    /// <summary>The cached member for a PropertyInfo or FieldInfo you already hold (for its compiled getter).</summary>
    internal static Member For(MemberInfo info) => _byInfo.GetValue(info, i => new Member(i));

    private static readonly ConditionalWeakTable<MemberInfo, Member> _byInfo = new();

    /// <summary>The value of <paramref name="name"/> on <paramref name="target"/> (compiled getter), or throws MissingMemberException naming the type.</summary>
    internal static object? Get(object target, string name, BindingFlags flags = PublicInstance)
        => (Find(target.GetType(), name, flags) ?? throw new MissingMemberException(target.GetType().FullName, name)).Get(target);

    /// <summary>The one-parameter indexer taking exactly <paramref name="keyType"/> (most-derived), or null.</summary>
    internal static PropertyInfo? Indexer(Type type, Type keyType)
    {
        foreach (var p in Of(type).Indexers)
        {
            var ps = p.GetIndexParameters();
            if (ps.Length == 1 && ps[0].ParameterType == keyType) return p;
        }
        return null;
    }

    /// <summary>Public instance indexers, most-derived first.</summary>
    internal static PropertyInfo[] Indexers(Type type) => Of(type).Indexers;

    /// <summary>Public instance members you can read (see <see cref="TypeMembers.Readable"/>), sorted by name.</summary>
    internal static Member[] Readable(Type type) => Of(type).Readable;

    /// <summary>A public instance method by exact parameter types (cached; most-derived on a tie), or null.</summary>
    internal static MethodInfo? Method(Type type, string name, params Type[] parameterTypes)
    {
        var key = (name, (Type?)null, Signature(parameterTypes));
        var tm = Of(type);
        if (tm.Methods.TryGetValue(key, out var hit)) return hit;
        return tm.Methods.GetOrAdd(key, type.GetMethods(PublicInstance)
            .Where(m => m.Name == name && !m.IsGenericMethodDefinition && SameParameters(m, parameterTypes))
            .OrderByDescending(m => Depth(m.DeclaringType)).FirstOrDefault());
    }

    /// <summary>
    /// A public instance generic method definition with one type parameter and the given parameters, closed over
    /// <paramref name="typeArg"/> (cached), e.g. GenericMethod(entityType, "GetComponent", lifeType). Null when there
    /// is none; throws ArgumentException when <paramref name="typeArg"/> breaks the method's constraints. The cached
    /// closed method keeps <paramref name="typeArg"/> alive as long as <paramref name="type"/>: pass HUD types, not a
    /// plugin's (collectible) types.
    /// </summary>
    internal static MethodInfo? GenericMethod(Type type, string name, Type typeArg, params Type[] parameterTypes)
    {
        var key = (name, (Type?)typeArg, Signature(parameterTypes));
        var tm = Of(type);
        if (tm.Methods.TryGetValue(key, out var hit)) return hit;
        var def = type.GetMethods(PublicInstance)
            .Where(m => m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && SameParameters(m, parameterTypes))
            .OrderByDescending(m => Depth(m.DeclaringType)).FirstOrDefault();
        var closed = def?.MakeGenericMethod(typeArg);   // throws on a constraint violation: not cached
        return tm.Methods.GetOrAdd(key, closed);
    }

    private static string Signature(Type[] types) => types.Length switch
    {
        0 => "",
        1 => types[0].AssemblyQualifiedName ?? types[0].Name,
        _ => string.Join(",", types.Select(t => t.AssemblyQualifiedName ?? t.Name)),
    };

    private static bool SameParameters(MethodInfo m, Type[] types)
    {
        var ps = m.GetParameters();
        if (ps.Length != types.Length) return false;
        for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType != types[i]) return false;
        return true;
    }

    private static Member? Lookup(Type type, string name, BindingFlags flags, Kinds kinds)
    {
        MemberInfo? best = null;
        int bestDepth = -1;
        if ((kinds & Kinds.Property) != 0)
            foreach (var p in type.GetProperties(flags))
            {
                if (p.Name != name || p.GetIndexParameters().Length > 0) continue;
                var d = Depth(p.DeclaringType);
                if (d > bestDepth) { best = p; bestDepth = d; }
            }
        if ((kinds & Kinds.Field) != 0)
            foreach (var f in type.GetFields(flags))
            {
                if (f.Name != name) continue;
                var d = Depth(f.DeclaringType);
                if (d > bestDepth) { best = f; bestDepth = d; }   // strictly deeper: a property on the same type wins
            }
        return best == null ? null : new Member(best);
    }

    /// <summary>How many base types a type has (object = 0).</summary>
    internal static int Depth(Type? t)
    {
        var d = 0;
        for (var b = t?.BaseType; b != null; b = b.BaseType) d++;
        return d;
    }

    // ── Types by name ────────────────────────────────────────────────

    /// <summary>One index over the types of every non-collectible loaded assembly (HUD core, game offsets, BCL).</summary>
    internal static TypeIndex Types => _typeIndex.Value;

    private static readonly Lazy<TypeIndex> _typeIndex = new(() => new TypeIndex(), LazyThreadSafetyMode.ExecutionAndPublication);

    internal sealed class TypeIndex
    {
        private static readonly Type[] None = [];
        private readonly Dictionary<string, Type[]> _byFull;
        private readonly Dictionary<string, Type[]> _bySimple;
        /// <summary>Assemblies whose types could only be read in part (ReflectionTypeLoadException), with the first loader error.</summary>
        public readonly IReadOnlyList<string> Partial;
        public readonly int TypeCount;
        public readonly long BuildMs;

        internal TypeIndex()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var full = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
            var simple = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
            var partial = new List<string>();
            var count = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Collectible = a plugin's load context (this bridge included): indexing it would keep it alive after a reload.
                if (asm.IsCollectible || asm.IsDynamic) continue;
                Type?[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                    partial.Add($"{asm.GetName().Name}: {ex.LoaderExceptions.FirstOrDefault()?.Message}");
                }
                catch (Exception ex) { partial.Add($"{asm.GetName().Name}: {ex.Message}"); continue; }
                foreach (var t in types)
                {
                    if (t == null || t.Name.Length == 0 || t.Name[0] == '<') continue;   // compiler-generated
                    count++;
                    Add(simple, t.Name, t);
                    if (t.FullName != null) Add(full, t.FullName, t);
                }
            }
            _byFull = full.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            _bySimple = simple.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            Partial = partial;
            TypeCount = count;
            BuildMs = sw.ElapsedMilliseconds;
        }

        private static void Add(Dictionary<string, List<Type>> map, string key, Type t)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<Type>(1);
            list.Add(t);
        }

        /// <summary>Every type with this full name (case-insensitive), in assembly load order.</summary>
        public Type[] ByFullName(string fullName) => _byFull.TryGetValue(fullName, out var t) ? t : None;

        /// <summary>Every type with this simple name (case-insensitive), in assembly load order.</summary>
        public Type[] BySimpleName(string name) => _bySimple.TryGetValue(name, out var t) ? t : None;

        /// <summary>Full-name matches when there are any, else simple-name matches.</summary>
        public Type[] Candidates(string name)
        {
            var full = name.Contains('.') ? ByFullName(name) : None;
            return full.Length > 0 ? full : BySimpleName(name);
        }

        /// <summary>
        /// The best candidate for <paramref name="name"/> (full or simple) that passes <paramref name="filter"/>: the
        /// highest <paramref name="rank"/>, the first loaded on a tie. Null when none passes.
        /// </summary>
        public Type? Resolve(string name, Func<Type, bool>? filter = null, Func<Type, int>? rank = null)
        {
            Type? best = null;
            var bestRank = int.MinValue;
            foreach (var t in Candidates(name))
            {
                if (filter != null && !filter(t)) continue;
                var r = rank?.Invoke(t) ?? 0;
                if (r > bestRank) { best = t; bestRank = r; }
            }
            return best;
        }
    }
}
