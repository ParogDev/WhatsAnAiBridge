using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using GameOffsets.Native;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

    // ── Serialization ─────────────────────────────────────────────────

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
        FloatFormatHandling = FloatFormatHandling.DefaultValue,
    };

    private static string Serialize(object obj)
        => JsonConvert.SerializeObject(obj, JsonSettings);

    // ── State ────────────────────────────────────────────────────────

    private string _bridgeDir = "";
    private string _requestFile = "";
    private string _responseFile = "";
    private DateTime _lastCheck = DateTime.MinValue;
    private readonly BridgeStatus _status = new();
    private readonly List<QueryLogEntry> _queryLog = new();
    private const int MaxLogEntries = 50;
    private WhatsAnAiBridgeSettingsUi? _settingsUi;

    // TCP server
    private TcpBridgeServer? _tcpServer;

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
        Name = "Whats An AI Bridge";
        Force = true;
        UpdatePaths();

        if (Settings.EnableTcp.Value)
            StartTcpServer();

        return true;
    }

    private void StartTcpServer()
    {
        try
        {
            _tcpServer?.Dispose();
            _tcpServer = new TcpBridgeServer(
                msg => LogMessage($"[TCP] {msg}"),
                msg => LogError($"[TCP] {msg}")
            );
            _tcpServer.Start(Settings.TcpPort.Value, _bridgeDir);
        }
        catch (Exception ex)
        {
            LogError($"[TCP] Failed to start server: {ex.Message}");
        }
    }

    public override void OnClose()
    {
        _tcpServer?.Dispose();
        _tcpServer = null;
        _recordingWriter?.Dispose();
        _recordingWriter = null;
    }

    public override void OnPluginDestroyForHotReload()
    {
        _tcpServer?.Dispose();
        _tcpServer = null;
        _recordingWriter?.Dispose();
        _recordingWriter = null;
    }

    // ── Tick: process TCP requests on main thread ────────────────────

    public override Job Tick()
    {
        if (_tcpServer == null) return default;

        var sw = Stopwatch.StartNew();

        // Drain up to 5 requests per tick to stay under 200ms
        int processed = 0;
        while (processed < 5 && sw.ElapsedMilliseconds < 150 && _tcpServer.PendingRequests.TryDequeue(out var request))
        {
            processed++;
            try
            {
                var result = ProcessTcpRequest(request.Method, request.Params);
                request.Response.TrySetResult(result);

                _status.TotalQueries++;
                _status.LastQueryType = request.Method;
                _status.LastQueryTimestamp = DateTime.UtcNow;
                _status.LastResponseTime = sw.Elapsed.TotalMilliseconds;
                _status.LastResponseSize = result.Length;
                _status.LastError = "";
            }
            catch (Exception ex)
            {
                request.Response.TrySetResult(Serialize(new ErrorResponse { Error = ex.Message }));
                _status.LastError = ex.Message;
            }
        }

        return default;
    }

    private string ProcessTcpRequest(string method, JToken? parameters)
    {
        // Map JSON-RPC method to query string
        var query = method switch
        {
            "query" => parameters?["type"]?.Value<string>() ?? "all",
            _ => method, // Allow raw query strings as method names too
        };

        // Handle parameterized queries
        if (query == "entities" && parameters?["range"] != null)
            query = $"entities:{parameters["range"]!.Value<int>()}";
        else if (query == "deep" && parameters?["filter"] != null)
        {
            var filter = parameters["filter"]!.Value<string>();
            var range = parameters["range"]?.Value<int>();
            query = range.HasValue ? $"deep:{filter}:{range}" : $"deep:{filter}";
        }
        else if (query.StartsWith("record:") || query.StartsWith("recording:") || query == "snapshot")
        {
            // Pass through recording commands directly
        }

        return ProcessQuery(query);
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
        var now = DateTime.UtcNow;

        // File IPC: poll for requests at configurable interval (legacy, behind toggle)
        if (!Settings.EnableFileIpc.Value) goto hud;
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

            try { File.WriteAllText(_responseFile, Serialize(new ErrorResponse { Error = ex.Message })); } catch { }
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

        var bgRect = new RectangleF(x, y, 220, 22);
        Graphics.DrawBox(bgRect, new Color(10, 10, 14, 200));

        var tcpClients = _tcpServer?.ConnectedClients ?? 0;
        var hasTcp = _tcpServer?.IsRunning == true;
        var statusColor = _status.State == "idle"
            ? (hasTcp ? new Color(0, 206, 209) : new Color(100, 100, 100))
            : new Color(255, 200, 50);
        Graphics.DrawBox(new RectangleF(x + 5, y + 6, 10, 10), statusColor);

        var tcpTag = hasTcp ? $" TCP:{tcpClients}" : "";
        var text = _status.TotalQueries == 0
            ? $"Bridge: idle{tcpTag}"
            : $"Bridge: {_status.TotalQueries} queries{tcpTag}";
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

        // Recording commands
        if (ql.StartsWith("record:") || ql == "snapshot" || ql.StartsWith("recording:"))
            return ProcessRecordingCommand(ql, query);

        // Eval/describe commands (case-sensitive, use original query)
        if (query.StartsWith("eval:"))
        {
            var expr = query.Substring(5);
            var walker = new ExpressionWalker(GameController);
            return walker.Evaluate(expr);
        }
        if (query.StartsWith("describe:"))
        {
            var expr = query.Substring(9);
            var walker = new ExpressionWalker(GameController);
            return walker.DescribeType(expr);
        }

        var incPlayerStats = ql == "playerstats";
        var incBuffProbe = ql == "buffprobe";

        var isDeep = ql.StartsWith("deep:");
        var incPlayer = ql == "all" || ql == "player";
        var incArea = ql == "all" || ql == "area";
        var incEntities = !isDeep && (ql == "all" || ql.StartsWith("entities") || ql == "monsters" || ql == "items");
        var incNpcDialog = ql == "all" || ql == "npcdialog";
        var incMapData = ql == "all" || ql == "mapdata";
        var incUi = ql == "ui";
        var incStash = ql == "stash";

        var response = new BridgeResponse
        {
            Query = ql,
            Timestamp = DateTime.UtcNow.ToString("O"),
        };

        if (incPlayerStats) response.Stats = BuildPlayerStats();
        if (incBuffProbe) response.BuffProbe = BuildBuffProbe();
        if (incPlayer) response.Player = BuildPlayer();
        if (incArea) response.Area = BuildArea();
        if (incNpcDialog) response.NpcDialog = BuildNpcDialog();
        if (incMapData) response.MapData = BuildMapData();
        if (incUi) response.Ui = BuildUi();
        if (incStash) response.StashTabs = BuildStash();
        if (incEntities) response.Entities = BuildEntities(ql);
        if (isDeep) BuildDeep(response, query);

        return Serialize(response);
    }

    // ── PLAYER ──────────────────────────────────────────────────────

    private PlayerDto BuildPlayer()
    {
        var player = GameController.Player;
        var life = player.GetComponent<Life>();
        var buffs = player.GetComponent<Buffs>();

        var dto = new PlayerDto
        {
            Path = player.Path ?? "",
            Hp = life?.CurHP ?? 0,
            MaxHp = life?.MaxHP ?? 0,
            Es = life?.CurES ?? 0,
            MaxEs = life?.MaxES ?? 0,
            Mana = life?.CurMana ?? 0,
            MaxMana = life?.MaxMana ?? 0,
            Pos = [(float)Math.Round(player.GridPosNum.X), (float)Math.Round(player.GridPosNum.Y)],
        };

        if (buffs?.BuffsList != null)
            dto.Buffs = buffs.BuffsList.Select(BuildBuff).ToList();

        var actor = player.GetComponent<ExileCore.PoEMemory.Components.Actor>();
        if (actor != null)
            dto.Skills = BuildSkills(actor);

        return dto;
    }

    // ── PLAYER STATS (full dump, no truncation) ────────────────────

    private Dictionary<string, int>? BuildPlayerStats()
    {
        var player = GameController.Player;
        var stats = player?.GetComponent<Stats>();
        if (stats?.StatDictionary == null) return null;

        var result = new Dictionary<string, int>();
        foreach (var kv in stats.StatDictionary)
            result[kv.Key.ToString()] = kv.Value;
        return result;
    }

    // ── BUFF PROBE (memory dump for reverse-engineering) ────────────

    private List<BuffProbeDto>? BuildBuffProbe()
    {
        var mem = GameController.Memory;
        var player = GameController.Player;
        var buffs = player?.GetComponent<Buffs>()?.BuffsList;
        if (buffs == null) return null;

        var result = new List<BuffProbeDto>();
        foreach (var b in buffs)
        {
            if (b.Name != "stolen_mods_buff" && b.Name != "herald_of_ice") continue;

            var probe = new BuffProbeDto
            {
                Name = b.Name ?? "",
                Timer = SafeFloat(b.Timer),
                Address = b.Address.ToString("X"),
            };

            // Raw memory: 0x48 to 0x100
            for (int off = 0x48; off <= 0x100; off += 8)
            {
                try { probe.Raw[$"0x{off:X2}"] = mem.Read<long>(b.Address + off).ToString("X16"); }
                catch { probe.Raw[$"0x{off:X2}"] = "ERROR"; }
            }

            // Read StdVector at 0x80: {First, Last, End}
            try
            {
                var svFirst = mem.Read<long>(b.Address + 0x80);
                var svLast = mem.Read<long>(b.Address + 0x88);
                var dataSize = svLast - svFirst;
                var sv = new StdVectorDto
                {
                    First = svFirst.ToString("X"),
                    Last = svLast.ToString("X"),
                    DataSize = dataSize,
                };

                if (svFirst > 0x10000 && dataSize > 0 && dataSize < 1000)
                {
                    // Read the raw data as ints
                    sv.DataInts = new List<object>();
                    for (long addr = svFirst; addr < svLast && addr < svFirst + 64; addr += 4)
                    {
                        try { sv.DataInts.Add(mem.Read<int>(addr)); }
                        catch { sv.DataInts.Add("ERR"); }
                    }

                    // Read as (GameStat, int) pairs if size is multiple of 8
                    if (dataSize % 8 == 0 && dataSize >= 8)
                    {
                        sv.StatPairs = new List<object>();
                        for (long addr = svFirst; addr < svLast; addr += 8)
                        {
                            try
                            {
                                var stat = mem.Read<int>(addr);
                                var val = mem.Read<int>(addr + 4);
                                sv.StatPairs.Add(new StatPairDto
                                {
                                    StatId = stat,
                                    Stat = ((GameStat)stat).ToString(),
                                    Val = val,
                                });
                            }
                            catch { sv.StatPairs.Add("ERR"); }
                        }
                    }
                }
                probe.Sv80 = sv;
            }
            catch { }

            // Also try following tree nodes at ptr80 - read first few nodes
            try
            {
                var ptr80 = mem.Read<long>(b.Address + 0x80);
                if (ptr80 > 0x10000 && ptr80 < long.MaxValue / 2)
                {
                    probe.TreeNode0 = ReadTreeNode(mem, ptr80);

                    var child = mem.Read<long>(ptr80);
                    if (child > 0x10000 && child < long.MaxValue / 2)
                        probe.TreeNode1 = ReadTreeNode(mem, child);
                }
            }
            catch { }

            result.Add(probe);
        }
        return result;
    }

    private static Dictionary<string, object> ReadTreeNode(ExileCore.Shared.Interfaces.IMemory mem, long ptr)
    {
        var node = new Dictionary<string, object>();
        for (int off = 0; off <= 0x38; off += 4)
        {
            try { node[$"0x{off:X2}"] = mem.Read<int>(ptr + off); }
            catch { node[$"0x{off:X2}"] = "ERR"; }
        }
        return node;
    }

    // ── AREA ────────────────────────────────────────────────────────

    private AreaDto BuildArea()
    {
        var area = GameController.IngameState.Data.CurrentArea;
        return new AreaDto
        {
            Name = area?.Name,
            AreaLevel = area?.AreaLevel ?? 0,
            Act = area?.Act ?? 0,
        };
    }

    // ── NPC DIALOG ──────────────────────────────────────────────────

    private NpcDialogDto BuildNpcDialog()
    {
        var dto = new NpcDialogDto();
        try
        {
            var ui = GameController.IngameState.IngameUi;
            var npcDlg = ui.NpcDialog;
            var sd = GameController.IngameState.Data.ServerData;
            dto.IsVisible = npcDlg?.IsVisible == true;
            dto.DialogDepth = sd.DialogDepth;

            if (npcDlg?.IsVisible == true)
            {
                dto.NpcName = npcDlg.NpcName;
                dto.IsLoreTalk = npcDlg.IsLoreTalkVisible;
                try
                {
                    var lines = npcDlg.NpcLines;
                    dto.Lines = lines.Select(l => l.Text ?? "").ToList();
                }
                catch { dto.Lines = []; }
            }
        }
        catch (Exception ex) { dto.Error = ex.Message; }
        return dto;
    }

    // ── MAP DATA ────────────────────────────────────────────────────

    private MapDataDto BuildMapData()
    {
        var dto = new MapDataDto();
        try
        {
            var data = GameController.IngameState.Data;
            var sd = data.ServerData;
            dto.DialogDepth = sd.DialogDepth;

            try
            {
                var ms = data.MapStats;
                if (ms != null && ms.Count > 0)
                {
                    dto.MapStats = new Dictionary<string, int>();
                    foreach (var kv in ms.Take(100))
                        dto.MapStats[kv.Key.ToString()] = kv.Value;
                }
            }
            catch { }

            try
            {
                var qf = sd.QuestFlags;
                if (qf != null)
                {
                    var questDto = new QuestFlagsDto { Total = qf.Count };
                    foreach (var kv in qf)
                    {
                        var name = kv.Key.ToString();
                        if (name.Contains("Djinn") || name.Contains("OrderOfThe") || name.Contains("Faridun"))
                            questDto.Flags[name] = kv.Value;
                    }
                    dto.QuestFlags = questDto;
                }
            }
            catch { }
        }
        catch (Exception ex) { dto.Error = ex.Message; }
        return dto;
    }

    // ── UI PANEL SCAN ───────────────────────────────────────────────

    private UiDto BuildUi()
    {
        var dto = new UiDto();
        try
        {
            var ui = GameController.IngameState.IngameUi;
            var sd = GameController.IngameState.Data.ServerData;
            dto.DialogDepth = sd.DialogDepth;

            dto.NpcDialog = ui.NpcDialog?.IsVisible == true;
            dto.PurchaseWindow = ui.PurchaseWindow?.IsVisible == true;
            dto.SellWindow = ui.SellWindow?.IsVisible == true;
            dto.MapDeviceWindow = ui.MapDeviceWindow?.IsVisible == true;
            dto.TradeWindow = ui.TradeWindow?.IsVisible == true;
            dto.PopUpWindow = ui.PopUpWindow?.IsVisible == true;
            dto.RitualWindow = ui.RitualWindow?.IsVisible == true;
            dto.VillageRewardWindow = ui.VillageRewardWindow?.IsVisible == true;
            dto.MercenaryEncounterWindow = ui.MercenaryEncounterWindow?.IsVisible == true;
            dto.ZanaMissionChoice = ui.ZanaMissionChoice?.IsVisible == true;

            try
            {
                var lm = ui.LeagueMechanicButtons;
                dto.LeagueMechanicButtons = new LeagueMechanicButtonsDto
                {
                    Vis = lm?.IsVisible == true,
                    Cc = (int)(lm?.ChildCount ?? 0),
                };
            }
            catch { }

            int maxChildren = Settings.MaxUiChildren.Value;
            for (int i = 0; i < maxChildren; i++)
            {
                try
                {
                    var child = ui.GetChildAtIndex(i);
                    if (child != null && child.IsVisible)
                    {
                        var childDto = new UiChildDto
                        {
                            I = i,
                            Cc = (int)child.ChildCount,
                        };

                        var txt = child.Text;
                        if (!string.IsNullOrEmpty(txt))
                            childDto.T = Trunc(txt, 80);

                        var ct = new List<string>();
                        for (int j = 0; j < Math.Min(child.ChildCount, 20); j++)
                        {
                            try
                            {
                                var c2 = child.GetChildAtIndex(j);
                                if (c2 == null) continue;
                                var t2 = c2.Text;
                                if (!string.IsNullOrEmpty(t2)) ct.Add(Trunc(t2, 60));
                                for (int k = 0; k < Math.Min(c2.ChildCount, 10); k++)
                                {
                                    try
                                    {
                                        var c3 = c2.GetChildAtIndex(k);
                                        if (c3 == null) continue;
                                        var t3 = c3.Text;
                                        if (!string.IsNullOrEmpty(t3)) ct.Add(Trunc(t3, 60));
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                        if (ct.Count > 0) childDto.Ct = ct;
                        dto.VisibleChildren.Add(childDto);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { dto.Error = ex.Message; }
        return dto;
    }

    // ── STASH TABS ──────────────────────────────────────────────────

    private List<StashTabDto> BuildStash()
    {
        var result = new List<StashTabDto>();
        try
        {
            var tabs = GameController.IngameState.Data.ServerData.PlayerStashTabs;
            if (tabs != null)
            {
                for (var i = 0; i < tabs.Count; i++)
                {
                    var tab = tabs[i];
                    var flags = tab.Flags;
                    result.Add(new StashTabDto
                    {
                        Index = i,
                        Name = tab.Name ?? "",
                        Type = tab.TabType.ToString(),
                        VisibleIndex = tab.VisibleIndex,
                        Color = new ColorDto { R = tab.Color2.R, G = tab.Color2.G, B = tab.Color2.B },
                        IsPremium = (flags & InventoryTabFlags.Premium) != 0,
                        IsPublic = (flags & InventoryTabFlags.Public) != 0,
                        IsRemoveOnly = tab.RemoveOnly,
                        IsHidden = tab.IsHidden,
                        IsMapSeries = (flags & InventoryTabFlags.MapSeries) != 0,
                        RawFlags = (byte)flags,
                        Affinity = (uint)tab.Affinity,
                    });
                }
            }
        }
        catch { }
        return result;
    }

    // ── ENTITIES (shallow) ──────────────────────────────────────────

    private List<EntityDto> BuildEntities(string ql)
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

        return entities.OrderBy(e => e.DistancePlayer)
            .Select(e => BuildShallowEntity(e))
            .ToList();
    }

    private EntityDto BuildShallowEntity(Entity e)
    {
        var life = e.GetComponent<Life>();
        var render = e.GetComponent<Render>();
        var dto = new EntityDto
        {
            Id = e.Id,
            Type = e.Type.ToString(),
            Path = e.Path ?? "",
            Name = render?.Name,
            Alive = e.IsAlive,
            Hostile = e.IsHostile,
            Rarity = e.Rarity.ToString(),
            Dist = SafeFloat(e.DistancePlayer),
            Pos = [(float)Math.Round(e.GridPosNum.X), (float)Math.Round(e.GridPosNum.Y)],
            Hp = life?.CurHP ?? 0,
            MaxHp = life?.MaxHP ?? 0,
        };

        PopulateEffectComponents(dto, e);
        return dto;
    }

    // ── DEEP ENTITY INSPECTION ──────────────────────────────────────

    private void BuildDeep(BridgeResponse response, string query)
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

        response.Filter = filter;
        response.MatchCount = entities.Count;

        var maxStats = Settings.MaxDeepStats.Value;
        response.Entities = entities.Select(e => BuildDeepEntityFull(e, maxStats)).ToList();
    }

    private EntityDto BuildDeepEntityFull(Entity e, int maxStats)
    {
        var life = e.GetComponent<Life>();
        var render = e.GetComponent<Render>();

        var dto = new EntityDto
        {
            Id = e.Id,
            Type = e.Type.ToString(),
            Path = e.Path ?? "",
            Name = render?.Name,
            Alive = e.IsAlive,
            Hostile = e.IsHostile,
            Rarity = e.Rarity.ToString(),
            Dist = SafeFloat(e.DistancePlayer),
            Pos = [(float)Math.Round(e.GridPosNum.X), (float)Math.Round(e.GridPosNum.Y)],
            Hp = life?.CurHP ?? 0,
            MaxHp = life?.MaxHP ?? 0,
            IsValid = e.IsValid,
            End = true,
        };

        var cc = e.CacheComp;
        if (cc != null)
            dto.AllComponents = cc.Keys.ToList();

        if (render != null)
        {
            dto.Render = new RenderDto
            {
                Name = render.Name,
                Pos = [
                    (float)Math.Round(render.PosNum.X, 1),
                    (float)Math.Round(render.PosNum.Y, 1),
                    (float)Math.Round(render.PosNum.Z, 1)
                ],
                Bounds = [
                    (float)Math.Round(render.BoundsNum.X, 1),
                    (float)Math.Round(render.BoundsNum.Y, 1),
                    (float)Math.Round(render.BoundsNum.Z, 1)
                ],
            };
        }

        var pos = e.GetComponent<Positioned>();
        if (pos != null)
        {
            dto.Positioned = new PositionedDto
            {
                Grid = [pos.GridX, pos.GridY],
                Reaction = pos.Reaction,
                Size = pos.Size,
                Scale = (float)Math.Round(pos.Scale, 3),
                Rotation = (float)Math.Round(pos.Rotation, 3),
            };
        }

        var anim = e.GetComponent<Animated>();
        if (anim != null)
        {
            try
            {
                var bao = anim.BaseAnimatedObjectEntity;
                dto.Animated = new AnimatedDto
                {
                    BaseEntityPath = bao?.Path,
                    BaseEntityId = bao?.Id ?? 0,
                };
            }
            catch { dto.Animated = new AnimatedDto { Error = true }; }
        }

        PopulateStateMachine(dto, e, includeTargetFlags: true);

        var npc = e.GetComponent<NPC>();
        if (npc != null)
        {
            dto.Npc = new NpcDto
            {
                HasIconOverhead = npc.HasIconOverhead,
                IsIgnoreHidden = npc.IsIgnoreHidden,
                IsMinMapLabelVisible = npc.IsMinMapLabelVisible,
            };
        }

        if (life != null)
        {
            dto.Life = new LifeDto
            {
                Hp = life.CurHP,
                MaxHp = life.MaxHP,
                Es = life.CurES,
                MaxEs = life.MaxES,
            };
        }

        var tgt = e.GetComponent<Targetable>();
        if (tgt != null)
        {
            dto.Targetable = new TargetableDto
            {
                IsTargetable = tgt.isTargetable,
                IsTargeted = tgt.isTargeted,
            };
        }

        PopulateChest(dto, e);
        PopulateOmp(dto, e);
        PopulateMinimapIcon(dto, e);
        PopulateBuffs(dto, e);
        PopulateStats(dto, e, maxStats);
        PopulateEffectComponents(dto, e);

        return dto;
    }

    // ── RECORDING ENGINE ─────────────────────────────────────────────

    private string ProcessRecordingCommand(string ql, string query)
    {
        if (ql == "record:start" || ql.StartsWith("record:start:"))
        {
            if (_isRecording)
                return Serialize(new ErrorResponse { Error = "Already recording", File = _currentRecordingPath });

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

            return Serialize(new RecordingStartResponse
            {
                File = _currentRecordingPath,
                IntervalMs = Settings.RecordingIntervalMs.Value,
            });
        }

        if (ql == "record:stop")
        {
            if (!_isRecording)
                return Serialize(new RecordingStatusResponse { Status = "not_recording" });

            _isRecording = false;
            _recordingWriter?.Flush();
            _recordingWriter?.Dispose();
            _recordingWriter = null;

            var elapsed = (DateTime.UtcNow - _recordingStart).TotalMilliseconds;
            long fileSize = 0;
            try { fileSize = new FileInfo(_currentRecordingPath!).Length; } catch { }

            var result = Serialize(new RecordingStopResponse
            {
                Frames = _frameCount,
                DurationMs = Math.Round(elapsed),
                File = _currentRecordingPath ?? "",
                SizeBytes = fileSize,
            });
            _currentRecordingPath = null;
            return result;
        }

        if (ql == "record:status")
        {
            if (!_isRecording)
                return Serialize(new RecordingStatusResponse { Status = "idle", IsRecording = false });
            var elapsed = (DateTime.UtcNow - _recordingStart).TotalMilliseconds;
            return Serialize(new RecordingStatusResponse
            {
                Status = "recording",
                IsRecording = true,
                Frames = _frameCount,
                DurationMs = Math.Round(elapsed),
                File = _currentRecordingPath,
                IntervalMs = Settings.RecordingIntervalMs.Value,
            });
        }

        if (ql == "snapshot")
        {
            var snapshot = BuildSnapshot();
            var json = Serialize(snapshot);
            if (_isRecording && _recordingWriter != null)
            {
                _recordingWriter.WriteLine(json);
                _frameCount++;
                if (_frameCount % 25 == 0) _recordingWriter.Flush();
            }
            return json;
        }

        if (ql == "recording:list")
        {
            var recDir = Path.Combine(_bridgeDir, "recordings");
            if (!Directory.Exists(recDir))
                return Serialize(new RecordingListResponse());
            var files = Directory.GetFiles(recDir, "*.jsonl");
            return Serialize(new RecordingListResponse
            {
                Recordings = files.OrderByDescending(f => f)
                    .Select(f => new FileInfo(f))
                    .Select(fi => new RecordingFileDto
                    {
                        Name = fi.Name,
                        SizeBytes = fi.Length,
                        Modified = fi.LastWriteTimeUtc.ToString("O"),
                    })
                    .ToList(),
            });
        }

        if (ql.StartsWith("recording:load:"))
        {
            var fileName = query.Substring("recording:load:".Length).Trim();
            var recDir = Path.Combine(_bridgeDir, "recordings");
            var filePath = Path.Combine(recDir, fileName);
            if (!File.Exists(filePath))
                return Serialize(new ErrorResponse { Error = "File not found", File = fileName });
            LoadRecording(filePath);
            return Serialize(new RecordingLoadResponse
            {
                File = fileName,
                Frames = _loadedFrameOffsets?.Count ?? 0,
            });
        }

        if (ql.StartsWith("recording:frame:"))
        {
            if (_loadedRecordingPath == null || _loadedFrameOffsets == null)
                return Serialize(new ErrorResponse { Error = "No recording loaded. Use recording:load:filename first." });
            if (!int.TryParse(ql.Substring("recording:frame:".Length), out var n))
                return Serialize(new ErrorResponse { Error = "Invalid frame number" });
            if (n < 0 || n >= _loadedFrameOffsets.Count)
                return Serialize(new ErrorResponse { Error = $"Frame {n} out of range", TotalFrames = _loadedFrameOffsets.Count });
            return ReadFrame(n);
        }

        if (ql.StartsWith("recording:range:"))
        {
            if (_loadedRecordingPath == null || _loadedFrameOffsets == null)
                return Serialize(new ErrorResponse { Error = "No recording loaded" });
            var parts = ql.Substring("recording:range:".Length).Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var rangeStart) || !int.TryParse(parts[1], out var rangeEnd))
                return Serialize(new ErrorResponse { Error = "Usage: recording:range:N:M" });
            rangeStart = Math.Max(0, rangeStart);
            rangeEnd = Math.Min(_loadedFrameOffsets.Count - 1, rangeEnd);
            var frames = new List<object>();
            for (int i = rangeStart; i <= rangeEnd; i++)
                frames.Add(ReadFrame(i));
            return Serialize(new RecordingFramesResponse { Frames = frames });
        }

        if (ql.StartsWith("recording:search:"))
        {
            if (_loadedRecordingPath == null || _loadedFrameOffsets == null)
                return Serialize(new ErrorResponse { Error = "No recording loaded" });
            var term = query.Substring("recording:search:".Length).Trim();
            var matches = SearchFrames(term);
            return Serialize(new RecordingSearchResponse
            {
                Term = term,
                MatchCount = matches.Count,
                Frames = matches,
            });
        }

        if (ql == "recording:summary")
        {
            if (_loadedRecordingPath == null || _loadedFrameOffsets == null)
                return Serialize(new ErrorResponse { Error = "No recording loaded" });
            return SummarizeRecording();
        }

        return Serialize(new ErrorResponse { Error = "Unknown recording command", Query = ql });
    }

    private SnapshotResponse BuildSnapshot()
    {
        var elapsed = _isRecording ? (DateTime.UtcNow - _recordingStart).TotalMilliseconds : 0;
        var snapshot = new SnapshotResponse
        {
            Frame = _frameCount,
            Timestamp = DateTime.UtcNow.ToString("O"),
            ElapsedMs = Math.Round(elapsed),
            Area = BuildArea(),
            Player = BuildPlayerFull(),
            Entities = BuildRecordingEntities(),
        };
        return snapshot;
    }

    private PlayerDto BuildPlayerFull()
    {
        var player = GameController.Player;
        var life = player.GetComponent<Life>();
        var buffs = player.GetComponent<Buffs>();
        var actor = player.GetComponent<ExileCore.PoEMemory.Components.Actor>();
        var pos = player.GetComponent<Positioned>();

        var dto = new PlayerDto
        {
            Path = player.Path ?? "",
            Hp = life?.CurHP ?? 0,
            MaxHp = life?.MaxHP ?? 0,
            Es = life?.CurES ?? 0,
            MaxEs = life?.MaxES ?? 0,
            Mana = life?.CurMana ?? 0,
            MaxMana = life?.MaxMana ?? 0,
            Pos = [(float)Math.Round(player.GridPosNum.X), (float)Math.Round(player.GridPosNum.Y)],
        };

        if (pos != null)
            dto.Rotation = (float)Math.Round(pos.Rotation, 3);

        if (actor != null)
        {
            dto.Actor = BuildActorDto(actor);
            dto.Skills = BuildSkills(actor);
        }

        if (buffs?.BuffsList != null)
            dto.Buffs = buffs.BuffsList.Select(BuildBuff).ToList();

        return dto;
    }

    private List<EntityDto> BuildRecordingEntities()
    {
        var maxDist = (float)Settings.RecordingEntityRange.Value;
        var maxStats = Settings.RecordingMaxDeepStats.Value;
        var autoDeep = Settings.AutoDeepScanBosses.Value;

        var entities = GameController.EntityListWrapper.ValidEntitiesByType
            .SelectMany(kv => kv.Value)
            .Where(e => e.DistancePlayer < maxDist)
            .OrderBy(e => e.DistancePlayer)
            .ToList();

        var result = new List<EntityDto>();
        foreach (var e in entities)
        {
            var isElite = autoDeep && (e.Rarity == MonsterRarity.Unique || e.Rarity == MonsterRarity.Rare);
            var hasEffects = e.GetComponent<Beam>() != null
                || e.GetComponent<GroundEffect>() != null
                || e.GetComponent<EffectPack>() != null;

            if (isElite || hasEffects)
                result.Add(BuildDeepEntityRecording(e, maxStats));
            else
                result.Add(BuildShallowEntity(e));
        }
        return result;
    }

    private EntityDto BuildDeepEntityRecording(Entity e, int maxStats)
    {
        var life = e.GetComponent<Life>();
        var render = e.GetComponent<Render>();
        var actor = e.GetComponent<ExileCore.PoEMemory.Components.Actor>();
        var pos = e.GetComponent<Positioned>();

        var dto = new EntityDto
        {
            Id = e.Id,
            Deep = true,
            Type = e.Type.ToString(),
            Path = e.Path ?? "",
            Name = render?.Name,
            Alive = e.IsAlive,
            Hostile = e.IsHostile,
            Rarity = e.Rarity.ToString(),
            Dist = SafeFloat(e.DistancePlayer),
            Pos = [(float)Math.Round(e.GridPosNum.X), (float)Math.Round(e.GridPosNum.Y)],
            End = true,
        };

        if (life != null)
        {
            dto.Life = new LifeDto
            {
                Hp = life.CurHP,
                MaxHp = life.MaxHP,
                Es = life.CurES,
                MaxEs = life.MaxES,
            };
        }

        if (render != null)
        {
            dto.Render = new RenderDto
            {
                Pos = [
                    (float)Math.Round(render.PosNum.X, 1),
                    (float)Math.Round(render.PosNum.Y, 1),
                    (float)Math.Round(render.PosNum.Z, 1)
                ],
                Bounds = [
                    (float)Math.Round(render.BoundsNum.X, 1),
                    (float)Math.Round(render.BoundsNum.Y, 1),
                    (float)Math.Round(render.BoundsNum.Z, 1)
                ],
            };
        }

        if (pos != null)
        {
            dto.Positioned = new PositionedDto
            {
                Rotation = (float)Math.Round(pos.Rotation, 3),
                TravelProgress = (float)Math.Round(pos.TravelProgress, 3),
            };
        }

        if (actor != null)
            dto.Actor = BuildActorDto(actor);

        PopulateBuffs(dto, e);
        PopulateStateMachine(dto, e, includeTargetFlags: false);

        var tgt = e.GetComponent<Targetable>();
        if (tgt != null)
        {
            dto.Targetable = new TargetableDto
            {
                IsTargetable = tgt.isTargetable,
                IsTargeted = tgt.isTargeted,
            };
        }

        PopulateOmp(dto, e);
        PopulateStats(dto, e, maxStats);
        PopulateEffectComponents(dto, e);

        return dto;
    }

    private void CaptureSnapshot()
    {
        if (_recordingWriter == null) return;
        try
        {
            var snapshot = BuildSnapshot();
            _recordingWriter.WriteLine(Serialize(snapshot));
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
            return Serialize(new ErrorResponse { Error = "Invalid frame" });

        using var fs = new FileStream(_loadedRecordingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(_loadedFrameOffsets[n], SeekOrigin.Begin);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        return sr.ReadLine() ?? Serialize(new ErrorResponse { Error = "Empty frame" });
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
            return Serialize(new ErrorResponse { Error = "No recording loaded" });

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

        return Serialize(new RecordingSummaryDto
        {
            File = Path.GetFileName(_loadedRecordingPath),
            TotalFrames = totalFrames,
            FirstTimestamp = firstTimestamp,
            LastTimestamp = lastTimestamp,
            UniqueEntityPaths = entityPaths.OrderBy(p => p).ToList(),
            UniqueBuffNames = buffNames.OrderBy(n => n).ToList(),
        });
    }

    // ── Shared builders ─────────────────────────────────────────────

    private ActorDto BuildActorDto(ExileCore.PoEMemory.Components.Actor actor)
    {
        var dto = new ActorDto
        {
            ActionId = actor.ActionId,
            Action = actor.Action.ToString(),
            AnimationId = actor.AnimationId,
            Animation = actor.Animation.ToString(),
            IsMoving = actor.isMoving,
            IsAttacking = actor.isAttacking,
        };

        try
        {
            var ca = actor.CurrentAction;
            if (ca != null)
            {
                dto.CurrentAction = new CurrentActionDto
                {
                    Skill = ca.Skill?.Name,
                    Destination = [ca.Destination.X, ca.Destination.Y],
                };
                try { if (ca.Target != null) dto.CurrentAction.TargetId = ca.Target.Id; } catch { }
            }
        }
        catch { }

        return dto;
    }

    private List<SkillDto>? BuildSkills(ExileCore.PoEMemory.Components.Actor actor)
    {
        try
        {
            var skills = actor.ActorSkills;
            if (skills == null || skills.Count == 0) return null;

            return skills.Select(s =>
            {
                var dto = new SkillDto
                {
                    Id = s.Id,
                    Name = s.Name ?? "",
                    CanBeUsed = s.CanBeUsed,
                    IsOnSkillBar = s.IsOnSkillBar,
                    Cooldown = SafeFloat((float)s.Cooldown),
                    IsUsing = s.IsUsing,
                };

                try
                {
                    var activeSkill = s.EffectsPerLevel?.SkillGemWrapper?.ActiveSkill;
                    if (activeSkill != null)
                    {
                        var iname = activeSkill.InternalName;
                        if (!string.IsNullOrEmpty(iname)) dto.InternalName = iname;
                        var dname = activeSkill.DisplayName;
                        if (!string.IsNullOrEmpty(dname)) dto.DisplayName = dname;
                    }
                }
                catch { }

                try { dto.IsUserSkill = s.IsUserSkill; } catch { }
                try { dto.IsMine = s.IsMine; } catch { }
                try { dto.TotalUses = s.TotalUses; } catch { }
                try { dto.Cost = s.Cost; } catch { }

                return dto;
            }).ToList();
        }
        catch { return null; }
    }

    private static BuffDto BuildBuff(Buff b)
    {
        var dto = new BuffDto
        {
            Name = b.Name ?? "",
            Charges = b.BuffCharges,
            Timer = SafeFloat(b.Timer),
            DisplayName = b.DisplayName ?? "",
            Stacks = b.BuffStacks,
            MaxTime = SafeFloat(b.MaxTime),
            SourceEntityId = b.SourceEntityId,
            SourceSkillId = b.SourceSkillId,
        };

        var desc = b.Description;
        if (!string.IsNullOrEmpty(desc))
            dto.Description = Trunc(desc, 200);

        try
        {
            var src = b.SourceEntityId != 0 ? b.SourceEntity : null;
            var srcName = src?.GetComponent<Render>()?.Name;
            if (!string.IsNullOrEmpty(srcName))
                dto.SourceName = srcName;
        }
        catch { }

        return dto;
    }

    // ── Shared entity populators ────────────────────────────────────

    private static void PopulateEffectComponents(EntityDto dto, Entity e)
    {
        try
        {
            var beam = e.GetComponent<Beam>();
            if (beam != null)
            {
                var bs = beam.BeamStartNum;
                var be = beam.BeamEndNum;
                dto.Beam = new BeamDto
                {
                    Start = [(float)Math.Round(bs.X, 1), (float)Math.Round(bs.Y, 1), (float)Math.Round(bs.Z, 1)],
                    End = [(float)Math.Round(be.X, 1), (float)Math.Round(be.Y, 1), (float)Math.Round(be.Z, 1)],
                };
            }
        }
        catch { }

        try
        {
            var ge = e.GetComponent<GroundEffect>();
            if (ge != null)
            {
                dto.GroundEffect = new GroundEffectDto
                {
                    Duration = (float)Math.Round(ge.Duration, 2),
                    MaxDuration = (float)Math.Round(ge.MaxDuration, 2),
                    Scale = ge.Scale,
                    SizeIncrease = ge.SizeIncreaseOverTime,
                };
            }
        }
        catch { }

        try
        {
            var ep = e.GetComponent<EffectPack>();
            if (ep != null)
                dto.HasEffectPack = true;
        }
        catch { }

        try
        {
            var ac = e.GetComponent<AnimationController>();
            if (ac != null)
            {
                dto.AnimController = new AnimControllerDto
                {
                    AnimId = ac.CurrentAnimationId,
                    Stage = ac.CurrentAnimationStage,
                    Progress = (float)Math.Round(ac.AnimationProgress, 3),
                    Speed = (float)Math.Round(ac.AnimationSpeed, 3),
                };
            }
        }
        catch { }
    }

    private static void PopulateBuffs(EntityDto dto, Entity e)
    {
        var buffs = e.GetComponent<Buffs>();
        if (buffs?.BuffsList != null && buffs.BuffsList.Count > 0)
            dto.Buffs = buffs.BuffsList.Select(BuildBuff).ToList();
    }

    private static void PopulateStats(EntityDto dto, Entity e, int maxStats)
    {
        var stats = e.GetComponent<Stats>();
        if (stats == null) return;
        try
        {
            var sd = stats.StatDictionary;
            if (sd != null && sd.Count > 0)
            {
                var statsDto = new StatsDto();
                foreach (var kv in sd.Take(maxStats))
                    statsDto.Values[kv.Key.ToString()] = kv.Value;
                if (sd.Count > maxStats)
                    statsDto.Truncated = sd.Count;
                dto.Stats = statsDto;
            }
        }
        catch { }
    }

    private static void PopulateStateMachine(EntityDto dto, Entity e, bool includeTargetFlags)
    {
        var sm = e.GetComponent<StateMachine>();
        if (sm == null) return;
        try
        {
            var sts = sm.States;
            if (sts != null && sts.Count > 0)
            {
                var smDto = new StateMachineDto();
                if (includeTargetFlags)
                {
                    smDto.CanBeTarget = sm.CanBeTarget;
                    smDto.InTarget = sm.InTarget;
                }
                foreach (var s in sts)
                    smDto.States[s.Name ?? ""] = (int)s.Value;
                dto.StateMachine = smDto;
            }
        }
        catch { dto.StateMachine = new StateMachineDto { Error = true }; }
    }

    private static void PopulateChest(EntityDto dto, Entity e)
    {
        var chest = e.GetComponent<Chest>();
        if (chest != null)
        {
            dto.Chest = new ChestDto
            {
                IsOpened = chest.IsOpened,
                IsLocked = chest.IsLocked,
                IsStrongbox = chest.IsStrongbox,
                DestroyAfterOpen = chest.DestroyingAfterOpen,
                IsLarge = chest.IsLarge,
                Stompable = chest.Stompable,
                OpenOnDamage = chest.OpenOnDamage,
            };
        }
    }

    private static void PopulateOmp(EntityDto dto, Entity e)
    {
        var omp = e.GetComponent<ObjectMagicProperties>();
        if (omp != null)
        {
            var ompDto = new OmpDto { Rarity = omp.Rarity.ToString() };
            try
            {
                var mods = omp.Mods;
                if (mods != null && mods.Count > 0)
                    ompDto.Mods = mods.ToList();
            }
            catch { }
            dto.Omp = ompDto;
        }
    }

    private static void PopulateMinimapIcon(EntityDto dto, Entity e)
    {
        var mi = e.GetComponent<MinimapIcon>();
        if (mi != null)
        {
            dto.MinimapIcon = new MinimapIconDto
            {
                Name = mi.Name ?? "",
                IsVisible = mi.IsVisible,
                IsHide = mi.IsHide,
            };
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string Trunc(string s, int max)
        => s.Length > max ? s[..max] : s;

    private static float SafeFloat(float v)
        => float.IsInfinity(v) || float.IsNaN(v) ? 999999f : (float)Math.Round(v, 1);
}
