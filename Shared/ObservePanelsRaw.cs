using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WhatsAnAiBridge;

/// <summary>
/// ObservePanels' 10 Hz visibility scan in few memory reads. Each top-level panel's visibility is one bit of its element
/// (calibrated in ObsCalibrateFlag). Reading that word per panel cost one read call each (~124 calls, ~0.9 us and
/// 40 B allocated by the HUD backend per call: ~130 us and ~5 KB per scan). The panels are heap neighbours, so the
/// flag words are grouped into runs (sorted by address, merged while the gap is small) and each run is one read; a
/// run whose read fails (an unmapped page in a gap) is read word by word from then on. Same bits, same order, fewer calls.
/// </summary>
public partial class WhatsAnAiBridge
{
    // One slot per distinct top-level child, in child-index order (the order events were always emitted in).
    private long[] _obsKidAddr = [];
    private int[] _obsKidIdx = [];
    private UiElement[] _obsKidEl = [];
    private bool[] _obsVisNow = [];

    // The batched read plan for the calibrated flag word: runs over slots sorted by address.
    private struct ObsRun { public long Start; public int Len; public int First, Count; public bool Split; }
    private ObsRun[] _obsRuns = [];
    private int[] _obsRunSlots = [];   // slots in address order; run r covers _obsRunSlots[First .. First + Count)
    private byte[] _obsRunBuf = [];
    private int _obsPlanOff = -1;      // the flag offset the plan was built for

    // Merge flag words whose gap is under 12 KB into one read, at most 64 KB per read. Measured on the live PoE2 backend:
    // a read costs ~0.9 us plus ~0.06 us per KB, so a 12 KB gap costs about what a second call would.
    private const int ObsRunGap = 12 * 1024, ObsRunMax = 64 * 1024;

    /// <summary>Takes the HUD's (cached) child list; rebuilds the slots only when the addresses changed.</summary>
    private void ObsRelistKids(IList<UiElement> kids)
    {
        var same = true;
        var n = 0;
        for (var i = 0; i < kids.Count; i++)
        {
            if (kids[i] is not { Address: not 0 } k) continue;
            if (n >= _obsKidAddr.Length || _obsKidAddr[n] != k.Address || _obsKidIdx[n] != i || !ReferenceEquals(_obsKidEl[n], k)) same = false;
            n++;
        }
        if (same && n == _obsKidAddr.Length) return;

        var addrs = new List<long>(kids.Count);
        var slotOf = new Dictionary<long, int>(kids.Count);
        var idx = new List<int>(kids.Count);
        var els = new List<UiElement>(kids.Count);
        for (var i = 0; i < kids.Count; i++)
        {
            if (kids[i] is not { Address: not 0 } k) continue;
            // A repeated address keeps its first slot and takes the later index (as the address-keyed map did).
            if (slotOf.TryGetValue(k.Address, out var s)) { idx[s] = i; els[s] = k; continue; }
            slotOf[k.Address] = addrs.Count;
            addrs.Add(k.Address); idx.Add(i); els.Add(k);
        }
        _obsKidAddr = addrs.ToArray(); _obsKidIdx = idx.ToArray(); _obsKidEl = els.ToArray();
        _obsVisNow = new bool[_obsKidAddr.Length];
        _obsPlanOff = -1;   // re-plan on the next read
    }

    private void ObsBuildPlan()
    {
        _obsPlanOff = _obsFlagOff;
        var order = new int[_obsKidAddr.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => _obsKidAddr[a].CompareTo(_obsKidAddr[b]));
        var runs = new List<ObsRun>();
        var maxLen = 0;
        for (var i = 0; i < order.Length;)
        {
            var start = _obsKidAddr[order[i]] + _obsFlagOff;
            var end = start + 8;
            var j = i + 1;
            for (; j < order.Length; j++)
            {
                var a = _obsKidAddr[order[j]] + _obsFlagOff;
                if (a - end > ObsRunGap || a + 8 - start > ObsRunMax) break;
                end = Math.Max(end, a + 8);
            }
            runs.Add(new ObsRun { Start = start, Len = (int)(end - start), First = i, Count = j - i });
            maxLen = Math.Max(maxLen, (int)(end - start));
            i = j;
        }
        _obsRuns = runs.ToArray();
        _obsRunSlots = order;
        if (_obsRunBuf.Length < maxLen) _obsRunBuf = new byte[maxLen];
    }

    /// <summary>Fills _obsVisNow for every slot: batched raw reads once the flag is calibrated, else IsVisibleLocal.</summary>
    private void ObsReadVisible()
    {
        if (_obsFlagOff < 0 || RawReader() is not { } read)
        {
            for (var i = 0; i < _obsKidEl.Length; i++) _obsVisNow[i] = ObsVisible(_obsKidEl[i]);
            return;
        }
        if (_obsPlanOff != _obsFlagOff) ObsBuildPlan();
        var bit = _obsFlagBit;
        for (var r = 0; r < _obsRuns.Length; r++)
        {
            ref var run = ref _obsRuns[r];
            if (run.Count > 1 && !run.Split)
            {
                var span = _obsRunBuf.AsSpan(0, run.Len);
                if (read(new IntPtr(run.Start), span))
                {
                    for (var k = run.First; k < run.First + run.Count; k++)
                    {
                        var slot = _obsRunSlots[k];
                        var off = (int)(_obsKidAddr[slot] + _obsFlagOff - run.Start);
                        _obsVisNow[slot] = (MemoryMarshal.Read<ulong>(span.Slice(off, 8)) >> bit & 1) != 0;
                    }
                    continue;
                }
                // A gap between two panels isn't readable (unmapped page): read this run's words one by one from now on.
                run.Split = true;
                LogQuiet($"[Observe] panel flag run at 0x{run.Start:X} (+{run.Len} B, {run.Count} panels) not readable in one read: reading its panels one by one");
            }
            for (var k = run.First; k < run.First + run.Count; k++)
            {
                var slot = _obsRunSlots[k];
                _obsVisNow[slot] = (RawRead<ulong>(_obsKidAddr[slot] + _obsFlagOff) >> bit & 1) != 0;
            }
        }
    }
}
