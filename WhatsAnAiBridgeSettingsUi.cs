using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore.Shared.Nodes;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// Self-contained neon-styled settings panel for WhatsAnAiBridge.
/// Teal (#00CED1) accent  - dev-tool / bridge theme.
/// Custom DrawList widgets, tabbed layout, live status display.
/// </summary>
public class WhatsAnAiBridgeSettingsUi
{
    private int _activeTab;
    private readonly Dictionary<string, float> _anims = new();

    private static readonly string[] Tabs = { "Status", "Settings", "Query Log", "Recording", "Guide" };

    // ── Palette ─────────────────────────────────────────────────────
    private static uint Accent => Col(0f, 0.81f, 0.82f);       // #00CED1 DarkTurquoise
    private static uint AccentDim => Col(0f, 0.50f, 0.52f);
    private static uint AccentSoft => Col(0f, 0.81f, 0.82f, 0.15f);
    private static uint Label => Col(0.88f, 0.88f, 0.88f);
    private static uint Desc => Col(0.38f, 0.40f, 0.44f);
    private static uint TabOff => Col(0.48f, 0.48f, 0.52f);
    private static uint CardBg => Col(0.05f, 0.05f, 0.07f, 1f);
    private static uint Green => Col(0.2f, 0.9f, 0.4f);
    private static uint Yellow => Col(1f, 0.82f, 0.2f);
    private static uint Red => Col(1f, 0.3f, 0.3f);
    private const float Row = 34f;
    private const float RowText = 40f;

    // ── Entry point ─────────────────────────────────────────────────

    public void Draw(WhatsAnAiBridgeSettings s, WhatsAnAiBridge.BridgeStatus status, List<WhatsAnAiBridge.QueryLogEntry> queryLog)
    {
        var contentMin = ImGui.GetCursorScreenPos();
        float contentW = ImGui.GetContentRegionAvail().X;
        var dl = ImGui.GetWindowDrawList();

        // Tab bar
        float tabH = 26f;
        float tabW = contentW / Tabs.Length;

        for (int i = 0; i < Tabs.Length; i++)
        {
            var tMin = new Vector2(contentMin.X + i * tabW, contentMin.Y);
            var tMax = new Vector2(contentMin.X + (i + 1) * tabW, contentMin.Y + tabH);
            bool active = i == _activeTab;

            if (active)
                dl.AddRectFilled(tMin, tMax, WithAlpha(Accent, 0.12f));

            ImGui.SetCursorScreenPos(tMin);
            ImGui.InvisibleButton($"##cb_tab_{i}", tMax - tMin);
            bool hov = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) _activeTab = i;

            if (hov && !active)
                dl.AddRectFilled(tMin, tMax, WithAlpha(Accent, 0.06f));
            if (active)
                dl.AddLine(tMin with { Y = tMax.Y - 1 } + new Vector2(3, 0),
                    tMax - new Vector2(3, 1), Accent, 2f);

            uint tc = active ? Accent : (hov ? AccentDim : TabOff);
            CenterText(dl, Tabs[i], (tMin + tMax) * 0.5f, tc);
        }

        float sepY = contentMin.Y + tabH + 1;
        dl.AddLine(new Vector2(contentMin.X, sepY),
            new Vector2(contentMin.X + contentW, sepY), WithAlpha(Accent, 0.10f), 1f);

        // Content area
        ImGui.SetCursorScreenPos(new Vector2(contentMin.X, sepY + 3));
        var avail = new Vector2(contentW, ImGui.GetContentRegionAvail().Y);
        ImGui.BeginChild("##cb_content", avail, ImGuiChildFlags.None, ImGuiWindowFlags.None);

        var cdl = ImGui.GetWindowDrawList();
        var cMin = ImGui.GetWindowPos();
        var cSz = ImGui.GetWindowSize();

        cdl.AddRectFilled(cMin, cMin + cSz, CardBg, 3f);
        float pulse = (float)(0.4 + 0.3 * Math.Sin(ImGui.GetTime() * 1.8));
        cdl.AddRect(cMin, cMin + cSz, WithAlpha(Accent, pulse * 0.18f), 3f, ImDrawFlags.None, 1f);

        float scrollY = ImGui.GetScrollY();
        float y = cMin.Y + 10 - scrollY;
        float x = cMin.X + 12;
        float cx = cMin.X + cSz.X * 0.50f;
        float sw = cSz.X * 0.40f;

