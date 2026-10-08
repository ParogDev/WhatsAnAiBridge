using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Read-only access to the game's data files (Data/*.dat) loaded in memory, for naming ids found in memory without
/// help: e.g. Data/StashTabAffinityId.dat names every stash affinity bit. Rows come back raw (hex) with every 8-byte
/// slot that points at UTF-16 text decoded, so string-keyed tables read naturally without a schema.
///   data.files {filter?, limit?}                       file names (AllFiles keys) containing filter
///   data.read  {file, offset?, limit?, find?}           rows: index, hex, strings (+slot:text), ints (+slot:int32)
///   data.find_value {values, size?, filter?, minHits?}  starts a background scan: which table column holds these values?
///   data.find_result {id}                               that scan's status / ranked columns
/// Uses the HUD's own FileInMemory record bounds (same on ExileCore and ExileCore2).
/// </summary>
public partial class WhatsAnAiBridge
{
    private sealed class RawDatFile : FileInMemory
    {
        public RawDatFile(IMemory m, Func<long> address) : base(m, address) { }
        public long First => FirstRecord;
        public long Length => RecordLength;
        public int Count => NumberOfRecords;
    }

    private string? ProcessDataMethod(string method, JToken? p) => method switch
    {
        "data.files" => SafeMemory(() => DataFiles(p)),
        "data.read" => SafeMemory(() => DataRead(p)),
        "data.find_value" => SafeMemory(() => DataFindValue(p)),
        "data.find_result" => SafeMemory(() =>
            _dataJobs.TryGetValue(p?["id"]?.ToString() ?? "", out var job) ? job : Err("unknown_id", "No such scan (results are kept for the last 20 scans).")),
        _ => null,
    };

    // ── data.find_value ──────────────────────────────────────────────
    // Ids found in memory are often a column of some table (a row's hash, key or index). Scanning every loaded table
    // for a handful of them names the column: the one that holds most of the values wins. Full scans take seconds, so
    // they run on a worker (memory reads are thread-safe); the table list is taken on the main thread.

    private static readonly ConcurrentDictionary<string, JObject> _dataJobs = new();
    private static readonly ConcurrentQueue<string> _dataJobOrder = new();

    private JObject DataFindValue(JToken? p)
    {
        var values = new HashSet<ulong>();
        foreach (var v in p?["values"] as JArray ?? (p?["value"] != null ? new JArray(p["value"]!) : new JArray()))
            if (ParseValue(v) is { } u) values.Add(u);
        if (values.Count == 0) return Err("missing_values", "Pass values: numbers or \"0x...\" strings, e.g. one id per stash page.");
        int size = p?["size"]?.Value<int>() ?? 0;
        if (size == 0) { var max = values.Max(); size = max <= 0xFF ? 1 : max <= 0xFFFF ? 2 : max <= 0xFFFF_FFFF ? 4 : 8; }
        if (size is not (1 or 2 or 4 or 8)) return Err("bad_size", "size is 1, 2, 4 or 8 bytes (default: the smallest that holds the largest value).");
        var minHits = Math.Clamp(p?["minHits"]?.Value<int>() ?? Math.Min(3, values.Count), 1, values.Count);
        var filter = p?["filter"]?.ToString();

        var m = GameController.Memory;
        var tables = new List<(string file, long first, int count, int len)>();
        foreach (var (name, info) in GameController.Files.AllFiles)
        {
            if (!name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var d = new RawDatFile(m, () => info.Ptr);
                int c = d.Count, l = (int)d.Length;
                if (c > 0 && l > 0 && (long)c * l <= 64_000_000) tables.Add((name, d.First, c, l));
            }
            catch { }
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        var job = new JObject { ["id"] = id, ["status"] = "running", ["values"] = values.Count, ["size"] = size, ["minHits"] = minHits, ["tables"] = tables.Count };
        _dataJobs[id] = job;
        _dataJobOrder.Enqueue(id);
        while (_dataJobOrder.Count > 20 && _dataJobOrder.TryDequeue(out var old)) _dataJobs.TryRemove(old, out _);
        Task.Run(() =>
        {
            try { _dataJobs[id] = ScanTables(m, tables, values, size, minHits, id); }
            catch (Exception ex) { _dataJobs[id] = new JObject { ["id"] = id, ["status"] = "failed", ["error"] = "scan_failed", ["message"] = ex.Message }; }
        });
        return job;
    }

    /// <summary>A number, or a "0x..." / decimal string; negative int32s are taken as their unsigned 32-bit pattern.</summary>
    private static ulong? ParseValue(JToken v)
    {
        var s = v.ToString().Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out var h) ? h : null;
        if (ulong.TryParse(s, out var u)) return u;
        if (long.TryParse(s, out var l)) return l >= int.MinValue ? (uint)(int)l : (ulong)l;
        return null;
    }

