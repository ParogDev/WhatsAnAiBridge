using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;
using Vector2N = System.Numerics.Vector2;
using Color = SharpDX.Color;

namespace WhatsAnAiBridge;

public class WhatsAnAiBridge : BaseSettingsPlugin<WhatsAnAiBridgeSettings>
{
    // ── Public types for UI ──────────────────────────────────────────

    public class BridgeStatus
    {
        public string State = "idle";
        public int TotalQueries;
        public string LastQueryType = "";
        public double LastResponseTime;
        public long LastResponseSize;
        public DateTime LastQueryTimestamp;
        public string LastError = "";
        public int PollIntervalMs;
        public bool IsRecording;
        public int RecordingFrames;
        public double RecordingElapsedMs;
        public string RecordingFile = "";
    }

    public class QueryLogEntry
    {
        public DateTime Timestamp;
        public string Query = "";
        public double DurationMs;
        public long ResponseSize;
        public bool Success;
    }

    // ── State ────────────────────────────────────────────────────────

    private string _bridgeDir = "";
    private string _requestFile = "";
    private string _responseFile = "";
    private DateTime _lastCheck = DateTime.MinValue;
    private readonly BridgeStatus _status = new();
    private readonly List<QueryLogEntry> _queryLog = new();
    private const int MaxLogEntries = 50;
    private WhatsAnAiBridgeSettingsUi? _settingsUi;

    // Recording state
    private bool _isRecording;
    private DateTime _recordingStart;
    private DateTime _lastSnapshot;
    private int _frameCount;
    private StreamWriter? _recordingWriter;
    private string? _currentRecordingPath;
    // Playback state
    private string? _loadedRecordingPath;
    private List<long>? _loadedFrameOffsets;

    // ── Lifecycle ────────────────────────────────────────────────────

    public override bool Initialise()
    {
        UpdatePaths();
        return true;
    }

    private void UpdatePaths()
    {
        _bridgeDir = Settings.BridgeDirectory.Value;
        if (!Directory.Exists(_bridgeDir))
            Directory.CreateDirectory(_bridgeDir);
        _requestFile = Path.Combine(_bridgeDir, "request.txt");
        _responseFile = Path.Combine(_bridgeDir, "response.json");
    }

    // ── Main loop ────────────────────────────────────────────────────

    public override void Render()
    {
        // Poll for requests at configurable interval
        var now = DateTime.UtcNow;
        if ((now - _lastCheck).TotalMilliseconds < Settings.PollIntervalMs.Value) goto hud;
        _lastCheck = now;
        _status.PollIntervalMs = Settings.PollIntervalMs.Value;

        if (!File.Exists(_requestFile)) goto hud;

        var sw = Stopwatch.StartNew();
        var logEntry = new QueryLogEntry { Timestamp = DateTime.Now };

        try
        {
            _status.State = "processing";
            var query = File.ReadAllText(_requestFile).Trim();
            File.Delete(_requestFile);

            logEntry.Query = query;
            _status.LastQueryType = query;
            _status.LastQueryTimestamp = DateTime.UtcNow;

            var result = ProcessQuery(query);
            File.WriteAllText(_responseFile, result);

            sw.Stop();
            logEntry.DurationMs = sw.Elapsed.TotalMilliseconds;
            logEntry.ResponseSize = result.Length;
            logEntry.Success = true;

            _status.TotalQueries++;
            _status.LastResponseTime = logEntry.DurationMs;
            _status.LastResponseSize = logEntry.ResponseSize;
            _status.LastError = "";
            _status.State = "idle";
        }
        catch (Exception ex)
        {
            sw.Stop();
            logEntry.DurationMs = sw.Elapsed.TotalMilliseconds;
            logEntry.Success = false;

            _status.LastError = ex.Message;
            _status.State = "idle";

            try { File.WriteAllText(_responseFile, $"{{\"error\":\"{Esc(ex.Message)}\"}}"); } catch { }
        }

        _queryLog.Add(logEntry);
        if (_queryLog.Count > MaxLogEntries)
            _queryLog.RemoveAt(0);

        hud:
        // Recording capture
        if (_isRecording && (now - _lastSnapshot).TotalMilliseconds >= Settings.RecordingIntervalMs.Value)
        {
            _lastSnapshot = now;
            CaptureSnapshot();
        }
        // Update recording status
        _status.IsRecording = _isRecording;
        _status.RecordingFrames = _frameCount;
        _status.RecordingElapsedMs = _isRecording ? (now - _recordingStart).TotalMilliseconds : 0;
        _status.RecordingFile = _currentRecordingPath ?? "";

        DrawStatusHud();
    }

    // ── Status HUD ──────────────────────────────────────────────────

    private void DrawStatusHud()
    {
        if (!Settings.ShowStatusHud.Value || !GameController.InGame) return;

        float x = Settings.HudX.Value;
        float y = Settings.HudY.Value;

        var bgRect = new RectangleF(x, y, 180, 22);
        Graphics.DrawBox(bgRect, new Color(10, 10, 14, 200));

        var statusColor = _status.State == "idle" ? new Color(0, 206, 209) : new Color(255, 200, 50);
        var dotPos = new Vector2N(x + 10, y + 11);
        Graphics.DrawBox(new RectangleF(x + 5, y + 6, 10, 10), statusColor);

        var text = _status.TotalQueries == 0
            ? "Bridge: idle"
            : $"Bridge: {_status.TotalQueries} queries";
        Graphics.DrawText(text, new Vector2N(x + 20, y + 3), new Color(200, 200, 200));

        Graphics.DrawFrame(bgRect, new Color(0, 130, 133, 80), 1);
    }

    // ── Settings UI ─────────────────────────────────────────────────

    public override void DrawSettings()
    {
        _settingsUi ??= new WhatsAnAiBridgeSettingsUi();
        _settingsUi.Draw(Settings, _status, _queryLog);
    }

    // ── Query processor ─────────────────────────────────────────────

