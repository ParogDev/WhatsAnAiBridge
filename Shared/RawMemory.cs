using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WhatsAnAiBridge;

/// <summary>
/// Allocation-free reads of a few bytes, straight from the HUD's leaf memory backend (the one that calls into the game
/// process), skipping its page cache. Memory.Read&lt;T&gt; on an address whose page isn't cached this frame rents a fresh
/// 4 KB page, and most rented pages end up as garbage (scaffolding research/hud-gc.md): reading one flag of 124 UI
/// elements that way allocated 362 KB. Use this for small, scattered, per-tick reads. Falls back to Memory.Read when
/// the backend can't be reached (HUD build changed: logged once).
/// </summary>
public partial class WhatsAnAiBridge
{
    private delegate bool RawReadFn(IntPtr address, Span<byte> target);
    private RawReadFn? _rawRead;
    private bool _rawReadTried;

    private RawReadFn? RawReader()
    {
        if (_rawReadTried) return _rawRead;
        _rawReadTried = true;
        try
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var mem = (object)GameController.Memory;
            var paged = mem.GetType().GetField("_backend", inst)?.GetValue(mem);
            var leaf = paged?.GetType().GetFields(inst).FirstOrDefault(f => f.FieldType.Name == "IMemoryBackend")?.GetValue(paged) ?? paged;
            var m = leaf?.GetType().GetMethod("TryReadMemory", [typeof(IntPtr), typeof(Span<byte>)]);
            if (leaf != null && m != null) _rawRead = (RawReadFn)Delegate.CreateDelegate(typeof(RawReadFn), leaf, m);
        }
        catch (Exception ex) { LogError($"[RawMemory] leaf backend not reachable, using Memory.Read: {ex.Message}"); }
        return _rawRead;
    }

    /// <summary>One unmanaged value read without allocating (stack buffer, no page cache).</summary>
    private T RawRead<T>(long address) where T : unmanaged
    {
        if (RawReader() is { } read)
        {
            Span<byte> buf = stackalloc byte[Marshal.SizeOf<T>()];
            if (read(new IntPtr(address), buf)) return MemoryMarshal.Read<T>(buf);
        }
        return GameController.Memory.Read<T>(address);
    }
}