    private JObject ScanTables(IMemory m, List<(string file, long first, int count, int len)> tables, HashSet<ulong> values, int size, int minHits, string id)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // (table, column) -> value -> first row holding it, and how many rows matched at all
        var columns = new List<(string file, int count, int len, long first, int offset, Dictionary<ulong, int> found, int rows)>();
        foreach (var (file, first, count, len) in tables)
        {
            var found = new Dictionary<int, Dictionary<ulong, int>>();
            var rowsMatched = new Dictionary<int, int>();
            const int chunkRows = 8192;
            for (int r0 = 0; r0 < count; r0 += chunkRows)
            {
                int n = Math.Min(chunkRows, count - r0);
                var bytes = m.ReadBytes(first + (long)r0 * len, n * len);
                if (bytes == null || bytes.Length < n * len) break;
                for (int r = 0; r < n; r++)
                for (int o = 0; o + size <= len; o++)
                {
                    int at = r * len + o;
                    ulong v = size switch { 1 => bytes[at], 2 => BitConverter.ToUInt16(bytes, at), 4 => BitConverter.ToUInt32(bytes, at), _ => BitConverter.ToUInt64(bytes, at) };
                    if (!values.Contains(v)) continue;
                    if (!found.TryGetValue(o, out var map)) found[o] = map = new Dictionary<ulong, int>();
                    map.TryAdd(v, r0 + r);
                    rowsMatched[o] = rowsMatched.GetValueOrDefault(o) + 1;
                }
            }
            foreach (var (o, map) in found)
                if (map.Count >= minHits) columns.Add((file, count, len, first, o, map, rowsMatched[o]));
        }

