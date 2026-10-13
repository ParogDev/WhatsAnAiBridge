using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace WhatsAnAiBridge;

internal enum LinkKind { Field, Property, Method, FieldOfType }

/// <summary>
/// One private/internal member the bridge reaches by reflection: a HUD, Harmony/MonoMod or runtime internal that a
/// HUD update (or a new .NET / Harmony) can rename or reshape. Call sites resolve through the link (so the preflight
/// checks exactly what the code uses) and get null when it broke; <see cref="Check"/> names the broken link.
/// </summary>
internal sealed class HudLink
{
    public string Id { get; }
    /// <summary>What stops working without it.</summary>
    public string Feature { get; }
    /// <summary>How the owner type is named in reports (the type itself may be found by shape, not by name).</summary>
    public string TypeLabel { get; }
    /// <summary>Member name; for <see cref="LinkKind.FieldOfType"/> a label (the field is found by its type).</summary>
    public string Member { get; }
    public LinkKind Kind { get; }
    public BindingFlags Flags { get; }
    /// <summary>Field/property type the code needs (assignable to it), or a method's return type; null = any.</summary>
    public Type? ValueType { get; }
    /// <summary>Methods: exact parameter types (also searched on the type's interfaces); null = the first method of that name.</summary>
    public Type[]? Parameters { get; }
    /// <summary>The link whose resolved member gives this link's owner type (checked first).</summary>
    public HudLink? DependsOn { get; }

    private readonly Func<Type?> _typeOf;
    private readonly Func<string?>? _notNow;
    private readonly ConcurrentDictionary<Type, MemberInfo?> _cache = new();

    public HudLink(string id, string feature, string typeLabel, string member, LinkKind kind, BindingFlags flags, Func<Type?> typeOf,
        Type? valueType = null, Type[]? parameters = null, HudLink? dependsOn = null, Func<string?>? notNow = null)
    {
        Id = id; Feature = feature; TypeLabel = typeLabel; Member = member; Kind = kind; Flags = flags;
        _typeOf = typeOf; ValueType = valueType; Parameters = parameters; DependsOn = dependsOn; _notNow = notNow;
        HudLinks.All.Add(this);
    }

    /// <summary>For tests: resolve as if the member had this name (a simulated HUD rename).</summary>
    internal string? SimulatedName { get; set; }
    private string LookupName => SimulatedName ?? Member;

    /// <summary>The owner type the preflight checks (call sites may pass the runtime type instead).</summary>
    public Type? DeclaredType
    {
        get { try { return _typeOf(); } catch { return null; } }
    }

    public string Expected => Kind switch
    {
        LinkKind.Field => $"{Scope} field {Member}{(ValueType != null ? $" : {Name(ValueType)}" : "")}",
        LinkKind.Property => $"{Scope} property {Member}{(ValueType != null ? $" : {Name(ValueType)}" : "")}",
        LinkKind.Method => $"method {(ValueType != null ? Name(ValueType) + " " : "")}{Member}({(Parameters == null ? "..." : string.Join(", ", Parameters.Select(Name)))})",
        _ => $"an instance field of type {Name(ValueType!)} ({Member})",
    };

    private string Scope => (Flags & BindingFlags.Static) != 0 && (Flags & BindingFlags.Instance) == 0 ? "static" : "instance";

    /// <summary>The member on <paramref name="t"/> with the expected shape, or null (cached per type).</summary>
    public MemberInfo? On(Type? t) => t == null ? null : SimulatedName != null ? Lookup(t, out _) : _cache.GetOrAdd(t, x => Lookup(x, out _));

    public FieldInfo? FieldOn(Type? t) => On(t) as FieldInfo;
    public MethodInfo? MethodOn(Type? t) => On(t) as MethodInfo;
    public PropertyInfo? PropertyOn(Type? t) => On(t) as PropertyInfo;
    /// <summary>The field on the declared owner type (static fields, and instance fields whose owner type is fixed).</summary>
    public FieldInfo? Field => FieldOn(DeclaredType);
    public MethodInfo? Method => MethodOn(DeclaredType);
    public PropertyInfo? Property => PropertyOn(DeclaredType);

