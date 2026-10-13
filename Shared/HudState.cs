using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// The HUD's live phase, so agents tell "not now, normal, back at X" from "broken" (contract sections 1 and 2):
///   - a worker timer (4 Hz, never the main thread) computes the phase from GameController.InGame / IsLoading / Area:
///     starting (Initialise ran, listener not up) | menu (not in game) | loading (area transition) | ready (in game). Every
///     read is guarded: a read that throws keeps the last phase and names the link in hud-state.json / hud.state `error`
///     and bridge.self_perf `hudState`.
///   - on a phase change it writes &lt;BridgeDirectory&gt;\hud-state.json atomically (temp + replace) and publishes pub/sub
///     events (source "bridge"): area.loading {from, at}, area.ready {area, from, loadMs}, hud.phase {phase, prev, since,
///     inGame, area}; the file is rewritten as a heartbeat every 2 s from the same timer. tickAt is the last Tick/Render.
///   - startup timing: process start, Initialise, listener up, first ready; recentStartupSec (last 10, carried over from
///     the previous file; a bridge reload in the same HUD process keeps the previous startup and adds no sample) and
///     typicalStartupSec (their median).
///   - OnClose writes restarting (restart-state.json phase stopping in the last 3 min: an agent or script restart),
///     user-restart (an open outage incident for this game on the board) or closed, with closeReason.
///   - the listener (TcpBridgeServer.Live) answers hud.state itself, adds phase / phaseSinceMs / tickAgeMs to status, answers
///     a queued request still waiting after ~1.5 s with -32010 hud_loading while the phase is loading or starting, and
///     gives the 10 s timeout a message and data that say frozen vs loading vs busy.
/// </summary>
public partial class WhatsAnAiBridge : IBridgeLiveInfo
{
    private const int HsTickMs = 250, HsHeartbeatMs = 2000, HsStartupKeep = 10;
    private const int HudLoadingCode = -32010, HudTimeoutCode = -32002;
    private const long HsFrozenTickMs = 3000;
    private static readonly TimeSpan HsRestartStateFresh = TimeSpan.FromMinutes(3);

    private readonly object _hsLock = new();       // _hs and the fields below it
    private readonly object _hsWriteLock = new();  // one file write at a time (timer, OnClose)
    private HudStateDto _hs = new();
    private Timer? _hsTimer;
    private int _hsBusy;                           // 1 while a timer tick runs: ticks never overlap
    private long _hsTickTs;                        // Stopwatch timestamp of the last main-thread Tick/Render, 0 = none yet
    private long _hsSinceTs;                       // Stopwatch timestamp of the current phase's start
    private long _hsLoadTs;                        // Stopwatch timestamp of the current load's start, 0 = not loading
    private long _hsWrittenTs;                     // Stopwatch timestamp of the last successful write
    private bool _hsDirty;                         // a phase change not yet written (a write failed)
    private bool _hsReload;                        // this load is a bridge reload in the same HUD process
    private bool _hsClosed;                        // OnClose wrote the final phase: a late timer tick writes nothing
    private long? _hsAreaHash;                     // the area hash last seen ready
    private string? _hsReadError, _hsWriteError, _hsPublishError;
    private long _hsReadErrors, _hsWrites, _hsTicks;
    private static readonly JsonSerializer HsJson = JsonSerializer.Create(new JsonSerializerSettings { DateTimeZoneHandling = DateTimeZoneHandling.Utc });

    private string HudStateFilePath => Path.Combine(_bridgeDir, "hud-state.json");
    private string RestartStateFilePath => Path.Combine(_bridgeDir, "restart-state.json");

    /// <summary>Main thread, every Tick and Render: one field write (tickAt / tickAgeMs).</summary>
    private void HudStateMainTick() => Volatile.Write(ref _hsTickTs, Stopwatch.GetTimestamp());

    // ── Lifecycle ────────────────────────────────────────────────────

