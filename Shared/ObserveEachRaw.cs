using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// The each mode's fast path. Reading Inventory.Hash through the HUD for 118 inventories cost ~660 us and 283 KB per
/// tick: every Inventory getter builds a ServerInventory and re-reads its 512-byte struct. Instead, once, each sub-path
/// is compiled on the first item into raw steps: an object segment becomes "read the pointer at +off" (the offset in
/// the parent's memory holding the child's Address, unique in the first 0x200 bytes), and the last, scalar segment
/// becomes "read T at +off" from the runtime layout of its object (RuntimeLayout.cs, only when it matches memory). Then
/// each tick is two or three allocation-free RawReads per value. The items (address and key) are listed through the HUD
/// every 5 s, and each listing re-checks the raw values of the first items against reflection: a mismatch turns the raw
/// path off for that prop and says which link broke (layer status rawPaths). A sub-path that can't be compiled stays on
/// reflection, with the reason.
/// </summary>
public partial class WhatsAnAiBridge
{
    private sealed class RawStep { public bool Ptr; public int Off; public Type? ValueType; }

    private sealed class EachCache
    {
        public List<(object key, long address, object item)> Items = [];
        public DateTime ListedAt = DateTime.MinValue;
        public RawStep[]?[]? Plans;                 // per prop: raw steps, or null = reflection
        public string[] PlanNotes = [];              // per prop: "raw" or why not
        public byte[] Block = [];                    // the values' span when every plan shares its pointers
        public int BlockFrom;
        public int Shared = -1;                      // leading pointer steps every plan shares, -1 = read each value alone
    }

    private const double EachRelistSeconds = 5;

    private void EachValuesRaw(LayerRun l, IEnumerable items, Dictionary<object, object?> into)
    {
        var props = l.Spec.Props!;
        var c = l.Each ??= new EachCache();
        if ((DateTime.UtcNow - c.ListedAt).TotalSeconds >= EachRelistSeconds)
        {
            c.ListedAt = DateTime.UtcNow;
            c.Items.Clear();
            var n = 0;
            foreach (var item in items)
            {
                if (item == null || ++n > 2000) continue;
                c.Items.Add((SubPath(l, item, l.Spec.Key ?? "Address") ?? n, ItemAddress(item), item));
            }
            if (c.Plans == null && c.Items.Count > 0) CompileEach(l, c);
            if (c.Plans != null) CheckEach(l, c);
            c.Shared = c.Plans != null && c.Plans.All(p => p != null) ? SharedPrefix(c.Plans!) : -1;
        }
        var shared = c.Shared;
        for (var j = 0; j < c.Items.Count; j++)
        {
            var (key, address, item) = c.Items[j];
            // All props behind the same pointers (Inventory.Hash, Inventory.ItemCount): follow them once and read the
            // span holding every value in one call, 2 reads per item instead of 4.
            var block = shared >= 0 && address != 0 ? ReadBlock(c, address, shared) : -1;
            for (var i = 0; i < props.Count; i++)
            {
                if (!l.EachUnits.TryGetValue((key, i), out var unit)) l.EachUnits[(key, i)] = unit = $"{Text(key)}.{props[i]}";
                into[unit] = block > 0 ? Decode(c.Plans![i]![^1], c.Block.AsSpan((int)(c.Plans[i]![^1].Off - c.BlockFrom)))
                    : block == 0 ? null
                    : c.Plans?[i] is { } plan && address != 0 ? RawValue(plan, address) : SubPath(l, item, props[i]);
            }
        }
    }

    /// <summary>How many leading pointer steps every plan shares (-1 when the final reads aren't close enough to batch).</summary>
    private static int SharedPrefix(RawStep[]?[] plans)
    {
        var first = plans[0]!;
        var n = first.Length - 1;
        foreach (var p in plans)
        {
            if (p!.Length != first.Length) return -1;
            for (var k = 0; k < n; k++) if (!p[k].Ptr || p[k].Off != first[k].Off) return -1;
        }
        var offs = plans.Select(p => p![^1].Off).ToArray();
        return offs.Max() + 8 - offs.Min() <= 0x400 ? n : -1;
    }

    /// <summary>Follows the shared pointers and reads the values' span into c.Block: 1 = read, 0 = null pointer, -1 = failed.</summary>
    private int ReadBlock(EachCache c, long address, int prefix)
    {
        var plan = c.Plans![0]!;
        for (var k = 0; k < prefix; k++) { address = RawRead<long>(address + plan[k].Off); if (address == 0) return 0; }
        if (c.Block.Length == 0)
        {
            c.BlockFrom = c.Plans.Min(p => p![^1].Off);
            c.Block = new byte[c.Plans.Max(p => p![^1].Off) + 8 - c.BlockFrom];
        }
        if (RawReader() is not { } read || !read(new IntPtr(address + c.BlockFrom), c.Block)) return -1;
        return 1;
    }

    private static object Decode(RawStep s, ReadOnlySpan<byte> b)
    {
        var t = s.ValueType!;
        var raw = t.IsEnum ? Enum.GetUnderlyingType(t) : t;
        object v = raw == typeof(long) ? BitConverter.ToInt64(b) : raw == typeof(int) ? BitConverter.ToInt32(b)
            : raw == typeof(short) ? BitConverter.ToInt16(b) : raw == typeof(byte) ? b[0]
            : raw == typeof(ulong) ? BitConverter.ToUInt64(b) : raw == typeof(uint) ? BitConverter.ToUInt32(b)
            : raw == typeof(ushort) ? BitConverter.ToUInt16(b) : raw == typeof(sbyte) ? (sbyte)b[0] : b[0] != 0;
        return t.IsEnum ? Enum.ToObject(t, v) : v;
    }

