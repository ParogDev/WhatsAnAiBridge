using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Two more lanes on the observer's timeline (same clock, t and frame), so a change in game state isn't mistaken for
/// something the HUD or an agent did:
///   hud    the HUD's own hiccups: a frame much longer than the recent ones ({cause: spike, intervalMs, gcMs, gen0..2}),
///          and plugin reloads ({cause: reload, plugin, ok, durationMs}). Spikes are capped at one event per 2 s; the
///          ones in between are counted in the next event's "suppressed".
///   agent  what an agent asked of the HUD or the user: guide cards and log lines, highlights, flows, experiments,
///          reloads, settings, focus, scripts, measurements ({method, params} with long values clipped and secrets
///          redacted). "The user clicked what we asked" lines up with what changed.
/// Recorded only while observing is on.
/// </summary>
public partial class WhatsAnAiBridge
{
    private long _obsFrameAt;                      // Stopwatch timestamp of the previous observed frame, 0 = none
    private TimeSpan _obsGcPause;
    private readonly int[] _obsGcCount = new int[3];
    private double _obsFrameAvg;                   // moving average of the frame interval, ms
    private double _obsHudLastT = double.MinValue;
    private int _obsHudSuppressed;
    private const double HudSpikeMinMs = 50, HudSpikeFactor = 2.5, HudGapMs = 1500, HudSpikeEveryMs = 2000;

    /// <summary>Once per observed frame: a frame much longer than recent ones becomes a hud event with its GC share.</summary>
    private void ObserveHudFrame()
    {
        var ts = System.Diagnostics.Stopwatch.GetTimestamp();
        var pause = GC.GetTotalPauseDuration();
        Span<int> gc = stackalloc int[3];
        for (var g = 0; g < 3; g++) gc[g] = GC.CollectionCount(g);
        if (_obsFrameAt != 0)
        {
            var interval = System.Diagnostics.Stopwatch.GetElapsedTime(_obsFrameAt, ts).TotalMilliseconds;
            // A gap over 1.5 s is the overlay hidden (Render doesn't run) or a loading screen (the area event covers it).
            if (interval < HudGapMs)
            {
                var avg = _obsFrameAvg <= 0 ? interval : _obsFrameAvg;
                if (interval >= HudSpikeMinMs && interval >= avg * HudSpikeFactor)
                {
                    var t = ObsClock.Elapsed.TotalMilliseconds;
                    if (t - _obsHudLastT < HudSpikeEveryMs) _obsHudSuppressed++;
                    else
                    {
                        _obsHudLastT = t;
                        ObsEmit(new JObject
                        {
                            ["kind"] = "hud", ["cause"] = "spike", ["intervalMs"] = Math.Round(interval, 1), ["typicalMs"] = Math.Round(avg, 1),
                            ["gcMs"] = Math.Round((pause - _obsGcPause).TotalMilliseconds, 1),
                            ["gen0"] = gc[0] - _obsGcCount[0], ["gen1"] = gc[1] - _obsGcCount[1], ["gen2"] = gc[2] - _obsGcCount[2],
                            ["suppressed"] = _obsHudSuppressed > 0 ? _obsHudSuppressed : null,
                        });
                        _obsHudSuppressed = 0;
                    }
                }
                else _obsFrameAvg = avg + (interval - avg) * 0.05;   // spikes don't raise the baseline
            }
        }
        _obsFrameAt = ts;
        _obsGcPause = pause;
        for (var g = 0; g < 3; g++) _obsGcCount[g] = gc[g];
    }

    /// <summary>A hud event from elsewhere in the bridge (plugin reloads), when observing.</summary>
    private void ObsHud(JObject e)
    {
        if (!Obs().Enabled) return;
        e["kind"] = "hud";
        ObsEmit(e);
    }

    /// <summary>Bridge methods that change what the user sees or what the HUD does: their calls go on the agent lane.</summary>
    private static readonly HashSet<string> AgentMethods =
    [
        "guide.set", "guide.log", "guide.highlight", "guide.highlight_advance", "guide.flow",
        "experiment.queue", "experiment.start", "experiment.cancel",
        "hud.reload_plugin", "settings.set", "focus.game", "script.run", "gc.pool", "lab.set",
        "pipeline.trace", "profile.plugin", "tracker.start", "tracker.stop",
        "stats.select", "stats.set_filter", "stats.set_pinned", "stats.set_view",
        "observe.layer_set", "observe.layer_remove",
    ];

    /// <summary>Called for every bridge request: the ones in AgentMethods become agent events while observing.</summary>
    private void ObsAgent(string method, JToken? p)
    {
        if (!AgentMethods.Contains(method) || !Obs().Enabled || !GameController.InGame) return;
        var args = p is JObject o ? (JObject)o.DeepClone() : new JObject();
        args.Remove("token");
        if (method == "settings.set" && args["path"]?.ToString() is { } path && IsSecret(path, args["value"]?.ToString()))
            args["value"] = "[redacted]";
        foreach (var v in args.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String).ToList())
            if (v.Value<string>() is { Length: > 160 } s) v.Value = s[..160] + "...";
        var text = args.ToString(Formatting.None);
        ObsEmit(new JObject
        {
            ["kind"] = "agent", ["method"] = method,
            ["params"] = text.Length <= 1200 ? args : new JObject { ["clipped"] = text[..1200] + "..." },
        });
    }
}
