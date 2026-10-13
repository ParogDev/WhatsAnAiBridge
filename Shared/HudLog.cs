using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace WhatsAnAiBridge;

/// <summary>
/// Where the bridge's own messages go. The HUD's top-left text (DebugWindow) is the player's screen, not our console:
/// the user found it far too busy while playing (2026-10-12). So:
///   - <see cref="LogQuiet"/>: routine diagnostics (connections, thread hand-overs, reloads that worked and their
///     warnings). HUD log file only, at Information; agents read it with MCP hud_log. Never drawn.
///   - <see cref="LogFault"/>: something broke. HUD log file at Error, full text (stack traces included), so hud_log
///     level=error and hud_plugins' runtime error counts still find it (fail at the broken link). The same fault again
///     within <see cref="FaultRepeatSec"/> (a per-frame exception) is counted and its count written with the next one,
///     as DebugWindow collapsed repeats while a line was up. Never drawn.
///   - <see cref="ToastFault"/>: a fault the player should notice (agents can't reach the HUD, F8 feedback is off):
///     <see cref="LogFault"/> plus one toast on the guide card's toast stack, once per load and key.
/// The file is written through the HUD's own Serilog logger (Core.Logger, the one DebugWindow writes to), found by
/// reflection so the bridge needs no Serilog reference (the CI reference set has none). While that logger is not set
/// yet (not now) a message falls back to the old on-screen line; when the link is broken (no such member, a write
/// throws) the same fallback applies and the break is named once: nothing is swallowed.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const double FaultRepeatSec = 5;
    private const int FaultKeysMax = 256;
    private const string LogPrefix = "[Whats An AI Bridge] ";

    private static readonly object FileLogLock = new();
    private static object? _fileLogger;
    private static MethodInfo? _fileInfo, _fileError;
    private static string? _fileLogBroken;
    private static bool _fileLogBrokenShown;

    private readonly object _faultLock = new();
    private readonly Dictionary<string, (DateTime at, int skipped)> _faultSeen = new();
    private readonly HashSet<string> _faultToasted = new();

    /// <summary>Routine diagnostics: the HUD log file only (hud_log), never the player's screen.</summary>
    internal void LogQuiet(string msg)
    {
        if (!WriteLogFile(false, LogPrefix + msg)) LogMessage(msg);
    }

    /// <summary>
    /// Something broke: the HUD log file at Error (hud_log level=error), full text, never the player's screen.
    /// <paramref name="key"/> groups repeats (default: the message; pass a stable key when the text carries ids or
    /// ports); a repeat within <see cref="FaultRepeatSec"/> is counted and reported with the next one written.
    /// </summary>
    internal void LogFault(string msg, string? key = null)
    {
        key ??= msg;
        var now = DateTime.UtcNow;
        int skipped;
        lock (_faultLock)
        {
            if (_faultSeen.TryGetValue(key, out var seen) && (now - seen.at).TotalSeconds < FaultRepeatSec)
            {
                _faultSeen[key] = (seen.at, seen.skipped + 1);
                return;
            }
            skipped = seen.skipped;
            if (_faultSeen.Count >= FaultKeysMax)   // keys are message texts: drop the expired ones, never the live ones
                foreach (var k in _faultSeen.Where(kv => (now - kv.Value.at).TotalSeconds >= FaultRepeatSec).Select(kv => kv.Key).ToList())
                    _faultSeen.Remove(k);
            _faultSeen[key] = (now, 0);
        }
        var text = skipped > 0 ? $"{msg} (after {skipped} more like it within {FaultRepeatSec:0} s of the previous one)" : msg;
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
        lock (_faultLock)
        {
            // One restart fixes both: when agents can't connect at all, the F8 toast would say the same thing twice.
            if (key == ToastKeyF8 && _faultToasted.Contains(ToastKeyTcp)) return;
            if (!_faultToasted.Add(key)) return;
        }
        try { GuideLog(new Newtonsoft.Json.Linq.JObject { ["text"] = toast, ["kind"] = kind, ["title"] = title, ["holdSec"] = holdSec }); }
        catch (Exception ex) { LogFault($"[Toast] '{title}' could not be posted: {ex.GetType().Name}: {ex.Message}"); }
    }

    // The toasts ToastFault posts (wording reviewed by Fable, 2026-10-12).
    private const string ToastKeyTcp = "tcp-start", ToastKeyF8 = "f8-register";
    private void ToastTcpStartFailed(string msg) =>
        ToastFault(ToastKeyTcp, "Agents can't connect", "The agent link didn't start. Restart the HUD to fix it.", msg);
    private void ToastF8RegisterFailed(string msg) =>
        ToastFault(ToastKeyF8, "F8 feedback is off", "F8 can't reach the agents. Restart the HUD to bring it back.", msg, "warn");

    /// <summary>Write one line to the HUD's log file. False: no logger now, or the link is broken (named once).</summary>
    private bool WriteLogFile(bool error, string text)
    {
        object? logger;
        MethodInfo? write;
        lock (FileLogLock)
        {
            if (_fileLogger == null && _fileLogBroken == null) BindFileLogger();
            logger = _fileLogger;
            write = error ? _fileError : _fileInfo;
        }
        if (logger != null && write != null)
        {
            try
            {
                // Template "{M:l}" with the text as its value: braces in the text are never parsed as a template.
                write.Invoke(logger, new object[] { "{M:l}", new object[] { text } });
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                lock (FileLogLock) { _fileLogBroken = $"writing to Core.Logger: {inner.GetType().Name}: {inner.Message}"; _fileLogger = null; }
            }
        }
        string? broken;
        lock (FileLogLock)
        {
            broken = _fileLogBroken;
            if (broken == null || _fileLogBrokenShown) return false;   // not set yet (not now), or already named
            _fileLogBrokenShown = true;
        }
        LogError($"[Log] the HUD log file can't be written ({broken}): bridge messages go to the screen instead");
        return false;
    }

    /// <summary>Find Core.Logger and its Information / Error(string, object[]). Sets _fileLogBroken naming the link
    /// when one is missing; leaves everything null while the logger is just not set yet.</summary>
    private static void BindFileLogger()
    {
        var prop = typeof(Core).GetProperties(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(p => p.Name == "Logger");
        if (prop == null) { _fileLogBroken = "Core.Logger: no such public static property in this HUD build"; return; }
        var logger = prop.GetValue(null);
        if (logger == null) return;
        var args = new[] { typeof(string), typeof(object[]) };
        var types = new[] { prop.PropertyType }.Concat(prop.PropertyType.GetInterfaces()).Append(logger.GetType());
        MethodInfo? info = null, err = null;
        foreach (var t in types)
        {
            info ??= t.GetMethod("Information", args);
            err ??= t.GetMethod("Error", args);
        }
        if (info == null || err == null) { _fileLogBroken = $"{prop.PropertyType.FullName}: no Information/Error(string, object[])"; return; }
        _fileInfo = info;
        _fileError = err;
        _fileLogger = logger;
    }
}