    /// <summary>Why <see cref="On"/> gave null for <paramref name="t"/>: the link, what was expected and what is there instead.</summary>
    public string Broken(Type? t)
    {
        if (t == null) return $"{TypeLabel}: type not found (expected it to declare {Expected})";
        Lookup(t, out var why);
        return $"{Name(t)}.{Member}: expected {Expected}; {why ?? "found"}";
    }

    private MemberInfo? Lookup(Type t, out string? why)
    {
        why = null;
        var name = LookupName;
        MemberInfo? hit = null;
        var types = Hierarchy(t).ToList();
        if (Kind == LinkKind.Method)
            types.AddRange(t.GetInterfaces());
        foreach (var c in types)
        {
            const BindingFlags decl = BindingFlags.DeclaredOnly;
            hit = Kind switch
            {
                LinkKind.Field => c.GetField(name, Flags | decl),
                LinkKind.Property => c.GetProperties(Flags | decl).FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0),
                LinkKind.Method => Parameters == null
                    ? c.GetMethods(Flags | decl).FirstOrDefault(m => m.Name == name)
                    : c.GetMethod(name, Flags | decl, null, Parameters, null),
                _ => c.GetFields(Flags | decl).FirstOrDefault(f => f.FieldType == ValueType),
            };
            if (hit != null) break;
        }
        if (hit == null) { why = FoundInstead(t, types); return null; }
        var actual = hit switch { FieldInfo f => f.FieldType, PropertyInfo p => p.PropertyType, MethodInfo m => m.ReturnType, _ => null };
        if (ValueType != null && actual != null && !ValueType.IsAssignableFrom(actual))
        {
            why = $"found {Describe(hit)} instead (not a {Name(ValueType)})";
            return null;
        }
        return hit;
    }

    /// <summary>What the type has instead: the same name with another kind/scope/signature, a near name, or the fields of the wanted type.</summary>
    private string FoundInstead(Type t, List<Type> types)
    {
        const BindingFlags every = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var all = types.SelectMany(c => c.GetMembers(every)).Where(m => m is FieldInfo or PropertyInfo or MethodInfo).ToList();
        var name = LookupName;
        var same = all.Where(m => m.Name == name).Take(3).ToList();
        if (same.Count > 0) return $"found {string.Join(", ", same.Select(Describe))} instead";
        static string Key(string s) => s.TrimStart('_').Replace("s_", "").ToLowerInvariant();
        var near = all.Where(m => Key(m.Name) == Key(name) || (Key(name).Length >= 5 && Key(m.Name).Contains(Key(name), StringComparison.Ordinal))).Take(3).ToList();
        if (near.Count > 0) return $"no member named {name}; near names: {string.Join(", ", near.Select(Describe))}";
        if (ValueType != null && Kind is LinkKind.Field or LinkKind.FieldOfType)
        {
            var typed = all.OfType<FieldInfo>().Where(f => ValueType.IsAssignableFrom(f.FieldType) && f.FieldType != typeof(object)).Take(4).ToList();
            if (typed.Count > 0) return $"no {(Kind == LinkKind.FieldOfType ? "such field" : $"member named {name}")}; fields of that type: {string.Join(", ", typed.Select(Describe))}";
        }
        return $"no member named {name} ({all.OfType<FieldInfo>().Count()} fields, {all.OfType<PropertyInfo>().Count()} properties, {all.OfType<MethodInfo>().Count()} methods on {Name(t)} and its bases)";
    }

    private static IEnumerable<Type> Hierarchy(Type t)
    {
        for (var c = t; c != null && c != typeof(object); c = c.BaseType) yield return c;
    }

    internal static string Describe(MemberInfo m) => m switch
    {
        FieldInfo f => $"{(f.IsStatic ? "static " : "")}{(f.IsPublic ? "public" : "non-public")} field {f.Name} : {Name(f.FieldType)}",
        PropertyInfo p => $"property {p.Name} : {Name(p.PropertyType)}",
        MethodInfo mi => $"{(mi.IsStatic ? "static " : "")}method {Name(mi.ReturnType)} {mi.Name}({string.Join(", ", mi.GetParameters().Select(x => Name(x.ParameterType)))})",
        _ => m.Name,
    };

    internal static string Name(Type t) =>
        t.IsArray ? Name(t.GetElementType()!) + "[]"
        : t.IsGenericType ? $"{t.Name[..Math.Max(0, t.Name.IndexOf('`'))]}<{string.Join(", ", t.GetGenericArguments().Select(Name))}>"
        : t.Name;

    /// <summary>The preflight row: ok, broken (gone: a HUD/runtime update), not_now (there, but its value isn't set yet), blocked (needs a broken link).</summary>
    public LinkCheckDto Check()
    {
        var row = new LinkCheckDto { Id = Id, Feature = Feature, Type = TypeLabel, Member = Member, Expected = Expected };
        if (DependsOn != null && DependsOn.On(DependsOn.DeclaredType) == null)
        {
            row.Status = "blocked";
            row.Found = $"needs {DependsOn.Id}, which is broken";
            return row;
        }
        Type? t;
        try { t = _typeOf(); }
        catch (Exception ex) { row.Status = "broken"; row.Found = $"finding {TypeLabel} threw {ex.GetType().Name}: {ex.Message}"; return row; }
        if (t == null) { row.Status = "broken"; row.Found = $"type {TypeLabel} not found"; return row; }
        row.Type = t.FullName ?? t.Name;
        var m = On(t);
        if (m == null) { row.Status = "broken"; Lookup(t, out var why); row.Found = why; return row; }
        row.Status = "ok";
        if (m.Name != Member) row.Found = Describe(m);
        try { if (_notNow?.Invoke() is { } note) { row.Status = "not_now"; row.Note = note; } }
        catch (Exception ex) { row.Status = "not_now"; row.Note = $"{ex.GetType().Name}: {ex.Message}"; }
        return row;
    }
}

