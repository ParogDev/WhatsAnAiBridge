using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Read-only memory inspection for agents mapping game structures:
///   memory.read    a region (at an address or a walker path's object Address) as classified 8-byte slots:
///                  zero / module pointer (section, RVA, Ghidra address, RTTI class of a vtable) / heap pointer
///                  (what it points at: an object with a vtable, text) / float / int, plus a hex+ASCII dump.
///   memory.layout  the offsets struct the HUD itself reads for an object (found by reflection: the wrapper's
///                  private CachedValue&lt;T&gt;/FrameCache&lt;T&gt; field - on PoE2 that is the real, obfuscated struct,
///                  not the GameOffsets2 decoys), overlaid on live memory: every [FieldOffset] field with its
///                  bytes, decoded value and a sanity check, plus the unmapped gaps between fields.
///   memory.where   what one address is: module + section + RVA + Ghidra address, or a heap region.
/// Only reads (IMemory.ReadBytes and VirtualQueryEx); nothing is written to the game.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int MaxReadBytes = 4096;

    private string? ProcessMemoryMethod(string method, JToken? p) => method switch
    {
        "memory.read" => SafeMemory(() => MemoryRead(p)),
        "memory.layout" => SafeMemory(() => MemoryLayout(p)),
        "memory.collect" => SafeMemory(() => MemoryCollect(p)),
        "memory.where" => SafeMemory(() =>
        {
            var a = ParseAddress(p?["address"]);
            return a == null ? Err("missing_address", "Pass address (number or \"0x...\").") : Where(a.Value, new MemoryContext(this));
        }),
        _ => null,
    };

    private static string SafeMemory(Func<JObject> f)
    {
        try { return f().ToString(Newtonsoft.Json.Formatting.None); }
        catch (Exception ex) { return Err("memory_failed", (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message).ToString(Newtonsoft.Json.Formatting.None); }
    }

    private static JObject Err(string code, string message) => new() { ["error"] = code, ["message"] = message };

    // ── memory.read ──────────────────────────────────────────────────

    private JObject MemoryRead(JToken? p)
    {
        var ctx = new MemoryContext(this);
        var (start, origin, err) = ResolveBase(p);
        if (err != null) return err;
        var offset = p?["offset"]?.Value<long>() ?? 0;
        var size = Math.Clamp(p?["size"]?.Value<int>() ?? 256, 8, MaxReadBytes);
        var address = start + offset;
        var bytes = ctx.Read(address, size);
        if (bytes == null) return Err("unreadable", $"0x{address:X} is not readable ({ctx.RegionText(address)}).");

        // classify=false: raw bytes only (base64), for cheap repeated sampling (watch_memory).
        if (p?["classify"]?.Value<bool>() == false)
            return new JObject { ["address"] = Hex(address), ["size"] = bytes.Length, ["data"] = Convert.ToBase64String(bytes) };

        var slots = new JArray();
        for (int off = 0; off + 8 <= bytes.Length; off += 8)
        {
            var slot = Classify(BitConverter.ToInt64(bytes, off), bytes, off, ctx, deep: true);
            slot.AddFirst(new JProperty("off", off));
            slots.Add(slot);
        }
        var o = new JObject
        {
            ["address"] = Hex(address), ["size"] = bytes.Length, ["origin"] = origin,
            ["region"] = ctx.RegionText(address),
            ["module"] = ctx.ModuleInfo(),
            ["slots"] = slots,
            ["hex"] = HexDump(bytes),
        };
        if (offset != 0) o["base"] = Hex(start);
        return o;
    }

    // ── memory.collect ───────────────────────────────────────────────

    /// <summary>
    /// The same byte range from every item of a collection (walker path to an IEnumerable of memory objects),
    /// with optional per-item labels read through dotted property paths (e.g. "Name", "Affinity", "Owner.Path"; no method
    /// calls). The population for correlating bits with known properties.
    /// </summary>
    private JObject MemoryCollect(JToken? p)
    {
        var ctx = new MemoryContext(this);
        var path = p?["path"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(path)) return Err("missing_path", "Pass path: a walker path to a collection of memory objects.");
        var coll = new ExpressionWalker(GameController).Resolve(path, out var error);
        if (error != null) return Err("resolve_failed", error);
        if (coll is not System.Collections.IEnumerable items || coll is string) return Err("not_a_collection", $"'{path}' is not a collection.");
        var offset = p?["offset"]?.Value<int>() ?? 0;
        var limit = Math.Clamp(p?["limit"]?.Value<int>() ?? 500, 1, 2000);
        var labels = (p?["labels"] as JArray)?.Select(x => x.ToString()).Where(s => s.Length > 0).Take(8).ToList() ?? [];

        int index = -1, size = p?["size"]?.Value<int>() ?? 0;
        Type? structType = null;
        var rows = new JArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var item in items)
        {
            index++;
            if (rows.Count >= limit || sw.ElapsedMilliseconds > 120) break;
            if (item == null || ReadAddress(item) is not { } addr || addr == 0) continue;
            if (size <= 0)
            {
                structType ??= CachedStructType(item.GetType(), out _);
                size = structType != null ? SizeOf(structType) : 64;
            }
            size = Math.Clamp(size, 1, 1024);
            var bytes = ctx.Read(addr + offset, size);
            if (bytes == null) continue;
            var row = new JObject { ["index"] = index, ["address"] = Hex(addr), ["data"] = Convert.ToBase64String(bytes) };
            if (labels.Count > 0)
            {
                var l = new JObject();
                foreach (var label in labels) l[label] = LabelValue(item, label);
                row["labels"] = l;
            }
            rows.Add(row);
        }
        var o = new JObject
        {
            ["path"] = path, ["offset"] = offset, ["size"] = size, ["count"] = rows.Count, ["items"] = rows,
            ["struct"] = structType?.FullName,
        };
        if (sw.ElapsedMilliseconds > 120) o["truncated"] = $"Stopped after {rows.Count} items (time budget); pass a smaller limit or size.";
        return o;
    }

    /// <summary>A dotted property path on an item, as a JSON scalar (enums as their number, so bit masks stay usable).</summary>
    private static JToken LabelValue(object item, string memberPath)
    {
        object? cur = item;
        foreach (var part in memberPath.Split('.'))
        {
            if (cur == null) return JValue.CreateNull();
            var t = cur.GetType();
            var prop = t.GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
            try
            {
                if (prop != null && prop.GetIndexParameters().Length == 0) cur = prop.GetValue(cur);
                else if (t.GetField(part, BindingFlags.Public | BindingFlags.Instance) is { } f) cur = f.GetValue(cur);
                else return $"[no member {part}]";
            }
            catch (Exception ex) { return $"[error: {(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message}]"; }
        }
        return cur switch
        {
            null => JValue.CreateNull(),
            Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture),
            string s => s,
            bool b => b,
            IConvertible c when cur.GetType().IsPrimitive => JToken.FromObject(c),
            _ => cur.ToString() ?? "",
        };
    }

    // ── memory.layout ────────────────────────────────────────────────

    private JObject MemoryLayout(JToken? p)
    {
        var ctx = new MemoryContext(this);
        var path = p?["path"]?.Value<string>();
        var typeName = p?["type"]?.Value<string>();
        long address;
        Type? structType = null;
        string how;
        object? target = null;
        if (path != null)
        {
            target = new ExpressionWalker(GameController).Resolve(path, out var error);
            if (error != null) return Err("resolve_failed", error);
            if (target == null) return Err("null", $"'{path}' is null.");
            if (ReadAddress(target) is not { } a || a == 0) return Err("no_address", $"'{path}' ({target.GetType().Name}) has no Address: it isn't a memory object.");
            address = a;
            how = "path";
        }
        else if (ParseAddress(p?["address"]) is { } a2) { address = a2; how = "address"; }
        else return Err("missing_target", "Pass path (a walker path to a memory object) or address plus type.");

        if (typeName != null)
        {
            structType = FindType(typeName);
            if (structType == null) return Err("unknown_type", $"No loaded type named '{typeName}' (use hud_find_types).");
        }
        else if (target != null)
        {
            structType = CachedStructType(target.GetType(), out var field);
            how = field != null ? $"{target.GetType().Name}.{field} (the struct the HUD reads)" : how;
        }
        if (structType == null)
            return Err("no_struct", "This object caches no offsets struct; pass type (a struct with [FieldOffset] fields, see hud_type).");

        var structSize = SizeOf(structType);
        // extend: also read past the declared end - patches often append members the HUD doesn't know yet.
        var readSize = Math.Clamp(structSize + Math.Clamp(p?["extend"]?.Value<int>() ?? 0, 0, MaxReadBytes), 8, MaxReadBytes);
        var bytes = ctx.Read(address, readSize);
        if (bytes == null) return Err("unreadable", $"0x{address:X} is not readable ({ctx.RegionText(address)}).");

        var fields = new List<JObject>();
        Flatten(structType, 0, "", bytes, ctx, fields, 0);
        fields.Sort((x, y) => x["off"]!.Value<int>().CompareTo(y["off"]!.Value<int>()));

        // Unmapped byte ranges between declared fields: where unknown data (or a shifted field) lives.
        var gaps = new JArray();
        int cursor = 0;
        foreach (var f in fields)
        {
            int fo = f["off"]!.Value<int>(), fs = f["size"]!.Value<int>();
            if (fo > cursor) gaps.Add(new JObject { ["off"] = cursor, ["size"] = fo - cursor });
            cursor = Math.Max(cursor, fo + fs);
        }
        if (cursor < readSize) gaps.Add(new JObject { ["off"] = cursor, ["size"] = readSize - cursor });

        // Inside unmapped ranges: 8-byte slots that look like structure (what the HUD may be missing).
        var candidates = Candidates(bytes, gaps, ctx, address, readSize);

        var suspicious = fields.Count(f => f["check"]?.ToString() is "suspicious" or "invalid");
        return new JObject
        {
            ["address"] = Hex(address), ["struct"] = structType.FullName, ["structSize"] = structSize, ["source"] = how,
            ["object"] = target?.GetType().FullName,
            ["fields"] = new JArray(fields), ["gaps"] = gaps, ["candidates"] = candidates,
            ["summary"] = $"{fields.Count} fields, {fields.Count(f => f["check"]?.ToString() == "ok")} ok, {suspicious} suspicious/invalid, " +
                          $"{gaps.Count} unmapped ranges, {candidates.Count} unmapped slots that look like structure",
            ["hex"] = HexDump(bytes),
        };
    }

    /// <summary>
    /// Leaf fields of an explicit-layout struct, with nested structs expanded (depth 3). Fields without
    /// [FieldOffset] (sequential structs) fall back to Marshal.OffsetOf.
    /// </summary>
    private static void Flatten(Type t, int baseOff, string prefix, byte[] bytes, MemoryContext ctx, List<JObject> into, int depth)
    {
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            int off;
            var fo = f.GetCustomAttribute<FieldOffsetAttribute>();
            if (fo != null) off = fo.Value;
            else { try { off = (int)Marshal.OffsetOf(t, f.Name); } catch { continue; } }
            off += baseOff;
            var ft = f.FieldType;
            var name = prefix + f.Name;
            var nested = ft.IsValueType && !ft.IsPrimitive && !ft.IsEnum && ft.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length > 0;
            if (nested && depth < 3 && !IsStdVector(ft))
            {
                Flatten(ft, off, name + ".", bytes, ctx, into, depth + 1);
                continue;
            }
            int size = Math.Max(1, SizeOf(ft));
            var row = new JObject { ["off"] = off, ["size"] = size, ["name"] = name, ["type"] = ExpressionWalker.FormatTypeName(ft) };
            if (off + size > bytes.Length) { row["check"] = "unread"; into.Add(row); continue; }
            row["bytes"] = BitConverter.ToString(bytes, off, Math.Min(size, 32)).Replace("-", " ");
            Decode(ft, name, bytes, off, size, ctx, row);
            into.Add(row);
        }
    }

    private static bool IsStdVector(Type t) => t.Name is "StdVector" or "NativePtrArray";

    private static void Decode(Type ft, string name, byte[] b, int off, int size, MemoryContext ctx, JObject row)
    {
        string check = "ok", why = "";
        var t = ft.IsEnum ? Enum.GetUnderlyingType(ft) : ft;
        if (t == typeof(float))
        {
            var v = BitConverter.ToSingle(b, off);
            row["value"] = Num(v);
            if (!float.IsFinite(v) && !float.IsPositiveInfinity(v)) { check = "suspicious"; why = "not a finite float"; }
            else if (Math.Abs(v) > 1e8) { check = "suspicious"; why = "implausibly large float"; }
        }
        else if (t == typeof(double)) { var v = BitConverter.ToDouble(b, off); row["value"] = Num(v); if (!double.IsFinite(v) || Math.Abs(v) > 1e12) { check = "suspicious"; why = "implausible double"; } }
        else if (t == typeof(bool) || t == typeof(byte) && name.StartsWith("Is", StringComparison.Ordinal))
        { var v = b[off]; row["value"] = v; if (v > 1) { check = "suspicious"; why = "bool byte is not 0/1"; } }
        else if (t == typeof(byte) || t == typeof(sbyte)) row["value"] = t == typeof(byte) ? b[off] : (sbyte)b[off];
        else if (t == typeof(short) || t == typeof(ushort)) row["value"] = t == typeof(short) ? BitConverter.ToInt16(b, off) : BitConverter.ToUInt16(b, off);
        else if (t == typeof(int) || t == typeof(uint))
        {
            long v = t == typeof(int) ? BitConverter.ToInt32(b, off) : BitConverter.ToUInt32(b, off);
            row["value"] = v;
            if (t == typeof(int) && Math.Abs(v) > 1_000_000_000) { check = "unusual"; why = "very large for an int field"; }
        }
        else if (t == typeof(long) || t == typeof(ulong) || t == typeof(IntPtr) || t == typeof(UIntPtr))
        {
            var v = BitConverter.ToInt64(b, off);
            var cls = Classify(v, b, off, ctx, deep: true);
            row["value"] = cls["kind"]!.ToString() is "zero" or "int" ? (JToken)v : Hex(v);
            foreach (var k in new[] { "kind", "module", "section", "rva", "ghidra", "rtti", "points", "text" })
                if (cls[k] != null) row[k] = cls[k];
            var pointerish = System.Text.RegularExpressions.Regex.IsMatch(name, "ptr|pointer|address|owner|vtable|first|last|end|base|head|data", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (pointerish && cls["kind"]!.ToString() is "int" or "bad-pointer") { check = "suspicious"; why = "named like a pointer but doesn't point at readable memory"; }
        }
        else if (IsStdVector(t))
        {
            long first = BitConverter.ToInt64(b, off), last = BitConverter.ToInt64(b, off + 8), end = size >= 24 ? BitConverter.ToInt64(b, off + 16) : last;
            row["value"] = $"First={Hex(first)} Last={Hex(last)} End={Hex(end)} ({last - first} bytes)";
            if (first == 0 && last == 0) { }
            else if (!(first <= last && last <= end) || last - first > 64_000_000) { check = "invalid"; why = "First <= Last <= End doesn't hold"; }
            else if (!ctx.Readable(first)) { check = "invalid"; why = "First is not readable memory"; }
        }
        else row["value"] = BitConverter.ToString(b, off, Math.Min(size, 16)).Replace("-", "");
        if (row["value"] is JValue { Type: JTokenType.Integer } iv && FlagLike(name, iv.Value<long>()) && Bits(iv.Value<long>(), size * 8) is { } bits)
            row["bits"] = bits;
        row["check"] = check;
        if (why.Length > 0) row["why"] = why;
    }

    private static bool FlagLike(string name, long v) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "flag|affinit|mask|state|bits|type|kind", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        || v >= 16 && System.Numerics.BitOperations.PopCount((ulong)v) == 1;

    /// <summary>
    /// Set bit indices for values that look like flags (2-12 bits set, not a small count) - e.g. stash tab
    /// affinities are a bit mask. Null for plain numbers.
    /// </summary>
    private static JArray? Bits(long v, int width)
    {
        if (width < 64) v &= (1L << width) - 1;
        if (v <= 0) return null;
        int pop = System.Numerics.BitOperations.PopCount((ulong)v);
        if (pop < 1 || pop > 12 || v < 8 && pop < 2) return null;
        var a = new JArray();
        for (int i = 0; i < width; i++) if ((v & (1L << i)) != 0) a.Add(i);
        return a;
    }

    /// <summary>
    /// Structure-looking data in the unmapped ranges (8-byte aligned): std::vector triplets (First &lt;= Last &lt;= End,
    /// all readable), pointers to objects with a vtable (RTTI class when present), vtables, text, module pointers.
    /// Plain ints, zeros and floats are left out: they are data, not structure.
    /// </summary>
    private static JArray Candidates(byte[] bytes, JArray gaps, MemoryContext ctx, long self, int selfSize)
    {
        var found = new JArray();
        foreach (var g in gaps)
        {
            int start = (g["off"]!.Value<int>() + 7) & ~7, end = g["off"]!.Value<int>() + g["size"]!.Value<int>();
            for (int off = start; off + 8 <= end && found.Count < 64; off += 8)
            {
                long a = BitConverter.ToInt64(bytes, off);
                if (off + 24 <= end)
                {
                    long b = BitConverter.ToInt64(bytes, off + 8), c = BitConverter.ToInt64(bytes, off + 16);
                    if (a != 0 && a <= b && b <= c && c - a < 64_000_000 && ctx.Readable(a) && !ctx.InModule(a, out _, out _))
                    {
                        found.Add(new JObject
                        {
                            ["off"] = off, ["size"] = 24, ["kind"] = "std::vector",
                            ["detail"] = $"First={Hex(a)} Last={Hex(b)} End={Hex(c)} ({b - a} bytes used, {c - a} capacity)",
                            ["first"] = Hex(a),
                        });
                        off += 16;
                        continue;
                    }
                }
                if (a >= self && a < self + selfSize)
                {
                    found.Add(new JObject { ["off"] = off, ["size"] = 8, ["kind"] = "self", ["value"] = Hex(a), ["detail"] = $"points into this struct at +{a - self}" });
                    continue;
                }
                var cls = Classify(a, bytes, off, ctx, deep: true);
                var kind = cls["kind"]!.ToString();
                if (kind is "zero" or "int" or "float" or "bad-pointer") continue;
                if (kind == "heap" && cls["points"] == null) kind = "pointer";
                var row = new JObject { ["off"] = off, ["size"] = 8, ["kind"] = kind, ["value"] = cls["hex"] };
                foreach (var k in new[] { "points", "text", "rtti", "section", "rva", "ghidra", "firstMethod" })
                    if (cls[k] != null) row[k] = cls[k];
                found.Add(row);
            }
        }
        return found;
    }

    // ── Slot classification ──────────────────────────────────────────

    /// <summary>What an 8-byte value probably is. deep: follow heap pointers one level (vtable/RTTI, text).</summary>
    private static JObject Classify(long v, byte[] b, int off, MemoryContext ctx, bool deep)
    {
        var o = new JObject { ["hex"] = Hex(v) };
        if (v == 0) { o["kind"] = "zero"; return o; }
        if (ctx.InModule(v, out var section, out var rva))
        {
            o["kind"] = "module";
            o["module"] = ctx.ModuleName;
            if (section != null) o["section"] = section;
            o["rva"] = Hex(rva);
            o["ghidra"] = Hex(ctx.ImageBase + rva);
            if (section is ".rdata" or ".data")
            {
                if (ctx.RttiName(v) is { } rtti) { o["kind"] = "vtable"; o["rtti"] = rtti; }
                // No RTTI (the game strips it): a .rdata slot whose first entry points into code is still a vtable.
                else if (ctx.Read(v, 8) is { } e && ctx.InModule(BitConverter.ToInt64(e, 0), out var es, out var erva) && es == ".text")
                { o["kind"] = "vtable"; o["firstMethod"] = Hex(ctx.ImageBase + erva); }
            }
            return o;
        }
        if (v > 0x10000 && v < 0x7FFF_FFFF_FFFF && ctx.Readable(v))
        {
            o["kind"] = "heap";
            if (deep && ctx.Read(v, 16) is { } peek)
            {
                var first = BitConverter.ToInt64(peek, 0);
                if (ctx.InModule(first, out var s2, out var r2) && s2 is ".rdata")
                    o["points"] = ctx.RttiName(first) is { } cls ? $"object {cls}" : $"object (vtable {Hex(ctx.ImageBase + r2)})";
                else if (Text(peek) is { } text) { o["points"] = "text"; o["text"] = text; }
            }
            return o;
        }
        // Not a pointer: two int32s or floats, whichever reads more plausibly.
        int lo = BitConverter.ToInt32(b, off), hi = BitConverter.ToInt32(b, off + 4);
        float flo = BitConverter.ToSingle(b, off), fhi = BitConverter.ToSingle(b, off + 4);
        if (Plausible(flo) && Plausible(fhi) && (Math.Abs((long)lo) > 100_000 || Math.Abs((long)hi) > 100_000)) // long: Math.Abs(int.MinValue) throws
        { o["kind"] = "float"; o["value"] = $"{Num(flo)}, {Num(fhi)}"; }
        else if (Text(b.AsSpan(off, 8).ToArray()) is { } t8) { o["kind"] = "text"; o["text"] = t8; }
        else
        {
            o["kind"] = (v >> 47) != 0 && (v >> 47) != -1 ? "bad-pointer" : "int";
            o["value"] = hi == 0 || hi == -1 ? lo.ToString(CultureInfo.InvariantCulture) : $"{lo}, {hi}";
            if (hi == 0 && lo >= 16 && System.Numerics.BitOperations.PopCount((uint)lo) is >= 1 and <= 3 && Bits(lo, 32) is { } bits) o["bits"] = bits;
        }
        return o;
    }

    private static bool Plausible(float f) => float.IsFinite(f) && (f == 0 || Math.Abs(f) > 1e-4 && Math.Abs(f) < 1e7);

    /// <summary>ASCII or UTF-16 text at the start of a buffer (at least 4 printable characters).</summary>
    private static string? Text(byte[] b)
    {
        int ascii = 0; while (ascii < b.Length && b[ascii] >= 0x20 && b[ascii] < 0x7F) ascii++;
        if (ascii >= 4) return Encoding.ASCII.GetString(b, 0, ascii);
        int u = 0; while (u + 1 < b.Length && b[u] >= 0x20 && b[u] < 0x7F && b[u + 1] == 0) u += 2;
        return u >= 8 ? Encoding.Unicode.GetString(b, 0, u) : null;
    }

    private static JObject Where(long a, MemoryContext ctx)
    {
        var o = Classify(a, BitConverter.GetBytes(a), 0, ctx, deep: true);
        o["address"] = Hex(a);
        o["region"] = ctx.RegionText(a);
        o["moduleInfo"] = ctx.ModuleInfo();
        return o;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private (long start, string origin, JObject? err) ResolveBase(JToken? p)
    {
        if (ParseAddress(p?["address"]) is { } a) return (a, "address", null);
        var path = p?["path"]?.Value<string>();
        if (path == null) return (0, "", Err("missing_target", "Pass address or path."));
        var obj = new ExpressionWalker(GameController).Resolve(path, out var error);
        if (error != null) return (0, "", Err("resolve_failed", error));
        if (obj == null) return (0, "", Err("null", $"'{path}' is null."));
        if (ReadAddress(obj) is not { } addr || addr == 0)
            return (0, "", Err("no_address", $"'{path}' ({obj.GetType().Name}) has no Address; pass a path to a memory object or an address."));
        return (addr, $"{path}.Address", null);
    }

    private static long? ReadAddress(object obj)
    {
        var p = obj.GetType().GetProperty("Address", BindingFlags.Public | BindingFlags.Instance);
        try { return p?.GetValue(obj) is long l ? l : null; } catch { return null; }
    }

    internal static long? ParseAddress(JToken? t)
    {
        if (t == null || t.Type == JTokenType.Null) return null;
        if (t.Type == JTokenType.Integer) return t.Value<long>();
        var s = t.ToString().Trim().Replace("`", "").Replace("_", "");
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        else if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)) return dec;
        return long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
    }

    /// <summary>The T of the first private CachedValue&lt;T&gt; / FrameCache&lt;T&gt; field (on the type or a base) whose T is a struct.</summary>
    private static Type? CachedStructType(Type t, out string? field)
    {
        for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            foreach (var f in cur.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                var ft = f.FieldType;
                for (var g = ft; g != null && g != typeof(object); g = g.BaseType)
                    if (g.IsGenericType && g.GetGenericArguments() is [var arg] && arg.IsValueType && !arg.IsPrimitive
                        && (g.Name.StartsWith("CachedValue", StringComparison.Ordinal) || g.Name.StartsWith("FrameCache", StringComparison.Ordinal)))
                    { field = f.Name; return arg; }
            }
        field = null;
        return null;
    }

    private static Type? FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            var t = asm.GetType(name, false);
            if (t != null) return t;
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray()!; }
            var hit = types.FirstOrDefault(x => x.IsValueType && x.Name == name && (x.Namespace?.StartsWith("GameOffsets", StringComparison.Ordinal) ?? false))
                      ?? types.FirstOrDefault(x => x.IsValueType && x.Name == name);
            if (hit != null) return hit;
        }
        return null;
    }

    private static int SizeOf(Type t)
    {
        try { return (int)typeof(System.Runtime.CompilerServices.Unsafe).GetMethod("SizeOf")!.MakeGenericMethod(t).Invoke(null, null)!; }
        catch { try { return Marshal.SizeOf(t); } catch { return 8; } }
    }

    internal static string Hex(long v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);
    private static string Num(double v) => double.IsFinite(v) ? v.ToString("0.####", CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);

    private static JArray HexDump(byte[] b)
    {
        var rows = new JArray();
        for (int i = 0; i < b.Length; i += 16)
        {
            int n = Math.Min(16, b.Length - i);
            var ascii = new StringBuilder(n);
            for (int j = 0; j < n; j++) ascii.Append(b[i + j] is >= 0x20 and < 0x7F ? (char)b[i + j] : '.');
            rows.Add(new JObject { ["off"] = i, ["bytes"] = BitConverter.ToString(b, i, n).Replace("-", " "), ["ascii"] = ascii.ToString() });
        }
        return rows;
    }

    /// <summary>Per-call view of the game process: module bounds and sections (from the PE header in memory), region queries, RTTI.</summary>
    private sealed class MemoryContext
    {
        private readonly dynamic _m;
        private readonly IntPtr _handle;
        private readonly Dictionary<long, (long start, long end, bool readable, string text)> _regions = new();
        private readonly List<(string name, long rva, long size)> _sections = new();
        public readonly long ModuleBase, ModuleSize, ImageBase;
        public readonly string ModuleName = "PathOfExile.exe";
        private readonly Dictionary<long, string?> _rtti = new();

        public MemoryContext(WhatsAnAiBridge plugin)
        {
            _m = plugin.GameController.Memory;
            try { _handle = (IntPtr)_m.OpenProcessHandle; } catch { _handle = IntPtr.Zero; }
            try { ModuleBase = (long)_m.AddressOfProcess; } catch { }
            try
            {
                var proc = (System.Diagnostics.Process)_m.Process;
                ModuleSize = proc.MainModule?.ModuleMemorySize ?? 0;
                ModuleName = proc.MainModule?.ModuleName ?? ModuleName;
            }
            catch { }
            ImageBase = ModuleBase;
            // PE header: e_lfanew at 0x3C; optional header ImageBase at PE+0x30; section table after the optional header.
            if (ModuleBase != 0 && Read(ModuleBase, 0x1000) is { } h && h[0] == 'M' && h[1] == 'Z')
            {
                int pe = BitConverter.ToInt32(h, 0x3C);
                if (pe > 0 && pe + 0x108 < h.Length && h[pe] == 'P' && h[pe + 1] == 'E')
                {
                    // The loader rewrites ImageBase in memory to the load address; Ghidra uses the file's value.
                    ImageBase = FileImageBase(_m) ?? 0x140000000;
                    if (ModuleSize == 0) ModuleSize = BitConverter.ToInt32(h, pe + 0x50);
                    int count = BitConverter.ToUInt16(h, pe + 6), optSize = BitConverter.ToUInt16(h, pe + 0x14);
                    int sec = pe + 0x18 + optSize;
                    for (int i = 0; i < count && sec + 40 <= h.Length; i++, sec += 40)
                        _sections.Add((Encoding.ASCII.GetString(h, sec, 8).TrimEnd('\0'), BitConverter.ToUInt32(h, sec + 12), BitConverter.ToUInt32(h, sec + 8)));
                }
            }
        }

        private static long? _fileImageBase;

        /// <summary>OptionalHeader.ImageBase from the exe on disk (cached for the session).</summary>
        private static long? FileImageBase(dynamic m)
        {
            if (_fileImageBase != null) return _fileImageBase;
            try
            {
                var path = ((System.Diagnostics.Process)m.Process).MainModule?.FileName;
                if (path == null) return null;
                using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                var h = new byte[0x400];
                fs.ReadExactly(h, 0, h.Length);
                int pe = BitConverter.ToInt32(h, 0x3C);
                if (pe <= 0 || pe + 0x38 > h.Length || h[pe] != 'P') return null;
                _fileImageBase = BitConverter.ToInt64(h, pe + 0x30);
                return _fileImageBase;
            }
            catch { return null; }
        }

        public JObject ModuleInfo() => new()
        {
            ["name"] = ModuleName, ["base"] = Hex(ModuleBase), ["size"] = Hex(ModuleSize), ["imageBase"] = Hex(ImageBase),
            ["note"] = "ghidra = imageBase + rva (the address in a Ghidra project of the exe at its preferred base)",
        };

        public byte[]? Read(long address, int size)
        {
            if (address <= 0 || !Readable(address)) return null;
            try { var b = (byte[])_m.ReadBytes(address, size); return b is { Length: > 0 } ? b : null; } catch { return null; }
        }

        public bool InModule(long v, out string? section, out long rva)
        {
            section = null; rva = v - ModuleBase;
            if (ModuleBase == 0 || v < ModuleBase || v >= ModuleBase + ModuleSize) return false;
            foreach (var (name, srva, ssize) in _sections)
                if (rva >= srva && rva < srva + ssize) { section = name; break; }
            return true;
        }

        public bool Readable(long a) => Region(a).readable;
        public string RegionText(long a) => Region(a).text;

        private (long start, long end, bool readable, string text) Region(long a)
        {
            foreach (var known in _regions.Values) if (a >= known.start && a < known.end) return known;
            if (_handle == IntPtr.Zero) return (a, a + 1, true, "region unknown");
            if (VirtualQueryEx(_handle, (IntPtr)a, out var mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION64>()) == IntPtr.Zero)
            {
                // The HUD's handle may lack PROCESS_QUERY_INFORMATION: probe one byte with ReadProcessMemory (VM_READ only).
                var probe = new byte[1];
                var ok = ReadProcessMemory(_handle, (IntPtr)a, probe, (IntPtr)1, out _);
                // Cache per 4 KB page so a region read doesn't probe every slot.
                long page = a & ~0xFFFL;
                var r0 = (page, page + 0x1000, ok, ok ? "readable (region details unavailable)" : "not readable");
                _regions[page] = r0;
                return r0;
            }
            long start = (long)mbi.BaseAddress, end = start + (long)mbi.RegionSize;
            const uint MEM_COMMIT = 0x1000, PAGE_NOACCESS = 0x01, PAGE_GUARD = 0x100;
            bool readable = mbi.State == MEM_COMMIT && (mbi.Protect & PAGE_NOACCESS) == 0 && (mbi.Protect & PAGE_GUARD) == 0 && mbi.Protect != 0;
            var text = $"{(mbi.State == MEM_COMMIT ? "committed" : mbi.State == 0x2000 ? "reserved" : "free")} {Protect(mbi.Protect)} {(mbi.Type == 0x1000000 ? "image" : mbi.Type == 0x40000 ? "mapped" : "private")} {Hex(start)}+{Hex(end - start)}";
            var r = (start, end, readable, text);
            _regions[start] = r;
            return r;
        }

        private static string Protect(uint p) => (p & 0xFF) switch
        {
            0x02 => "R", 0x04 => "RW", 0x08 => "WC", 0x10 => "X", 0x20 => "RX", 0x40 => "RWX", 0x80 => "WCX", 0x01 => "NA", _ => $"0x{p:X}",
        };

        /// <summary>MSVC x64 RTTI: vtable[-1] = CompleteObjectLocator; COL+12 = TypeDescriptor RVA; name at TD+16 (".?AVName@ns@@").</summary>
        public string? RttiName(long vtable)
        {
            if (_rtti.TryGetValue(vtable, out var cached)) return cached;
            string? result = null;
            try
            {
                if (Read(vtable - 8, 8) is { } colp)
                {
                    long col = BitConverter.ToInt64(colp, 0);
                    if (InModule(col, out _, out _) && Read(col, 24) is { } c && BitConverter.ToInt32(c, 0) == 1)
                    {
                        long td = ModuleBase + BitConverter.ToInt32(c, 12);
                        if (Read(td + 16, 128) is { } nb)
                        {
                            int len = Array.IndexOf(nb, (byte)0);
                            var raw = Encoding.ASCII.GetString(nb, 0, len < 0 ? nb.Length : len);
                            if (raw.StartsWith(".?A", StringComparison.Ordinal)) result = Demangle(raw);
                        }
                    }
                }
            }
            catch { }
            _rtti[vtable] = result;
            return result;
        }

        private static string Demangle(string raw)
        {
            var body = raw.Length > 4 ? raw[4..] : raw; // .?AV / .?AU
            var parts = body.Split('@', StringSplitOptions.RemoveEmptyEntries);
            Array.Reverse(parts);
            return string.Join("::", parts);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION64
        {
            public ulong BaseAddress, AllocationBase;
            public uint AllocationProtect, Alignment1;
            public ulong RegionSize;
            public uint State, Protect, Type, Alignment2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION64 lpBuffer, IntPtr dwLength);
    }
}
