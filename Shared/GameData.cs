using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Read-only access to the game's data files (Data/*.dat) loaded in memory, for naming ids found in memory without
/// help: e.g. Data/StashTabAffinityId.dat names every stash affinity bit. Rows come back raw (hex) with every 8-byte
/// slot that points at UTF-16 text decoded, so string-keyed tables read naturally without a schema.
///   data.files {filter?, limit?}                       file names (AllFiles keys) containing filter
///   data.read  {file, offset?, limit?, find?}           rows: index, hex, strings (+slot:text), ints (+slot:int32)
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
        _ => null,
    };

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
