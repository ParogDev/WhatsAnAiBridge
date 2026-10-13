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
///     The first ready after starting publishes hud.phase only (no arrival: the area is adopted quietly). A load past 60 s
///     posts the LONG LOAD warn toast once, taken down when the load ends.
///   - startup timing: process start, Initialise, listener up, first ready; recentStartupSec (last 10, carried over from
///     the previous file; a bridge reload in the same HUD process keeps the previous startup and adds no sample) and
///     typicalStartupSec (their median).
///   - OnClose writes restarting (restart-state.json phase stopping in the last 3 min: an agent or script restart),
///     user-restart (an open outage incident for this game on the board in phase down | waitingGame) or closed, with
///     closeReason. A bridge reload (OnClose comes for it too; the caller's stack tells HsCloseIsReload) writes starting with
///     reloading = true and no closeReason, kept by the next load until its first phase change. Initialise carries the previous file's closeReason as prevCloseReason for the whole HUD run.
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
    private long _hsDirtyVer, _hsWrittenVer;       // +1 per change of _hs; the version the last successful write serialized
    private bool HsDirtyLocked => _hsDirtyVer != _hsWrittenVer;   // a change not yet written (a write failed, or raced one)
    private int _hsCloseDone;                      // 1 once HudStateClose ran for this load
    private bool _hsLongLoadPosted;               // this load's LONG LOAD toast is out (once per load)
    private bool _hsReload;                        // this load is a bridge reload in the same HUD process
    private bool _hsClosed;                        // OnClose wrote the final phase: a late timer tick writes nothing
    private long? _hsAreaHash;                     // the area hash last seen ready
    // LONG LOAD (wording by Fable): a warn toast once per load past 60 s, the bridge's own (agents never toast loads: the MCP's
    // stuckLoading note says so). ExileCore2 ticks and renders the bridge during load screens (seen live), so it shows there.
    // Taken down when the load ends (phase ready, which area.ready follows, or menu).
    private const double HsLongLoadMs = 60000, HsLongLoadHoldSec = 10;
    private const string HsLongLoadTitle = "LONG LOAD", HsLongLoadTag = "hudstate:longload";
    private static string HsLongLoadText(long sec, string? from) =>
        from != null ? $"Load screen for {sec} s (from {from}) - the game, not the HUD. Agents wait."
                     : $"Load screen for {sec} s - the game, not the HUD. Agents wait.";
    private const double HsAwaitAreaMs = 3000;    // after a load, how long area.ready waits for the area data to move on
    private bool _hsAwaitArea;                     // a load ended but the area data still shows the area we left
    private long _hsAwaitTs;
    private long? _hsAwaitLoadMs;
    private string? _hsAwaitFrom;
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
                    // A reload's closing load already wrote starting (reloading): keep the phase it came from.
                    Game = GameId, Phase = "starting", PrevPhase = _hsReload && prev!.Reloading == true ? prev.PrevPhase : prev?.Phase, Since = now,
                    Reloading = _hsReload ? true : null, Seq = (prev?.Seq ?? 0) + 1, Heartbeat = now,
                    InGame = false, Loading = false,
                    LastArea = prev?.Area ?? prev?.LastArea, Area = null, AreaChangedAt = prev?.AreaChangedAt, LastLoadMs = prev?.LastLoadMs,
                    HudPid = pid, ListenerUp = false,
                    // A bridge reload keeps this HUD process's startup (it measured the HUD start, not this load).
                    Startup = _hsReload && prev!.Startup != null ? prev.Startup : new HudStartupDto { ProcessStartedAt = processStart, InitAt = now },
                    RecentStartupSec = recent, TypicalStartupSec = HsMedian(recent),
                    Error = _hsReadError,
                    // Why the previous HUD closed, kept for this whole HUD run (restart-hud.ps1's post-start hold reads it).
                    // A bridge reload's previous file is this run's own close: carry the run's value instead.
                    PrevCloseReason = _hsReload ? prev!.PrevCloseReason : prev?.CloseReason,
                };
                _hsSinceTs = Stopwatch.GetTimestamp();
                _hsLoadTs = 0;
                _hsAreaHash = null;
                Volatile.Write(ref _hsClosed, false);
                _hsDirtyVer++;
            }
            HudStateWrite();
        }
        catch (Exception ex) { _hsWriteError = $"hud-state start: {ex.GetType().Name}: {ex.Message}"; LogFault($"[HudState] {_hsWriteError}", "hudstate"); }
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
        catch (Exception ex) { LogFault($"[HudState] after init: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Bridge reload: stop the timer (the next load writes starting and carries the history over).</summary>
    private void HudStateStopTimer()
    {
        _hsTimer?.Dispose();
        _hsTimer = null;
    }

    /// <summary>
    /// OnClose: restarting (an agent or script restart: restart-state.json phase stopping in the last 3 min), user-restart
    /// (an open outage incident for this game on the board in phase down or waitingGame: the user restarts by hand) or
    /// closed (by hand, unannounced), written synchronously. An outage still announced / holding is not the user's restart
    /// yet: that close follows the ordinary rules.
    /// </summary>
    private void HudStateClose()
    {
        HudStateStopTimer();
        // Once per load: the HUD calls OnClose twice on shutdown (PluginWrapper.Close, then BaseSettingsPlugin.Dispose; seen
        // live 2026-10-13 as hud.phase restarting -> restarting).
        if (Interlocked.Exchange(ref _hsCloseDone, 1) == 1) return;
        Volatile.Write(ref _hsClosed, true);   // a tick still running writes and publishes nothing from here on
        try
        {
            string phase;
            string? reason;
            // The HUD calls OnClose for a plugin reload too (PluginWrapper.Close: OnPluginDestroyForHotReload, then OnClose).
            // A reload is no HUD close: phase starting with reloading = true, no closeReason; the next load keeps it.
            var reload = HsCloseIsReload(out var via);
            LogQuiet(reload ? $"[HudState] bridge reload ({via}): phase starting (reloading), not a close" : $"[HudState] HUD close ({via})");
            if (reload) { phase = "starting"; reason = null; }
            // The outage first: its own stop (restart-hud.ps1 -Outage) also writes restart-state.json 'stopping'
            // (seen live 2026-10-13: a user restart closed as agent-restart).
            else if (OutageOpenOnBoard(out _, out var outagePhase) && outagePhase is ("down" or "waitingGame")) { phase = "user-restart"; reason = "user-restart"; }
            else if (HudStateAgentRestartNow()) { phase = "restarting"; reason = "agent-restart"; }
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
                _hs.Reloading = reload ? true : null;
                _hs.ListenerUp = false;
                _hs.Loading = false;
                _hs.Heartbeat = now;
                _hsSinceTs = Stopwatch.GetTimestamp();
                _hsDirtyVer++;
                ev = new HudPhaseEvent { Phase = phase, Prev = _hs.PrevPhase, Since = now, InGame = _hs.InGame, Area = _hs.Area };
            }
            HudStateWrite(final: true);
            HudStatePublish("hud.phase", ev);   // appended to events.jsonl: the next bridge load reads it back
        }
        catch (Exception ex) { LogFault($"[HudState] close: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>
    /// Is this OnClose a plugin reload (the HUD keeps running) rather than the HUD shutting down? Both come through
    /// PluginWrapper.Close; the caller tells them apart: PluginManager.CloseAllPlugins on shutdown, UnloadAssemblyPlugins /
    /// ReloadSourcePlugin / ReloadChangedDll / a ReloadPluginDll lambda on a reload (small ones such as
    /// UnloadAssemblyPlugins may be inlined out of the stack). `via` names the frame that decided, or the frames seen when
    /// none did (then a close, as before); HudStateClose logs it either way.
    /// </summary>
    private static bool HsCloseIsReload(out string via)
    {
        try
        {
            var frames = new StackTrace(1, false).GetFrames();
            var seen = new List<string>();
            foreach (var f in frames)
            {
                var m = f.GetMethod();
                if (m == null) continue;
                var name = m.Name;
                if (seen.Count < 16) seen.Add($"{m.DeclaringType?.Name}.{name}");
                if (name == "CloseAllPlugins") { via = $"{m.DeclaringType?.Name}.{name}"; return false; }
                if (name == "UnloadAssemblyPlugins" || name.Contains("Reload", StringComparison.Ordinal))
                { via = $"{m.DeclaringType?.Name}.{name}"; return true; }
            }
            via = $"no CloseAllPlugins / Reload frame in {frames.Length}: {string.Join(" < ", seen)}";
            return false;
        }
        catch (Exception ex) { via = $"StackTrace: {ex.GetType().Name}: {ex.Message}"; return false; }
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
            _hsDirtyVer++;
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
            bool write, retireLongLoad = false;
            string? longLoad = null;
            string phaseNow;
            lock (_hsLock)
            {
                var now = DateTime.UtcNow;
                var nowTs = Stopwatch.GetTimestamp();
                var prevPhase = _hs.Phase;
                var phase = tcpWanted && !_hs.ListenerUp ? "starting"
                    : err != null ? prevPhase   // a failed read keeps what we knew (starting stays starting until a read works)
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
                        if (prevPhase == "loading" && hash != null && _hsAreaHash != null && hash == _hsAreaHash)
                        {
                            // IsLoading drops before the area data moves on (seen live 2026-10-13: area.ready said
                            // Kingsmarch -> Kingsmarch, the new area's name came a second later). Hold area.ready
                            // until the area hash changes, or HsAwaitAreaMs passes (the same area again).
                            _hsAwaitArea = true; _hsAwaitTs = nowTs; _hsAwaitLoadMs = loadMs; _hsAwaitFrom = from;
                            _hs.Area = null;
                        }
                        else if (prevPhase == "starting")
                        {
                            // The HUD (or the bridge) came up in an area it was already in: no arrival, so hud.phase only
                            // (seen live 2026-10-13: "Ngakanu from Ngakanu" after every restart). The area is adopted quietly.
                            _hs.Area = area;
                            _hs.LastArea = area ?? from;
                            _hsAreaHash = hash;
                        }
                        else
                        {
                            events.Add(("area.ready", new AreaReadyEvent { Area = area, From = from, LoadMs = loadMs }));
                            _hs.AreaChangedAt = now;
                            _hs.Area = area;
                            _hs.LastArea = area ?? from;
                            _hsAreaHash = hash;
                        }
                        if (_hs.Startup != null && _hs.Startup.FirstReadyAt == null && !_hsReload) _hs.Startup.FirstReadyAt = now;
                    }
                    if (phase != "ready") _hsAwaitArea = false;
                    _hs.PrevPhase = prevPhase;
                    _hs.Phase = phase;
                    _hs.Since = now;
                    _hs.Seq++;
                    _hs.CloseReason = null;
                    _hs.Reloading = null;   // a reload's starting ends with its first phase change
                    _hsSinceTs = nowTs;
                    _hsDirtyVer++;
                    if (prevPhase == "loading" && _hsLongLoadPosted) retireLongLoad = true;   // the load is over
                    if (phase == "loading") _hsLongLoadPosted = false;
                    events.Add(("hud.phase", new HudPhaseEvent { Phase = phase, Prev = prevPhase, Since = now, InGame = _hs.InGame, Area = _hs.Area }));
                }
                else if (phase == "ready" && _hsAwaitArea && err == null
                         && ((hash != null && hash != _hsAreaHash) || Stopwatch.GetElapsedTime(_hsAwaitTs, nowTs).TotalMilliseconds >= HsAwaitAreaMs))
                {
                    _hsAwaitArea = false;
                    events.Add(("area.ready", new AreaReadyEvent { Area = area, From = _hsAwaitFrom, LoadMs = _hsAwaitLoadMs }));
                    _hs.AreaChangedAt = now;
                    _hs.Area = area;
                    _hs.LastArea = area ?? _hsAwaitFrom;
                    _hsAreaHash = hash;
                    _hsDirtyVer++;
                }
                else if (phase == "ready" && !_hsAwaitArea && err == null && hash != null && _hsAreaHash != null && hash != _hsAreaHash)
                {
                    // A new area with no load screen seen (a load shorter than a tick): an arrival all the same.
                    var from = _hs.Area ?? _hs.LastArea;
                    events.Add(("area.ready", new AreaReadyEvent { Area = area, From = from, LoadMs = null }));
                    _hs.AreaChangedAt = now;
                    _hs.Area = area;
                    _hs.LastArea = area ?? from;
                    _hsAreaHash = hash;
                    _hsDirtyVer++;
                }
                else if (phase == "ready" && !_hsAwaitArea && err == null && area != null && _hs.Area == null)
                {
                    _hs.Area = area; _hs.LastArea = area; _hsAreaHash ??= hash; _hsDirtyVer++;   // the name arrived after the phase
                }
                if (phase == "loading" && !_hsLongLoadPosted && _hsLoadTs != 0
                    && Stopwatch.GetElapsedTime(_hsLoadTs, nowTs).TotalMilliseconds >= HsLongLoadMs)
                {
                    _hsLongLoadPosted = true;
                    longLoad = HsLongLoadText((long)Stopwatch.GetElapsedTime(_hsLoadTs, nowTs).TotalSeconds, _hs.LastArea);
                }
                write = HsDirtyLocked || Stopwatch.GetElapsedTime(_hsWrittenTs, nowTs).TotalMilliseconds >= HsHeartbeatMs;
                phaseNow = _hs.Phase;
            }
            if (write) HudStateWrite();
            foreach (var (kind, data) in events) HudStatePublish(kind, data);
            if (retireLongLoad) GuideToastRetire(HsLongLoadTag);
            if (longLoad != null) OutageToast("warn", HsLongLoadTitle, longLoad, HsLongLoadHoldSec, HsLongLoadTag);
            OutageLeaseTick(phaseNow);
        }
        catch (Exception ex) { _hsReadError = $"hud-state tick: {ex.GetType().Name}: {ex.Message}"; }
        finally { Volatile.Write(ref _hsBusy, 0); }
    }

    /// <summary>
    /// Under the write lock (lock order: write, then state): serialize under the state lock, write temp + replace. Serializing
    /// inside the write lock keeps two writers from landing an older snapshot last; the dirty mark clears only up to the
    /// version this write serialized, so a change made while the file was being written is written by the next tick.
    /// </summary>
    private void HudStateWrite(bool final = false)
    {
        lock (_hsWriteLock)
        {
            if (Volatile.Read(ref _hsClosed) && !final) return;   // a tick racing OnClose never overwrites the final phase
            string json;
            long ver;
            lock (_hsLock)
            {
                _hs.Heartbeat = DateTime.UtcNow;
                _hs.TickAt = HsTickAt();
                json = JsonConvert.SerializeObject(_hs, Formatting.Indented);
                ver = _hsDirtyVer;
            }
            var path = HudStateFilePath;
            var tmp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
                _hsWriteError = null;
                _hsWrites++;
                lock (_hsLock) { _hsWrittenTs = Stopwatch.GetTimestamp(); if (ver > _hsWrittenVer) _hsWrittenVer = ver; }
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
