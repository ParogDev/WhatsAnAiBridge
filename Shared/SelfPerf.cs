using System;
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

    private void SelfEnd(int step, (long t, long a) s)
    {
        _selfTicks[step] += Stopwatch.GetTimestamp() - s.t;
        _selfBytes[step] += GC.GetAllocatedBytesForCurrentThread() - s.a;
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
        };
        Array.Clear(_selfTicks); Array.Clear(_selfBytes); _selfFrames = 0; _selfSince = Stopwatch.GetTimestamp();
        return o.ToString(Newtonsoft.Json.Formatting.None);
    }
}