    /// <summary>
    /// First thing in Initialise (right after the bridge folder exists): phase starting, the previous file's history
    /// carried over. Written at once, so readers see "starting" while the listener is not up yet.
    /// </summary>
    private void HudStateStart()
    {
        try
        {
            var now = DateTime.UtcNow;
            var prev = HudStateReadPrevious();
            var pid = Environment.ProcessId;
            DateTime? processStart = null;
            try { processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
            catch (Exception ex) { _hsReadError = $"Process.GetCurrentProcess().StartTime: {ex.GetType().Name}: {ex.Message}"; }
            _hsReload = prev != null && prev.HudPid == pid;
            var recent = prev?.RecentStartupSec?.Where(s => s > 0 && !double.IsNaN(s)).TakeLast(HsStartupKeep).ToList() ?? new List<double>();
            lock (_hsLock)
            {
                _hs = new HudStateDto
                {
                    Game = GameId, Phase = "starting", PrevPhase = prev?.Phase, Since = now, Seq = (prev?.Seq ?? 0) + 1, Heartbeat = now,
                    InGame = false, Loading = false,
                    LastArea = prev?.Area ?? prev?.LastArea, Area = null, AreaChangedAt = prev?.AreaChangedAt, LastLoadMs = prev?.LastLoadMs,
                    HudPid = pid, ListenerUp = false,
                    // A bridge reload keeps this HUD process's startup (it measured the HUD start, not this load).
                    Startup = _hsReload && prev!.Startup != null ? prev.Startup : new HudStartupDto { ProcessStartedAt = processStart, InitAt = now },
                    RecentStartupSec = recent, TypicalStartupSec = HsMedian(recent),
                    Error = _hsReadError,
                };
                _hsSinceTs = Stopwatch.GetTimestamp();
                _hsLoadTs = 0;
                _hsAreaHash = null;
                Volatile.Write(ref _hsClosed, false);
                _hsDirty = true;
            }
            HudStateWrite();
        }
        catch (Exception ex) { _hsWriteError = $"hud-state start: {ex.GetType().Name}: {ex.Message}"; LogError($"[HudState] {_hsWriteError}"); }
    }

    /// <summary>End of Initialise: hook the listener, record listener-up, adopt a user restart from the board, start the timer.</summary>
    private void HudStateAfterInit()
    {
        try
        {
            if (_tcpServer != null) _tcpServer.Live = this;
            HudStateNoteListener();
            OutageAdoptFromBoard();
            _hsTimer?.Dispose();
            _hsTimer = new Timer(_ => HudStateTick(), null, HsTickMs, HsTickMs);
        }
        catch (Exception ex) { LogError($"[HudState] after init: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Bridge reload: stop the timer (the next load writes starting and carries the history over).</summary>
    private void HudStateStopTimer()
    {
        _hsTimer?.Dispose();
        _hsTimer = null;
    }

    /// <summary>
    /// OnClose: restarting (an agent or script restart: restart-state.json phase stopping in the last 3 min), user-restart
    /// (an open outage incident for this game on the board) or closed (by hand, unannounced), written synchronously.
    /// </summary>
    private void HudStateClose()
    {
        HudStateStopTimer();
        Volatile.Write(ref _hsClosed, true);   // a tick still running writes and publishes nothing from here on
        try
        {
            string phase, reason;
            if (HudStateAgentRestartNow()) { phase = "restarting"; reason = "agent-restart"; }
            else if (OutageOpenOnBoard(out _, out _)) { phase = "user-restart"; reason = "user-restart"; }
            else { phase = "closed"; reason = "unknown"; }
            HudPhaseEvent ev;
            lock (_hsLock)
            {
                var now = DateTime.UtcNow;
                _hs.PrevPhase = _hs.Phase;
                _hs.Phase = phase;
                _hs.Since = now;
                _hs.Seq++;
                _hs.CloseReason = reason;
                _hs.ListenerUp = false;
                _hs.Loading = false;
                _hs.Heartbeat = now;
                _hsSinceTs = Stopwatch.GetTimestamp();
                _hsDirty = true;
                ev = new HudPhaseEvent { Phase = phase, Prev = _hs.PrevPhase, Since = now, InGame = _hs.InGame, Area = _hs.Area };
            }
            HudStateWrite(final: true);
            HudStatePublish("hud.phase", ev);   // appended to events.jsonl: the next bridge load reads it back
        }
        catch (Exception ex) { LogError($"[HudState] close: {ex.GetType().Name}: {ex.Message}"); }
    }

    private bool HudStateAgentRestartNow()
    {
        try
        {
            if (!File.Exists(RestartStateFilePath)) return false;
            var rs = JsonConvert.DeserializeObject<RestartStateFileDto>(HsReadShared(RestartStateFilePath));
            return rs?.Phase == "stopping" && rs.At is DateTime at && DateTime.UtcNow - at.ToUniversalTime() < HsRestartStateFresh;
        }
        catch (Exception ex) { _hsReadError = $"reading {RestartStateFilePath}: {ex.GetType().Name}: {ex.Message}"; return false; }
    }

    // ── The worker tick (4 Hz) ───────────────────────────────────────

    private void HudStateNoteListener()
    {
        var server = _tcpServer;
        var up = server?.IsRunning == true;
        lock (_hsLock)
        {
            _hs.ListenerUp = up;
            _hs.Port = up ? server!.Port : null;
            if (!up || _hsReload || _hs.Startup == null || _hs.Startup.ListenerAt != null) return;
            var now = DateTime.UtcNow;
            _hs.Startup.ListenerAt = now;
            if (_hs.Startup.ProcessStartedAt is DateTime ps)
            {
                var sec = Math.Round((now - ps).TotalSeconds, 1);
                _hs.Startup.Sec = sec;
                _hs.RecentStartupSec.Add(sec);
                while (_hs.RecentStartupSec.Count > HsStartupKeep) _hs.RecentStartupSec.RemoveAt(0);
                _hs.TypicalStartupSec = HsMedian(_hs.RecentStartupSec);
            }
            _hsDirty = true;
        }
    }

    private void HudStateTick()
    {
        if (Interlocked.Exchange(ref _hsBusy, 1) == 1) return;
        try
        {
            _hsTicks++;
            // Reads first, outside the lock. GameController.InGame / IsLoading are the HUD's per-frame copies; the area is
            // its AreaInstance. Any read that throws names its link and keeps the last phase.
            bool inGame = false, loading = false; string? area = null; long? hash = null; string? err = null;
            var step = "GameController.InGame";
            try
            {
                inGame = GameController.InGame;
                step = "GameController.IsLoading";
                loading = GameController.IsLoading;
                if (inGame && !loading)
                {
                    step = "GameController.Area.CurrentArea";
                    var a = GameController.Area?.CurrentArea;
                    if (a != null)
                    {
                        step = "AreaInstance.Name";
                        area = a.Name;
                        step = "AreaInstance.Hash";
                        hash = a.Hash;
                    }
                }
            }
            catch (Exception ex) { err = $"{step}: {ex.GetType().Name}: {ex.Message}"; }
            // The game's pid: once (the HUD stays attached to one process), retried every 5 s while unknown. Its failure
            // is reported but never holds the phase.
            int? gamePid = null;
            if (_hs.GamePid == null && _hsTicks % 20 == 1)
                try { gamePid = GameController.Window.Process.Id; }
                catch (Exception ex) { _hsReadError = $"GameController.Window.Process.Id: {ex.GetType().Name}: {ex.Message}"; _hsReadErrors++; }
            if (err != null) { _hsReadError = err; _hsReadErrors++; }
            if (Volatile.Read(ref _hsClosed)) return;   // OnClose wrote the final phase
            HudStateNoteListener();
            var tcpWanted = Settings.EnableTcp.Value;

            var events = new List<(string kind, object data)>();
            bool write;
            string phaseNow;
            lock (_hsLock)
            {
                var now = DateTime.UtcNow;
                var nowTs = Stopwatch.GetTimestamp();
                var prevPhase = _hs.Phase;
                var phase = tcpWanted && !_hs.ListenerUp ? "starting"
                    : err != null ? (prevPhase is "starting" ? "menu" : prevPhase)   // a failed read keeps what we knew
                    : loading ? "loading" : !inGame ? "menu" : "ready";
                if (gamePid != null) _hs.GamePid = gamePid;
                _hs.Error = err;
                if (err == null) { _hs.InGame = inGame; _hs.Loading = loading; }

                if (phase != prevPhase)
                {
                    if (phase == "loading")
                    {
                        _hsLoadTs = nowTs;
                        if (_hs.Area != null) _hs.LastArea = _hs.Area;
                        _hs.Area = null;
                        events.Add(("area.loading", new AreaLoadingEvent { From = _hs.LastArea, At = now }));
                    }
                    else if (phase == "menu")
                    {
                        if (_hs.Area != null) _hs.LastArea = _hs.Area;
                        _hs.Area = null;
                        _hsLoadTs = 0;
                    }
                    else if (phase == "ready")
                    {
                        long? loadMs = null;
                        if (prevPhase == "loading" && _hsLoadTs != 0)
                        {
                            loadMs = (long)Stopwatch.GetElapsedTime(_hsLoadTs, nowTs).TotalMilliseconds;
                            _hs.LastLoadMs = loadMs;
                        }
                        _hsLoadTs = 0;
                        var from = _hs.LastArea;
                        events.Add(("area.ready", new AreaReadyEvent { Area = area, From = from, LoadMs = loadMs }));
                        _hs.AreaChangedAt = now;
                        _hs.Area = area;
                        _hs.LastArea = area ?? from;
                        _hsAreaHash = hash;
                        if (_hs.Startup != null && _hs.Startup.FirstReadyAt == null && !_hsReload) _hs.Startup.FirstReadyAt = now;
                    }
                    _hs.PrevPhase = prevPhase;
                    _hs.Phase = phase;
                    _hs.Since = now;
                    _hs.Seq++;
                    _hs.CloseReason = null;
                    _hsSinceTs = nowTs;
                    _hsDirty = true;
                    events.Add(("hud.phase", new HudPhaseEvent { Phase = phase, Prev = prevPhase, Since = now, InGame = _hs.InGame, Area = _hs.Area }));
                }
                else if (phase == "ready" && err == null && hash != null && _hsAreaHash != null && hash != _hsAreaHash)
                {
                    // A new area with no load screen seen (a load shorter than a tick): an arrival all the same.
                    var from = _hs.Area ?? _hs.LastArea;
                    events.Add(("area.ready", new AreaReadyEvent { Area = area, From = from, LoadMs = null }));
                    _hs.AreaChangedAt = now;
                    _hs.Area = area;
                    _hs.LastArea = area ?? from;
                    _hsAreaHash = hash;
                    _hsDirty = true;
                }
                else if (phase == "ready" && err == null && area != null && _hs.Area == null)
                {
                    _hs.Area = area; _hs.LastArea = area; _hsAreaHash ??= hash; _hsDirty = true;   // the name arrived after the phase
                }
                write = _hsDirty || Stopwatch.GetElapsedTime(_hsWrittenTs, nowTs).TotalMilliseconds >= HsHeartbeatMs;
                phaseNow = _hs.Phase;
            }
            if (write) HudStateWrite();
            foreach (var (kind, data) in events) HudStatePublish(kind, data);
            OutageLeaseTick(phaseNow);
        }
        catch (Exception ex) { _hsReadError = $"hud-state tick: {ex.GetType().Name}: {ex.Message}"; }
        finally { Volatile.Write(ref _hsBusy, 0); }
    }

    /// <summary>Serialize under the state lock, write temp + replace under the write lock.</summary>
    private void HudStateWrite(bool final = false)
    {
        string json;
        lock (_hsLock)
        {
            _hs.Heartbeat = DateTime.UtcNow;
            _hs.TickAt = HsTickAt();
            json = JsonConvert.SerializeObject(_hs, Formatting.Indented);
        }
        lock (_hsWriteLock)
        {
            var path = HudStateFilePath;
            var tmp = path + ".tmp";
            if (Volatile.Read(ref _hsClosed) && !final) return;   // a tick racing OnClose never overwrites the final phase
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
                _hsWriteError = null;
                _hsWrites++;
                lock (_hsLock) { _hsWrittenTs = Stopwatch.GetTimestamp(); _hsDirty = false; }
            }
            catch (Exception ex) { _hsWriteError = $"writing {path}: {ex.GetType().Name}: {ex.Message}"; }   // retried next tick (still dirty)
        }
    }

    private void HudStatePublish(string kind, object data)
    {
        try
        {
            PubPublish(kind, JObject.FromObject(data, HsJson), "bridge");
            _hsPublishError = null;
        }
        catch (Exception ex) { _hsPublishError = $"publishing {kind}: {ex.GetType().Name}: {ex.Message}"; }
    }

    private HudStateDto? HudStateReadPrevious()
    {
        try
        {
            var path = HudStateFilePath;
            return File.Exists(path) ? JsonConvert.DeserializeObject<HudStateDto>(HsReadShared(path)) : null;
        }
        catch (Exception ex) { _hsReadError = $"reading the previous {HudStateFilePath}: {ex.GetType().Name}: {ex.Message}"; return null; }
    }

    // ── Listener side (TcpBridgeServer.Live) ─────────────────────────

    string? IBridgeLiveInfo.TryLocal(string method, JToken? p, int pending) => method switch
    {
        "hud.state" => JsonConvert.SerializeObject(HudStateReply(pending)),
        "outage.set" => OutageSet(p),
        "outage.state" => JsonConvert.SerializeObject(OutageStateNow()),
        _ => null,
    };

    void IBridgeLiveInfo.AddStatus(JObject status)
    {
        lock (_hsLock)
        {
            status["phase"] = _hs.Phase;
            status["phaseSinceMs"] = HsSinceMs();
        }
        status["tickAgeMs"] = HsTickAgeMs() is long t ? t : JValue.CreateNull();
    }

    BridgeRpcError? IBridgeLiveInfo.EarlyReply(TimeSpan waited, int pending)
    {
        lock (_hsLock)
        {
            if (_hs.Phase is not ("loading" or "starting")) return null;
            var sinceMs = HsSinceMs();
            var data = new HudLoadingErrorData
            {
                Phase = _hs.Phase, Since = _hs.Since, SinceMs = sinceMs, LastArea = _hs.LastArea, LastLoadMs = _hs.LastLoadMs,
                RetryAfterMs = HsRetryAfterMs(sinceMs),
            };
            var secs = (sinceMs / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            var msg = _hs.Phase == "loading"
                ? $"HUD is on a load screen (from {_hs.LastArea ?? "an unknown area"}, {secs} s) - normal, comes back by itself"
                : $"HUD is starting (bridge not fully up yet, {secs} s in) - normal, comes back by itself";
            return new BridgeRpcError(HudLoadingCode, msg, data);
        }
    }

    BridgeRpcError IBridgeLiveInfo.Timeout(int seconds, int pending)
    {
        var tickAge = HsTickAgeMs();
        lock (_hsLock)
        {
            var sinceMs = HsSinceMs();
            var data = new HudTimeoutErrorData { Phase = _hs.Phase, SinceMs = sinceMs, TickAgeMs = tickAge, LastArea = _hs.LastArea, Pending = pending };
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string msg;
            if (_hs.Phase == "loading")
                msg = $"Timed out after {seconds}s: the HUD is on a load screen (from {_hs.LastArea ?? "an unknown area"}, {(sinceMs / 1000.0).ToString("0.0", inv)} s) - normal, retry once it is back (pubsub area.ready)";
            else if (tickAge == null)
                msg = $"Timed out after {seconds}s: the HUD main thread has not ticked since the bridge loaded (frozen, or the HUD is paused), and the worker fallback did not answer";
            else if (tickAge >= HsFrozenTickMs)
                msg = $"Timed out after {seconds}s: the HUD main thread is frozen (no Tick/Render for {(tickAge.Value / 1000.0).ToString("0.0", inv)} s; phase {_hs.Phase}), and the worker fallback did not answer";
            else
                msg = $"Timed out after {seconds}s: the HUD is ticking (last tick {tickAge} ms ago, phase {_hs.Phase}) but busy: {pending} request(s) still queued";
            return new BridgeRpcError(HudTimeoutCode, msg, data);
        }
    }

    private HudStateReplyDto HudStateReply(int pending)
    {
        var tickAge = HsTickAgeMs();
        lock (_hsLock)
        {
            var o = JObject.FromObject(_hs, HsJson);
            var r = o.ToObject<HudStateReplyDto>(HsJson)!;
            r.Heartbeat = DateTime.UtcNow;
            r.TickAt = HsTickAt();
            r.TickAgeMs = tickAge;
            r.Pending = pending;
            r.PhaseSinceMs = HsSinceMs();
            return r;
        }
    }

    /// <summary>bridge.self_perf `hudState`: the tracker's health (the broken link when a read or write fails).</summary>
    private JObject HudStateSelfPerf()
    {
        lock (_hsLock)
            return new JObject
            {
                ["phase"] = _hs.Phase, ["seq"] = _hs.Seq, ["ticks"] = _hsTicks, ["writes"] = _hsWrites, ["readErrors"] = _hsReadErrors,
                ["readError"] = _hsReadError, ["writeError"] = _hsWriteError, ["publishError"] = _hsPublishError,
                ["timer"] = _hsTimer != null, ["tickAgeMs"] = HsTickAgeMs() is long t ? t : JValue.CreateNull(),
            };
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private long HsSinceMs() => _hsSinceTs == 0 ? 0 : (long)Stopwatch.GetElapsedTime(_hsSinceTs).TotalMilliseconds;

    private long? HsTickAgeMs()
    {
        var t = Volatile.Read(ref _hsTickTs);
        return t == 0 ? null : (long)Stopwatch.GetElapsedTime(t).TotalMilliseconds;
    }

    private DateTime? HsTickAt() => HsTickAgeMs() is long age ? DateTime.UtcNow.AddMilliseconds(-age) : null;

    /// <summary>When to try again: loading, what is left of a load as long as the last one (0.5-5 s); starting, what is left
    /// of a typical startup (1-30 s).</summary>
    private long HsRetryAfterMs(long sinceMs)
    {
        if (_hs.Phase == "loading")
            return _hs.LastLoadMs is long last ? Math.Clamp(last - sinceMs, 500, 5000) : 2000;
        if (_hs.TypicalStartupSec is double typ && _hs.Startup?.ProcessStartedAt is DateTime ps)
            return Math.Clamp((long)((typ - (DateTime.UtcNow - ps).TotalSeconds) * 1000), 1000, 30000);
        return 5000;
    }

    private static double? HsMedian(List<double> xs)
    {
        if (xs.Count == 0) return null;
        var s = xs.OrderBy(x => x).ToList();
        var m = s.Count / 2;
        return Math.Round(s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2, 1);
    }

    /// <summary>Read a file another process may be replacing (share read, write and delete).</summary>
    private static string HsReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8);
        return r.ReadToEnd();
    }
}
