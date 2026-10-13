using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// The bridge's settings panel: the generated SettingsMenu (Shared\SettingsMenu.cs) draws every setting from the
/// attributes in WhatsAnAiBridgeSettings (tabs Bridge / Limits / Recording / Permissions / Stats panel / Agent guide),
/// and this class adds what isn't a setting: the live bridge status on top of the Bridge tab, the recording status and
/// saved recordings on top of the Recording tab, the "Show player stats panel" row (a flag of the shared stats view,
/// written through its mutator, not a node) and a Queries tab with the recent query log and the query reference.
/// Teal (#00CED1) accent: the bridge's colour.
/// </summary>
public class WhatsAnAiBridgeSettingsUi
{
    private static readonly Vector4 Accent = new(0f, 0.81f, 0.82f, 1f);   // #00CED1 DarkTurquoise
    private static readonly Vector4 Green = new(0.2f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Yellow = new(1f, 0.82f, 0.2f, 1f);
    private static readonly Vector4 Red = new(1f, 0.3f, 0.3f, 1f);
    private const float ValueColumn = 170f;

    private readonly SettingsMenu _menu;
    private WhatsAnAiBridgeSettings? _s;
    private WhatsAnAiBridge.BridgeStatus? _status;
    private List<WhatsAnAiBridge.QueryLogEntry>? _queryLog;

    // Saved recordings, listed at most every 2 s (the old panel listed the folder every frame).
    private (string name, long size, DateTime written)[] _recordings = [];
    private string? _recordingsNote;
    private DateTime _recordingsAt;

    /// <summary>
    /// Opens/closes the in-HUD stats panel through the shared view's mutator (StatsUiState.PanelOpen),
    /// so the Claude app and agents see the toggle too. Set by the plugin.
    /// </summary>
    public Action<bool>? SetStatsPanelOpen { get; set; }

    public WhatsAnAiBridgeSettingsUi()
    {
        _menu = new SettingsMenu { Accent = Accent, RootTabName = "Bridge" }
            .Tab("Bridge", DrawStatus, first: true)
            .Tab("Recording", DrawRecording, first: true)
            .Tab("Queries", DrawQueries)
            .Custom("StatsUi", DrawStatsPanelRow);
    }

    public void Draw(WhatsAnAiBridgeSettings s, WhatsAnAiBridge.BridgeStatus status, List<WhatsAnAiBridge.QueryLogEntry> queryLog)
    {
        _s = s;
        _status = status;
        _queryLog = queryLog;
        _menu.Draw(s);
    }

    // ---- Bridge tab: live status ----

    private void DrawStatus()
    {
        var status = _status;
        if (status == null) return;
        _menu.SectionHeader("Bridge status");
        var (col, text) = status.State switch
        {
            "idle" => (Green, "Idle - waiting for requests"),
            "processing" => (Yellow, "Processing a query..."),
            _ => (Red, status.State),
        };
        StatusDot(col, pulse: status.State != "idle");
        SettingsMenu.Text(col, text);

        Row("Total queries", status.TotalQueries.ToString());
        Row("Last query", string.IsNullOrEmpty(status.LastQueryType) ? "-" : status.LastQueryType);
        Row("Last response", status.LastResponseTime > 0 ? $"{status.LastResponseTime:F0} ms" : "-");
        Row("Last response size", status.LastResponseSize > 0 ? FormatBytes(status.LastResponseSize) : "-");
        Row("Poll interval", $"{status.PollIntervalMs} ms");
        Row("Recording", status.IsRecording ? $"active ({status.RecordingFrames} frames)" : "idle");
        if (status.LastQueryTimestamp != default)
        {
            var ago = DateTime.UtcNow - status.LastQueryTimestamp;
            Row("Last activity", ago.TotalSeconds < 60 ? $"{ago.TotalSeconds:F0} s ago"
                : ago.TotalMinutes < 60 ? $"{ago.TotalMinutes:F0} min ago" : $"{ago.TotalHours:F1} h ago");
        }
        if (!string.IsNullOrEmpty(status.LastError))
        {
            _menu.SectionHeader("Last error");
            ImGui.PushTextWrapPos(0f);
            SettingsMenu.Text(Red, status.LastError.Length > 400 ? status.LastError[..400] + "..." : status.LastError);
            ImGui.PopTextWrapPos();
        }
        ImGui.Spacing();
    }

    // ---- Recording tab: status and saved recordings, above the recording settings ----

    private void DrawRecording()
    {
        var status = _status;
        if (status == null) return;
        _menu.SectionHeader("Recording status");
        StatusDot(status.IsRecording ? Red : Green, pulse: status.IsRecording);
        SettingsMenu.Text(status.IsRecording ? Red : Green, status.IsRecording ? "RECORDING" : "IDLE");
        Row("Frames captured", status.RecordingFrames.ToString());
        if (status.IsRecording)
        {
            var e = TimeSpan.FromMilliseconds(status.RecordingElapsedMs);
            Row("Elapsed", $"{e.Minutes:D2}:{e.Seconds:D2}.{e.Milliseconds / 100}");
        }
        if (!string.IsNullOrEmpty(status.RecordingFile)) Row("File", Path.GetFileName(status.RecordingFile));

        _menu.SectionHeader("Saved recordings", "Files in <bridge folder>\\recordings (record:start / record:stop).");
        RefreshRecordings();
        if (_recordingsNote != null) SettingsMenu.Dim(_recordingsNote);
        foreach (var (name, size, written) in _recordings)
        {
            SettingsMenu.Text(Accent, name);
            ImGui.SameLine(Math.Max(ValueColumn + 60f, ImGui.GetContentRegionAvail().X * 0.55f));
            ImGui.TextUnformatted(FormatBytes(size));
            ImGui.SameLine();
            SettingsMenu.Dim(written.ToString("yyyy-MM-dd HH:mm"));
        }
        ImGui.Spacing();
    }

    private void RefreshRecordings()
    {
        if (DateTime.UtcNow - _recordingsAt < TimeSpan.FromSeconds(2) || _s == null) return;
        _recordingsAt = DateTime.UtcNow;
        try
        {
            var dir = Path.Combine(_s.BridgeDirectory.Value ?? "", "recordings");
            if (!Directory.Exists(dir)) { _recordings = []; _recordingsNote = "No recordings folder yet."; return; }
            _recordings = Directory.GetFiles(dir, "*.jsonl").OrderByDescending(f => f)
                .Select(f => new FileInfo(f)).Select(fi => (fi.Name, fi.Length, fi.LastWriteTime)).ToArray();
            _recordingsNote = _recordings.Length == 0 ? "No recordings yet. Use record:start to begin." : null;
        }
        catch (Exception ex) { _recordings = []; _recordingsNote = $"Can't list the recordings folder: {ex.GetType().Name}: {ex.Message}"; }
    }

    // ---- Stats panel tab: the shared view's PanelOpen flag ----

    private void DrawStatsPanelRow()
    {
        var s = _s;
        if (s == null) return;
        SettingsMenu.Label("Player stats panel", "In-game panel of the shared stats view: pins, filter and selection sync with the Claude app.");
        ImGui.SameLine(_menu.ControlColumn);
        var open = s.StatsUi.PanelOpen;
        if (_menu.PillToggle("##stats_panel_open", ref open, "StatsUi.PanelOpen")) SetStatsPanelOpen?.Invoke(open);
    }

    // ---- Queries tab: recent queries and the query reference ----

    private void DrawQueries()
    {
        _menu.SectionHeader("Recent queries");
        var log = _queryLog;
        if (log == null || log.Count == 0) SettingsMenu.Dim("No queries yet.");
        else
        {
            float w = ImGui.GetContentRegionAvail().X;
            float c1 = 80f, c2 = w * 0.55f, c3 = w * 0.72f, c4 = w * 0.88f;
            SettingsMenu.Dim("Time"); ImGui.SameLine(c1); SettingsMenu.Dim("Query"); ImGui.SameLine(c2); SettingsMenu.Dim("Duration");
            ImGui.SameLine(c3); SettingsMenu.Dim("Size"); ImGui.SameLine(c4); SettingsMenu.Dim("Status");
            for (int i = log.Count - 1; i >= 0; i--)
            {
                var e = log[i];
                SettingsMenu.Dim(e.Timestamp.ToString("HH:mm:ss"));
                ImGui.SameLine(c1);
                SettingsMenu.Text(e.Success ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : Red, e.Query.Length > 30 ? e.Query[..30] + "..." : e.Query);
                if (e.Query.Length > 30 && ImGui.IsItemHovered()) SettingsMenu.Tooltip(e.Query);
                ImGui.SameLine(c2);
                SettingsMenu.Text(Accent, $"{e.DurationMs:F0} ms");
                ImGui.SameLine(c3);
                ImGui.TextUnformatted(FormatBytes(e.ResponseSize));
                ImGui.SameLine(c4);
                SettingsMenu.Text(e.Success ? Green : Red, e.Success ? "ok" : "failed");
            }
        }

        _menu.SectionHeader("Query reference", "query types for method \"query\" (params.type) and the legacy request file.");
        Ref("all", "Full dump: player + area + NPC dialog + map data + entities");
        Ref("player", "Player vitals, position, buffs");
        Ref("area", "Current zone name, level, act");
        Ref("entities[:range]", "All nearby entities (default 200 range)");
        Ref("monsters", "Alive hostile monsters only");
        Ref("items", "World items (drops on ground)");
        Ref("deep:Filter[:range]", "Deep component dump for entities matching Filter in path");
        Ref("npcdialog", "NPC dialog state: visible, name, lines, dialog depth");
        Ref("mapdata", "Map stats, Djinn quest flags, dialog depth");
        Ref("ui", "Scan all visible UI panels with child text (2 levels deep)");
        _menu.SectionHeader("Recording and playback");
        Ref("record:start[:interval]", "Start recording snapshots (optional interval in ms)");
        Ref("record:stop", "Stop recording, returns summary with frame count and file");
        Ref("record:status", "Check current recording state");
        Ref("snapshot", "Capture a single snapshot (also written to file if recording)");
        Ref("recording:list", "List saved .jsonl recording files");
        Ref("recording:load:filename", "Load a recording for playback/analysis");
        Ref("recording:frame:N", "Read frame N from the loaded recording");
        Ref("recording:range:N:M", "Read frames N through M");
        Ref("recording:search:term", "Find frames containing term in entity/buff data");
        Ref("recording:summary", "Analyze the loaded recording: entities, buffs, timestamps");
        ImGui.Spacing();
    }

    // ---- helpers ----

    private static void Row(string label, string value)
    {
        SettingsMenu.Dim(label);
        ImGui.SameLine(ValueColumn);
        ImGui.TextUnformatted(value);
    }

    private static void Ref(string query, string desc)
    {
        SettingsMenu.Text(Accent, query);
        ImGui.SameLine(Math.Max(ValueColumn + 40f, ImGui.CalcTextSize(query).X + 24f));
        SettingsMenu.Dim(desc);
    }

    /// <summary>A small status dot before the next text on the same line, pulsing while something is going on.</summary>
    private static void StatusDot(Vector4 col, bool pulse)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetTextLineHeight();
        var c = new Vector2(p.X + 7, p.Y + h * 0.5f);
        dl.AddCircleFilled(c, 5f, ImGui.GetColorU32(col));
        if (pulse)
        {
            float a = (float)(0.3 + 0.7 * Math.Abs(Math.Sin(ImGui.GetTime() * 2.5)));
            dl.AddCircle(c, 8f, ImGui.GetColorU32(col with { W = a * 0.5f }), 12, 1.5f);
        }
        ImGui.Dummy(new Vector2(18f, h));
        ImGui.SameLine();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB",
    };
}
