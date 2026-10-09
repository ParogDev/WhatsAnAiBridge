using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// The observer's "server" layer: the client's copy of what the server sent (ServerData: inventories, stash tabs,
/// quest flags, party, league mechanics... ~10 KB on PoE2, of which the HUD maps a fraction) read raw at 10 Hz and
/// diffed. Each changed byte range becomes an event on the observer's clock (t, frame) with old and new bytes and the
/// HUD's name for that offset when it has one (a property, else the offsets struct's field), so it can be lined up
/// with the other layers: a panel that opened, an area change, a stat that moved. Unmapped ranges that change are the
/// map of what we don't know yet; changes with no UI or user cause next to them are what the server pushed.
///
/// Read-only, leaf backend (no page cache, ~10 KB per tick). Offsets that change on most ticks (timers, counters) are
/// reported once as "noisy" and then only counted (observe.status). The baseline at start and after each area change
/// is not an event.
/// </summary>
public partial class WhatsAnAiBridge
{
    private sealed class ServerWatch
    {
        public string Label = "", Path = "";
        public long Address;
        public int Size;
        public byte[] Prev = [], Now = [];
        public bool HasPrev;
        public List<(int off, int len, string name, bool property)> Names = [];
        public readonly Dictionary<int, (int count, double windowStart)> Rate = new();
        public readonly Dictionary<int, int> Noisy = new();   // 256-byte block -> changes counted since it went noisy
        public readonly Dictionary<int, SrvOffset> Map = new();   // every changed offset, for mapping
        public long Events;
        public string? Broken;
    }

    private readonly List<ServerWatch> _srvWatches = [];
    private DateTime _srvLastTick = DateTime.MinValue, _srvLastResolve = DateTime.MinValue;
    private string? _srvArea;
    private const double SrvTickMs = 100, SrvNoisyPer10s = 20;
    private const int SrvMaxBytes = 64 * 1024, SrvShowBytes = 32;

    /// <summary>Default watch list. More targets: observe.start {server: [paths]} (any walker path with a cached struct).</summary>
    private static readonly string[] SrvDefaultPaths = ["GameController.IngameState.ServerData"];

    private void ObserveServer(DateTime now)
    {
        if ((now - _srvLastTick).TotalMilliseconds < SrvTickMs) return;
        _srvLastTick = now;
        if (_srvWatches.Count == 0) foreach (var p in SrvDefaultPaths) _srvWatches.Add(new ServerWatch { Path = p, Label = p.Split('.').Last() });

        // Re-resolve addresses every 2 s and on area change (the struct can move); a moved struct starts a new baseline.
        var area = GameController.Area?.CurrentArea?.Name;
        var resolve = area != _srvArea || (now - _srvLastResolve).TotalSeconds >= 2;
        if (resolve) { _srvLastResolve = now; if (area != _srvArea) foreach (var w in _srvWatches) w.HasPrev = false; _srvArea = area; }

        if (RawReader() is not { } read) return;
        foreach (var w in _srvWatches)
        {
            if (resolve && !SrvResolve(w)) continue;
            if (w.Address == 0 || w.Size == 0) continue;
            if (w.Now.Length != w.Size) { w.Now = new byte[w.Size]; w.Prev = new byte[w.Size]; w.HasPrev = false; }
            if (!read(new IntPtr(w.Address), w.Now)) continue;
            if (w.HasPrev) SrvDiff(w);
            (w.Prev, w.Now) = (w.Now, w.Prev);
            w.HasPrev = true;
        }
    }

    /// <summary>Address, size and offset names of a watch target; false (with Broken named) when it can't be read now.</summary>
    private bool SrvResolve(ServerWatch w)
    {
        var obj = new ExpressionWalker(GameController).Resolve(w.Path, out var error);
        if (error != null) { w.Broken = $"{w.Path}: {error}"; return false; }   // a walker break: report, don't spam
        if (obj == null) { w.Address = 0; return false; }                        // not now (loading screen)
        var address = obj.GetType().GetProperty("Address")?.GetValue(obj) as long? ?? 0;
        if (address == 0) { w.Address = 0; return false; }
        if (address == w.Address && w.Size > 0) return true;

        var (structValue, _) = FindOffsetsStruct(obj);
        if (structValue == null) { w.Broken = $"{obj.GetType().Name} at {w.Path} holds no cached offsets struct: size unknown"; return false; }
        int size;
        try { size = System.Runtime.InteropServices.Marshal.SizeOf(structValue.GetType()); }
        catch (Exception ex) { w.Broken = $"{structValue.GetType().Name}: {ex.Message}"; return false; }
        w.Address = address;
        w.Size = Math.Min(size, SrvMaxBytes);
        w.HasPrev = false;
        w.Broken = null;
        w.Names = SrvNames(w.Path);
        return true;
    }

