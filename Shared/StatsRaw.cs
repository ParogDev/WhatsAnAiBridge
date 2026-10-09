using System;
using System.Collections.Generic;

namespace WhatsAnAiBridge;

/// <summary>
/// Cheap "did any player stat change?" check. Stats.StatDictionary costs ~60-150 us and ~40 KB on its first read per
/// frame (317 stats on PoE2), and the stats snapshot refreshes at 4 Hz even while nothing changes. The game keeps the
/// stats as (GameStat id, int value) pairs in a few vectors reached from the Stats component; reading those raw bytes
/// (a few KB through the leaf backend, no page cache) and comparing them with the last read tells us when the HUD's
/// dictionary is worth reading.
///
/// No per-game offsets: the vectors are found once per Stats component by scanning its pointers for (begin, end) pairs
/// whose items match the HUD's own dictionary. Every <see cref="StatsRawFullEvery"/> the dictionary is read anyway and
/// the raw pairs are checked against it; a mismatch turns the raw check off and records which link broke
/// (<see cref="StatsRawStatus"/>), so stats are never more than that stale.
/// </summary>
public partial class WhatsAnAiBridge
{
    private static readonly TimeSpan StatsRawFullEvery = TimeSpan.FromSeconds(2);
    private const int StatsRawMaxBytes = 64 * 1024;

    private long _statsRawFor;                               // Stats component address the vectors were found for
    private readonly List<(int ptrOff, int vecOff)> _statsRawVecs = [];
    private byte[] _statsRawBuf = new byte[8192], _statsRawPrev = [];
    private int _statsRawPrevLen = -1;
    private DateTime _statsRawFullAt = DateTime.MinValue;
    private string? _statsRawBroken;
    private int _statsRawSkips, _statsRawReads;

    /// <summary>For bridge.self_perf: whether the raw check is on, and how often it saved the dictionary read.</summary>
    private object StatsRawStatus() => new
    {
        on = _statsRawBroken == null && _statsRawVecs.Count > 0,
        vectors = _statsRawVecs.Count,
        broken = _statsRawBroken,
        skippedReads = _statsRawSkips,
        dictionaryReads = _statsRawReads,
    };

    /// <summary>True when the raw stat bytes equal the last read's, so the HUD's dictionary need not be read now.</summary>
    private bool StatsUnchangedRaw(long statsAddress, DateTime now)
    {
        if (_statsRawBroken != null || statsAddress == 0) return false;
        if (now - _statsRawFullAt >= StatsRawFullEvery) return false;   // periodic full read (and validation)
        if (_statsRawFor != statsAddress || _statsRawVecs.Count == 0) return false;
        var len = ReadStatsRaw(statsAddress);
        if (len < 0 || len != _statsRawPrevLen || !_statsRawBuf.AsSpan(0, len).SequenceEqual(_statsRawPrev.AsSpan(0, len))) return false;
        _statsRawSkips++;
        return true;
    }

    /// <summary>After a dictionary read: (re)find the vectors if needed, validate them against it, remember the bytes.</summary>
    private void StatsRawAfterRead(long statsAddress, IReadOnlyDictionary<GameStat, int>? dict, DateTime now)
    {
        _statsRawReads++;
        _statsRawFullAt = now;
        if (_statsRawBroken != null || statsAddress == 0 || dict == null || dict.Count == 0) return;
        try
        {
            if (_statsRawFor != statsAddress) { FindStatsVectors(statsAddress, dict); _statsRawFor = statsAddress; }
            if (_statsRawVecs.Count == 0) { _statsRawPrevLen = -1; return; }
            var len = ReadStatsRaw(statsAddress);
            if (len < 0) { _statsRawPrevLen = -1; return; }
            // Validate: the raw pairs must explain the dictionary. A miss means the layout moved.
            var span = _statsRawBuf.AsSpan(0, len);
            if (StatsRawMismatch(span, dict) is { } why)
            {
                _statsRawBroken = $"{why}: vector layout moved, raw check off";
                LogError($"[StatsRaw] {_statsRawBroken}");
                return;
            }
            if (_statsRawPrev.Length < len) _statsRawPrev = new byte[_statsRawBuf.Length];
            span.CopyTo(_statsRawPrev);
            _statsRawPrevLen = len;
        }
        catch (Exception ex) { _statsRawBroken = $"exception: {ex.Message}"; }
    }

