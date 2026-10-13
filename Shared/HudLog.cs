using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace WhatsAnAiBridge;

/// <summary>
/// Where the bridge's own messages go. The HUD's top-left text (DebugWindow) is the player's screen, not our console:
/// the user found it far too busy while playing (2026-10-12). So:
///   - <see cref="LogQuiet"/>: routine diagnostics (connections, thread hand-overs, reloads that worked). HUD log file
///     only, at Information; agents read it with MCP hud_log. Never drawn.
///   - <see cref="LogFault"/>: something broke. HUD log file at Error, full text (stack traces included), so hud_log
///     level=error and hud_plugins' runtime error counts still find it (fail at the broken link). Repeats of the same
///     fault are written at most once per <see cref="FaultRepeatSec"/> with how many were skipped. Never drawn.
///   - <see cref="ToastFault"/>: a fault the player should notice (agents can't reach the HUD, F8 feedback is off):
///     <see cref="LogFault"/> plus one error toast on the guide card's toast stack, once per load and key.
/// The file is written through the HUD's own Serilog logger (Core.Logger, the one DebugWindow writes to), found by
/// reflection so the bridge needs no Serilog reference (the CI reference set has none). When that link is broken
/// the message falls back to the old on-screen line, naming the break once: nothing is swallowed.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const double FaultRepeatSec = 60;
    private const string LogPrefix = "[Whats An AI Bridge] ";

    private static object? _fileLogger;
    private static MethodInfo? _fileInfo, _fileError;
    private static string? _fileLogBroken;
    private static bool _fileLogBrokenShown;

    private readonly ConcurrentDictionary<string, (DateTime at, int skipped)> _faultSeen = new();
    private readonly ConcurrentDictionary<string, byte> _faultToasted = new();

    /// <summary>Routine diagnostics: the HUD log file only (hud_log), never the player's screen.</summary>
    internal void LogQuiet(string msg)
    {
        if (!WriteLogFile(false, LogPrefix + msg)) LogMessage(msg);
    }

    /// <summary>
    /// Something broke: the HUD log file at Error (hud_log level=error), full text, never the player's screen.
    /// <paramref name="key"/> groups repeats (default: the message); a repeat within <see cref="FaultRepeatSec"/> is
    /// counted and reported with the next one written.
    /// </summary>
    internal void LogFault(string msg, string? key = null)
    {
        key ??= msg;
        var now = DateTime.UtcNow;
        var skipped = 0;
        if (_faultSeen.TryGetValue(key, out var seen))
        {
            if ((now - seen.at).TotalSeconds < FaultRepeatSec)
            {
                _faultSeen[key] = (seen.at, seen.skipped + 1);
                return;
            }
            skipped = seen.skipped;
        }
        if (_faultSeen.Count > 500) _faultSeen.Clear();   // keys are message texts: never let them pile up
        _faultSeen[key] = (now, 0);
        var text = skipped > 0 ? $"{msg} (and {skipped} more like it in the last {FaultRepeatSec:0} s)" : msg;
        if (!WriteLogFile(true, LogPrefix + text)) LogError(text);
    }

    /// <summary>
    /// A fault the player should notice: logged like <see cref="LogFault"/>, plus one toast of <paramref name="kind"/>
    /// (error | warn; title up to 40 chars, text up to 200) the first time this load. <paramref name="holdSec"/> is how
    /// long the toast stays.
    /// </summary>
    internal void ToastFault(string key, string title, string toast, string msg, string kind = "error", double holdSec = 8)
    {
        LogFault(msg);   // keyed by its own text: two faults sharing one toast are both written
        if (!_faultToasted.TryAdd(key, 0)) return;
        // One restart fixes both: when agents can't connect at all, the F8 toast would say the same thing twice.
        if (key == "f8-register" && _faultToasted.ContainsKey("tcp-start")) return;
        try { GuideLog(new Newtonsoft.Json.Linq.JObject { ["text"] = toast, ["kind"] = kind, ["title"] = title, ["holdSec"] = holdSec }); }
        catch (Exception ex) { LogFault($"[Toast] '{title}' could not be posted: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Write one line to the HUD's log file. False: the logger link is broken (named once on screen).</summary>
    private bool WriteLogFile(bool error, string text)
    {
        try
        {
            if (_fileLogger == null && _fileLogBroken == null) BindFileLogger();
            if (_fileLogger != null)
            {
                // Template "{M:l}" with the text as its value: braces in the text are never parsed as a template.
                (error ? _fileError : _fileInfo)!.Invoke(_fileLogger, new object[] { "{M:l}", new object[] { text } });
                return true;
            }
        }
        catch (Exception ex) { _fileLogBroken = $"writing to Core.Logger: {ex.GetType().Name}: {ex.Message}"; _fileLogger = null; }
        if (!_fileLogBrokenShown)
        {
            _fileLogBrokenShown = true;
            LogError($"[Log] the HUD log file can't be written ({_fileLogBroken}): bridge messages go to the screen instead");
        }
        return false;
    }

    private static void BindFileLogger()
    {
        var prop = typeof(Core).GetProperty("Logger", BindingFlags.Public | BindingFlags.Static);
        if (prop == null) { _fileLogBroken = "Core.Logger: no such static property in this HUD build"; return; }
        var logger = prop.GetValue(null);
        if (logger == null) return;   // not set yet (not now): try again next message
        var iface = prop.PropertyType;   // Serilog.ILogger
        var args = new[] { typeof(string), typeof(object[]) };
        var info = iface.GetMethod("Information", args);
        var err = iface.GetMethod("Error", args);
        if (info == null || err == null) { _fileLogBroken = $"{iface.FullName}: no Information/Error(string, object[])"; return; }
        _fileInfo = info;
        _fileError = err;
        _fileLogger = logger;
    }
}