    /// <summary>Offset -> name from the runtime layout: the HUD's property where one maps to the field, else the field.</summary>
    private List<(int off, int len, string name, bool property)> SrvNames(string path)
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

    private void SrvDiff(ServerWatch w)
    {
        var a = w.Prev; var b = w.Now;
        var t = ObsClock.Elapsed.TotalMilliseconds;
        for (var i = 0; i < w.Size;)
        {
            if (a[i] == b[i]) { i++; continue; }
            // A changed range: extend over equal gaps of up to 3 bytes (a field rarely changes in every byte).
            var start = i; var end = i;
            for (var j = i + 1; j < w.Size && j <= end + 4; j++) if (a[j] != b[j]) end = j;
            i = end + 1;
            SrvRange(w, start, end - start + 1, t);
        }
    }

    private void SrvRange(ServerWatch w, int off, int len, double t)
    {
        // Every change goes to the per-offset map (observe.server_map), whatever the journal does with it.
        var m = w.Map.TryGetValue(off, out var mm) ? mm : w.Map[off] = new SrvOffset { FirstT = t, Len = len };
        m.Changes++; m.LastT = t; m.Len = Math.Max(m.Len, len);
        m.Last = Convert.ToHexString(w.Now, off, Math.Min(len, SrvShowBytes));

        // Noise, per 256-byte block (an array of positions changes all over its block): more than SrvNoisyPer10s
        // changes in 10 s -> say so once, then only count.
        var block = off >> 8;
        if (w.Noisy.TryGetValue(block, out var n)) { w.Noisy[block] = n + 1; return; }
        var r = w.Rate.TryGetValue(block, out var rr) && t - rr.windowStart < 10_000 ? (rr.count + 1, rr.windowStart) : (1, t);
        w.Rate[block] = r;
        if (r.Item1 > SrvNoisyPer10s)
        {
            w.Noisy[block] = 0;
            ObsEmit(new JObject
            {
                ["kind"] = "server.noisy", ["target"] = w.Label, ["off"] = $"0x{block << 8:X}", ["len"] = 256, ["name"] = SrvName(w, block << 8, 256),
                ["note"] = $"this 256-byte block changed {r.Item1} times in 10 s: counted per offset in observe.server_map, not logged",
            });
            return;
        }

        var show = Math.Min(len, SrvShowBytes);
        var e = new JObject
        {
            ["kind"] = "server", ["target"] = w.Label, ["off"] = $"0x{off:X}", ["len"] = len,
            ["name"] = SrvName(w, off, len),
            ["old"] = Convert.ToHexString(w.Prev, off, show), ["new"] = Convert.ToHexString(w.Now, off, show),
        };
        // Integer readings of an aligned 1-8 byte change: what most server-sent scalars are.
        if (len <= 8)
        {
            var al = off & ~3; var span = Math.Min(8, w.Size - al);
            if (off + len <= al + 4 && al + 4 <= w.Size) e["i32"] = $"{BitConverter.ToInt32(w.Prev, al)} -> {BitConverter.ToInt32(w.Now, al)}";
            else if (span == 8 && off + len <= al + 8) e["i64"] = $"{BitConverter.ToInt64(w.Prev, al)} -> {BitConverter.ToInt64(w.Now, al)}";
        }
        w.Events++;
        ObsEmit(e);
    }

    /// <summary>The HUD's name for the bytes at off (a field containing them), or null when unmapped.</summary>
    private static string? SrvName(ServerWatch w, int off, int len)
    {
        // Among the fields containing the first changed byte: a HUD property beats a bare field, then the smallest.
        string? best = null; var bestLen = int.MaxValue; var bestProp = false;
        foreach (var (o, l, name, prop) in w.Names)
        {
            if (o > off) break;
            if (off >= o + l) continue;
            if (best == null || (prop && !bestProp) || (prop == bestProp && l < bestLen)) { best = name; bestLen = l; bestProp = prop; }
        }
        return best;
    }