    private string ProcessQuery(string query)
    {
        var ql = query.ToLower();
        var sb = new StringBuilder(8192);
        sb.Append('{');

        // Recording commands
        if (ql.StartsWith("record:") || ql == "snapshot" || ql.StartsWith("recording:"))
            return ProcessRecordingCommand(ql, query);

        var isDeep = ql.StartsWith("deep:");
        var incPlayer = ql == "all" || ql == "player";
        var incArea = ql == "all" || ql == "area";
        var incEntities = !isDeep && (ql == "all" || ql.StartsWith("entities") || ql == "monsters" || ql == "items");
        var incNpcDialog = ql == "all" || ql == "npcdialog";
        var incMapData = ql == "all" || ql == "mapdata";
        var incUi = ql == "ui";
        var incStash = ql == "stash";

        if (incPlayer) WritePlayer(sb);
        if (incArea) WriteArea(sb);
        if (incNpcDialog) WriteNpcDialog(sb);
        if (incMapData) WriteMapData(sb);
        if (incUi) WriteUi(sb);
        if (incStash) WriteStash(sb);
        if (incEntities) WriteEntities(sb, ql);
        if (isDeep) WriteDeep(sb, query);

        sb.Append($"\"query\":\"{Esc(ql)}\",\"timestamp\":\"{DateTime.UtcNow:O}\"}}");
        return sb.ToString();
    }

    // ── PLAYER ──────────────────────────────────────────────────────

