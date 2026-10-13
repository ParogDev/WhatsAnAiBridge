using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace WhatsAnAiBridge;

/// <summary>
/// Where the bridge's own messages go. The HUD's top-left text (DebugWindow) is the player's screen, not our console:
/// the user found it far too busy while playing (2026-10-12). So:
///   - <see cref="LogQuiet"/>: routine diagnostics (connections, thread hand-overs, reloads that worked and their
///     warnings). HUD log file only, at Information; agents read it with MCP hud_log. Never drawn.
///   - <see cref="LogFault"/>: something broke. HUD log file at Error, full text (stack traces included), so hud_log
///     level=error and hud_plugins' runtime error counts still find it (fail at the broken link). A key is written at
///     most once per <see cref="FaultRepeatSec"/>; repeats inside that window (a per-frame exception) are counted and
///     a 1 s timer writes "(N more in the last 5 s)" with the last text once the window closes, so a burst that stops
///     still reports its size. Never drawn.
///   - <see cref="ToastFault"/>: a fault the player should notice (agents can't reach the HUD, F8 feedback is off):
///     <see cref="LogFault"/> plus one toast on the guide card's toast stack, once per load and key.
/// The file is written through the HUD's own Serilog logger (Core.Logger, the one DebugWindow writes to), found by
/// reflection so the bridge needs no Serilog reference (the CI reference set has none). While that logger is not set
/// yet (not now) a message falls back to the old on-screen line. A missing member is broken for good; a write that
/// throws (a sink mid-rotation) falls back for <see cref="FileRetrySec"/> and then tries again. Either break is named
/// once on screen: nothing is swallowed. Lines only reach the file when Core.Logger lets Information through (both
/// HUDs' loggers do, at Verbose).
/// </summary>
public partial class WhatsAnAiBridge
{
    private const double FaultRepeatSec = 5, FileRetrySec = 30;
    private const string LogPrefix = "[Whats An AI Bridge] ";

    private static readonly object FileLogLock = new();
    private static object? _fileLogger;
    private static MethodInfo? _fileInfo, _fileError;
    private static string? _fileLogBroken;        // why the file can't be written now (null = fine or not set yet)
    private static bool _fileLogPermanent;        // the break is a missing member: never retried
    private static DateTime _fileRetryAt;
    private static bool _fileLogBrokenShown;

    private readonly object _faultLock = new();
    private readonly Dictionary<string, (DateTime at, int skipped, string last)> _faultSeen = new();
    private readonly HashSet<string> _faultToasted = new();
    private Timer? _faultFlushTimer;
    private bool _faultClosed;   // HudLogStop ran: this instance is closing or being replaced by a reload

    /// <summary>Routine diagnostics: the HUD log file only (hud_log), never the player's screen.</summary>
    internal void LogQuiet(string msg)
    {
        if (!WriteLogFile(false, LogPrefix + msg)) LogMessage(msg);
    }

    /// <summary>
    /// Something broke: the HUD log file at Error (hud_log level=error), full text, never the player's screen.
    /// <paramref name="key"/> groups repeats (default: the message; pass a stable key when the text carries ids or
    /// ports). See the class summary for how repeats are counted.
    /// </summary>
    internal void LogFault(string msg, string? key = null)
    {
        key ??= msg;
        var now = DateTime.UtcNow;
        lock (_faultLock)
        {
            // Once closing (HudLogStop), every fault is written and no timer starts: it would keep this instance alive.
            if (!_faultClosed)
            {
                if (_faultSeen.TryGetValue(key, out var seen) && (now - seen.at).TotalSeconds < FaultRepeatSec)
                {
                    _faultSeen[key] = (seen.at, seen.skipped + 1, msg);
                    return;
                }
                _faultSeen[key] = (now, 0, msg);
                _faultFlushTimer ??= new Timer(_ => FlushFaultRepeats(), null, 1000, 1000);
            }
        }
        if (!WriteLogFile(true, LogPrefix + msg)) LogError(msg);
    }

