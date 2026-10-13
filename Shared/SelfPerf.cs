using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// The bridge's own cost per Render step (time and bytes allocated), always on and allocation-free: two counter reads
/// around each step. The plugin profiler can't profile the bridge itself, so this is how we keep our own per-frame
/// work honest. bridge.self_perf -> averages per frame since the previous call (then resets).
/// </summary>
public partial class WhatsAnAiBridge
{
    private static readonly string[] SelfSteps =
        ["reload", "script", "experimentQueue", "observer", "flow", "ipcAndRecording", "statusHud", "statsPanel", "guidePanel", "highlights", "renderLab", "tracker"];
    private readonly long[] _selfTicks = new long[SelfSteps.Length], _selfBytes = new long[SelfSteps.Length];
    private long _selfFrames, _selfSince = Stopwatch.GetTimestamp();

    private static (long t, long a) SelfStart() => (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

    // Bridge requests (eval, explore, ...) per kind: they run outside the Render steps (Tick, or the watchdog thread),
    // one at a time under _requestLock. Key: the method, "query:<type>" for queries (eval, describe, entities...).
    private readonly Dictionary<string, long[]> _selfRequests = new(StringComparer.Ordinal);

    private string? _selfRequestsBroken;   // why request accounting failed last (self_perf "requestsBroken")

    private void SelfRequest(string method, JToken? p, (long t, long a) s, bool failed)
    {
        try
        {
            var ticks = Stopwatch.GetTimestamp() - s.t;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - s.a;
            var key = method;
            if (method == "query" && p is JObject o && o["type"] is JValue { Type: JTokenType.String } t)
            {
                var type = (string)t!;
                var colon = type.IndexOf(':');
                key = "query:" + (colon < 0 ? type : type[..colon]);
            }
            lock (_selfRequests)
            {
                // Keys come from callers: past 64 kinds (nobody polled self_perf), the rest count as "other".
                if (!_selfRequests.ContainsKey(key) && _selfRequests.Count >= 64) key = "other";
                if (!_selfRequests.TryGetValue(key, out var row)) _selfRequests[key] = row = new long[4];
                row[0]++; row[1] += ticks; row[2] += bytes;
                if (failed) row[3]++;
            }
        }
        catch (Exception ex) { _selfRequestsBroken = $"{method}: {ex.GetType().Name}: {ex.Message}"; }   // never fails a request; named in self_perf
    }

    private void SelfEnd(int step, (long t, long a) s)
    {
        _selfTicks[step] += Stopwatch.GetTimestamp() - s.t;
        _selfBytes[step] += GC.GetAllocatedBytesForCurrentThread() - s.a;
    }

    // The observer step split into its parts (self_perf "observerParts"), plus each layer's cost ("layers").
    private static readonly string[] ObsParts = ["hudFrame", "layers", "uiCatalogue", "area", "panels", "entities", "flush"];
    private readonly long[] _obsPartTicks = new long[ObsParts.Length], _obsPartBytes = new long[ObsParts.Length];

    private void ObsPartEnd(int part, (long t, long a) s)
    {
        _obsPartTicks[part] += Stopwatch.GetTimestamp() - s.t;
        _obsPartBytes[part] += GC.GetAllocatedBytesForCurrentThread() - s.a;
    }

    private string? ProcessSelfPerfMethod(string method, JToken? p)
    {
        if (method != "bridge.self_perf") return null;
        var frames = Math.Max(1, _selfFrames);
        var secs = (Stopwatch.GetTimestamp() - _selfSince) / (double)Stopwatch.Frequency;
        var steps = new JObject(Enumerable.Range(0, SelfSteps.Length)
            .OrderByDescending(i => _selfTicks[i])
            .Select(i => new JProperty(SelfSteps[i], new JObject
            {
                ["usPerFrame"] = Math.Round(_selfTicks[i] * 1e6 / Stopwatch.Frequency / frames, 1),
                ["bytesPerFrame"] = _selfBytes[i] / frames,
            })));
        var o = new JObject
        {
            ["frames"] = _selfFrames, ["seconds"] = Math.Round(secs, 1),
            ["totalUsPerFrame"] = Math.Round(_selfTicks.Sum() * 1e6 / Stopwatch.Frequency / frames, 1),
            ["totalBytesPerFrame"] = _selfBytes.Sum() / frames,
            ["steps"] = steps,
            ["observerParts"] = new JObject(Enumerable.Range(0, ObsParts.Length).Select(i => new JProperty(ObsParts[i], new JObject
            {
                ["usPerFrame"] = Math.Round(_obsPartTicks[i] * 1e6 / Stopwatch.Frequency / frames, 2),
                ["bytesPerFrame"] = _obsPartBytes[i] / frames,
            }))),
            ["layers"] = LayersSelfPerf(),
            ["statsRawCheck"] = JObject.FromObject(StatsRawStatus()),
            ["hudState"] = HudStateSelfPerf(),   // the phase tracker (HudState.cs): reads, writes, the broken link
        };
        lock (_selfRequests)
        {
            // Bridge requests since the previous call (outside the per-frame steps): calls (those that threw counted in
            // errors, their time included), average us and bytes per call.
            o["requests"] = new JObject(_selfRequests.OrderByDescending(kv => kv.Value[1]).Select(kv =>
            {
                var r = new JObject
                {
                    ["calls"] = kv.Value[0],
                    ["usPerCall"] = Math.Round(kv.Value[1] * 1e6 / Stopwatch.Frequency / kv.Value[0], 1),
                    ["bytesPerCall"] = kv.Value[2] / kv.Value[0],
                };
                if (kv.Value[3] > 0) r["errors"] = kv.Value[3];
                return new JProperty(kv.Key, r);
            }));
            _selfRequests.Clear();
        }
        if (_selfRequestsBroken != null) o["requestsBroken"] = _selfRequestsBroken;
        o["memberIndex"] = MemberIndex.Status();   // the reflection core (MemberIndex.cs): compiled getters, failures, type index
        Array.Clear(_selfTicks); Array.Clear(_selfBytes); Array.Clear(_obsPartTicks); Array.Clear(_obsPartBytes); _selfFrames = 0; _selfSince = Stopwatch.GetTimestamp();
        return o.ToString(Newtonsoft.Json.Formatting.None);
    }
}