        switch (_activeTab)
        {
            case 0: TabStatus(cdl, status, x, cx, ref y, sw, cSz.X - 24); break;
            case 1: TabSettings(cdl, s, x, cx, ref y, sw); break;
            case 2: TabQueryLog(cdl, queryLog, x, ref y, cSz.X - 24); break;
            case 3: TabRecording(cdl, status, s, x, cx, ref y, sw, cSz.X - 24); break;
            case 4: TabGuide(cdl, x, ref y, cSz.X - 24); break;
        }

        ImGui.SetCursorScreenPos(new Vector2(x, y));
        ImGui.Dummy(new Vector2(1, 4));
        ImGui.EndChild();
    }

    // ── Tabs ────────────────────────────────────────────────────────

    private void TabStatus(ImDrawListPtr dl, WhatsAnAiBridge.BridgeStatus status,
        float x, float cx, ref float y, float sw, float width)
    {
        SectionHeader(dl, x, ref y, "Bridge Status");

        // Status indicator
        uint statusCol = status.State switch
        {
            "idle" => Green,
            "processing" => Yellow,
            _ => Red
        };
        string statusText = status.State switch
        {
            "idle" => "Idle  - waiting for request",
            "processing" => "Processing query...",
            _ => status.State
        };

        dl.AddCircleFilled(new Vector2(x + 6, y + 8), 6f, statusCol);

        // Pulse animation for the status dot
        float statusPulse = (float)(0.3 + 0.7 * Math.Abs(Math.Sin(ImGui.GetTime() * 2.0)));
        dl.AddCircle(new Vector2(x + 6, y + 8), 8f, WithAlpha(statusCol, statusPulse * 0.5f), 12, 1.5f);

        dl.AddText(new Vector2(x + 20, y + 1), Label, statusText);
        y += 24;

        // Stats
        SectionHeader(dl, x, ref y, "Statistics");
        StatusRow(dl, x, cx, ref y, "Total Queries", status.TotalQueries.ToString());
        StatusRow(dl, x, cx, ref y, "Last Query", string.IsNullOrEmpty(status.LastQueryType) ? " -" : status.LastQueryType);
        StatusRow(dl, x, cx, ref y, "Last Response", status.LastResponseTime > 0 ? $"{status.LastResponseTime:F0}ms" : " -");
        StatusRow(dl, x, cx, ref y, "Last Response Size", status.LastResponseSize > 0 ? FormatBytes(status.LastResponseSize) : " -");
        StatusRow(dl, x, cx, ref y, "Poll Interval", $"{status.PollIntervalMs}ms");
        StatusRow(dl, x, cx, ref y, "Recording", status.IsRecording ? $"active ({status.RecordingFrames} frames)" : "idle");

        if (status.LastQueryTimestamp != default)
        {
            var ago = DateTime.UtcNow - status.LastQueryTimestamp;
            string agoStr = ago.TotalSeconds < 60 ? $"{ago.TotalSeconds:F0}s ago"
                : ago.TotalMinutes < 60 ? $"{ago.TotalMinutes:F0}m ago"
                : $"{ago.TotalHours:F1}h ago";
            StatusRow(dl, x, cx, ref y, "Last Activity", agoStr);
        }

        y += 8;

        // Last error
        if (!string.IsNullOrEmpty(status.LastError))
        {
            SectionHeader(dl, x, ref y, "Last Error");
            dl.AddText(new Vector2(x + 4, y), Red, status.LastError.Length > 120 ? status.LastError[..120] + "..." : status.LastError);
            y += 20;
        }
    }

    private void TabSettings(ImDrawListPtr dl, WhatsAnAiBridgeSettings s,
        float x, float cx, ref float y, float sw)
    {
        SectionHeader(dl, x, ref y, "Bridge");
        IntSlider("cb_poll", s.PollIntervalMs, dl, x, cx, ref y, sw,
            "Poll Interval (ms)", "How often to check for request.txt");
        TextInput("cb_dir", s.BridgeDirectory, dl, x, cx, ref y, sw,
            "Bridge Directory", "Path where request.txt and response.json live");

        SectionHeader(dl, x, ref y, "Status HUD");
        Toggle("cb_hud", s.ShowStatusHud, dl, x, cx, ref y,
            "Show Status HUD", "Display a small bridge status indicator on screen");
        IntSlider("cb_hx", s.HudX, dl, x, cx, ref y, sw,
            "HUD X", "Horizontal position of the status HUD");
        IntSlider("cb_hy", s.HudY, dl, x, cx, ref y, sw,
            "HUD Y", "Vertical position of the status HUD");

        SectionHeader(dl, x, ref y, "Query Limits");
        IntSlider("cb_er", s.MaxEntityRange, dl, x, cx, ref y, sw,
            "Default Entity Range", "Default max distance for entity queries");
        IntSlider("cb_ds", s.MaxDeepStats, dl, x, cx, ref y, sw,
            "Deep Scan Max Stats", "Maximum stat entries per entity in deep scans");
        IntSlider("cb_uc", s.MaxUiChildren, dl, x, cx, ref y, sw,
            "UI Scan Depth", "How many UI children to scan in 'ui' queries");

        SectionHeader(dl, x, ref y, "Recording");
        IntSlider("cb_ri", s.RecordingIntervalMs, dl, x, cx, ref y, sw,
            "Recording Interval (ms)", "Time between snapshot captures during recording");
        IntSlider("cb_rr", s.RecordingEntityRange, dl, x, cx, ref y, sw,
            "Recording Entity Range", "Max distance for entities in recording snapshots");
        Toggle("cb_rd", s.AutoDeepScanBosses, dl, x, cx, ref y,
            "Auto Deep Scan Bosses", "Full deep scan for Unique/Rare entities during recording");
        IntSlider("cb_rs", s.RecordingMaxDeepStats, dl, x, cx, ref y, sw,
            "Recording Max Stats", "Maximum stat entries per deep-scanned entity");
    }

    private void TabQueryLog(ImDrawListPtr dl, List<WhatsAnAiBridge.QueryLogEntry> log,
        float x, ref float y, float width)
    {
        SectionHeader(dl, x, ref y, "Recent Queries");

        if (log.Count == 0)
        {
            dl.AddText(new Vector2(x + 4, y), Desc, "No queries yet. Write a request.txt to start.");
            y += 20;
            return;
        }

        // Column headers
        dl.AddText(new Vector2(x + 4, y), AccentDim, "Time");
        dl.AddText(new Vector2(x + 80, y), AccentDim, "Query");
        dl.AddText(new Vector2(x + width * 0.55f, y), AccentDim, "Duration");
        dl.AddText(new Vector2(x + width * 0.72f, y), AccentDim, "Size");
        dl.AddText(new Vector2(x + width * 0.88f, y), AccentDim, "Status");
        y += 18;
        dl.AddLine(new Vector2(x, y), new Vector2(x + width, y), WithAlpha(Accent, 0.15f), 1f);
        y += 4;

        // Show newest first
        for (int i = log.Count - 1; i >= 0; i--)
        {
            var entry = log[i];
            uint rowCol = entry.Success ? Label : Red;

            dl.AddText(new Vector2(x + 4, y), Desc, entry.Timestamp.ToString("HH:mm:ss"));
            dl.AddText(new Vector2(x + 80, y), rowCol, entry.Query.Length > 30 ? entry.Query[..30] + "..." : entry.Query);
            dl.AddText(new Vector2(x + width * 0.55f, y), Accent, $"{entry.DurationMs:F0}ms");
            dl.AddText(new Vector2(x + width * 0.72f, y), Label, FormatBytes(entry.ResponseSize));

            uint statusBadge = entry.Success ? Green : Red;
            dl.AddCircleFilled(new Vector2(x + width * 0.88f + 6, y + 7), 4f, statusBadge);

            y += 18;
        }
    }

    private void TabRecording(ImDrawListPtr dl, WhatsAnAiBridge.BridgeStatus status,
        WhatsAnAiBridgeSettings s, float x, float cx, ref float y, float sw, float width)
    {
        SectionHeader(dl, x, ref y, "Recording Status");

        // Recording indicator
        if (status.IsRecording)
        {
            float pulse = (float)(0.4 + 0.6 * Math.Abs(Math.Sin(ImGui.GetTime() * 3.0)));
            dl.AddCircleFilled(new Vector2(x + 8, y + 8), 8f, WithAlpha(Red, pulse));
            dl.AddCircle(new Vector2(x + 8, y + 8), 11f, WithAlpha(Red, pulse * 0.5f), 12, 1.5f);
            dl.AddText(new Vector2(x + 24, y + 1), Red, "RECORDING");
        }
        else
        {
            dl.AddCircleFilled(new Vector2(x + 8, y + 8), 8f, Green);
            dl.AddText(new Vector2(x + 24, y + 1), Green, "IDLE");
        }
        y += 24;

        // Stats
        StatusRow(dl, x, cx, ref y, "Frames Captured", status.RecordingFrames.ToString());
        if (status.IsRecording)
        {
            var elapsed = TimeSpan.FromMilliseconds(status.RecordingElapsedMs);
            StatusRow(dl, x, cx, ref y, "Elapsed", $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}.{elapsed.Milliseconds / 100}");
        }
        if (!string.IsNullOrEmpty(status.RecordingFile))
        {
            var fileName = System.IO.Path.GetFileName(status.RecordingFile);
            StatusRow(dl, x, cx, ref y, "File", fileName);
            try
            {
                if (System.IO.File.Exists(status.RecordingFile))
                {
                    var fi = new System.IO.FileInfo(status.RecordingFile);
                    StatusRow(dl, x, cx, ref y, "File Size", FormatBytes(fi.Length));
                }
            }
            catch { }
        }

        y += 8;

        // Recordings list
        SectionHeader(dl, x, ref y, "Saved Recordings");
        try
        {
            var recDir = System.IO.Path.Combine(s.BridgeDirectory.Value, "recordings");
            if (System.IO.Directory.Exists(recDir))
            {
                var files = System.IO.Directory.GetFiles(recDir, "*.jsonl");
                if (files.Length == 0)
                {
                    dl.AddText(new Vector2(x + 4, y), Desc, "No recordings yet. Use record:start to begin.");
                    y += 20;
                }
                else
                {
                    foreach (var f in files.OrderByDescending(f => f))
                    {
                        var fi = new System.IO.FileInfo(f);
                        dl.AddText(new Vector2(x + 4, y), Accent, fi.Name);
                        dl.AddText(new Vector2(x + width * 0.55f, y), Label, FormatBytes(fi.Length));
                        dl.AddText(new Vector2(x + width * 0.75f, y), Desc, fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                        y += 18;
                    }
                }
            }
            else
            {
                dl.AddText(new Vector2(x + 4, y), Desc, "No recordings directory yet.");
                y += 20;
            }
        }
        catch { }
    }

    private void TabGuide(ImDrawListPtr dl, float x, ref float y, float width)
    {
        SectionHeader(dl, x, ref y, "How It Works");
        dl.AddText(new Vector2(x + 4, y), Label, "Claude writes a query to request.txt in the bridge directory.");
        y += 16;
        dl.AddText(new Vector2(x + 4, y), Label, "This plugin reads it, processes the query, and writes response.json.");
        y += 16;
        dl.AddText(new Vector2(x + 4, y), Label, "Claude reads the response and analyzes the game state.");
        y += 24;

        SectionHeader(dl, x, ref y, "Available Queries");
        QueryGuideEntry(dl, x, ref y, width, "all", "Full dump: player + area + NPC dialog + map data + entities");
        QueryGuideEntry(dl, x, ref y, width, "player", "Player vitals, position, buffs");
        QueryGuideEntry(dl, x, ref y, width, "area", "Current zone name, level, act");
        QueryGuideEntry(dl, x, ref y, width, "entities[:range]", "All nearby entities (default 200 range)");
        QueryGuideEntry(dl, x, ref y, width, "monsters", "Alive hostile monsters only");
        QueryGuideEntry(dl, x, ref y, width, "items", "World items (drops on ground)");
        QueryGuideEntry(dl, x, ref y, width, "deep:Filter[:range]", "Deep component dump for entities matching Filter in path");
        QueryGuideEntry(dl, x, ref y, width, "npcdialog", "NPC dialog state: visible, name, lines, dialog depth");
        QueryGuideEntry(dl, x, ref y, width, "mapdata", "Map stats, Djinn quest flags, dialog depth");
        QueryGuideEntry(dl, x, ref y, width, "ui", "Scan all visible UI panels with child text (2 levels deep)");

        y += 8;
        SectionHeader(dl, x, ref y, "Recording Commands");
        QueryGuideEntry(dl, x, ref y, width, "record:start[:interval]", "Start recording snapshots (optional interval in ms)");
        QueryGuideEntry(dl, x, ref y, width, "record:stop", "Stop recording, returns summary with frame count and file");
        QueryGuideEntry(dl, x, ref y, width, "record:status", "Check current recording state");
        QueryGuideEntry(dl, x, ref y, width, "snapshot", "Capture single snapshot (also written to file if recording)");

        y += 8;
        SectionHeader(dl, x, ref y, "Playback Commands");
        QueryGuideEntry(dl, x, ref y, width, "recording:list", "List saved .jsonl recording files");
        QueryGuideEntry(dl, x, ref y, width, "recording:load:filename", "Load a recording for playback/analysis");
        QueryGuideEntry(dl, x, ref y, width, "recording:frame:N", "Read frame N from loaded recording");
        QueryGuideEntry(dl, x, ref y, width, "recording:range:N:M", "Read frames N through M");
        QueryGuideEntry(dl, x, ref y, width, "recording:search:term", "Find frames containing term in entity/buff data");
        QueryGuideEntry(dl, x, ref y, width, "recording:summary", "Analyze loaded recording: entities, buffs, timestamps");

        y += 8;
        SectionHeader(dl, x, ref y, "Snapshot Data (per frame)");
        dl.AddText(new Vector2(x + 4, y), Label, "Each snapshot captures the full game state:"); y += 18;
        dl.AddText(new Vector2(x + 8, y), Accent, "Player"); y += 16;
        dl.AddText(new Vector2(x + 16, y), Desc, "HP/ES/Mana, position, rotation, actor (action, animation,"); y += 16;
        dl.AddText(new Vector2(x + 16, y), Desc, "current skill/destination/target), buffs (stacks, maxTime, source)"); y += 20;
        dl.AddText(new Vector2(x + 8, y), Accent, "Entities (adaptive depth)"); y += 16;
        dl.AddText(new Vector2(x + 16, y), Desc, "Unique/Rare: deep scan with Life, Actor, Buffs, StateMachine,"); y += 16;
        dl.AddText(new Vector2(x + 16, y), Desc, "Stats, Targetable, OMP, Render, Positioned"); y += 16;
        dl.AddText(new Vector2(x + 16, y), Desc, "Normal/Magic: id, type, path, name, alive, hostile, rarity, hp"); y += 20;
        dl.AddText(new Vector2(x + 8, y), Accent, "Visual Effects (all entities)"); y += 16;
        dl.AddText(new Vector2(x + 16, y), Desc, "Beam (start/end positions), GroundEffect (duration, scale),"); y += 16;
        dl.AddText(new Vector2(x + 16, y), Desc, "EffectPack (presence flag), AnimationController (id, progress, speed)"); y += 16;
        dl.AddText(new Vector2(x + 16, y), Desc, "Entities with effects auto-promoted to deep scan"); y += 20;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static void StatusRow(ImDrawListPtr dl, float x, float cx, ref float y, string label, string value)
    {
        dl.AddText(new Vector2(x + 6, y), Desc, label);
        dl.AddText(new Vector2(cx, y), Label, value);
        y += 20;
    }

    private static void QueryGuideEntry(ImDrawListPtr dl, float x, ref float y, float width, string query, string desc)
    {
        dl.AddText(new Vector2(x + 8, y), Accent, query);
        y += 16;
        dl.AddText(new Vector2(x + 16, y), Desc, desc);
        y += 20;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }

    // ── Section header ──────────────────────────────────────────────

    private static void SectionHeader(ImDrawListPtr dl, float x, ref float y, string title)
    {
        y += 4f;
        dl.AddText(new Vector2(x, y), Accent, title);
        y += 18f;
        dl.AddLine(new Vector2(x, y - 4), new Vector2(x + ImGui.CalcTextSize(title).X + 40, y - 4),
            WithAlpha(Accent, 0.25f), 1f);
    }

    // ── Widget primitives ───────────────────────────────────────────

    private void Toggle(string key, ToggleNode node, ImDrawListPtr dl,
        float x, float cx, ref float y, string label, string desc)
    {
        dl.AddText(new Vector2(x + 6, y + 1), Label, label);
        dl.AddText(new Vector2(x + 6, y + 16), Desc, desc);
        ImGui.SetCursorScreenPos(new Vector2(cx, y + 5));
        var v = node.Value;
        _anims.TryGetValue(key, out float a);
        if (PillToggle($"##{key}", ref v, ref a))
            node.Value = v;
        _anims[key] = a;
        y += Row;
    }

    private void IntSlider(string key, RangeNode<int> node, ImDrawListPtr dl,
        float x, float cx, ref float y, float sw, string label, string desc)
    {
        dl.AddText(new Vector2(x + 6, y + 1), Label, label);
        dl.AddText(new Vector2(x + 6, y + 16), Desc, desc);
        ImGui.SetCursorScreenPos(new Vector2(cx, y + 7));
        var v = node.Value;
        if (SliderInt($"##{key}", ref v, node.Min, node.Max, sw))
            node.Value = v;
        dl.AddText(new Vector2(cx + sw + 6, y + 7), Accent, v.ToString());
        y += Row;
    }

    private void TextInput(string key, TextNode node, ImDrawListPtr dl,
        float x, float cx, ref float y, float sw, string label, string desc)
    {
        dl.AddText(new Vector2(x + 6, y + 1), Label, label);
        dl.AddText(new Vector2(x + 6, y + 16), Desc, desc);

        ImGui.SetCursorScreenPos(new Vector2(cx, y + 3));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.07f, 0.07f, 0.09f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.88f, 0.88f, 0.88f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0.50f, 0.52f, 0.6f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);

        string val = node.Value ?? "";
        ImGui.SetNextItemWidth(sw);
        if (ImGui.InputText($"##{key}", ref val, 512))
            node.Value = val;

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(3);
        y += RowText;
    }

    // ── Self-contained drawing helpers ──────────────────────────────

    private static bool PillToggle(string id, ref bool value, ref float animState)
    {
        var dl = ImGui.GetWindowDrawList();
        var cur = ImGui.GetCursorScreenPos();
        const float w = 40f, h = 20f, r = h * 0.5f;

        ImGui.InvisibleButton(id, new Vector2(w, h));
        bool changed = ImGui.IsItemClicked();
        if (changed) value = !value;

        float dt = ImGui.GetIO().DeltaTime;
        float target = value ? 1f : 0f;
        animState = Math.Clamp(animState + (target - animState) * Math.Min(dt * 10f, 1f), 0f, 1f);

        uint trackOff = ImGui.GetColorU32(new Vector4(0.2f, 0.2f, 0.2f, 1f));
        uint trackColor = LerpCol(trackOff, Accent, animState);
        dl.AddRectFilled(cur, cur + new Vector2(w, h), trackColor, r);

        float knobX = cur.X + r + animState * (w - h);
        uint knobCol = ImGui.GetColorU32(new Vector4(
            0.5f + 0.5f * animState, 0.5f + 0.5f * animState,
            0.5f + 0.5f * animState, 1f));
        dl.AddCircleFilled(new Vector2(knobX, cur.Y + r), r - 2f, knobCol);

        return changed;
    }

    private static bool SliderInt(string id, ref int value, int min, int max, float width)
    {
        var dl = ImGui.GetWindowDrawList();
        var cur = ImGui.GetCursorScreenPos();
        const float h = 16f, th = 4f, tr = 7f;

        ImGui.InvisibleButton(id, new Vector2(width, h));
        bool changed = false;
        if (ImGui.IsItemActive())
        {
            float frac = Math.Clamp((ImGui.GetMousePos().X - cur.X) / width, 0f, 1f);
            int nv = min + (int)(frac * (max - min));
            if (nv != value) { value = nv; changed = true; }
        }

        float trackY = cur.Y + (h - th) * 0.5f;
        dl.AddRectFilled(cur with { Y = trackY }, new Vector2(cur.X + width, trackY + th),
            ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f)), 2f);

        float f = (max > min) ? (float)(value - min) / (max - min) : 0f;
        float fw = f * width;
        dl.AddRectFilled(cur with { Y = trackY }, new Vector2(cur.X + fw, trackY + th), Accent, 2f);

        float tx = cur.X + fw, ty = cur.Y + h * 0.5f;
        dl.AddCircleFilled(new Vector2(tx, ty), tr, Accent);
        dl.AddCircleFilled(new Vector2(tx, ty), tr - 2f,
            ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.95f, 1f)));

        return changed;
    }

    // ── Color utilities ─────────────────────────────────────────────

    private static uint Col(float r, float g, float b, float a = 1f)
        => ImGui.GetColorU32(new Vector4(r, g, b, a));

    private static uint WithAlpha(uint color, float alpha)
    {
        var v = ImGui.ColorConvertU32ToFloat4(color);
        v.W = alpha;
        return ImGui.GetColorU32(v);
    }

    private static uint LerpCol(uint a, uint b, float t)
    {
        var va = ImGui.ColorConvertU32ToFloat4(a);
        var vb = ImGui.ColorConvertU32ToFloat4(b);
        return ImGui.GetColorU32(new Vector4(
            va.X + (vb.X - va.X) * t, va.Y + (vb.Y - va.Y) * t,
            va.Z + (vb.Z - va.Z) * t, va.W + (vb.W - va.W) * t));
    }

    private static void CenterText(ImDrawListPtr dl, string text, Vector2 center, uint color)
    {
        var sz = ImGui.CalcTextSize(text);
        dl.AddText(center - sz * 0.5f, color, text);
    }
}