    /// <summary>Every second: write the count of repeats whose window closed, and forget closed windows.</summary>
    private void FlushFaultRepeats()
    {
        try
        {
            List<(int n, string last)>? due = null;
            var now = DateTime.UtcNow;
            lock (_faultLock)
            {
                if (_faultSeen.Count == 0) return;
                foreach (var k in _faultSeen.Keys.ToList())
                {
                    var e = _faultSeen[k];
                    if ((now - e.at).TotalSeconds < FaultRepeatSec) continue;
                    if (e.skipped > 0) (due ??= new()).Add((e.skipped, e.last));
                    _faultSeen.Remove(k);
                }
            }
            if (due == null) return;
            foreach (var (n, last) in due)
            {
                var text = $"({n} more in the last {FaultRepeatSec:0} s) {last}";
                if (!WriteLogFile(true, LogPrefix + text)) LogError(text);
            }
        }
        catch { /* a timer thread must never throw; the faults themselves were written when they began */ }
    }

    /// <summary>OnClose: stop the repeat flush (the timer would keep an unloaded bridge alive).</summary>
    private void HudLogStop()
    {
        Timer? t;
        lock (_faultLock) { _faultClosed = true; t = _faultFlushTimer; _faultFlushTimer = null; }
        t?.Dispose();
        FlushFaultRepeatsNow();
    }

    private void FlushFaultRepeatsNow()
    {
        lock (_faultLock)
            foreach (var k in _faultSeen.Keys.ToList()) _faultSeen[k] = (DateTime.MinValue, _faultSeen[k].skipped, _faultSeen[k].last);
        FlushFaultRepeats();
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
        try
        {
            var r = GuideLog(new Newtonsoft.Json.Linq.JObject { ["text"] = toast, ["kind"] = kind, ["title"] = title, ["holdSec"] = holdSec });
            if (r["error"] != null) LogFault($"[Toast] '{title}' could not be posted: {r["error"]}");
        }
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
        string? recovered = null;
        lock (FileLogLock)
        {
            if (_fileLogger == null && (_fileLogBroken == null || (!_fileLogPermanent && DateTime.UtcNow >= _fileRetryAt)))
            {
                var was = _fileLogBroken;
                _fileLogBroken = null;
                BindFileLogger();
                if (was != null && _fileLogger != null) recovered = was;
            }
            logger = _fileLogger;
            write = error ? _fileError : _fileInfo;
        }
        if (logger != null && write != null)
        {
            try
            {
                // Template "{M:l}" with the text as its value: braces in the text are never parsed as a template.
                if (recovered != null)
                    _fileInfo!.Invoke(logger, new object[] { "{M:l}", new object[] { $"{LogPrefix}[Log] the HUD log file works again (it failed: {recovered})" } });
                write.Invoke(logger, new object[] { "{M:l}", new object[] { text } });
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                lock (FileLogLock)
                {
                    _fileLogBroken = $"writing to Core.Logger: {inner.GetType().Name}: {inner.Message}";
                    _fileLogger = null;
                    _fileRetryAt = DateTime.UtcNow.AddSeconds(FileRetrySec);
                }
            }
        }
        string? broken;
        lock (FileLogLock)
        {
            broken = _fileLogBroken;
            if (broken == null || _fileLogBrokenShown) return false;   // not set yet (not now), or already named
            _fileLogBrokenShown = true;
        }
        LogError($"[Log] the HUD log file can't be written ({broken}): bridge messages go to the screen instead"
            + (_fileLogPermanent ? "" : $", retried every {FileRetrySec:0} s"));
        return false;
    }

    /// <summary>Find Core.Logger and its Information / Error(string, object[]). Sets _fileLogBroken (permanent) naming
    /// the link when one is missing; leaves everything null while the logger is just not set yet.</summary>
    private static void BindFileLogger()
    {
        // Declared links (HudLinks.cs), preflighted at load.
        var prop = HudLinks.CoreLogger.Property;
        if (prop == null) { _fileLogBroken = $"{HudLinks.CoreLogger.Broken(typeof(Core))} in this HUD build"; _fileLogPermanent = true; return; }
        var logger = prop.GetValue(null);
        if (logger == null) return;
        // The property's type and its interfaces first, then the logger's own type.
        var info = HudLinks.LoggerInformation.MethodOn(prop.PropertyType) ?? HudLinks.LoggerInformation.MethodOn(logger.GetType());
        var err = HudLinks.LoggerError.MethodOn(prop.PropertyType) ?? HudLinks.LoggerError.MethodOn(logger.GetType());
        if (info == null || err == null)
        {
            _fileLogBroken = (info == null ? HudLinks.LoggerInformation : HudLinks.LoggerError).Broken(prop.PropertyType);
            _fileLogPermanent = true;
            return;
        }
        _fileInfo = info;
        _fileError = err;
        _fileLogger = logger;
    }
}