    /// <summary>For observe.status: what the server layer watches and has seen.</summary>
    private JArray ServerLayerStatus() => new(_srvWatches.Select(w => new JObject
    {
        ["target"] = w.Label, ["path"] = w.Path, ["bytes"] = w.Size, ["namedRanges"] = w.Names.Count, ["events"] = w.Events,
        ["noisyBlocks"] = new JObject(w.Noisy.OrderByDescending(kv => kv.Value).Take(20).Select(kv => new JProperty($"0x{kv.Key << 8:X}", new JObject { ["name"] = SrvName(w, kv.Key << 8, 256), ["changesSinceNoisy"] = kv.Value }))),
        ["offsetsChanged"] = w.Map.Count,
        ["broken"] = w.Broken,
    }));
}

public partial class WhatsAnAiBridge
{
    /// <summary>observe.start {server: [paths]} replaces the server layer's targets (walker paths to objects with a cached struct).</summary>
    private void SrvConfigure(JArray? paths)
    {
        if (paths == null) return;
        _srvWatches.Clear();
        foreach (var p in paths.Select(x => x.ToString()).Where(x => x.Length > 0).Distinct().Take(16))
            _srvWatches.Add(new ServerWatch { Path = p, Label = p.Split('.').Last() });
        _srvLastResolve = DateTime.MinValue;
    }
}

public partial class WhatsAnAiBridge
{
    private sealed class SrvOffset { public int Len; public long Changes; public double FirstT, LastT; public string Last = ""; }

    /// <summary>
    /// observe.server_map {target?, unmappedOnly?, minChanges?, sort?: changes|recent|offset, limit?}: every offset of a
    /// watched struct that changed since observing started, with its HUD name when it has one, how often and when (t on
    /// the observer clock) it changed, and its last bytes. The mapping worklist: unmapped offsets that change rarely are
    /// events worth naming (line them up with the journal around their t); ones that change constantly are live values.
    /// </summary>
    private JObject ServerMap(JToken? p)
    {
        var target = p?["target"]?.ToString();
        var unmappedOnly = p?["unmappedOnly"]?.Value<bool>() == true;
        var minChanges = p?["minChanges"]?.Value<long>() ?? 1;
        var sort = p?["sort"]?.ToString() ?? "changes";
        var limit = Math.Clamp(p?["limit"]?.Value<int>() ?? 100, 1, 1000);
        var now = ObsClock.Elapsed.TotalMilliseconds;
        var o = new JObject { ["ok"] = true, ["t"] = Math.Round(now, 1) };
        var arr = new JArray();
        foreach (var w in _srvWatches.Where(w => target == null || w.Label == target || w.Path == target))
        {
            var rows = w.Map.ToArray()
                .Select(kv => (off: kv.Key, s: kv.Value, name: SrvName(w, kv.Key, kv.Value.Len)))
                .Where(x => x.s.Changes >= minChanges && (!unmappedOnly || x.name == null));
            rows = sort switch
            {
                "recent" => rows.OrderByDescending(x => x.s.LastT),
                "offset" => rows.OrderBy(x => x.off),
                _ => rows.OrderByDescending(x => x.s.Changes),
            };
            arr.Add(new JObject
            {
                ["target"] = w.Label, ["bytes"] = w.Size, ["offsetsChanged"] = w.Map.Count,
                ["offsets"] = new JArray(rows.Take(limit).Select(x => new JObject
                {
                    ["off"] = $"0x{x.off:X}", ["len"] = x.s.Len, ["name"] = x.name, ["changes"] = x.s.Changes,
                    ["perMinute"] = Math.Round(x.s.Changes * 60_000.0 / Math.Max(1000, now - x.s.FirstT), 1),
                    ["firstT"] = Math.Round(x.s.FirstT, 1), ["lastT"] = Math.Round(x.s.LastT, 1), ["last"] = x.s.Last,
                    ["logged"] = !w.Noisy.ContainsKey(x.off >> 8),
                })),
            });
        }
        o["targets"] = arr;
        return o;
    }
}