        // Most values found first; among equals, columns where the values are rare (keys, hashes) beat constant-ish ones.
        var ranked = columns.OrderByDescending(c => c.found.Count).ThenBy(c => (double)c.rows / c.found.Count).Take(15).ToList();
        var result = new JArray();
        foreach (var c in ranked)
        {
            var matches = new JArray();
            foreach (var (v, row) in c.found.OrderBy(kv => kv.Value).Take(30))
            {
                var label = RowLabel(m, c.first + (long)row * c.len);
                matches.Add(new JObject { ["value"] = v, ["row"] = row, ["label"] = label });
            }
            result.Add(new JObject
            {
                ["file"] = c.file, ["offset"] = c.offset, ["size"] = size, ["found"] = c.found.Count, ["rowsMatched"] = c.rows,
                ["rowCount"] = c.count, ["recordLength"] = c.len, ["matches"] = matches,
            });
        }
        return new JObject
        {
            ["id"] = id, ["status"] = "done", ["values"] = values.Count, ["size"] = size, ["minHits"] = minHits, ["tables"] = tables.Count,
            ["ms"] = sw.ElapsedMilliseconds, ["columns"] = result,
            ["missing"] = ranked.Count > 0 ? new JArray(values.Where(v => !ranked[0].found.ContainsKey(v)).Take(30)) : new JArray(values.Take(30)),
            ["note"] = "Columns are scanned at every byte offset, so small values (1-2 bytes) give chance hits: trust a column when it holds " +
                       "(nearly) all values and rowsMatched is close to found. A value packed with flags must be unpacked first (e.g. q >> 5).",
        };
    }

    /// <summary>The row's first text field, if any (same idea as RowAt's label).</summary>
    private static string RowLabel(IMemory m, long rowAddress)
    {
        try
        {
            var first = m.Read<long>(rowAddress);
            if (first > 0x10000 && first < 0x7FFF_FFFF_FFFF && m.ReadStringU(first, 96) is { } t && LooksLikeText(t)) return t;
        }
        catch { }
        return "";
    }

    private JObject DataFiles(JToken? p)
    {
        var filter = p?["filter"]?.ToString() ?? ".dat";
        var limit = Math.Clamp(p?["limit"]?.Value<int>() ?? 100, 1, 2000);
        var all = GameController.Files.AllFiles;
        var names = all.Keys.Where(k => k.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        return new JObject { ["filter"] = filter, ["total"] = names.Count, ["files"] = new JArray(names.Take(limit)) };
    }

    private JObject DataRead(JToken? p)
    {
        var file = p?["file"]?.ToString();
        if (string.IsNullOrWhiteSpace(file)) return Err("missing_file", "Pass file, e.g. Data/StashTabAffinityId.dat (data.files lists them).");
        var all = GameController.Files.AllFiles;
        if (!all.TryGetValue(file, out var info))
        {
            var guess = all.Keys.FirstOrDefault(k => k.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase) || k.Equals("Data/" + file, StringComparison.OrdinalIgnoreCase));
            if (guess == null) return Err("unknown_file", $"No loaded file '{file}' (data.files filter=... lists names).");
            file = guess;
            info = all[guess];
        }
        var m = GameController.Memory;
        var dat = new RawDatFile(m, () => info.Ptr);
        int count = dat.Count, length = (int)dat.Length;
        if (count <= 0 || length <= 0) return new JObject { ["file"] = file, ["count"] = count, ["recordLength"] = length, ["rows"] = new JArray(), ["note"] = "Empty or not a fixed-record table." };
        var offset = Math.Clamp(p?["offset"]?.Value<int>() ?? 0, 0, count);
        var limit = Math.Clamp(p?["limit"]?.Value<int>() ?? 50, 1, 500);
        var find = p?["find"]?.ToString();
        var rows = new JArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = offset; i < count && rows.Count < limit && sw.ElapsedMilliseconds < 120; i++)
        {
            var bytes = m.ReadBytes(dat.First + (long)i * length, Math.Min(length, 512));
            if (bytes == null || bytes.Length == 0) continue;
            var strings = new List<string>();
            var refs = new List<string>();
            for (int o = 0; o + 8 <= bytes.Length; o += 8)
            {
                long ptr = BitConverter.ToInt64(bytes, o);
                if (ptr <= 0x10000 || ptr >= 0x7FFF_FFFF_FFFF) continue;
                // A pointer into another table's records is a foreign key: name the table and row.
                if (RowAt(ptr) is { } fk) { refs.Add($"+{o}:{fk}"); continue; }
                string? s;
                try { s = m.ReadStringU(ptr, 256); } catch { continue; }
                if (LooksLikeText(s)) strings.Add($"+{o}:{s}");
            }
            if (find != null && !strings.Any(s => s.Contains(find, StringComparison.OrdinalIgnoreCase))) continue;
            var ints = new List<string>();
            for (int o = 0; o + 4 <= Math.Min(bytes.Length, 64); o += 4) ints.Add($"+{o}:{BitConverter.ToInt32(bytes, o)}");
            rows.Add(new JObject
            {
                ["index"] = i, ["strings"] = new JArray(strings), ["refs"] = new JArray(refs), ["ints"] = string.Join(" ", ints),
                ["hex"] = BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 96)).Replace("-", " ") + (bytes.Length > 96 ? " ..." : ""),
            });
        }
        var o2 = new JObject { ["file"] = file, ["count"] = count, ["recordLength"] = length, ["offset"] = offset, ["rows"] = rows,
            ["note"] = "refs: 8-byte slots pointing at a row of another loaded table (foreign keys), as File[row] \"first text of that row\"" };
        if (find != null) o2["find"] = find;
        if (sw.ElapsedMilliseconds >= 120) o2["truncated"] = "time budget reached; continue with offset";
        return o2;
    }

    /// <summary>Mostly-ASCII text (game ids, English names, paths); pointers misread as UTF-16 come out as CJK noise.</summary>
    private static bool LooksLikeText(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 2) return false;
        if (s.Any(c => c < 32 || char.IsSurrogate(c) || c >= 0xFFFE)) return false;
        return s.Count(c => c < 128) * 10 >= s.Length * 8;
    }

    // Record ranges of every loaded .dat table, to resolve foreign keys. Rebuilt at most once a minute (tables reload
    // on patch / area changes rarely; a stale entry only costs a missing ref name).
    private static (DateTime at, List<(long start, long end, long len, string file, RawDatFile dat)> ranges)? _datRanges;

    private string? RowAt(long ptr)
    {
        var cache = _datRanges;
        if (cache == null || (DateTime.UtcNow - cache.Value.at).TotalSeconds > 60)
        {
            var list = new List<(long, long, long, string, RawDatFile)>();
            var m = GameController.Memory;
            foreach (var (name, info) in GameController.Files.AllFiles)
            {
                if (!name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var d = new RawDatFile(m, () => info.Ptr);
                    int c = d.Count; long l = d.Length;
                    if (c > 0 && l > 0) list.Add((d.First, d.First + c * l, l, name, d));
                }
                catch { }
            }
            cache = (DateTime.UtcNow, list);
            _datRanges = cache;
        }
        foreach (var (start, end, len, file, _) in cache.Value.ranges)
        {
            if (ptr < start || ptr >= end || (ptr - start) % len != 0) continue;
            var row = (ptr - start) / len;
            // Label with the row's first text field when it has one.
            string label = "";
            try
            {
                var first = GameController.Memory.Read<long>(ptr);
                if (first > 0x10000 && first < 0x7FFF_FFFF_FFFF && GameController.Memory.ReadStringU(first, 64) is { } t && LooksLikeText(t)) label = $" \"{t}\"";
            }
            catch { }
            return $"{file.Replace("Data/", "")}[{row}]{label}";
        }
        return null;
    }
}