/// <summary>
/// The declared table of every non-public (or otherwise drift-prone) reflection target of the bridge. Call sites use
/// these links, so the preflight (Preflight.cs) checks exactly what the code reaches. Members the user names at run
/// time (eval paths, explore, settings paths) are not here: they are checked where they are walked.
/// </summary>
internal static class HudLinks
{
    // Declared first: every HudLink adds itself in its constructor.
    internal static readonly List<HudLink> All = new();

    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static Type? Concrete<TInterface>(Func<Type, bool>? where = null)
    {
        Type[] types;
        try { types = typeof(TInterface).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray()!; }
        return types.FirstOrDefault(t => t is { IsClass: true, IsAbstract: false } && typeof(TInterface).IsAssignableFrom(t) && (where == null || where(t)));
    }

    private static Type? Loaded(string assembly, string type) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)?.GetType(type);

    /// <summary>The compile-time type of a GameController member (the lambda is never run): no lookup by name, so
    /// it holds on PoE2, whose GameController.PluginBridge isn't found by GetProperty.</summary>
    private static Type DeclaredTypeOf<T>(Func<GameController, T> member) => typeof(T);

    private static Type? ElementOf(HudLink link) => link.Field?.FieldType is { IsArray: true } a ? a.GetElementType() : null;

    // ── HUD memory backend (RawMemory.cs, PipelineTrace.cs) ──
    // Route's FreshView.cs reaches Memory._backend the same way (its own copy, not covered here).

    internal static readonly HudLink MemoryBackend = new("memory.backend", "raw reads skipping the page cache (RawMemory), pipeline_trace read timing",
        "Memory (the IMemory implementation)", "_backend", LinkKind.Field, Inst,
        () => Concrete<IMemory>(), valueType: typeof(IMemoryBackend));

    internal static readonly HudLink PagedInner = new("memory.paged_inner", "raw reads go to the game, not the page cache; pipeline_trace times game fetches",
        "PagedMemoryBackend (an IMemoryBackend wrapping another)", "inner IMemoryBackend", LinkKind.FieldOfType, Inst,
        () => Concrete<IMemoryBackend>(t => t.Name == "PagedMemoryBackend") ?? Concrete<IMemoryBackend>(t => t.GetFields(Inst).Any(f => f.FieldType == typeof(IMemoryBackend))),
        valueType: typeof(IMemoryBackend));

    internal static readonly HudLink BackendRead = new("memory.try_read", "raw reads (RawMemory)",
        "IMemoryBackend", "TryReadMemory", LinkKind.Method, BindingFlags.Instance | BindingFlags.Public,
        () => typeof(IMemoryBackend), valueType: typeof(bool), parameters: [typeof(IntPtr), typeof(Span<byte>)]);

    // ── HUD core ──

    internal static readonly HudLink ReloadSourcePlugin = new("hud.reload_source_plugin", "reload_plugin",
        "PluginManager", "ReloadSourcePlugin", LinkKind.Method, Inst,
        () => typeof(PluginManager), parameters: [typeof(string), typeof(Action)]);

    internal static readonly HudLink BridgeMethods = new("hud.plugin_bridge_methods", "plugin_bridge_methods / plugin_bridge_call, stale-method cleanup after reloads",
        "PluginBridge", "_methods", LinkKind.Field, BindingFlags.Instance | BindingFlags.NonPublic,
        () => DeclaredTypeOf(g => g.PluginBridge), valueType: typeof(IDictionary),
        notNow: () => Core.Current?.GameController?.PluginBridge == null ? "GameController.PluginBridge is not created yet" : null);

    internal static readonly HudLink CoreLogger = new("hud.core_logger", "bridge lines in the HUD log file (HudLog)",
        "Core", "Logger", LinkKind.Property, BindingFlags.Static | BindingFlags.Public,
        () => typeof(Core),
        notNow: () => typeof(Core).GetProperty("Logger", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) == null ? "Core.Logger is not set yet" : null);

    internal static readonly HudLink LoggerInformation = new("hud.logger_information", "bridge lines in the HUD log file (HudLog)",
        "Core.Logger's type", "Information", LinkKind.Method, BindingFlags.Instance | BindingFlags.Public,
        () => CoreLogger.Property?.PropertyType, parameters: [typeof(string), typeof(object[])], dependsOn: CoreLogger);

    internal static readonly HudLink LoggerError = new("hud.logger_error", "bridge errors in the HUD log file (HudLog)",
        "Core.Logger's type", "Error", LinkKind.Method, BindingFlags.Instance | BindingFlags.Public,
        () => CoreLogger.Property?.PropertyType, parameters: [typeof(string), typeof(object[])], dependsOn: CoreLogger);

    // ── HUD overlay frame (PipelineTrace.cs) ──

    private const BindingFlags AllDeclared = Inst | BindingFlags.Static;

    internal static readonly HudLink RendererUpdate = new("overlay.imgui_update", "pipeline_trace / hud_health_report frame timing",
        "ClickableTransparentOverlay.ImGuiRenderer", "Update", LinkKind.Method, AllDeclared,
        () => Loaded("ClickableTransparentOverlay", "ClickableTransparentOverlay.ImGuiRenderer"));

    internal static readonly HudLink RendererRender = new("overlay.imgui_render", "pipeline_trace draw-data timing",
        "ClickableTransparentOverlay.ImGuiRenderer", "Render", LinkKind.Method, AllDeclared,
        () => Loaded("ClickableTransparentOverlay", "ClickableTransparentOverlay.ImGuiRenderer"));

    internal static readonly HudLink OverlayReplaceFont = new("overlay.replace_font", "pipeline_trace Present timing",
        "ClickableTransparentOverlay.Overlay", "ReplaceFontIfRequired", LinkKind.Method, AllDeclared,
        () => Loaded("ClickableTransparentOverlay", "ClickableTransparentOverlay.Overlay"));

    // ── Harmony / MonoMod (Instrumentation.cs) ──

    private static Type? Harmony(string type) => typeof(Harmony).Assembly.GetType(type);

    internal static readonly HudLink MonoModAssemblyCache = new("monomod.assembly_cache", "patching a reloaded plugin binds to its own load (profile_plugin, pipeline_trace)",
        "MonoMod.Utils.ReflectionHelper", "AssemblyCache", LinkKind.Field, Stat, () => Harmony("MonoMod.Utils.ReflectionHelper"), valueType: typeof(IDictionary));
    internal static readonly HudLink MonoModAssembliesCache = new("monomod.assemblies_cache", "patching a reloaded plugin binds to its own load (profile_plugin, pipeline_trace)",
        "MonoMod.Utils.ReflectionHelper", "AssembliesCache", LinkKind.Field, Stat, () => Harmony("MonoMod.Utils.ReflectionHelper"), valueType: typeof(IDictionary));
    internal static readonly HudLink MonoModResolveCache = new("monomod.resolve_reflection_cache", "patching a reloaded plugin binds to its own load (profile_plugin, pipeline_trace)",
        "MonoMod.Utils.ReflectionHelper", "ResolveReflectionCache", LinkKind.Field, Stat, () => Harmony("MonoMod.Utils.ReflectionHelper"), valueType: typeof(IDictionary));

    internal static readonly HudLink[] MonoModCaches = [MonoModAssemblyCache, MonoModAssembliesCache, MonoModResolveCache];

    internal static readonly HudLink HarmonyDetours = new("harmony.detours", "restoring a patched method's original code when instrumentation ends",
        "HarmonyLib.PatchTools", "detours", LinkKind.Field, Stat, () => Harmony("HarmonyLib.PatchTools"), valueType: typeof(IDictionary));
    internal static readonly HudLink HarmonyState = new("harmony.state", "restoring a patched method's original code when instrumentation ends",
        "HarmonyLib.HarmonySharedState", "state", LinkKind.Field, Stat, () => Harmony("HarmonyLib.HarmonySharedState"), valueType: typeof(IDictionary));
    internal static readonly HudLink HarmonyOriginals = new("harmony.originals", "restoring a patched method's original code when instrumentation ends",
        "HarmonyLib.HarmonySharedState", "originals", LinkKind.Field, Stat, () => Harmony("HarmonyLib.HarmonySharedState"), valueType: typeof(IDictionary));
    internal static readonly HudLink HarmonyLocker = new("harmony.locker", "restoring a patched method's original code when instrumentation ends",
        "HarmonyLib.PatchProcessor", "locker", LinkKind.Field, Stat, () => Harmony("HarmonyLib.PatchProcessor"));

    // ── .NET ArrayPool<byte>.Shared (GcPool.cs, PipelineTrace.cs) ──

    internal static readonly HudLink PoolBuckets = new("runtime.pool_buckets", "gc.pool and the health report's ArrayPool check",
        "SharedArrayPool<byte>", "_buckets", LinkKind.Field, Inst, () => System.Buffers.ArrayPool<byte>.Shared.GetType(), valueType: typeof(Array));
    internal static readonly HudLink PoolPartitions = new("runtime.pool_partitions", "gc.pool and the health report's ArrayPool check",
        "SharedArrayPool<byte> bucket (element of _buckets)", "_partitions", LinkKind.Field, Inst, () => ElementOf(PoolBuckets), valueType: typeof(Array), dependsOn: PoolBuckets);
    internal static readonly HudLink PoolArrays = new("runtime.pool_arrays", "gc.pool and the health report's ArrayPool check",
        "SharedArrayPool<byte> partition (element of _partitions)", "_arrays", LinkKind.Field, Inst, () => ElementOf(PoolPartitions), valueType: typeof(Array), dependsOn: PoolPartitions);
    internal static readonly HudLink PoolCount = new("runtime.pool_count", "gc.pool",
        "SharedArrayPool<byte> partition (element of _partitions)", "_count", LinkKind.Field, Inst, () => ElementOf(PoolPartitions), valueType: typeof(int), dependsOn: PoolPartitions);
    internal static readonly HudLink PoolPartitionCount = new("runtime.pool_partition_count", "the health report's ArrayPool limits",
        "System.Buffers.SharedArrayPoolStatics", "s_partitionCount", LinkKind.Field, Stat,
        () => typeof(System.Buffers.ArrayPool<byte>).Assembly.GetType("System.Buffers.SharedArrayPoolStatics"), valueType: typeof(int));
    internal static readonly HudLink PoolMaxPerPartition = new("runtime.pool_max_per_partition", "the health report's ArrayPool limits",
        "System.Buffers.SharedArrayPoolStatics", "s_maxArraysPerPartition", LinkKind.Field, Stat,
        () => typeof(System.Buffers.ArrayPool<byte>).Assembly.GetType("System.Buffers.SharedArrayPoolStatics"), valueType: typeof(int));
}
