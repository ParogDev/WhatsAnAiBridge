using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// The observer's layers: runtime specs, not code. A layer is {id, path, mode, hz}: any walker path, watched by one of
/// five generic modes that work on whatever the path resolves to, through reflection:
///   struct  the object's cached offsets struct, read raw (leaf backend) and diffed byte by byte; each changed range is
///           named by the runtime layout (HUD property, else struct field; null = unmapped). For server-sent state.
///   props   its public scalar properties (numbers, enums, bools, strings, vectors), diffed by value.
///   dict    a dictionary (IDictionary): keys added, removed, values changed (e.g. Stats.StatDictionary).
///   list    a collection: items added and removed, by identity (spec.key property, default Address).
///   each    a collection, a few values per item: spec.props (dotted sub-paths, e.g. Inventory.Hash) of each item, keyed
///           by spec.key (a dotted sub-path too, default Address); unit = "&lt;key&gt;.&lt;prop&gt;". One layer over all
///           the player's inventories shows which one changed and when.
/// Every change is a "layer" event {layer, mode, unit, name?, old, new, ...} on the observer's clock (t, frame), so
/// layers line up with each other and with ui/area/level/entity. A unit (offset block, property, key) that changes more
/// than 20 times in 10 s is reported once as layer.noisy and then only counted; every change of every unit is counted
/// in the layer's map (observe.layer_map) whatever the journal does. Specs persist in observe\state.json.
///   observe.layers {} / observe.layer_set {id, path, mode, hz?, enabled?, key?, props?} / observe.layer_remove {id}
///   observe.layer_map {layer, unmappedOnly?, minChanges?, sort?: changes|recent|unit, limit?}
/// Read-only. Runs from Render (main thread) within a per-tick time budget.
/// </summary>
public partial class WhatsAnAiBridge
{
    /// <summary>A layer spec (persisted; the JSON contract of observe.layer_set).</summary>
    public sealed class LayerSpec
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("path")] public string Path { get; set; } = "";
        [JsonProperty("mode")] public string Mode { get; set; } = "props";
        [JsonProperty("hz")] public double Hz { get; set; } = 4;
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        /// <summary>list mode: the item property that identifies an item (default Address).</summary>
        [JsonProperty("key", NullValueHandling = NullValueHandling.Ignore)] public string? Key { get; set; }
        /// <summary>each mode: the values to watch on every item, as dotted sub-paths of the item.</summary>
        [JsonProperty("props", NullValueHandling = NullValueHandling.Ignore)] public List<string>? Props { get; set; }
    }

    private static readonly string[] LayerModes = ["struct", "props", "dict", "list", "each"];

    private static List<LayerSpec> DefaultLayers() =>
    [
        new() { Id = "server", Path = "GameController.IngameState.ServerData", Mode = "struct", Hz = 10 },
        new() { Id = "stats", Path = "GameController.Player.GetComponent<Stats>().StatDictionary", Mode = "dict", Hz = 4 },
        new() { Id = "life", Path = "GameController.Player.GetComponent<Life>()", Mode = "props", Hz = 4 },
        new() { Id = "buffs", Path = "GameController.Player.GetComponent<Buffs>().BuffsList", Mode = "list", Hz = 4, Key = "Name" },
        new() { Id = "inventories", Path = "GameController.IngameState.ServerData.PlayerInventories", Mode = "each", Hz = 2, Key = "TypeId", Props = ["Inventory.Hash", "Inventory.ItemCount"] },
    ];

    private sealed class LayerUnit { public long Changes; public double FirstT, LastT; public string Last = ""; public string? Name; }

    private sealed class LayerRun
    {
        public LayerSpec Spec = new();
        public DateTime NextAt = DateTime.MinValue;
        public string? Broken;            // a link that doesn't exist (walker error, wrong mode): reported, not retried every tick
        public string? NotNow;            // resolved to null right now (loading, no player)
        public long Events, Ticks;
        public double CostMs;             // last tick
        // Since the last bridge.self_perf (reset there): time and bytes of every tick, the path resolve's share, the worst tick.
        public long SumTicks, SumBytes, ResolveTicks, PerfTicks; public double MaxMs;
        public Type? CheckedType;         // the resolved type ModeMismatch last passed
        public EachCache? Each;           // each: items listed every 5 s and the raw read plans
        // struct
        public long Address; public int Size; public byte[] Prev = [], Now = []; public bool HasPrev; public DateTime ResolvedAt;
        public List<(int off, int len, string name, bool property)> Names = [];
        // props / dict / list
        public Dictionary<object, string>? ListA, ListB;               // list: key -> preview (swapped)
        public Type? ListType; public MemberIndex.Member? ListKey;     // list: the key member of the last item type seen
        public Type? DictType; public DictDiff? Dict;                  // dict: the typed differ for the last dictionary type
        public string? DictNote;                                       // dict: why the typed differ couldn't be built (object maps then)
        public StatsRawGate? StatsGate;                                // dict over Stats.StatDictionary: the raw change gate
        public Dictionary<object, object?>? ValuesA, ValuesB;          // props / dict (swapped)
        public readonly Dictionary<object, string> KeyNames = new();
        public Dictionary<Type, MemberIndex.Member[]> PropCache = new();
        public HashSet<string> SlowProps = new();
        // noise and map
        public readonly Dictionary<string, (int count, double windowStart)> Rate = new();
        public readonly Dictionary<string, long> Noisy = new();
        public readonly Dictionary<string, LayerUnit> Map = new();
    }

    private readonly List<LayerRun> _layers = [];
    private bool _layersLoaded;
    private string? _layerArea;
    private const double LayerNoisyPer10s = 20, LayerTickBudgetMs = 4, StructResolveSeconds = 1;
    private const int LayerMaxStructBytes = 64 * 1024, LayerShowBytes = 32;

    // ── Specs ────────────────────────────────────────────────────────

    private void LayersLoad()
    {
        if (_layersLoaded) return;
        _layersLoaded = true;
        var specs = Obs().Layers ?? DefaultLayers();
        foreach (var s in specs) _layers.Add(new LayerRun { Spec = s });
    }

    private void LayersSave()
    {
        lock (_obsLock) Obs().Layers = _layers.Select(l => l.Spec).ToList();
        SaveObs();
    }

    /// <summary>Per tick since the last bridge.self_perf: average and worst time, the path resolve's share, bytes allocated.</summary>
    private static JObject? LayerPerf(LayerRun l)
    {
        if (l.PerfTicks == 0) return null;
        var f = System.Diagnostics.Stopwatch.Frequency;
        return new JObject
        {
            ["ticks"] = l.PerfTicks, ["avgUs"] = Math.Round(l.SumTicks * 1e6 / f / l.PerfTicks, 1), ["maxMs"] = Math.Round(l.MaxMs, 3),
            ["resolveUs"] = Math.Round(l.ResolveTicks * 1e6 / f / l.PerfTicks, 1), ["bytesPerTick"] = l.SumBytes / l.PerfTicks,
        };
    }

    /// <summary>For bridge.self_perf: every layer's cost since the previous call, then reset (main-thread counters, read racily).</summary>
    private JObject LayersSelfPerf()
    {
        var o = new JObject();
        foreach (var l in _layers.ToArray())
        {
            if (LayerPerf(l) is { } p) o[l.Spec.Id] = p;
            l.SumTicks = l.SumBytes = l.ResolveTicks = l.PerfTicks = 0; l.MaxMs = 0;
        }
        return o;
    }

    private JObject LayersList()
    {
        LayersLoad();
        return new JObject { ["ok"] = true, ["modes"] = new JArray(LayerModes), ["layers"] = new JArray(_layers.Select(LayerStatus)) };
    }

    private JObject LayerStatus(LayerRun l) => new()
    {
        ["spec"] = JObject.FromObject(l.Spec),
        ["events"] = l.Events, ["ticks"] = l.Ticks, ["costMs"] = Math.Round(l.CostMs, 3), ["perf"] = LayerPerf(l),
        ["unitsChanged"] = l.Map.Count, ["noisyUnits"] = l.Noisy.Count,
        ["bytes"] = l.Spec.Mode == "struct" ? l.Size : null, ["namedRanges"] = l.Spec.Mode == "struct" ? l.Names.Count : null,
        ["slowProps"] = l.SlowProps.Count > 0 ? new JArray(l.SlowProps) : null,
        ["rawGate"] = l.StatsGate is { } g ? JObject.FromObject(g.Status()) : null,
        ["rawPaths"] = l.Each?.Plans != null ? new JObject(l.Spec.Props!.Select((p, i) => new JProperty(p, l.Each.PlanNotes[i]))) : null,
        ["items"] = l.Each?.Items.Count,
        ["dictDiff"] = l.DictNote,
        ["broken"] = l.Broken, ["notNow"] = l.NotNow,
    };

    private JObject LayerSet(JToken? p)
    {
        LayersLoad();
        var spec = p?.ToObject<LayerSpec>() ?? new LayerSpec();
        if (string.IsNullOrWhiteSpace(spec.Id)) return Err("bad_request", "id is required (a short name for the layer, e.g. buffs)");
        if (string.IsNullOrWhiteSpace(spec.Path) || !spec.Path.StartsWith("GameController", StringComparison.Ordinal))
            return Err("bad_request", "path is required and starts with GameController (a walker path, as in eval_path / explore_object)");
        if (!LayerModes.Contains(spec.Mode)) return Err("bad_request", $"mode must be one of {string.Join(", ", LayerModes)}");
        if (spec.Mode == "each" && (spec.Props == null || spec.Props.Count == 0 || spec.Props.Count > 8))
            return Err("bad_request", "each mode needs props: 1-8 dotted sub-paths of each item, e.g. [\"Inventory.Hash\"]");
        spec.Hz = Math.Clamp(spec.Hz, 0.2, 30);
        // Preflight while in game: the path must resolve and suit the mode, or say which link is wrong now.
        string? preflight = null;
        if (GameController.InGame)
        {
            var obj = new ExpressionWalker(GameController).Resolve(spec.Path, out var error);
            preflight = error != null ? $"path: {error}" : obj == null ? null : ModeMismatch(spec.Mode, obj);
            if (preflight == null && spec.Mode == "each" && obj is IEnumerable items && items.Cast<object?>().FirstOrDefault(i => i != null) is { } first)
            {
                var probe = new LayerRun { Spec = spec };
                try { foreach (var sub in spec.Props!.Prepend(spec.Key ?? "Address")) SubPath(probe, first, sub); }
                catch (MissingMemberException ex) { return Err("resolve_failed", $"each item ({first.GetType().Name}): {ex.Message}"); }
            }
            if (preflight != null && error != null) return Err("resolve_failed", preflight);
            if (preflight != null) return Err("wrong_mode", preflight);
        }
        _layers.RemoveAll(l => l.Spec.Id == spec.Id);
        _layers.Add(new LayerRun { Spec = spec });
        LayersSave();
        return new JObject { ["ok"] = true, ["layer"] = JObject.FromObject(spec), ["preflight"] = GameController.InGame ? "resolved" : "not in game: checked on first tick" };
    }

    private JObject LayerRemove(JToken? p)
    {
        LayersLoad();
        var id = p?["id"]?.ToString();
        var n = _layers.RemoveAll(l => l.Spec.Id == id);
        if (n == 0) return Err("not_found", $"No layer '{id}'. Layers: {string.Join(", ", _layers.Select(l => l.Spec.Id))}");
        LayersSave();
        return new JObject { ["ok"] = true, ["removed"] = id };
    }

    /// <summary>Why obj can't be watched in mode, or null.</summary>
    private static string? ModeMismatch(string mode, object obj) => mode switch
    {
        "struct" => MemoryObjects.CachedStruct(obj).value == null ? $"{obj.GetType().Name} holds no cached offsets struct (struct mode needs one; try props)" : null,
        "dict" => obj is IDictionary ? null : $"{obj.GetType().Name} is not a dictionary (IDictionary)",
        "list" or "each" => obj is IEnumerable && obj is not string ? null : $"{obj.GetType().Name} is not a collection",
        _ => null,
    };

    // ── Tick ─────────────────────────────────────────────────────────

    private void ObserveLayers(DateTime now)
    {
        LayersLoad();
        var area = GameController.Area?.CurrentArea?.Name;
        if (area != _layerArea)
        {
            _layerArea = area;
            foreach (var l in _layers.Where(l => l.Spec.Mode is "struct" or "list" or "each")) { l.HasPrev = false; l.ListA = null; l.Address = 0; l.Each = null; l.ValuesA = null; }
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var l in _layers)
        {
            if (!l.Spec.Enabled || l.Broken != null || now < l.NextAt) continue;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds > LayerTickBudgetMs) break;   // the rest next frame
            l.NextAt = now.AddMilliseconds(1000.0 / l.Spec.Hz);
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            try { LayerTick(l); }
            catch (Exception ex) { l.Broken = $"{ex.GetType().Name}: {ex.Message}"; LogFault($"[Observe] layer {l.Spec.Id}: {l.Broken}"); }
            var dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            l.CostMs = dt * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            l.SumTicks += dt; l.SumBytes += GC.GetAllocatedBytesForCurrentThread() - a0; l.PerfTicks++;
            if (l.CostMs > l.MaxMs) l.MaxMs = l.CostMs;
            l.Ticks++;
        }
    }

    private void LayerTick(LayerRun l)
    {
        // each: between listings the items are known (address, key): no need to resolve the collection again.
        if (l.Spec.Mode == "each" && l.Each is { Plans: not null } ec && (DateTime.UtcNow - ec.ListedAt).TotalSeconds < EachRelistSeconds)
        {
            ValuesTick(l, null!, ObsClock.Elapsed.TotalMilliseconds);
            return;
        }
        // struct: the object's address is known; the path is resolved again once a second (and after an area change) to
        // follow a move, and every tick in between is just the raw read.
        if (l.Spec.Mode == "struct" && l.Address != 0 && l.Size > 0 && (DateTime.UtcNow - l.ResolvedAt).TotalSeconds < StructResolveSeconds)
        {
            l.NotNow = null;
            StructDiff(l, ObsClock.Elapsed.TotalMilliseconds);
            return;
        }
        // dict over a Stats component's StatDictionary: skip the HUD's dictionary read while the raw stat pairs are unchanged.
        if (l.Spec.Mode == "dict" && l.Spec.Path.EndsWith(StatDictSuffix, StringComparison.Ordinal) && StatsDictTick(l)) return;
        var r0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var obj = new ExpressionWalker(GameController).Resolve(l.Spec.Path, out var error);
        l.ResolveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - r0;
        if (error != null) { l.Broken = $"path: {error}"; return; }   // the link no longer exists: say so once
        if (obj == null) { l.NotNow = "resolves to null right now"; return; }
        l.NotNow = null;
        // Once per resolved type: the struct check boxes the whole cached struct (34 KB for ServerData) every call.
        if (obj.GetType() != l.CheckedType)
        {
            if (ModeMismatch(l.Spec.Mode, obj) is { } wrong) { l.Broken = wrong; return; }
            l.CheckedType = obj.GetType();
        }
        var t = ObsClock.Elapsed.TotalMilliseconds;
        switch (l.Spec.Mode)
        {
            case "struct": StructTick(l, obj, t); break;
            case "props": case "dict": case "each": ValuesTick(l, obj, t); break;
            case "list": ListTick(l, (IEnumerable)obj, t); break;
        }
    }

    private const string StatDictSuffix = ".StatDictionary";

    /// <summary>
    /// The stats layer's fast path (StatsRaw.cs): resolve the Stats component, and read the HUD's StatDictionary (~90 us,
    /// ~65 KB on PoE2) only when its raw (id, value) pairs changed or the gate's periodic full read is due; the gate checks
    /// the raw pairs against every dictionary it is given and turns itself off naming the link on a mismatch. False when
    /// the path's parent isn't a Stats component or doesn't resolve: the caller takes the generic path (which names a
    /// broken link).
    /// </summary>
    private bool StatsDictTick(LayerRun l)
    {
        var r0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var parent = new ExpressionWalker(GameController).Resolve(l.Spec.Path[..^StatDictSuffix.Length], out var error);
        if (error != null || parent is not Stats stats) { l.StatsGate = null; return false; }
        var now = DateTime.UtcNow;
        var gate = l.StatsGate ??= new StatsRawGate($"layer {l.Spec.Id}");
        var address = stats.Address;
        if (gate.Unchanged(this, address, now)) { l.NotNow = null; l.ResolveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - r0; return true; }
        var dict = stats.StatDictionary;
        l.ResolveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - r0;
        if (dict == null) { l.NotNow = "resolves to null right now"; return true; }
        gate.AfterRead(this, address, dict, now);
        l.NotNow = null;
        if (dict.GetType() != l.CheckedType)
        {
            if (ModeMismatch(l.Spec.Mode, dict) is { } wrong) { l.Broken = wrong; return true; }
            l.CheckedType = dict.GetType();
        }
        ValuesTick(l, dict, ObsClock.Elapsed.TotalMilliseconds);
        return true;
    }

    // ── struct ───────────────────────────────────────────────────────

    private void StructTick(LayerRun l, object obj, double t)
    {
        var address = MemoryObjects.Address(obj) ?? 0;
        if (address == 0) { l.NotNow = "Address is 0 right now"; return; }
        if (address != l.Address || l.Size == 0)
        {
            var (structValue, _) = MemoryObjects.CachedStruct(obj);
            if (structValue == null) { l.Broken = $"{obj.GetType().Name} holds no cached offsets struct"; return; }
            l.Size = Math.Min(System.Runtime.InteropServices.Marshal.SizeOf(structValue.GetType()), LayerMaxStructBytes);
            l.Address = address;
            l.Now = new byte[l.Size]; l.Prev = new byte[l.Size]; l.HasPrev = false;
            l.Names = StructNames(l.Spec.Path);
        }
        l.ResolvedAt = DateTime.UtcNow;
        StructDiff(l, t);
    }

    /// <summary>Reads the struct's bytes raw at the known address and reports the changed ranges.</summary>
    private void StructDiff(LayerRun l, double t)
    {
        if (RawReader() is not { } read || !read(new IntPtr(l.Address), l.Now)) { l.NotNow = "raw read failed"; return; }
        if (l.HasPrev)
        {
            var a = l.Prev; var b = l.Now;
            for (var i = 0; i < l.Size;)
            {
                if (a[i] == b[i]) { i++; continue; }
                var start = i; var end = i;
                for (var j = i + 1; j < l.Size && j <= end + 4; j++) if (a[j] != b[j]) end = j;   // join gaps of up to 3 bytes
                i = end + 1;
                StructChange(l, start, end - start + 1, t);
            }
        }
        (l.Prev, l.Now) = (l.Now, l.Prev);
        l.HasPrev = true;
    }

    private void StructChange(LayerRun l, int off, int len, double t)
    {
        var unit = $"0x{off:X}";
        var name = StructName(l, off);
        var show = Math.Min(len, LayerShowBytes);
        var newHex = Convert.ToHexString(l.Now, off, show);
        // Noise per 256-byte block: an array of positions changes all over its block.
        if (!Count(l, unit, name, newHex, t, $"block 0x{off & ~0xFF:X}")) return;
        var e = Event(l, unit, name, Convert.ToHexString(l.Prev, off, show), newHex);
        e["off"] = unit; e["len"] = len;
        if (len <= 8)
        {
            // i64 of the aligned 8 bytes whenever the change fits in them, plus i32 when it fits in an aligned 4: a series
            // (MCP observe_series) needs one width for every change of a unit, and which bytes change varies.
            var al = off & ~3; var al8 = off & ~7;
            if (off + len <= al + 4 && al + 4 <= l.Size) e["i32"] = $"{BitConverter.ToInt32(l.Prev, al)} -> {BitConverter.ToInt32(l.Now, al)}";
            if (off + len <= al8 + 8 && al8 + 8 <= l.Size) e["i64"] = $"{BitConverter.ToInt64(l.Prev, al8)} -> {BitConverter.ToInt64(l.Now, al8)}";
            else if (e["i32"] == null && al + 8 <= l.Size && off + len <= al + 8) e["i64"] = $"{BitConverter.ToInt64(l.Prev, al)} -> {BitConverter.ToInt64(l.Now, al)}";
        }
        Emit(l, e);
    }

    /// <summary>Offset -> name from the runtime layout: the HUD's property where one maps to the field, else the field.</summary>
    private List<(int off, int len, string name, bool property)> StructNames(string path)
    {
        var names = new List<(int, int, string, bool)>();
        try
        {
            var layout = RuntimeLayout(new JObject { ["path"] = path });
            static int Hex(JToken? t) => t?.Value<string>() is { } s && s.StartsWith("0x") ? Convert.ToInt32(s[2..], 16) : -1;
            var props = (layout["properties"] as JArray ?? []).Where(x => x["offset"] != null)
                .GroupBy(x => Hex(x["offset"])).ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(x => x["property"]!.ToString())));
            foreach (var f in layout["fields"] as JArray ?? [])
            {
                var off = Hex(f["offset"]);
                if (off < 0) continue;
                var len = f["size"]?.Value<int>() ?? 1;
                names.Add(props.TryGetValue(off, out var p) ? (off, len, p, true) : (off, len, f["name"]!.ToString(), false));
            }
        }
        catch { }
        return names.OrderBy(n => n.Item1).ToList();
    }

    private static string? StructName(LayerRun l, int off)
    {
        // Among the fields containing the first changed byte: a HUD property beats a bare field, then the smallest.
        string? best = null; var bestLen = int.MaxValue; var bestProp = false;
        foreach (var (o, len, name, prop) in l.Names)
        {
            if (o > off) break;
            if (off >= o + len) continue;
            if (best == null || (prop && !bestProp) || (prop == bestProp && len < bestLen)) { best = name; bestLen = len; bestProp = prop; }
        }
        return best;
    }

    // ── props / dict ─────────────────────────────────────────────────
    // Values are compared as boxed objects (Equals) and only the changed ones are formatted: formatting every key of a
    // 317-entry StatDictionary (a huge enum) every tick cost ~4 ms. Two maps per layer are swapped, not reallocated.

    private void PropValues(LayerRun l, object obj, Dictionary<object, object?> into)
    {
        var type = obj.GetType();
        if (!l.PropCache.TryGetValue(type, out var props))
            l.PropCache[type] = props = MemberIndex.ReadableProperties(type)
                .Where(p => IsScalar(p.Type) && p.Name is not ("Address" or "M")).ToArray();
        foreach (var p in props)
        {
            if (l.SlowProps.Contains(p.Name)) continue;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { into[p.Name] = p.Get(obj); } catch { continue; }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds > 1) l.SlowProps.Add(p.Name);   // skipped from now on
        }
    }

    private static void DictValues(IDictionary d, Dictionary<object, object?> into)
    {
        foreach (DictionaryEntry kv in d) into[kv.Key] = kv.Value;
    }

    private void ValuesTick(LayerRun l, object obj, double t)
    {
        if (l.Spec.Mode == "dict" && DictDiffFor(l, obj.GetType()) is { } typed) { typed.Tick(this, l, obj, t); return; }
        var now = l.ValuesB ??= new Dictionary<object, object?>();
        now.Clear();
        if (l.Spec.Mode == "dict") DictValues((IDictionary)obj, now);
        else if (l.Spec.Mode == "each") EachValuesRaw(l, (obj as IEnumerable)!, now);
        else PropValues(l, obj, now);
        var prev = l.ValuesA;
        (l.ValuesA, l.ValuesB) = (now, prev);
        if (prev == null) return;   // baseline
        foreach (var (k, v) in now)
            if (!prev.TryGetValue(k, out var old)) ValueChange(l, KeyText(l, k), null, Text(v), t);
            else if (!Equals(old, v)) ValueChange(l, KeyText(l, k), Text(old), Text(v), t);
        foreach (var (k, old) in prev)
            if (!now.ContainsKey(k)) ValueChange(l, KeyText(l, k), Text(old), null, t);
    }

    // ── dict, typed ──────────────────────────────────────────────────
    // A generic dictionary (any IEnumerable<KeyValuePair<K, V>>) is diffed in its own types: copying it into object maps
    // boxed every key and value each tick (317 GameStat/int pairs = ~15 KB per tick for the stats layer). Keys and values
    // are boxed and formatted only for a change. Built once per dictionary type (MakeGenericType), then no reflection.

    private abstract class DictDiff
    {
        public abstract void Tick(WhatsAnAiBridge b, LayerRun l, object dict, double t);
    }

    private sealed class DictDiff<TK, TV> : DictDiff where TK : notnull
    {
        private Dictionary<TK, TV>? _prev;
        private Dictionary<TK, TV> _now = new();

        public override void Tick(WhatsAnAiBridge b, LayerRun l, object dict, double t)
        {
            var now = _now;
            now.Clear();
            foreach (var kv in (IEnumerable<KeyValuePair<TK, TV>>)dict) now[kv.Key] = kv.Value;
            var prev = _prev;
            _prev = now;
            _now = prev ?? new Dictionary<TK, TV>();
            if (prev == null) return;   // baseline
            var cmp = EqualityComparer<TV>.Default;
            foreach (var (k, v) in now)
                if (!prev.TryGetValue(k, out var old)) b.ValueChange(l, KeyText(l, k), null, Text(v), t);
                else if (!cmp.Equals(old, v)) b.ValueChange(l, KeyText(l, k), Text(old), Text(v), t);
            foreach (var (k, old) in prev)
                if (!now.ContainsKey(k)) b.ValueChange(l, KeyText(l, k), Text(old), null, t);
        }
    }

    /// <summary>
    /// The typed differ for a dictionary type, or null when it isn't an IEnumerable&lt;KeyValuePair&lt;K, V&gt;&gt; or the
    /// differ can't be built for its types (then: object maps). A build failure is named in the layer's status
    /// (<c>dictDiff</c>) and logged once per dictionary type, not swallowed.
    /// </summary>
    private DictDiff? DictDiffFor(LayerRun l, Type type)
    {
        if (type == l.DictType) return l.Dict;
        l.DictType = type;
        l.Dict = null;
        l.DictNote = null;
        var kvp = type.GetInterfaces().Prepend(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            && i.GetGenericArguments()[0] is { IsGenericType: true } a && a.GetGenericTypeDefinition() == typeof(KeyValuePair<,>));
        if (kvp?.GetGenericArguments()[0].GetGenericArguments() is [var k, var v])
            try { l.Dict = (DictDiff)Activator.CreateInstance(typeof(DictDiff<,>).MakeGenericType(k, v))!; }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                l.DictNote = $"typed differ DictDiff<{k.Name}, {v.Name}> for {ExpressionWalker.FormatTypeName(type)} could not be built "
                             + $"({inner.GetType().Name}: {inner.Message}); diffing through object maps (boxes every entry each tick)";
                LogFault($"[Observe] layer {l.Spec.Id}: {l.DictNote}");
            }
        return l.Dict;
    }

    private static string KeyText(LayerRun l, object key)
    {
        if (!l.KeyNames.TryGetValue(key, out var s)) l.KeyNames[key] = s = Text(key);
        return s;
    }

    private void ValueChange(LayerRun l, string unit, string? old, string? @new, double t)
    {
        if (!Count(l, unit, null, @new ?? "(removed)", t, unit)) return;
        var e = Event(l, unit, null, old, @new);
        if (old != null && @new != null && double.TryParse(old, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(@new, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) e["delta"] = b - a;
        if (old == null) e["change"] = "added"; else if (@new == null) e["change"] = "removed";
        Emit(l, e);
    }

    // ── each ─────────────────────────────────────────────────────────

    // The per-tick reads are in ObserveEachRaw.cs (raw steps compiled once, reflection as the fallback).

    /// <summary>A dotted property path below obj (null when a link is null now). A segment that doesn't exist is broken.</summary>
    private static object? SubPath(LayerRun l, object obj, string path)
    {
        object? cur = obj;
        foreach (var seg in path.Split('.'))
        {
            if (cur == null) return null;
            var prop = MemberIndex.Find(cur.GetType(), seg, kinds: MemberIndex.Kinds.Property);
            if (prop is not { CanRead: true }) throw new MissingMemberException($"{cur.GetType().Name} has no public property '{seg}' (in '{path}')");
            cur = prop.Get(cur);
        }
        return cur;
    }

    // ── list ─────────────────────────────────────────────────────────

    // Items are keyed by the key's value itself (a boxed long for the default Address), formatted only for an event, and
    // an item's preview (its naming getter: RenderName reads a string from memory) is read once, when the item appears:
    // the per-item key text and preview read every tick were most of a 5000-item list's cost. The key member is looked
    // up once per item type. Two maps per layer are swapped, not reallocated.
    private void ListTick(LayerRun l, IEnumerable items, double t)
    {
        var keyProp = l.Spec.Key ?? "Address";
        var prev = l.ListA;
        var now = l.ListB ??= new Dictionary<object, string>();
        now.Clear();
        var n = 0;
        foreach (var item in items)
        {
            if (item == null || ++n > 5000) continue;
            var type = item.GetType();
            if (type != l.ListType) { l.ListType = type; l.ListKey = MemberIndex.Find(type, keyProp, kinds: MemberIndex.Kinds.Property); }
            var id = ListId(l.ListKey?.Get(item) ?? item);
            // A preview that fell back to the type name (nothing named it yet, e.g. an entity still loading) is read again.
            now[id] = prev != null && prev.TryGetValue(id, out var p) && p != type.Name ? p : Preview(item);
        }
        (l.ListA, l.ListB) = (now, prev);
        if (prev == null) return;
        foreach (var (id, preview) in now)
            if (!prev.ContainsKey(id) && Count(l, Text(id), null, preview, t, $"added {preview}"))
            { var e = Event(l, Text(id), null, null, preview); e["change"] = "added"; Emit(l, e); }
        foreach (var (id, preview) in prev)
            if (!now.ContainsKey(id) && Count(l, Text(id), null, "(removed)", t, $"removed {preview}"))
            { var e = Event(l, Text(id), null, preview, null); e["change"] = "removed"; Emit(l, e); }
    }

    /// <summary>
    /// An item's identity: a string, number, enum or other value type as itself (compared by value, formatted only for an
    /// event); any other object (no key member, or a key that is a wrapper object) by its text, as before.
    /// </summary>
    private static object ListId(object v) => v is string || v.GetType().IsValueType ? v : Text(v);

    private static string Preview(object item)
    {
        foreach (var m in MemoryObjects.NamingMembers(item.GetType()))
            try { if (m.Get(item) is string s && s.Length > 0) return s.Length > 80 ? s[..80] : s; }
            catch { }   // a getter that throws (a disposed entity's RenderName) names nothing; the next one may
        return item.GetType().Name;
    }

    // ── shared ───────────────────────────────────────────────────────

    /// <summary>Counts the change in the layer's map; false when its noise group is (or just became) noisy: don't log it.</summary>
    private bool Count(LayerRun l, string unit, string? name, string last, double t, string noiseGroup)
    {
        var u = l.Map.TryGetValue(unit, out var uu) ? uu : l.Map[unit] = new LayerUnit { FirstT = t, Name = name };
        u.Changes++; u.LastT = t; u.Last = last;
        if (l.Noisy.TryGetValue(noiseGroup, out var nn)) { l.Noisy[noiseGroup] = nn + 1; return false; }
        var r = l.Rate.TryGetValue(noiseGroup, out var rr) && t - rr.windowStart < 10_000 ? (rr.count + 1, rr.windowStart) : (1, t);
        l.Rate[noiseGroup] = r;
        if (r.Item1 <= LayerNoisyPer10s) return true;
        l.Noisy[noiseGroup] = 0;
        ObsEmit(new JObject
        {
            ["kind"] = "layer.noisy", ["layer"] = l.Spec.Id, ["mode"] = l.Spec.Mode, ["group"] = noiseGroup, ["name"] = name,
            ["note"] = $"changed {r.Item1} times in 10 s: counted in observe.layer_map, not logged",
        });
        return false;
    }

    private static JObject Event(LayerRun l, string unit, string? name, string? old, string? @new) => new()
    {
        ["kind"] = "layer", ["layer"] = l.Spec.Id, ["mode"] = l.Spec.Mode, ["unit"] = unit, ["name"] = name, ["old"] = old, ["new"] = @new,
    };

    private void Emit(LayerRun l, JObject e) { l.Events++; ObsEmit(e); }

    private static bool IsScalar(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t.Name is "Vector2" or "Vector3" or "Vector2i" or "Vector4";
    }

    private static string Text(object? v) => v switch
    {
        null => "null",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>observe.layer_map: every unit of a layer that changed, with counts and timing. The mapping worklist.</summary>
    private JObject LayerMap(JToken? p)
    {
        LayersLoad();
        var id = p?["layer"]?.ToString() ?? "server";
        var l = _layers.FirstOrDefault(x => x.Spec.Id == id);
        if (l == null) return Err("not_found", $"No layer '{id}'. Layers: {string.Join(", ", _layers.Select(x => x.Spec.Id))}");
        var unmappedOnly = p?["unmappedOnly"]?.Value<bool>() == true;
        var minChanges = p?["minChanges"]?.Value<long>() ?? 1;
        var limit = Math.Clamp(p?["limit"]?.Value<int>() ?? 100, 1, 1000);
        var now = ObsClock.Elapsed.TotalMilliseconds;
        var rows = l.Map.ToArray().Where(kv => kv.Value.Changes >= minChanges && (!unmappedOnly || (l.Spec.Mode == "struct" && kv.Value.Name == null)));
        rows = (p?["sort"]?.ToString() ?? "changes") switch
        {
            "recent" => rows.OrderByDescending(kv => kv.Value.LastT),
            "unit" => rows.OrderBy(kv => kv.Key.StartsWith("0x") ? Convert.ToInt64(kv.Key[2..], 16) : 0).ThenBy(kv => kv.Key, StringComparer.Ordinal),
            _ => rows.OrderByDescending(kv => kv.Value.Changes),
        };
        return new JObject
        {
            ["ok"] = true, ["t"] = Math.Round(now, 1), ["layer"] = LayerStatus(l),
            ["units"] = new JArray(rows.Take(limit).Select(kv => new JObject
            {
                ["unit"] = kv.Key, ["name"] = kv.Value.Name, ["changes"] = kv.Value.Changes,
                ["perMinute"] = Math.Round(kv.Value.Changes * 60_000.0 / Math.Max(1000, now - kv.Value.FirstT), 1),
                ["firstT"] = Math.Round(kv.Value.FirstT, 1), ["lastT"] = Math.Round(kv.Value.LastT, 1), ["last"] = kv.Value.Last,
                ["logged"] = !l.Noisy.ContainsKey(l.Spec.Mode == "struct" ? $"block 0x{Convert.ToInt64(kv.Key[2..], 16) & ~0xFF:X}" : kv.Key),
            })),
        };
    }
}
