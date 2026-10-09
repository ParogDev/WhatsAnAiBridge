using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// gc.pool {maxArraysPerPartition?, maxBucketBytes?}: reports, and on request raises, how many arrays per partition
/// ArrayPool&lt;byte&gt;.Shared keeps for its small buckets. The HUD's page cache returns ~1,900 4 KB pages per frame to
/// that pool, which keeps 16 x 32 = 512, so most become garbage and the GC pauses ~8 times a second (every frame-time
/// spike; scaffolding research/hud-gc.md). DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXARRAYSPERPARTITION=256 fixes it
/// at startup; this does the same at runtime, so the effect can be measured without restarting the HUD elevated.
///
/// How: each partition (SharedArrayPool's Partitions._partitions[i]) caps itself by the length of its readonly
/// _arrays field and takes Monitor.Enter(this) around every push and pop. Under that same lock the array is replaced
/// by a longer copy. Only buckets up to maxBucketBytes (default 16 KB) are touched, so pooled memory stays bounded
/// (16 partitions x 256 x 16 KB at most). maxArraysPerPartition=32 puts the default back (arrays beyond it are
/// dropped). Behind 'Allow HUD Instrumentation'. Every missing internal is named (fail at the broken link).
/// </summary>
public partial class WhatsAnAiBridge
{
    private string? ProcessGcPoolMethod(string method, JToken? p)
    {
        if (method != "gc.pool") return null;
        return SafeMemory(() => GcPool(p));
    }

    private JObject GcPool(JToken? p)
    {
        var want = p?["maxArraysPerPartition"]?.Value<int?>();
        var maxBucket = Math.Clamp(p?["maxBucketBytes"]?.Value<int?>() ?? 16384, 16, 1 << 20);
        if (want != null && !Settings.AllowHudInstrumentation.Value)
            return Err("instrumentation_disabled", "Enable 'Allow HUD Instrumentation' in the bridge settings (Dev Loop) to change the pool.");
        if (want is < 8 or > 4096) return Err("bad_request", "maxArraysPerPartition must be 8-4096 (the runtime default is 32).");

        const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        if (pool.GetType().GetField("_buckets", inst)?.GetValue(pool) is not Array buckets)
            return Err("broken", $"{pool.GetType().Name} has no _buckets field (runtime {Environment.Version}): the pool layout changed");

        var rows = new JArray();
        var changed = 0;
        for (var i = 0; i < buckets.Length; i++)
        {
            var size = 16 << i;
            var bucket = buckets.GetValue(i);
            if (bucket == null) continue;   // created on first use
            if (bucket.GetType().GetField("_partitions", inst)?.GetValue(bucket) is not Array parts)
                return Err("broken", $"{bucket.GetType().Name} has no _partitions field: the pool layout changed");
            int cap = 0, pooled = 0;
            foreach (var part in parts)
            {
                var arraysField = part.GetType().GetField("_arrays", inst);
                var countField = part.GetType().GetField("_count", inst);
                if (arraysField == null || countField == null)
                    return Err("broken", $"{part.GetType().Name} has no _arrays/_count field: the pool layout changed");
                Monitor.Enter(part);   // the lock the pool itself takes around push and pop
                try
                {
                    var arrays = (Array)arraysField.GetValue(part)!;
                    var count = (int)countField.GetValue(part)!;
                    if (want is { } w && size <= maxBucket && arrays.Length != w)
                    {
                        var bigger = Array.CreateInstance(arrays.GetType().GetElementType()!, w);
                        var keep = Math.Min(count, w);
                        Array.Copy(arrays, bigger, keep);
                        arraysField.SetValue(part, bigger);
                        if (keep != count) countField.SetValue(part, keep);
                        arrays = bigger; count = keep; changed++;
                    }
                    cap += arrays.Length; pooled += count;
                }
                finally { Monitor.Exit(part); }
            }
            rows.Add(new JObject { ["bytes"] = size, ["partitions"] = parts.Length, ["capacity"] = cap, ["pooledNow"] = pooled });
        }
        return new JObject
        {
            ["runtime"] = Environment.Version.ToString(),
            ["envMaxArraysPerPartition"] = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXARRAYSPERPARTITION"),
            ["partitionsChanged"] = changed,
            ["maxBucketBytes"] = maxBucket,
            ["buckets"] = rows,
            ["note"] = want == null ? "report only: pass maxArraysPerPartition to change it (256 = the fix, 32 = the runtime default)" : "applied until the HUD restarts",
        };
    }
}

public partial class WhatsAnAiBridge
{
    /// <summary>How many arrays of this size ArrayPool&lt;byte&gt;.Shared can keep right now (all partitions), or null.</summary>
    private static int? PoolCapacity(int bytes)
    {
        try
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            if (pool.GetType().GetField("_buckets", inst)?.GetValue(pool) is not Array buckets) return null;
            var i = System.Numerics.BitOperations.Log2((uint)bytes) - 4;
            if (i < 0 || i >= buckets.Length || buckets.GetValue(i) is not { } bucket) return null;
            if (bucket.GetType().GetField("_partitions", inst)?.GetValue(bucket) is not Array parts) return null;
            var cap = 0;
            foreach (var part in parts) cap += (part.GetType().GetField("_arrays", inst)?.GetValue(part) as Array)?.Length ?? 0;
            return cap;
        }
        catch { return null; }
    }
}
