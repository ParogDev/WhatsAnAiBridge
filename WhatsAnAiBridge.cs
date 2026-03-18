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

        var isDeep = ql.StartsWith("deep:");
        var incPlayer = ql == "all" || ql == "player";
        var incArea = ql == "all" || ql == "area";
        var incEntities = !isDeep && (ql == "all" || ql.StartsWith("entities") || ql == "monsters" || ql == "items");
        var incNpcDialog = ql == "all" || ql == "npcdialog";
        var incMapData = ql == "all" || ql == "mapdata";
        var incUi = ql == "ui";

        if (incPlayer) WritePlayer(sb);
        if (incArea) WriteArea(sb);
        if (incNpcDialog) WriteNpcDialog(sb);
        if (incMapData) WriteMapData(sb);
        if (incUi) WriteUi(sb);
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

            sb.Append("\"_end\":true}");
        }
        sb.Append("],");
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