    private void WritePlayer(StringBuilder sb)
    {
        var player = GameController.Player;
        var life = player.GetComponent<Life>();
        var buffs = player.GetComponent<Buffs>();

        sb.Append($"\"player\":{{\"path\":\"{Esc(player.Path)}\",");
        sb.Append($"\"hp\":{life?.CurHP ?? 0},\"maxHp\":{life?.MaxHP ?? 0},");
        sb.Append($"\"es\":{life?.CurES ?? 0},\"maxEs\":{life?.MaxES ?? 0},");
        sb.Append($"\"mana\":{life?.CurMana ?? 0},\"maxMana\":{life?.MaxMana ?? 0},");
        sb.Append($"\"pos\":[{player.GridPosNum.X:F0},{player.GridPosNum.Y:F0}]");

        if (buffs?.BuffsList != null)
        {
            sb.Append(",\"buffs\":[");
            var first = true;
            foreach (var b in buffs.BuffsList)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append($"{{\"name\":\"{Esc(b.Name)}\",\"charges\":{b.BuffCharges},\"timer\":{Num(b.Timer)}}}");
            }
            sb.Append(']');
        }
        sb.Append("},");
    }

    // ── AREA ────────────────────────────────────────────────────────

    private void WriteArea(StringBuilder sb)
    {
        var area = GameController.IngameState.Data.CurrentArea;
        sb.Append($"\"area\":{{\"name\":\"{Esc(area?.Name)}\",\"areaLevel\":{area?.AreaLevel ?? 0},\"act\":{area?.Act ?? 0}}},");
    }

    // ── NPC DIALOG ──────────────────────────────────────────────────

    private void WriteNpcDialog(StringBuilder sb)
    {
        sb.Append("\"npcDialog\":{");
        try
        {
            var ui = GameController.IngameState.IngameUi;
            var npcDlg = ui.NpcDialog;
            var sd = GameController.IngameState.Data.ServerData;
            sb.Append($"\"isVisible\":{Bool(npcDlg?.IsVisible == true)},\"dialogDepth\":{sd.DialogDepth}");

            if (npcDlg?.IsVisible == true)
            {
                sb.Append($",\"npcName\":\"{Esc(npcDlg.NpcName)}\"");
                sb.Append($",\"isLoreTalk\":{Bool(npcDlg.IsLoreTalkVisible)}");
                try
                {
                    var lines = npcDlg.NpcLines;
                    sb.Append(",\"lines\":[");
                    var first = true;
                    foreach (var line in lines)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append($"\"{Esc(line.Text)}\"");
                    }
                    sb.Append(']');
                }
                catch { sb.Append(",\"lines\":[]"); }
            }
        }
        catch (Exception ex) { sb.Append($"\"error\":\"{Esc(ex.Message)}\""); }
        sb.Append("},");
    }

    // ── MAP DATA ────────────────────────────────────────────────────

    private void WriteMapData(StringBuilder sb)
    {
        sb.Append("\"mapData\":{");
        try
        {
            var data = GameController.IngameState.Data;
            var sd = data.ServerData;
            sb.Append($"\"dialogDepth\":{sd.DialogDepth}");

            try
            {
                var ms = data.MapStats;
                if (ms != null && ms.Count > 0)
                {
                    sb.Append(",\"mapStats\":{");
                    var first = true;
                    foreach (var kv in ms.Take(100))
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append($"\"{kv.Key}\":{kv.Value}");
                    }
                    sb.Append('}');
                }
            }
            catch { }

            try
            {
                var qf = sd.QuestFlags;
                if (qf != null)
                {
                    sb.Append(",\"questFlags\":{");
                    var first = true;
                    foreach (var kv in qf)
                    {
                        var name = kv.Key.ToString();
                        if (name.Contains("Djinn") || name.Contains("OrderOfThe") || name.Contains("Faridun"))
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            sb.Append($"\"{name}\":{Bool(kv.Value)}");
                        }
                    }
                    sb.Append($",\"_total\":{qf.Count}}}");
                }
            }
            catch { }
        }
        catch (Exception ex) { sb.Append($"\"error\":\"{Esc(ex.Message)}\""); }
        sb.Append("},");
    }

    // ── UI PANEL SCAN ───────────────────────────────────────────────

    private void WriteUi(StringBuilder sb)
    {
        sb.Append("\"ui\":{");
        try
        {
            var ui = GameController.IngameState.IngameUi;
            var sd = GameController.IngameState.Data.ServerData;
            sb.Append($"\"dialogDepth\":{sd.DialogDepth},");

            sb.Append($"\"npcDialog\":{Bool(ui.NpcDialog?.IsVisible == true)},");
            sb.Append($"\"purchaseWindow\":{Bool(ui.PurchaseWindow?.IsVisible == true)},");
            sb.Append($"\"sellWindow\":{Bool(ui.SellWindow?.IsVisible == true)},");
            sb.Append($"\"mapDeviceWindow\":{Bool(ui.MapDeviceWindow?.IsVisible == true)},");
            sb.Append($"\"tradeWindow\":{Bool(ui.TradeWindow?.IsVisible == true)},");
            sb.Append($"\"popUpWindow\":{Bool(ui.PopUpWindow?.IsVisible == true)},");
            sb.Append($"\"ritualWindow\":{Bool(ui.RitualWindow?.IsVisible == true)},");
            sb.Append($"\"villageRewardWindow\":{Bool(ui.VillageRewardWindow?.IsVisible == true)},");
            sb.Append($"\"mercenaryEncounterWindow\":{Bool(ui.MercenaryEncounterWindow?.IsVisible == true)},");
            sb.Append($"\"zanaMissionChoice\":{Bool(ui.ZanaMissionChoice?.IsVisible == true)},");

            try
            {
                var lm = ui.LeagueMechanicButtons;
                sb.Append($"\"leagueMechanicButtons\":{{\"vis\":{Bool(lm?.IsVisible == true)},\"cc\":{lm?.ChildCount ?? 0}}},");
            }
            catch { }

            int maxChildren = Settings.MaxUiChildren.Value;
            sb.Append("\"visibleChildren\":[");
            var fc = true;
            for (int i = 0; i < maxChildren; i++)
            {
                try
                {
                    var child = ui.GetChildAtIndex(i);
                    if (child != null && child.IsVisible)
                    {
                        if (!fc) sb.Append(',');
                        fc = false;
                        sb.Append($"{{\"i\":{i},\"cc\":{child.ChildCount}");
                        var txt = child.Text;
                        if (!string.IsNullOrEmpty(txt))
                            sb.Append($",\"t\":\"{Esc(Trunc(txt, 80))}\"");

                        var ct = new List<string>();
                        for (int j = 0; j < Math.Min(child.ChildCount, 20); j++)
                        {
                            try
                            {
                                var c2 = child.GetChildAtIndex(j);
                                if (c2 == null) continue;
                                var t2 = c2.Text;
                                if (!string.IsNullOrEmpty(t2)) ct.Add(Esc(Trunc(t2, 60)));
                                for (int k = 0; k < Math.Min(c2.ChildCount, 10); k++)
                                {
                                    try
                                    {
                                        var c3 = c2.GetChildAtIndex(k);
                                        if (c3 == null) continue;
                                        var t3 = c3.Text;
                                        if (!string.IsNullOrEmpty(t3)) ct.Add(Esc(Trunc(t3, 60)));
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                        if (ct.Count > 0)
                            sb.Append($",\"ct\":[{string.Join(",", ct.Select(t => $"\"{t}\""))}]");
                        sb.Append('}');
                    }
                }
                catch { }
            }
            sb.Append(']');
        }
        catch (Exception ex) { sb.Append($"\"error\":\"{Esc(ex.Message)}\""); }
        sb.Append("},");
    }

    // ── STASH TABS ────────────────────────────────────────────────────
    // Query: "stash"
    // Returns all stash tabs from ServerData.PlayerStashTabs with:
    //   index        - raw position in PlayerStashTabs array
    //   name         - display name (includes "(Remove-only)" suffix from game)
    //   type         - InventoryTabType enum: Normal, Premium, Currency, Map, etc.
    //   visibleIndex - display order in stash UI (what Stashie uses for arrow-key nav)
    //   color        - RGB tab color
    //   isPremium, isPublic, isRemoveOnly, isHidden, isMapSeries - flag booleans
    //   rawFlags     - raw InventoryTabFlags byte for debugging
    // Note: Tab affinity data is not yet exposed by the API.

    private void WriteStash(StringBuilder sb)
    {
        sb.Append("\"stashTabs\":[");
        try
        {
            var tabs = GameController.IngameState.Data.ServerData.PlayerStashTabs;
            if (tabs != null)
            {
                var first = true;
                for (var i = 0; i < tabs.Count; i++)
                {
                    var tab = tabs[i];
                    if (!first) sb.Append(',');
                    first = false;

                    var flags = tab.Flags;
                    sb.Append('{');
                    sb.Append($"\"index\":{i},");
                    sb.Append($"\"name\":\"{Esc(tab.Name)}\",");
                    sb.Append($"\"type\":\"{tab.TabType}\",");
                    sb.Append($"\"visibleIndex\":{tab.VisibleIndex},");
                    sb.Append($"\"color\":{{\"r\":{tab.Color2.R},\"g\":{tab.Color2.G},\"b\":{tab.Color2.B}}},");
                    sb.Append($"\"isPremium\":{Bool((flags & InventoryTabFlags.Premium) != 0)},");
                    sb.Append($"\"isPublic\":{Bool((flags & InventoryTabFlags.Public) != 0)},");
                    sb.Append($"\"isRemoveOnly\":{Bool(tab.RemoveOnly)},");
                    sb.Append($"\"isHidden\":{Bool(tab.IsHidden)},");
                    sb.Append($"\"isMapSeries\":{Bool((flags & InventoryTabFlags.MapSeries) != 0)},");
                    sb.Append($"\"rawFlags\":{(byte)flags}");
                    sb.Append('}');
                }
            }
        }
        catch (Exception ex) { sb.Append($"{{\"error\":\"{Esc(ex.Message)}\"}}"); }
        sb.Append("],");
    }

    // ── ENTITIES (shallow) ──────────────────────────────────────────

    private void WriteEntities(StringBuilder sb, string ql)
    {
        var maxDist = (float)Settings.MaxEntityRange.Value;
        if (ql.Contains(':'))
            float.TryParse(ql.Split(':').Last(), out maxDist);

        var entities = GameController.EntityListWrapper.ValidEntitiesByType
            .SelectMany(kv => kv.Value)
            .Where(e => e.DistancePlayer < maxDist);

        if (ql == "monsters")
            entities = entities.Where(e => e.IsAlive && e.IsHostile && e.Type == EntityType.Monster);
        else if (ql == "items")
            entities = entities.Where(e => e.Type == EntityType.WorldItem);

        sb.Append("\"entities\":[");
        var first = true;
        foreach (var e in entities.OrderBy(e => e.DistancePlayer))
        {
            var life = e.GetComponent<Life>();
            var render = e.GetComponent<Render>();
            if (!first) sb.Append(',');
            first = false;
            sb.Append($"{{\"id\":{e.Id},");
            sb.Append($"\"type\":\"{e.Type}\",");
            sb.Append($"\"path\":\"{Esc(e.Path)}\",");
            sb.Append($"\"name\":\"{Esc(render?.Name)}\",");
            sb.Append($"\"alive\":{Bool(e.IsAlive)},");
            sb.Append($"\"hostile\":{Bool(e.IsHostile)},");
            sb.Append($"\"rarity\":\"{e.Rarity}\",");
            sb.Append($"\"dist\":{Num(e.DistancePlayer)},");
            sb.Append($"\"pos\":[{e.GridPosNum.X:F0},{e.GridPosNum.Y:F0}],");
            sb.Append($"\"hp\":{life?.CurHP ?? 0},\"maxHp\":{life?.MaxHP ?? 0}}}");
        }
        sb.Append("],");
    }

    // ── DEEP ENTITY INSPECTION ──────────────────────────────────────

    private void WriteDeep(StringBuilder sb, string query)
    {
        var parts = query.Substring(5).Split(':');
        var filter = parts[0];
        var maxDist = parts.Length > 1 && float.TryParse(parts[1], out var d) ? d : 500f;

        var entities = GameController.EntityListWrapper.ValidEntitiesByType
            .SelectMany(kv => kv.Value)
            .Where(e => e.DistancePlayer < maxDist
                     && e.Path != null
                     && e.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(e => e.DistancePlayer)
            .ToList();

        sb.Append($"\"filter\":\"{Esc(filter)}\",\"matchCount\":{entities.Count},\"entities\":[");

        var maxStats = Settings.MaxDeepStats.Value;
        var first = true;
        foreach (var e in entities)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('{');

            sb.Append($"\"id\":{e.Id},");
            sb.Append($"\"type\":\"{e.Type}\",");
            sb.Append($"\"path\":\"{Esc(e.Path)}\",");
            sb.Append($"\"alive\":{Bool(e.IsAlive)},");
            sb.Append($"\"hostile\":{Bool(e.IsHostile)},");
            sb.Append($"\"rarity\":\"{e.Rarity}\",");
            sb.Append($"\"dist\":{Num(e.DistancePlayer)},");
            sb.Append($"\"isValid\":{Bool(e.IsValid)},");

            var cc = e.CacheComp;
            if (cc != null)
                sb.Append($"\"allComponents\":[{string.Join(",", cc.Keys.Select(k => $"\"{Esc(k)}\""))}],");

            var render = e.GetComponent<Render>();
            if (render != null)
                sb.Append($"\"render\":{{\"name\":\"{Esc(render.Name)}\",\"pos\":[{render.PosNum.X:F1},{render.PosNum.Y:F1},{render.PosNum.Z:F1}],\"bounds\":[{render.BoundsNum.X:F1},{render.BoundsNum.Y:F1},{render.BoundsNum.Z:F1}]}},");

            var pos = e.GetComponent<Positioned>();
            if (pos != null)
                sb.Append($"\"positioned\":{{\"grid\":[{pos.GridX},{pos.GridY}],\"reaction\":{pos.Reaction},\"size\":{pos.Size},\"scale\":{pos.Scale:F3},\"rotation\":{pos.Rotation:F3}}},");

            var anim = e.GetComponent<Animated>();
            if (anim != null)
            {
                try
                {
                    var bao = anim.BaseAnimatedObjectEntity;
                    sb.Append($"\"animated\":{{\"baseEntityPath\":\"{Esc(bao?.Path)}\",\"baseEntityId\":{bao?.Id ?? 0}}},");
                }
                catch { sb.Append("\"animated\":{\"error\":true},"); }
            }

            var sm = e.GetComponent<StateMachine>();
            if (sm != null)
            {
                try
                {
                    var sts = sm.States;
                    if (sts != null && sts.Count > 0)
                    {
                        sb.Append("\"stateMachine\":{\"canBeTarget\":");
                        sb.Append(Bool(sm.CanBeTarget));
                        sb.Append(",\"inTarget\":");
                        sb.Append(Bool(sm.InTarget));
                        sb.Append(",\"states\":{");
                        var smf = true;
                        foreach (var s in sts)
                        {
                            if (!smf) sb.Append(',');
                            smf = false;
                            sb.Append($"\"{Esc(s.Name)}\":{s.Value}");
                        }
                        sb.Append("}},");
                    }
                }
                catch { sb.Append("\"stateMachine\":{\"error\":true},"); }
            }

            var npc = e.GetComponent<NPC>();
            if (npc != null)
                sb.Append($"\"npc\":{{\"hasIconOverhead\":{Bool(npc.HasIconOverhead)},\"isIgnoreHidden\":{Bool(npc.IsIgnoreHidden)},\"isMinMapLabelVisible\":{Bool(npc.IsMinMapLabelVisible)}}},");

            var life = e.GetComponent<Life>();
            if (life != null)
                sb.Append($"\"life\":{{\"hp\":{life.CurHP},\"maxHp\":{life.MaxHP},\"es\":{life.CurES},\"maxEs\":{life.MaxES}}},");

            var tgt = e.GetComponent<Targetable>();
            if (tgt != null)
                sb.Append($"\"targetable\":{{\"isTargetable\":{Bool(tgt.isTargetable)},\"isTargeted\":{Bool(tgt.isTargeted)}}},");

            var chest = e.GetComponent<Chest>();
            if (chest != null)
            {
                sb.Append($"\"chest\":{{\"isOpened\":{Bool(chest.IsOpened)},");
                sb.Append($"\"isLocked\":{Bool(chest.IsLocked)},");
                sb.Append($"\"isStrongbox\":{Bool(chest.IsStrongbox)},");
                sb.Append($"\"destroyAfterOpen\":{Bool(chest.DestroyingAfterOpen)},");
                sb.Append($"\"isLarge\":{Bool(chest.IsLarge)},");
                sb.Append($"\"stompable\":{Bool(chest.Stompable)},");
                sb.Append($"\"openOnDamage\":{Bool(chest.OpenOnDamage)}}},");
            }

            var omp = e.GetComponent<ObjectMagicProperties>();
            if (omp != null)
            {
                sb.Append($"\"omp\":{{\"rarity\":\"{omp.Rarity}\"");
                try
                {
                    var mods = omp.Mods;
                    if (mods != null && mods.Count > 0)
                        sb.Append($",\"mods\":[{string.Join(",", mods.Select(m => $"\"{Esc(m)}\""))}]");
                }
                catch { }
                sb.Append("},");
            }

            var mi = e.GetComponent<MinimapIcon>();
            if (mi != null)
                sb.Append($"\"minimapIcon\":{{\"name\":\"{Esc(mi.Name)}\",\"isVisible\":{Bool(mi.IsVisible)},\"isHide\":{Bool(mi.IsHide)}}},");

            var buffs = e.GetComponent<Buffs>();
            if (buffs?.BuffsList != null && buffs.BuffsList.Count > 0)
            {
                sb.Append("\"buffs\":[");
                var bf = true;
                foreach (var b in buffs.BuffsList)
                {
                    if (!bf) sb.Append(',');
                    bf = false;
                    sb.Append($"{{\"name\":\"{Esc(b.Name)}\",\"charges\":{b.BuffCharges},\"timer\":{Num(b.Timer)}}}");

                }
                sb.Append("],");
            }

            var stats = e.GetComponent<Stats>();
            if (stats != null)
            {
                try
                {
                    var sd = stats.StatDictionary;
                    if (sd != null && sd.Count > 0)
                    {
                        sb.Append("\"stats\":{");
                        var sf = true;
                        foreach (var kv in sd.Take(maxStats))
                        {
                            if (!sf) sb.Append(',');
                            sf = false;
                            sb.Append($"\"{kv.Key}\":{kv.Value}");
                        }
                        if (sd.Count > maxStats) sb.Append($",\"_truncated\":{sd.Count}");
                        sb.Append("},");
                    }
                }
                catch { }
            }

            // Visual effect components
            WriteEffectComponents(sb, e);

            sb.Append("\"_end\":true}");
        }
        sb.Append("],");
    }

    // ── RECORDING ENGINE ─────────────────────────────────────────────

    private string ProcessRecordingCommand(string ql, string query)
    {
        if (ql == "record:start" || ql.StartsWith("record:start:"))
        {
            if (_isRecording)
                return "{\"error\":\"Already recording\",\"file\":\"" + Esc(_currentRecordingPath) + "\"}";

            if (ql.StartsWith("record:start:") && int.TryParse(ql.Substring("record:start:".Length), out var interval))
                Settings.RecordingIntervalMs.Value = Math.Clamp(interval, Settings.RecordingIntervalMs.Min, Settings.RecordingIntervalMs.Max);

            var recDir = Path.Combine(_bridgeDir, "recordings");
            if (!Directory.Exists(recDir)) Directory.CreateDirectory(recDir);

            var fileName = $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl";
            _currentRecordingPath = Path.Combine(recDir, fileName);
            _recordingWriter = new StreamWriter(_currentRecordingPath, false, System.Text.Encoding.UTF8);
            _isRecording = true;
            _recordingStart = DateTime.UtcNow;
            _lastSnapshot = DateTime.MinValue;
            _frameCount = 0;

            return $"{{\"status\":\"recording_started\",\"file\":\"{Esc(_currentRecordingPath)}\",\"intervalMs\":{Settings.RecordingIntervalMs.Value}}}";
        }

        if (ql == "record:stop")
        {
            if (!_isRecording)
                return "{\"status\":\"not_recording\"}";

            _isRecording = false;
            _recordingWriter?.Flush();
            _recordingWriter?.Dispose();
            _recordingWriter = null;

            var elapsed = (DateTime.UtcNow - _recordingStart).TotalMilliseconds;
            long fileSize = 0;
            try { fileSize = new FileInfo(_currentRecordingPath!).Length; } catch { }

            var result = $"{{\"status\":\"recording_stopped\",\"frames\":{_frameCount},\"durationMs\":{elapsed:F0},\"file\":\"{Esc(_currentRecordingPath)}\",\"sizeBytes\":{fileSize}}}";
            _currentRecordingPath = null;
            return result;
        }

        if (ql == "record:status")
        {
            if (!_isRecording)
                return "{\"status\":\"idle\",\"isRecording\":false}";
            var elapsed = (DateTime.UtcNow - _recordingStart).TotalMilliseconds;
            return $"{{\"status\":\"recording\",\"isRecording\":true,\"frames\":{_frameCount},\"durationMs\":{elapsed:F0},\"file\":\"{Esc(_currentRecordingPath)}\",\"intervalMs\":{Settings.RecordingIntervalMs.Value}}}";
        }

        if (ql == "snapshot")
        {
            var snapshot = BuildSnapshot();
            if (_isRecording && _recordingWriter != null)
            {
                _recordingWriter.WriteLine(snapshot);
                _frameCount++;
                if (_frameCount % 25 == 0) _recordingWriter.Flush();
            }
            return snapshot;
        }

        if (ql == "recording:list")
        {
            var recDir = Path.Combine(_bridgeDir, "recordings");
            if (!Directory.Exists(recDir))
                return "{\"recordings\":[]}";
            var files = Directory.GetFiles(recDir, "*.jsonl");
            var sb = new StringBuilder("{\"recordings\":[");
            var first = true;
            foreach (var f in files.OrderByDescending(f => f))
            {
                if (!first) sb.Append(',');
                first = false;
                var fi = new FileInfo(f);
                sb.Append($"{{\"name\":\"{Esc(fi.Name)}\",\"sizeBytes\":{fi.Length},\"modified\":\"{fi.LastWriteTimeUtc:O}\"}}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        if (ql.StartsWith("recording:load:"))
        {
            var fileName = query.Substring("recording:load:".Length).Trim();
            var recDir = Path.Combine(_bridgeDir, "recordings");
            var filePath = Path.Combine(recDir, fileName);
            if (!File.Exists(filePath))
                return $"{{\"error\":\"File not found\",\"file\":\"{Esc(fileName)}\"}}";
            LoadRecording(filePath);
            return $"{{\"status\":\"loaded\",\"file\":\"{Esc(fileName)}\",\"frames\":{_loadedFrameOffsets?.Count ?? 0}}}";
        }

        if (ql.StartsWith("recording:frame:"))
        {
            if (_loadedRecordingPath == null || _loadedFrameOffsets == null)
                return "{\"error\":\"No recording loaded. Use recording:load:filename first.\"}";
            if (!int.TryParse(ql.Substring("recording:frame:".Length), out var n))
                return "{\"error\":\"Invalid frame number\"}";
            if (n < 0 || n >= _loadedFrameOffsets.Count)
                return $"{{\"error\":\"Frame {n} out of range\",\"totalFrames\":{_loadedFrameOffsets.Count}}}";
            return ReadFrame(n);
        }

        if (ql.StartsWith("recording:range:"))
        {
            if (_loadedRecordingPath == null || _loadedFrameOffsets == null)
                return "{\"error\":\"No recording loaded\"}";
            var parts = ql.Substring("recording:range:".Length).Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var rangeStart) || !int.TryParse(parts[1], out var rangeEnd))
                return "{\"error\":\"Usage: recording:range:N:M\"}";
            rangeStart = Math.Max(0, rangeStart);
            rangeEnd = Math.Min(_loadedFrameOffsets.Count - 1, rangeEnd);
            var sb = new StringBuilder("{\"frames\":[");
            for (int i = rangeStart; i <= rangeEnd; i++)
            {
                if (i > rangeStart) sb.Append(',');
                sb.Append(ReadFrame(i));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        if (ql.StartsWith("recording:search:"))
        {
            if (_loadedRecordingPath == null || _loadedFrameOffsets == null)
                return "{\"error\":\"No recording loaded\"}";
            var term = query.Substring("recording:search:".Length).Trim();
            var matches = SearchFrames(term);
            return $"{{\"term\":\"{Esc(term)}\",\"matchCount\":{matches.Count},\"frames\":[{string.Join(",", matches)}]}}";
        }

        if (ql == "recording:summary")
        {
            if (_loadedRecordingPath == null || _loadedFrameOffsets == null)
                return "{\"error\":\"No recording loaded\"}";
            return SummarizeRecording();
        }

        return $"{{\"error\":\"Unknown recording command\",\"query\":\"{Esc(ql)}\"}}";
    }

    private string BuildSnapshot()
    {
        var sb = new StringBuilder(16384);
        sb.Append('{');

        // Frame metadata
        var elapsed = _isRecording ? (DateTime.UtcNow - _recordingStart).TotalMilliseconds : 0;
        sb.Append($"\"frame\":{_frameCount},\"timestamp\":\"{DateTime.UtcNow:O}\",\"elapsedMs\":{elapsed:F0},");

        // Area
        WriteArea(sb);

        // Player (full with actor)
        WritePlayerFull(sb);

        // Entities (adaptive depth)
        WriteRecordingEntities(sb);

        sb.Append("\"_end\":true}");
        return sb.ToString();
    }

    private void WritePlayerFull(StringBuilder sb)
    {
        var player = GameController.Player;
        var life = player.GetComponent<Life>();
        var buffs = player.GetComponent<Buffs>();
        var actor = player.GetComponent<ExileCore.PoEMemory.Components.Actor>();
        var pos = player.GetComponent<Positioned>();

        sb.Append($"\"player\":{{\"path\":\"{Esc(player.Path)}\",");
        sb.Append($"\"hp\":{life?.CurHP ?? 0},\"maxHp\":{life?.MaxHP ?? 0},");
        sb.Append($"\"es\":{life?.CurES ?? 0},\"maxEs\":{life?.MaxES ?? 0},");
        sb.Append($"\"mana\":{life?.CurMana ?? 0},\"maxMana\":{life?.MaxMana ?? 0},");
        sb.Append($"\"pos\":[{player.GridPosNum.X:F0},{player.GridPosNum.Y:F0}]");

        if (pos != null)
            sb.Append($",\"rotation\":{pos.Rotation:F3}");

        // Actor data
        if (actor != null)
        {
            sb.Append($",\"actor\":{{\"actionId\":{actor.ActionId},\"action\":\"{actor.Action}\"");
            sb.Append($",\"animationId\":{actor.AnimationId},\"animation\":\"{actor.Animation}\"");
            sb.Append($",\"isMoving\":{Bool(actor.isMoving)},\"isAttacking\":{Bool(actor.isAttacking)}");
            try
            {
                var ca = actor.CurrentAction;
                if (ca != null)
                {
                    sb.Append(",\"currentAction\":{");
                    sb.Append($"\"skill\":\"{Esc(ca.Skill?.Name)}\"");
                    sb.Append($",\"destination\":[{ca.Destination.X},{ca.Destination.Y}]");
                    try { if (ca.Target != null) sb.Append($",\"targetId\":{ca.Target.Id}"); } catch { }
                    sb.Append('}');
                }
            }
            catch { }
            sb.Append('}');
        }

        // Buffs with enhanced fields
        if (buffs?.BuffsList != null)
        {
            sb.Append(",\"buffs\":[");
            var first = true;
            foreach (var b in buffs.BuffsList)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append($"{{\"name\":\"{Esc(b.Name)}\",\"charges\":{b.BuffCharges},\"timer\":{Num(b.Timer)}");
                sb.Append($",\"stacks\":{b.BuffStacks},\"maxTime\":{Num(b.MaxTime)},\"sourceEntityId\":{b.SourceEntityId}}}");
            }
            sb.Append(']');
        }
        sb.Append("},");
    }

    private void WriteRecordingEntities(StringBuilder sb)
    {
        var maxDist = (float)Settings.RecordingEntityRange.Value;
        var maxStats = Settings.RecordingMaxDeepStats.Value;
        var autoDeep = Settings.AutoDeepScanBosses.Value;

        var entities = GameController.EntityListWrapper.ValidEntitiesByType
            .SelectMany(kv => kv.Value)
            .Where(e => e.DistancePlayer < maxDist)
            .OrderBy(e => e.DistancePlayer)
            .ToList();

        sb.Append("\"entities\":[");
        var first = true;
        foreach (var e in entities)
        {
            if (!first) sb.Append(',');
            first = false;

            var isElite = autoDeep && (e.Rarity == MonsterRarity.Unique || e.Rarity == MonsterRarity.Rare);
            var hasEffects = e.GetComponent<Beam>() != null
                || e.GetComponent<GroundEffect>() != null
                || e.GetComponent<EffectPack>() != null;

            if (isElite || hasEffects)
                WriteDeepEntity(sb, e, maxStats);
            else
                WriteShallowEntity(sb, e);
        }
        sb.Append("],");
    }

    private void WriteShallowEntity(StringBuilder sb, Entity e)
    {
        var life = e.GetComponent<Life>();
        var render = e.GetComponent<Render>();
        sb.Append($"{{\"id\":{e.Id},");
        sb.Append($"\"type\":\"{e.Type}\",");
        sb.Append($"\"path\":\"{Esc(e.Path)}\",");
        sb.Append($"\"name\":\"{Esc(render?.Name)}\",");
        sb.Append($"\"alive\":{Bool(e.IsAlive)},");
        sb.Append($"\"hostile\":{Bool(e.IsHostile)},");
        sb.Append($"\"rarity\":\"{e.Rarity}\",");
        sb.Append($"\"dist\":{Num(e.DistancePlayer)},");
        sb.Append($"\"pos\":[{e.GridPosNum.X:F0},{e.GridPosNum.Y:F0}],");
        sb.Append($"\"hp\":{life?.CurHP ?? 0},\"maxHp\":{life?.MaxHP ?? 0}");

        // Effects on shallow entities (important for visual mechanic entities)
        WriteEffectComponents(sb, e);

        sb.Append('}');
    }

    private void WriteDeepEntity(StringBuilder sb, Entity e, int maxStats)
    {
        var life = e.GetComponent<Life>();
        var render = e.GetComponent<Render>();
        var actor = e.GetComponent<ExileCore.PoEMemory.Components.Actor>();
        var buffs = e.GetComponent<Buffs>();
        var pos = e.GetComponent<Positioned>();
        var sm = e.GetComponent<StateMachine>();
        var stats = e.GetComponent<Stats>();
        var tgt = e.GetComponent<Targetable>();
        var omp = e.GetComponent<ObjectMagicProperties>();

        sb.Append($"{{\"id\":{e.Id},\"deep\":true,");
        sb.Append($"\"type\":\"{e.Type}\",");
        sb.Append($"\"path\":\"{Esc(e.Path)}\",");
        sb.Append($"\"name\":\"{Esc(render?.Name)}\",");
        sb.Append($"\"alive\":{Bool(e.IsAlive)},");
        sb.Append($"\"hostile\":{Bool(e.IsHostile)},");
        sb.Append($"\"rarity\":\"{e.Rarity}\",");
        sb.Append($"\"dist\":{Num(e.DistancePlayer)},");
        sb.Append($"\"pos\":[{e.GridPosNum.X:F0},{e.GridPosNum.Y:F0}],");

        // Life
        if (life != null)
            sb.Append($"\"life\":{{\"hp\":{life.CurHP},\"maxHp\":{life.MaxHP},\"es\":{life.CurES},\"maxEs\":{life.MaxES}}},");

        // Render
        if (render != null)
            sb.Append($"\"render\":{{\"pos\":[{render.PosNum.X:F1},{render.PosNum.Y:F1},{render.PosNum.Z:F1}],\"bounds\":[{render.BoundsNum.X:F1},{render.BoundsNum.Y:F1},{render.BoundsNum.Z:F1}]}},");

        // Positioned
        if (pos != null)
            sb.Append($"\"positioned\":{{\"rotation\":{pos.Rotation:F3},\"travelProgress\":{pos.TravelProgress:F3}}},");

        // Actor
        if (actor != null)
        {
            sb.Append($"\"actor\":{{\"actionId\":{actor.ActionId},\"action\":\"{actor.Action}\"");
            sb.Append($",\"animationId\":{actor.AnimationId},\"animation\":\"{actor.Animation}\"");
            sb.Append($",\"isMoving\":{Bool(actor.isMoving)},\"isAttacking\":{Bool(actor.isAttacking)}");
            try
            {
                var ca = actor.CurrentAction;
                if (ca != null)
                {
                    sb.Append(",\"currentAction\":{");
                    sb.Append($"\"skill\":\"{Esc(ca.Skill?.Name)}\"");
                    sb.Append($",\"destination\":[{ca.Destination.X},{ca.Destination.Y}]");
                    try { if (ca.Target != null) sb.Append($",\"targetId\":{ca.Target.Id}"); } catch { }
                    sb.Append('}');
                }
            }
            catch { }
            sb.Append("},");
        }

        // Buffs
        if (buffs?.BuffsList != null && buffs.BuffsList.Count > 0)
        {
            sb.Append("\"buffs\":[");
            var bf = true;
            foreach (var b in buffs.BuffsList)
            {
                if (!bf) sb.Append(',');
                bf = false;
                sb.Append($"{{\"name\":\"{Esc(b.Name)}\",\"charges\":{b.BuffCharges},\"timer\":{Num(b.Timer)}");
                sb.Append($",\"stacks\":{b.BuffStacks},\"maxTime\":{Num(b.MaxTime)},\"sourceEntityId\":{b.SourceEntityId}}}");
            }
            sb.Append("],");
        }

        // StateMachine
        if (sm != null)
        {
            try
            {
                var sts = sm.States;
                if (sts != null && sts.Count > 0)
                {
                    sb.Append("\"stateMachine\":{\"states\":{");
                    var smf = true;
                    foreach (var s in sts)
                    {
                        if (!smf) sb.Append(',');
                        smf = false;
                        sb.Append($"\"{Esc(s.Name)}\":{s.Value}");
                    }
                    sb.Append("}},");
                }
            }
            catch { }
        }

        // Targetable
        if (tgt != null)
            sb.Append($"\"targetable\":{{\"isTargetable\":{Bool(tgt.isTargetable)},\"isTargeted\":{Bool(tgt.isTargeted)}}},");

        // ObjectMagicProperties
        if (omp != null)
        {
            sb.Append($"\"omp\":{{\"rarity\":\"{omp.Rarity}\"");
            try
            {
                var mods = omp.Mods;
                if (mods != null && mods.Count > 0)
                    sb.Append($",\"mods\":[{string.Join(",", mods.Select(m => $"\"{Esc(m)}\""))}]");
            }
            catch { }
            sb.Append("},");
        }

        // Stats
        if (stats != null)
        {
            try
            {
                var sd = stats.StatDictionary;
                if (sd != null && sd.Count > 0)
                {
                    sb.Append("\"stats\":{");
                    var sf = true;
                    foreach (var kv in sd.Take(maxStats))
                    {
                        if (!sf) sb.Append(',');
                        sf = false;
                        sb.Append($"\"{kv.Key}\":{kv.Value}");
                    }
                    if (sd.Count > maxStats) sb.Append($",\"_truncated\":{sd.Count}");
                    sb.Append("},");
                }
            }
            catch { }
        }

        // Visual effect components
        WriteEffectComponents(sb, e);

        sb.Append("\"_end\":true}");
    }

    /// <summary>
    /// Writes visual effect components (Beam, GroundEffect, EffectPack, AnimationController)
    /// for any entity. These capture spell visuals, ground markers, beams, and animation
    /// state -- critical for mechanics like Maven memory game where the game communicates
    /// sequences through visual effects rather than entity state.
    /// </summary>
    private void WriteEffectComponents(StringBuilder sb, Entity e)
    {
        // Beam: directional effects with start/end world positions (laser beams, tethers)
        try
        {
            var beam = e.GetComponent<Beam>();
            if (beam != null)
            {
                var bs = beam.BeamStartNum;
                var be = beam.BeamEndNum;
                sb.Append($",\"beam\":{{\"start\":[{bs.X:F1},{bs.Y:F1},{bs.Z:F1}],\"end\":[{be.X:F1},{be.Y:F1},{be.Z:F1}]}}");
            }
        }
        catch { }

        // GroundEffect: ground degens, markers, AoE zones with duration tracking
        try
        {
            var ge = e.GetComponent<GroundEffect>();
            if (ge != null)
            {
                sb.Append($",\"groundEffect\":{{\"duration\":{ge.Duration:F2},\"maxDuration\":{ge.MaxDuration:F2}");
                sb.Append($",\"scale\":{ge.Scale},\"sizeIncrease\":{ge.SizeIncreaseOverTime}}}");
            }
        }
        catch { }

        // EffectPack: presence indicates visual effects on this entity
        // (Effects list is private in compiled DLL, so we flag presence only)
        try
        {
            var ep = e.GetComponent<EffectPack>();
            if (ep != null)
                sb.Append(",\"hasEffectPack\":true");
        }
        catch { }

        // AnimationController: animation progress, speed, timing for visual sequences
        try
        {
            var ac = e.GetComponent<AnimationController>();
            if (ac != null)
            {
                sb.Append($",\"animController\":{{\"animId\":{ac.CurrentAnimationId}");
                sb.Append($",\"stage\":{ac.CurrentAnimationStage}");
                sb.Append($",\"progress\":{ac.AnimationProgress:F3}");
                sb.Append($",\"speed\":{ac.AnimationSpeed:F3}}}");
            }
        }
        catch { }
    }

    private void CaptureSnapshot()
    {
        if (_recordingWriter == null) return;
        try
        {
            var snapshot = BuildSnapshot();
            _recordingWriter.WriteLine(snapshot);
            _frameCount++;
            if (_frameCount % 25 == 0) _recordingWriter.Flush();
        }
        catch (Exception ex)
        {
            LogError($"Recording capture error: {ex.Message}");
        }
    }

    // ── PLAYBACK ─────────────────────────────────────────────────────

    private void LoadRecording(string path)
    {
        _loadedRecordingPath = path;
        _loadedFrameOffsets = new List<long>();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long offset = 0;
        int b;
        _loadedFrameOffsets.Add(0);
        while ((b = fs.ReadByte()) != -1)
        {
            offset++;
            if (b == '\n' && offset < fs.Length)
                _loadedFrameOffsets.Add(offset);
        }
        // Remove last entry if it points to EOF or empty line
        if (_loadedFrameOffsets.Count > 0)
        {
            var lastOff = _loadedFrameOffsets[_loadedFrameOffsets.Count - 1];
            if (lastOff >= fs.Length)
                _loadedFrameOffsets.RemoveAt(_loadedFrameOffsets.Count - 1);
        }
    }

    private string ReadFrame(int n)
    {
        if (_loadedRecordingPath == null || _loadedFrameOffsets == null || n < 0 || n >= _loadedFrameOffsets.Count)
            return "{\"error\":\"Invalid frame\"}";

        using var fs = new FileStream(_loadedRecordingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(_loadedFrameOffsets[n], SeekOrigin.Begin);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        return sr.ReadLine() ?? "{\"error\":\"Empty frame\"}";
    }

    private List<int> SearchFrames(string term)
    {
        var matches = new List<int>();
        if (_loadedRecordingPath == null) return matches;

        using var sr = new StreamReader(_loadedRecordingPath, System.Text.Encoding.UTF8);
        int frameNum = 0;
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (line.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                matches.Add(frameNum);
            frameNum++;
        }
        return matches;
    }

    private string SummarizeRecording()
    {
        if (_loadedRecordingPath == null || _loadedFrameOffsets == null)
            return "{\"error\":\"No recording loaded\"}";

        var entityPaths = new HashSet<string>();
        var buffNames = new HashSet<string>();
        int totalFrames = 0;
        string? firstTimestamp = null;
        string? lastTimestamp = null;

        using var sr = new StreamReader(_loadedRecordingPath, System.Text.Encoding.UTF8);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            totalFrames++;

            // Extract timestamp
            var tsIdx = line.IndexOf("\"timestamp\":\"");
            if (tsIdx >= 0)
            {
                var tsStart = tsIdx + 13;
                var tsEnd = line.IndexOf('"', tsStart);
                if (tsEnd > tsStart)
                {
                    var ts = line.Substring(tsStart, tsEnd - tsStart);
                    if (firstTimestamp == null) firstTimestamp = ts;
                    lastTimestamp = ts;
                }
            }

            // Extract entity paths (look for "path":" patterns)
            var searchPos = 0;
            while (true)
            {
                var pathIdx = line.IndexOf("\"path\":\"", searchPos);
                if (pathIdx < 0) break;
                var pStart = pathIdx + 8;
                var pEnd = line.IndexOf('"', pStart);
                if (pEnd > pStart)
                    entityPaths.Add(line.Substring(pStart, pEnd - pStart));
                searchPos = pEnd + 1;
            }

            // Extract buff names
            searchPos = 0;
            while (true)
            {
                var bIdx = line.IndexOf("\"name\":\"", searchPos);
                if (bIdx < 0) break;
                var bStart = bIdx + 8;
                var bEnd = line.IndexOf('"', bStart);
                if (bEnd > bStart)
                    buffNames.Add(line.Substring(bStart, bEnd - bStart));
                searchPos = bEnd + 1;
            }
        }

        var sb = new StringBuilder("{");
        sb.Append($"\"file\":\"{Esc(Path.GetFileName(_loadedRecordingPath))}\",");
        sb.Append($"\"totalFrames\":{totalFrames},");
        sb.Append($"\"firstTimestamp\":\"{Esc(firstTimestamp)}\",");
        sb.Append($"\"lastTimestamp\":\"{Esc(lastTimestamp)}\",");

        sb.Append("\"uniqueEntityPaths\":[");
        sb.Append(string.Join(",", entityPaths.OrderBy(p => p).Select(p => $"\"{Esc(p)}\"")));
        sb.Append("],");

        sb.Append("\"uniqueBuffNames\":[");
        sb.Append(string.Join(",", buffNames.OrderBy(n => n).Select(n => $"\"{Esc(n)}\"")));
        sb.Append("]");

        sb.Append('}');
        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string Esc(string? s)
        => s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "").Replace("\r", "") ?? "";

    private static string Bool(bool v) => v ? "true" : "false";

    private static string Trunc(string s, int max)
        => s.Length > max ? s[..max] : s;

    private static string Num(float v)
        => float.IsInfinity(v) || float.IsNaN(v) ? "999999" : v.ToString("F1");
}