    private static long ItemAddress(object item) => item.GetType().GetProperty("Address")?.GetValue(item) as long? ?? 0;

    private void CompileEach(LayerRun l, EachCache c)
    {
        var props = l.Spec.Props!;
        c.Plans = new RawStep[]?[props.Count];
        c.PlanNotes = new string[props.Count];
        for (var i = 0; i < props.Count; i++)
        {
            try { c.Plans[i] = CompileRaw(l, c.Items[0].item, props[i], out c.PlanNotes[i]); }
            catch (Exception ex) { c.PlanNotes[i] = $"reflection: {ex.Message}"; }
        }
    }

    /// <summary>Raw steps for a dotted sub-path below item (the collection's item 0), or null with the reason in note.</summary>
    private RawStep[]? CompileRaw(LayerRun l, object item, string path, out string note)
    {
        var segs = path.Split('.');
        var steps = new List<RawStep>();
        object cur = item;
        var addr = ItemAddress(item);
        var walk = $"{l.Spec.Path}[0]";
        for (var k = 0; k < segs.Length; k++)
        {
            if (addr == 0) { note = $"reflection: '{(k == 0 ? "item" : segs[k - 1])}' has no Address"; return null; }
            var prop = cur.GetType().GetProperty(segs[k]);
            var v = prop?.GetValue(cur);
            if (prop == null || v == null) { note = $"reflection: '{segs[k]}' is null on item 0"; return null; }
            if (k < segs.Length - 1)
            {
                var child = ItemAddress(v);
                var hits = Enumerable.Range(0, 0x200 / 8).Where(o => RawRead<long>(addr + o * 8) == child).ToList();
                if (child == 0 || hits.Count != 1) { note = $"reflection: {hits.Count} pointers to '{segs[k]}' in the first 0x200 bytes of {cur.GetType().Name}"; return null; }
                steps.Add(new RawStep { Ptr = true, Off = hits[0] * 8 });
                walk += "." + segs[k];
                cur = v; addr = child;
                continue;
            }
            var t = prop.PropertyType;
            var raw = t.IsEnum ? Enum.GetUnderlyingType(t) : t;
            if (raw != typeof(long) && raw != typeof(int) && raw != typeof(short) && raw != typeof(byte) && raw != typeof(ulong)
                && raw != typeof(uint) && raw != typeof(ushort) && raw != typeof(sbyte) && raw != typeof(bool))
            { note = $"reflection: '{segs[k]}' is {t.Name}, not a fixed-size number"; return null; }
            var layout = RuntimeLayout(new JObject { ["path"] = walk });
            var row = (layout["properties"] as JArray)?.FirstOrDefault(p => p["property"]?.ToString() == segs[k] && p["memoryNow"]?.ToString() == "same");
            if (row?["offset"]?.ToString() is not { } hex || !hex.StartsWith("0x"))
            { note = $"reflection: the runtime layout of {cur.GetType().Name} doesn't map '{segs[k]}' to memory ({layout["error"] ?? "no matching field"})"; return null; }
            steps.Add(new RawStep { Off = Convert.ToInt32(hex[2..], 16), ValueType = t });
        }
        note = "raw: " + string.Join(" -> ", steps.Select(s => s.Ptr ? $"[+0x{s.Off:X}]" : $"{s.ValueType!.Name} +0x{s.Off:X}"));
        return steps.ToArray();
    }

    private object? RawValue(RawStep[] plan, long address)
    {
        foreach (var s in plan)
        {
            if (s.Ptr) { address = RawRead<long>(address + s.Off); if (address == 0) return null; continue; }
            var t = s.ValueType!;
            var raw = t.IsEnum ? Enum.GetUnderlyingType(t) : t;
            object v = raw == typeof(long) ? RawRead<long>(address + s.Off) : raw == typeof(int) ? RawRead<int>(address + s.Off)
                : raw == typeof(short) ? RawRead<short>(address + s.Off) : raw == typeof(byte) ? RawRead<byte>(address + s.Off)
                : raw == typeof(ulong) ? RawRead<ulong>(address + s.Off) : raw == typeof(uint) ? RawRead<uint>(address + s.Off)
                : raw == typeof(ushort) ? RawRead<ushort>(address + s.Off) : raw == typeof(sbyte) ? RawRead<sbyte>(address + s.Off)
                : RawRead<byte>(address + s.Off) != 0;
            return t.IsEnum ? Enum.ToObject(t, v) : v;
        }
        return null;
    }

    /// <summary>The raw values of the first items must equal what the HUD reads; a mismatch (twice) turns raw off for that prop.</summary>
    private void CheckEach(LayerRun l, EachCache c)
    {
        var props = l.Spec.Props!;
        for (var i = 0; i < props.Count; i++)
        {
            if (c.Plans![i] is not { } plan) continue;
            foreach (var (key, address, item) in c.Items.Take(3))
            {
                if (Equals(RawValue(plan, address), SubPath(l, item, props[i]))) continue;
                var raw = RawValue(plan, address); var hud = SubPath(l, item, props[i]);   // once more: it may have changed in between
                if (Equals(raw, hud)) continue;
                c.Plans[i] = null;
                c.PlanNotes[i] = $"reflection: the raw read ({c.PlanNotes[i]}) gave {Text(raw)} for {Text(key)}, the HUD {Text(hud)}: an offset moved";
                LogError($"[Observe] layer {l.Spec.Id} {props[i]}: {c.PlanNotes[i]}");
                break;
            }
        }
    }
}
