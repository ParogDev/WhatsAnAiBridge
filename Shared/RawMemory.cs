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
            // The links are declared (and preflighted at load) in HudLinks.cs.
            var mem = (object)GameController.Memory;
            if (HudLinks.MemoryBackend.FieldOn(mem.GetType()) is not { } backendField)
            {
                LogFault($"[RawMemory] {HudLinks.MemoryBackend.Broken(mem.GetType())}: using Memory.Read");
                return null;
            }
            var paged = backendField.GetValue(mem);
            object? leaf = paged;
            if (paged != null)
            {
                if (HudLinks.PagedInner.FieldOn(paged.GetType()) is { } innerField) leaf = innerField.GetValue(paged) ?? paged;
                // No inner backend: read from the backend itself. Fine when it is the leaf (caching disabled); when it is the
                // paged one the link broke, and the reads go through its page cache (allocating pages): said, never silent.
                else if (paged.GetType().Name.Contains("Paged", StringComparison.Ordinal))
                    LogFault($"[RawMemory] {HudLinks.PagedInner.Broken(paged.GetType())}: reading through {paged.GetType().Name} itself, i.e. its page cache (RawRead allocates pages again)");
                else
                    LogQuiet($"[RawMemory] Memory._backend is {paged.GetType().Name}, no wrapped backend: reading from it directly");
            }
            var m = leaf == null ? null : HudLinks.BackendRead.MethodOn(leaf.GetType());
            if (leaf != null && m != null) _rawRead = (RawReadFn)Delegate.CreateDelegate(typeof(RawReadFn), leaf, m);
            else LogFault($"[RawMemory] {(leaf == null ? "Memory._backend is null" : HudLinks.BackendRead.Broken(leaf.GetType()))}: using Memory.Read");
        }
        catch (Exception ex) { LogFault($"[RawMemory] leaf backend not reachable, using Memory.Read: {ex.Message}"); }
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
