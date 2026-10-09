using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Measurements need the game in front: in the background the HUD hides its overlay (plugins don't render) and the game
/// may cap itself (its own "background framerate limit", 30 fps by default). Three helpers:
///   - MeasureBegin/MeasureEnd around a trace or profile: tell the user on the guide card (only when the card is idle;
///     else a toast, so an agent's instruction is never replaced) and sample, every 100 ms on a timer (plugin Render
///     doesn't run while the overlay is hidden), whether the game was in front. Results carry {foreground}: the share of
///     the time it was, at start and at end, so every number says if it is trustworthy.
///   - focus.game {}: brings the game window to the front (restore if minimized, SetForegroundWindow with the
///     foreground thread's input attached). The window manager only: never a key or mouse event. Clients call it only
///     when the user asked or agreed, since it takes focus from what they're doing.
///   - game.config {}: the game's display settings that change measurements (background framerate limit, fps caps),
///     read from its production config. The HUD runs elevated, so it can read the game user's profile; only these keys
///     are returned, never the file (it may hold account details).
/// </summary>
public partial class WhatsAnAiBridge
{
    private sealed class Measure
    {
        public string What = "";
        public int Samples, Foreground;
        public bool? AtStart, AtEnd;
        public bool OwnsCard;
        public Timer? Timer;
    }

    private Measure? _measure;
    private readonly object _measureLock = new();

    private string? ProcessFocusMethod(string method, JToken? p) => method switch
    {
        "focus.game" => SafeMemory(FocusGame),
        "game.config" => SafeMemory(GameConfig),
        _ => null,
    };

    private bool GameInFront()
    {
        try { return GameController.Window.IsForeground(); } catch { return false; }
    }

    /// <summary>Start of a measurement of about durationMs: announce it and start sampling focus.</summary>
    private void MeasureBegin(string what, int durationMs)
    {
        lock (_measureLock)
        {
            MeasureStopLocked();
            var m = new Measure { What = what, AtStart = GameInFront() };
            var seconds = Math.Max(1, (int)Math.Round(durationMs / 1000.0));
            if (Settings.ShowAgentGuide.Value)
            {
                var (_, instruction, _, _, status, _, _, _, _, _) = GuideSnapshot();
                var text = $"Keep the game in front for {seconds} s (no alt-tab): the HUD is being measured";
                if (instruction == null && status is "idle" or "done" or "info" or "captured")
                {
                    GuideSet(new JObject { ["title"] = $"Measuring: {what}", ["instruction"] = text, ["status"] = "settling", ["step"] = null, ["steps"] = null, ["detail"] = null });
                    m.OwnsCard = true;
                }
                else GuideLog(new JObject { ["text"] = $"Measuring ({what}, {seconds} s): keep the game in front", ["kind"] = "warn" });
            }
            m.Timer = new Timer(_ =>
            {
                var fg = GameInFront();
                lock (_measureLock) { if (_measure != m) return; m.Samples++; if (fg) m.Foreground++; }
            }, null, 0, 100);
            _measure = m;
        }
    }

    /// <summary>End of the measurement: stop sampling, release the card, and say how much of it had the game in front.</summary>
    private JObject MeasureEnd()
    {
        lock (_measureLock)
        {
            var m = _measure;
            if (m == null) return new JObject();
            m.AtEnd = GameInFront();
            MeasureStopLocked();
            var share = m.Samples == 0 ? (m.AtStart == true && m.AtEnd == true ? 1.0 : 0.0) : (double)m.Foreground / m.Samples;
            if (m.OwnsCard)
                GuideSet(new JObject { ["clear"] = true, ["title"] = "Measured", ["instruction"] = share >= 0.95 ? "Done: thanks, you can switch away" : "Done, but the game wasn't in front the whole time", ["status"] = share >= 0.95 ? "done" : "info" });
            return new JObject
            {
                ["share"] = Math.Round(share, 3), ["atStart"] = m.AtStart, ["atEnd"] = m.AtEnd, ["samples"] = m.Samples,
                ["note"] = share >= 0.95 ? null : "The game wasn't in front for the whole measurement: the overlay was hidden (plugins don't render) and the game may have capped its frame rate (game.config). Ask the user to keep it in front, or focus.game if they agree.",
            };
        }
    }

    private void MeasureStopLocked()
    {
        if (_measure?.Timer is { } t) t.Dispose();
        _measure = null;
    }

    // ── focus.game ───────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private JObject FocusGame()
    {
        IntPtr hwnd;
        try { hwnd = GameController.Window.Process.MainWindowHandle; }
        catch (Exception ex) { return Err("no_window", $"GameController.Window.Process.MainWindowHandle: {ex.Message}"); }
        if (hwnd == IntPtr.Zero) return Err("no_window", "The game process has no main window right now (minimized to the tray or loading).");
        var before = GameInFront();
        if (!before)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);   // SW_RESTORE
            // Windows only lets the foreground thread hand over focus: attach to it for the call (no input is sent).
            var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            var me = GetCurrentThreadId();
            var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }
            Thread.Sleep(150);
        }
        var after = GameInFront();
        return new JObject
        {
            ["ok"] = after, ["wasInFront"] = before, ["inFront"] = after,
            ["message"] = after ? (before ? "The game was already in front." : "The game is in front.")
                : "Windows refused to change the foreground window (it does when another app is busy or a menu is open): ask the user to click the game.",
        };
    }

    // ── game.config ──────────────────────────────────────────────────

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    /// <summary>The profile folder of the user running the game (needs the HUD's elevation to open the game's token), or null.</summary>
    private string? GameOwnerProfile()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            var proc = GameController.Window.Process;
            if (!OpenProcessToken(proc.Handle, 0x0008 /* TOKEN_QUERY */, out token)) return null;
            using var identity = new System.Security.Principal.WindowsIdentity(token);
            var sid = identity.User?.Value;
            if (sid == null) return null;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            return key?.GetValue("ProfileImagePath") as string;
        }
        catch { return null; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }

    /// <summary>The config keys that change what a measurement sees. Nothing else from the file is ever returned.</summary>
    private static readonly string[] ConfigKeys =
    [
        "background_framerate_limit_enabled", "background_framerate_limit", "foreground_framerate_limit_enabled", "foreground_framerate_limit",
        "max_frame_rate", "vsync", "triple_buffer", "fullscreen", "borderless_windowed_fullscreen", "dynamic_resolution_fps", "renderer_type",
    ];

    private JObject GameConfig()
    {
        var game = GameId == "poe2" ? ("Path of Exile 2", "poe2_production_Config.ini") : ("Path of Exile", "production_Config.ini");
        // The game may run as another user (here: a separate account). Its config is in that user's profile: find the
        // owner of the game process (its token's SID -> ProfileList), else take the newest config in any profile.
        FileInfo? file = null;
        var via = "game process owner";
        if (GameOwnerProfile() is { } profile)
        {
            var f = new FileInfo(Path.Combine(profile, "Documents", "My Games", game.Item1, game.Item2));
            try { if (f.Exists) file = f; } catch { }
        }
        if (file == null)
        {
            via = "newest config in any profile (the game owner's profile wasn't readable)";
            var candidates = new List<FileInfo>();
            try
            {
                foreach (var user in Directory.GetDirectories(@"C:\Users"))
                {
                    var f = new FileInfo(Path.Combine(user, "Documents", "My Games", game.Item1, game.Item2));
                    try { if (f.Exists) candidates.Add(f); } catch { }
                }
            }
            catch (Exception ex) { return Err("no_access", $"C:\\Users: {ex.Message}"); }
            file = candidates.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        }
        if (file == null) return Err("not_found", $"No {game.Item2} under C:\\Users\\*\\Documents\\My Games\\{game.Item1} (or the HUD can't read it).");
        var values = new JObject();
        try
        {
            foreach (var line in File.ReadLines(file.FullName))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                if (ConfigKeys.Contains(key)) values[key] = line[(eq + 1)..].Trim();
            }
        }
        catch (Exception ex) { return Err("unreadable", $"{game.Item2}: {ex.Message}"); }
        bool? limitOn = values["background_framerate_limit_enabled"]?.ToString() is { } on ? on == "true" : null;
        return new JObject
        {
            ["ok"] = true, ["file"] = game.Item2, ["profile"] = file.Directory?.Parent?.Parent?.Parent?.Name == Environment.UserName ? "this user" : "another user",
            ["foundVia"] = via,
            ["lastWrite"] = file.LastWriteTimeUtc.ToString("O"), ["settings"] = values,
            ["note"] = limitOn == true
                ? $"The game caps itself to {values["background_framerate_limit"]} fps when it isn't the foreground window (Options > Graphics): measure with it in front, or turn the cap off."
                : null,
        };
    }
}