    /// <summary>
    /// Why these raw (id, value) pairs don't explain the dictionary, or null when they do. A stat can sit in two vectors
    /// (PoE2: 21 of them in both the main and the secondary one) and the dictionary keeps one value (the main one), so a
    /// pair may differ from the dictionary as long as another pair for the same id matches it. Every dictionary stat
    /// must be matched by some pair.
    /// </summary>
    private static string? StatsRawMismatch(ReadOnlySpan<byte> pairs, IReadOnlyDictionary<GameStat, int> dict)
    {
        var matched = new HashSet<int>();
        for (var i = 0; i + 8 <= pairs.Length; i += 8)
            if (dict.TryGetValue((GameStat)BitConverter.ToInt32(pairs[i..]), out var v) && v == BitConverter.ToInt32(pairs[(i + 4)..]))
                matched.Add(BitConverter.ToInt32(pairs[i..]));
        for (var i = 0; i + 8 <= pairs.Length; i += 8)
        {
            var id = BitConverter.ToInt32(pairs[i..]);
            if (!matched.Contains(id))
                return $"raw pair at byte {i} (id {id} = {BitConverter.ToInt32(pairs[(i + 4)..])}) matches no Stats.StatDictionary entry " +
                       $"({(dict.TryGetValue((GameStat)id, out var dv) ? $"there it is {dv}" : "id absent")})";
        }
        return matched.Count < dict.Count ? $"raw pairs cover {matched.Count} of {dict.Count} stats" : null;
    }

    /// <summary>Reads all known vectors back to back into _statsRawBuf; returns the byte count, or -1 on a failed read.</summary>
    private int ReadStatsRaw(long statsAddress)
    {
        if (RawReader() is not { } read) return -1;
        var total = 0;
        foreach (var (ptrOff, vecOff) in _statsRawVecs)
        {
            var sub = RawRead<long>(statsAddress + ptrOff);
            if (sub == 0) return -1;
            var begin = RawRead<long>(sub + vecOff);
            var end = RawRead<long>(sub + vecOff + 8);
            var n = end - begin;
            if (begin == 0 || n < 0 || n % 8 != 0 || total + n > StatsRawMaxBytes) return -1;
            if (total + n > _statsRawBuf.Length) Array.Resize(ref _statsRawBuf, (int)Math.Min(StatsRawMaxBytes, Math.Max(total + n, _statsRawBuf.Length * 2L)));
            if (n > 0 && !read(new IntPtr(begin), _statsRawBuf.AsSpan(total, (int)n))) return -1;
            total += (int)n;
        }
        return total;
    }

    /// <summary>
    /// Scans the Stats component's pointers (first 0x200 bytes) and, behind each, (begin, end) pairs (first 0x200
    /// bytes) for vectors of 8-byte (id, value) items that match the dictionary. Runs once per Stats component
    /// (area change / character), through the page cache.
    /// </summary>
    private void FindStatsVectors(long statsAddress, IReadOnlyDictionary<GameStat, int> dict)
    {
        _statsRawVecs.Clear();
        var m = GameController.Memory;
        var seen = new HashSet<long>();
        var all = new List<byte>();
        for (var po = 0; po < 0x200; po += 8)
        {
            var sub = m.Read<long>(statsAddress + po);
            if (sub < 0x10000 || sub > 0x7FFF_FFFF_FFFF) continue;
            for (var vo = 0; vo < 0x200; vo += 8)
            {
                var begin = m.Read<long>(sub + vo);
                var end = m.Read<long>(sub + vo + 8);
                var n = (end - begin) / 8;
                if (begin < 0x10000 || end <= begin || (end - begin) % 8 != 0 || n < 4 || n > 4096 || !seen.Add(begin)) continue;
                var items = m.ReadBytes(begin, (int)(end - begin));
                var hits = 0;
                for (var i = 0; i + 8 <= items.Length; i += 8)
                    if (dict.TryGetValue((GameStat)BitConverter.ToInt32(items, i), out var v) && v == BitConverter.ToInt32(items, i + 4)) hits++;
                // Mostly matching: a stat kept in two vectors differs in one of them (the dictionary keeps one value).
                if (hits < n * 9 / 10) continue;
                _statsRawVecs.Add((po, vo));
                all.AddRange(items);
            }
        }
        // Together they must explain every stat, or a change to an uncovered one would wait for the periodic full read.
        if (_statsRawVecs.Count == 0)
            _statsRawBroken = $"no (begin, end) vector of (id, value) pairs matching Stats.StatDictionary ({dict.Count} stats) behind the Stats component's pointers: raw check off";
        else if (StatsRawMismatch(all.ToArray(), dict) is { } why)
            _statsRawBroken = $"the {_statsRawVecs.Count} matching vectors don't explain the dictionary: {why}: raw check off";
    }
}
