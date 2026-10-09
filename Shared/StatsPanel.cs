using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// In-HUD player stats panel (ImGui). One surface over the shared stats view in
/// <see cref="StatsUiState"/>: it reads Settings.StatsUi every frame and writes only through the
/// mutators in StatsFeature.cs (SetStatPinned, SetStatsFilter, SelectStat, SetStatsView), so the
/// MCP App and agents see each change within one poll, and the panel shows theirs the next frame.
/// Game-agnostic: ImGui plus the shared snapshot; nothing here touches ExileCore* directly.
/// </summary>
public partial class WhatsAnAiBridge
{
    // ── Fixed game hues (meaning never changes: life is red, mana is blue ...) ──
    private static readonly Vector4 HueLife = Hex(0xE2554B), HueEs = Hex(0x7FB8D9), HueMana = Hex(0x5F86EE);
    private static readonly Vector4 HueFire = Hex(0xF07A3C), HueCold = Hex(0x5CB4EC), HueLightning = Hex(0xE8C43A), HueChaos = Hex(0xC77AD4);
    private static readonly Vector4 ToneOk = Hex(0x4ADE80), ToneBad = Hex(0xF87171), ToneWarn = Hex(0xFBBF24);
    private static readonly Vector4 ToneAccent = Hex(0x00CED1), ToneNeutral = Hex(0x85837D);

    private const int DefaultResistCap = 75;
    private const double DeltaVisibleSec = 30, FlashSec = 1.6, RemoteNoticeSec = 2.5;

    private readonly struct ElementDef
    {
        public readonly string Element, Label, Key, BaseKey, UncappedKey, MaxKey, MaxKey2;
        public readonly Vector4 Hue;
        public ElementDef(string element, string label, Vector4 hue)
        {
            Element = element; Label = label; Hue = hue;
            Key = element + "_damage_resistance_%";
            BaseKey = "base_" + Key;
            UncappedKey = "uncapped_" + Key;
            MaxKey = "maximum_" + Key;
            MaxKey2 = "max_" + Key;
        }
        public bool Owns(string key) => key == Key || key == BaseKey || key == UncappedKey || key == MaxKey || key == MaxKey2;
    }

    private static readonly ElementDef[] Elements =
    [
        new("fire", "Fire", HueFire), new("cold", "Cold", HueCold),
        new("lightning", "Lightning", HueLightning), new("chaos", "Chaos", HueChaos),
    ];

    private struct ResistView
    {
        public int? Value, Uncapped;
        public int Cap;
        public bool CapAssumed;
    }

    /// <summary>Panel-local state. Nothing here is shared: the shared view lives in Settings.StatsUi.</summary>
    private sealed class PanelState
    {
        public StatsUiState? Seen;             // the shared state as the panel last saw it (for diffing)
        public long OwnRev = -1;               // rev produced by the panel's own last mutation
        public double RemoteAt = -1e9;         // ImGui time a change from the app / an agent was noticed
        public string RemoteWhat = "";
        public bool ScrollToSelected;
        public double HighlightUntil;
        public string FilterDraft = "";
        public bool FilterActive;
        public List<StatDto>? SnapRef;
        public readonly List<StatDto> Rows = new();
        public readonly Dictionary<string, StatDto> ByKey = new(StringComparer.Ordinal);
        public readonly int[] CategoryCounts = new int[StatCategories.All.Length];
        public string RowsFilter = "\0", RowsCategory = "", RowsSort = "";
        public bool RowsDesc;
        public readonly Dictionary<string, int> Prev = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (int delta, double at)> Changes = new(StringComparer.Ordinal);
        public double LastPrune;
        public string? DetailKey;
        public StatDetailResponse? Detail;
        public List<StatDto>? DetailSnap;
        public double CopiedAt = -1e9;
        public string Notice = "";
        public double NoticeAt = -1e9;
        public readonly Dictionary<string, string> Human = new(StringComparer.Ordinal);
        public string? LastError;
    }

    private readonly PanelState _panel = new();

    /// <summary>Colours derived from the HUD's current ImGui theme, read once per frame.</summary>
    private readonly struct PanelTheme
    {
        public readonly Vector4 WindowBg, FrameBg, Text, TextDim, Border, Card, Tile, Header;
        private PanelTheme(Vector4 windowBg, Vector4 frameBg, Vector4 text, Vector4 textDim, Vector4 border, Vector4 header)
        {
            WindowBg = windowBg; FrameBg = frameBg; Text = text; TextDim = textDim; Border = border; Header = header;
            Card = SurfaceFill(windowBg, frameBg);
            Tile = Shift(Card, Luma(Card) > 0.5f ? -0.06f : 0.06f);
        }
        public static PanelTheme Current()
        {
            var c = ImGui.GetStyle().Colors;
            return new PanelTheme(c[(int)ImGuiCol.WindowBg], c[(int)ImGuiCol.FrameBg], c[(int)ImGuiCol.Text],
                c[(int)ImGuiCol.TextDisabled], c[(int)ImGuiCol.Border], c[(int)ImGuiCol.Header]);
        }
    }

    // ── Frame ────────────────────────────────────────────────────────

    private void DrawStatsPanel()
    {
        var state = StatsUi;
        if (!state.PanelOpen) { _panel.Seen = null; return; }

        var now = ImGui.GetTime();
        ObservePanelState(state, now);

        var inGame = GameController.InGame;
        if (inGame) RefreshPanelData(state, GetStatsSnapshot(), now);

        // Default: under the bridge status strip, ending just above the life globe at 1080p; clear of the minimap.
        ImGui.SetNextWindowPos(new Vector2(16, 232), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(440, 620), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(380, 300), new Vector2(float.MaxValue, float.MaxValue));
        ImGui.SetNextWindowBgAlpha(0.95f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 5));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        // Quiet scrollbars: the theme's accent grab shouts next to the data.
        var textCol = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, 0);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, U(textCol, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, U(textCol, 0.3f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, U(textCol, 0.4f));

        var open = true;
        var shown = ImGui.Begin(GameId == "poe2" ? "Player Stats - PoE 2###bridge_stats_panel" : "Player Stats - PoE 1###bridge_stats_panel",
            ref open, ImGuiWindowFlags.NoCollapse);
        try
        {
            if (shown) DrawStatsPanelBody(state, inGame, now);
        }
        catch (Exception ex)
        {
            if (_panel.LastError != ex.Message)
            {
                _panel.LastError = ex.Message;
                LogError($"[StatsPanel] {ex}");
            }
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar(4);
        }

        if (!open) PanelApply(SetStatsView(null, null, false), now);
    }

    /// <summary>Notice changes made elsewhere (app / agent) so the panel can point at them.</summary>
    private void ObservePanelState(StatsUiState state, double now)
    {
        var seen = _panel.Seen;
        if (seen == null)
        {
            // Panel (re)opened: bring an existing selection into view, quietly.
            _panel.Seen = state.Clone();
            _panel.ScrollToSelected = state.SelectedStatKey != null;
            return;
        }
        if (state.Rev == seen.Rev) return;

        if (state.Rev != _panel.OwnRev)
        {
            var what = new List<string>(4);
            if (state.SelectedStatKey != seen.SelectedStatKey)
            {
                what.Add("selection");
                if (state.SelectedStatKey != null) { _panel.ScrollToSelected = true; _panel.HighlightUntil = now + RemoteNoticeSec; }
            }
            if (!SameKeys(state.PinnedStatKeys, seen.PinnedStatKeys)) what.Add("pins");
            if (state.Filter != seen.Filter || state.Category != seen.Category) what.Add("filter");
            if (state.SortBy != seen.SortBy || state.SortDesc != seen.SortDesc) what.Add("sort");
            // The row list moved under the selection: keep the selected row in view (quietly).
            if ((what.Contains("filter") || what.Contains("sort")) && state.SelectedStatKey != null) _panel.ScrollToSelected = true;
            if (what.Count > 0)
            {
                _panel.RemoteAt = now;
                _panel.RemoteWhat = what.Count == 1 ? what[0] : $"{what.Count} changes";
            }
        }
        _panel.Seen = state.Clone();
    }

    private static bool SameKeys(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Every panel write goes through here: remembers the rev it produced and surfaces errors.</summary>
    private void PanelApply(StatsMutationResponse r, double now)
    {
        if (r.Ok)
        {
            _panel.OwnRev = r.Rev;
            _panel.Seen = r.State;
            return;
        }
        _panel.Notice = r.Message ?? r.Error ?? "error";
        _panel.NoticeAt = now;
    }

    /// <summary>Rebuilds the per-snapshot indexes and the filtered/sorted rows only when their inputs change.</summary>
    private void RefreshPanelData(StatsUiState state, List<StatDto> snap, double now)
    {
        var p = _panel;
        var snapChanged = !ReferenceEquals(snap, p.SnapRef);
        if (snapChanged)
        {
            p.SnapRef = snap;
            p.ByKey.Clear();
            foreach (var s in snap)
            {
                p.ByKey[s.Key] = s;
                if (p.Prev.TryGetValue(s.Key, out var pv) && pv != s.Value) p.Changes[s.Key] = (s.Value - pv, now);
                p.Prev[s.Key] = s.Value;
            }
            if (now - p.LastPrune > 5)
            {
                p.LastPrune = now;
                List<string>? stale = null;
                foreach (var kv in p.Changes)
                    if (now - kv.Value.at > DeltaVisibleSec) (stale ??= new List<string>()).Add(kv.Key);
                if (stale != null) foreach (var k in stale) p.Changes.Remove(k);
            }
        }

        if (!snapChanged && p.RowsFilter == state.Filter && p.RowsCategory == state.Category
            && p.RowsSort == state.SortBy && p.RowsDesc == state.SortDesc) return;

        p.RowsFilter = state.Filter; p.RowsCategory = state.Category; p.RowsSort = state.SortBy; p.RowsDesc = state.SortDesc;
        p.Rows.Clear();
        Array.Clear(p.CategoryCounts);
        var f = state.Filter.Trim();
        var cat = state.Category;
        foreach (var s in snap)
        {
            // Category counts follow the text filter (like the app), not the category pick.
            if (f.Length > 0 && !s.Key.Contains(f, StringComparison.OrdinalIgnoreCase)
                             && !(s.Text?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            var ci = Array.IndexOf(StatCategories.All, s.Category);
            if (ci >= 0) p.CategoryCounts[ci]++;
            if (cat != "all" && s.Category != cat) continue;
            p.Rows.Add(s);
        }
        p.Rows.Sort((state.SortBy, state.SortDesc) switch
        {
            ("value", false) => static (a, b) => a.Value != b.Value ? a.Value.CompareTo(b.Value) : string.CompareOrdinal(a.Key, b.Key),
            ("value", true) => static (a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key),
            ("key", false) => static (a, b) => string.CompareOrdinal(a.Key, b.Key),
            ("key", true) => static (a, b) => string.CompareOrdinal(b.Key, a.Key),
            (_, false) => static (a, b) => CategoryRank(a) != CategoryRank(b) ? CategoryRank(a).CompareTo(CategoryRank(b)) : string.CompareOrdinal(a.Key, b.Key),
            (_, true) => static (a, b) => CategoryRank(a) != CategoryRank(b) ? CategoryRank(b).CompareTo(CategoryRank(a)) : string.CompareOrdinal(a.Key, b.Key),
        });
    }

    private static int CategoryRank(StatDto s) => Array.IndexOf(StatCategories.All, s.Category);

    // ── Body ─────────────────────────────────────────────────────────

    private void DrawStatsPanelBody(StatsUiState state, bool inGame, double now)
    {
        var th = PanelTheme.Current();
        DrawPanelHeader(state, th, now);

        if (!inGame)
        {
            PanelBanner(th, "Not in game. Values refresh once you are in an area.");
        }

        // Summaries only while collapsed: expanded sections already show the numbers.
        var vitals = inGame ? BuildVitals() : null;
        if (PanelSection("vitals", "VITALS", Settings.StatsPanelVitalsOpen,
                vitals == null || Settings.StatsPanelVitalsOpen.Value ? null : VitalsSummary(vitals)))
            DrawVitals(vitals, th, now);

        if (PanelSection("resists", "RESISTANCES", Settings.StatsPanelResistsOpen,
                Settings.StatsPanelResistsOpen.Value ? null : ResistSummary()))
            DrawResists(state, th, now);

        var pinCount = state.PinnedStatKeys.Count;
        if (PanelSection("pinned", "PINNED", Settings.StatsPanelPinnedOpen, pinCount == 0 ? "none" : pinCount.ToString()))
            DrawPinned(state, th, now);

        DrawStatTable(state, th, now);

        if (state.SelectedStatKey != null) DrawDetail(state, th, now);
    }

    private void DrawPanelHeader(StatsUiState state, PanelTheme th, double now)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var f = ImGui.GetFontSize();
        var h = f + 8;
        var x = p.X;

        // Game badge
        var badge = GameId == "poe2" ? "PoE 2" : "PoE 1";
        var bw = ImGui.CalcTextSize(badge).X + 10;
        dl.AddRectFilled(new Vector2(x, p.Y + 2), new Vector2(x + bw, p.Y + h - 2), U(th.Text), 4f);
        dl.AddText(new Vector2(x + 5, p.Y + 4), U(th.WindowBg, 1f), badge);
        x += bw + 8;

        if (_panel.ByKey.TryGetValue("level", out var lvl))
        {
            var t = T("Lv ", (int)lvl.Value);
            dl.AddText(new Vector2(x, p.Y + 4), U(th.TextDim), t);
            x += ImGui.CalcTextSize(t).X + 10;
        }

        var vitals = GameController.InGame ? BuildVitals() : null;
        if (vitals?.WeaponSet is int set)
        {
            var t = set == 0 ? "Set I" : "Set II";
            var tw = ImGui.CalcTextSize(t).X + 10;
            dl.AddRect(new Vector2(x, p.Y + 2), new Vector2(x + tw, p.Y + h - 2), U(th.Border), 4f);
            dl.AddText(new Vector2(x + 5, p.Y + 4), U(th.TextDim), t);
            ImGui.SetCursorScreenPos(new Vector2(x, p.Y + 2));
            ImGui.InvisibleButton("##wset", new Vector2(tw, h - 4));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Active weapon set; stats differ per set");
            x += tw + 8;
        }

        // Right side: error notice or the sync pill
        if (now - _panel.NoticeAt < 4)
        {
            var t = _panel.Notice.Length > 60 ? _panel.Notice[..60] + ".." : _panel.Notice;
            var tw = ImGui.CalcTextSize(t).X;
            dl.AddText(new Vector2(p.X + w - tw, p.Y + 4), U(ToneBad), t);
        }
        else
        {
            var remote = now - _panel.RemoteAt < RemoteNoticeSec;
            var clients = _tcpServer?.ConnectedClients ?? 0;
            var label = remote ? "Synced: " + _panel.RemoteWhat : clients > 0 ? "Linked" : "Shared view";
            var tw = ImGui.CalcTextSize(label).X;
            var pillW = tw + 22;
            var px = p.X + w - pillW;
            dl.AddRectFilled(new Vector2(px, p.Y + 2), new Vector2(px + pillW, p.Y + h - 2), U(th.Card), (h - 4) * 0.5f);
            var dotCol = remote ? ToneAccent : clients > 0 ? ToneAccent : ToneNeutral;
            var r = 3f;
            if (remote)
            {
                var pulse = (float)(0.5 + 0.5 * Math.Sin((now - _panel.RemoteAt) * 9));
                dl.AddCircle(new Vector2(px + 10, p.Y + h * 0.5f), r + 2 + pulse * 2, U(ToneAccent, 0.5f - pulse * 0.4f), 12, 1.5f);
            }
            dl.AddCircleFilled(new Vector2(px + 10, p.Y + h * 0.5f), r, U(dotCol));
            dl.AddText(new Vector2(px + 17, p.Y + 4), U(remote ? ToneAccent : th.TextDim), label);
            ImGui.SetCursorScreenPos(new Vector2(px, p.Y + 2));
            ImGui.InvisibleButton("##syncpill", new Vector2(pillW, h - 4));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Pins, filter, sort and selection are one shared view:\nthis panel, the Claude app and agents all edit the same state.\nrev {state.Rev}  -  bridge clients: {clients}");
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(w, h));
    }

    private static void PanelBanner(PanelTheme th, string text)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var f = ImGui.GetFontSize();
        var h = f + 10;
        dl.AddRectFilled(p, new Vector2(p.X + w, p.Y + h), U(ToneWarn, 0.12f), 4f);
        dl.AddRect(p, new Vector2(p.X + w, p.Y + h), U(ToneWarn, 0.4f), 4f);
        dl.AddText(new Vector2(p.X + 8, p.Y + 5), U(ToneWarn), text);
        ImGui.Dummy(new Vector2(w, h));
    }

    /// <summary>Collapsible section header: a disclosure triangle, a small-caps title and a right-aligned summary.</summary>
    private static bool PanelSection(string id, string title, ToggleNode openNode, string? summary)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var f = ImGui.GetFontSize();
        var h = f + 4;
        var small = f * 0.85f;
        var th = PanelTheme.Current();

        ImGui.InvisibleButton("##sec_" + id, new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked()) openNode.Value = !openNode.Value;
        var open = openNode.Value;
        var col = U(hovered ? th.Text : th.TextDim);

        var cy = p.Y + h * 0.5f;
        var tx = p.X + 4;
        if (open)
            dl.AddTriangleFilled(new Vector2(tx, cy - 2), new Vector2(tx + 7, cy - 2), new Vector2(tx + 3.5f, cy + 2.5f), col);
        else
            dl.AddTriangleFilled(new Vector2(tx + 1, cy - 3.5f), new Vector2(tx + 5.5f, cy), new Vector2(tx + 1, cy + 3.5f), col);

        dl.AddText(ImGui.GetFont(), small, new Vector2(p.X + 16, cy - small * 0.5f), col, title);
        if (summary != null)
        {
            var sw = ImGui.CalcTextSize(summary).X * (small / f);
            dl.AddText(ImGui.GetFont(), small, new Vector2(p.X + w - sw, cy - small * 0.5f), U(th.TextDim), summary);
        }
        return open;
    }

    // ── Vitals ───────────────────────────────────────────────────────

    private static string VitalsSummary(VitalsDto v)
    {
        var key = (v.Hp, v.MaxEs > 0 ? v.Es : int.MinValue, v.Mana);
        if (VitalsCache.TryGetValue(key, out var s)) return s;
        if (VitalsCache.Count > 1024) VitalsCache.Clear();
        return VitalsCache[key] = v.MaxEs > 0 ? $"{N(v.Hp)} / {N(v.Es)} / {N(v.Mana)}" : $"{N(v.Hp)} / {N(v.Mana)}";
    }

    private static void DrawVitals(VitalsDto? v, PanelTheme th, double now)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var f = ImGui.GetFontSize();
        var tileH = f * 3.3f;

        if (v == null)
        {
            dl.AddText(new Vector2(p.X + 4, p.Y + 2), U(th.TextDim), "No vitals (player not loaded).");
            ImGui.Dummy(new Vector2(w, f + 4));
            return;
        }

        var n = v.MaxEs > 0 ? 3 : 2;
        const float gap = 6f;
        var tileW = (w - gap * (n - 1)) / n;
        var x = p.X;
        for (var i = 0; i < n; i++)
        {
            string label; int cur, max; Vector4 hue;
            if (i == 0) { label = "Life"; cur = v.Hp; max = v.MaxHp; hue = HueLife; }
            else if (n == 3 && i == 1) { label = "Energy Shield"; cur = v.Es; max = v.MaxEs; hue = HueEs; }
            else { label = "Mana"; cur = v.Mana; max = v.MaxMana; hue = HueMana; }
            var frac = max > 0 ? Math.Clamp(cur / (float)max, 0f, 1f) : 0f;

            var min = new Vector2(x, p.Y);
            var mx = new Vector2(x + tileW, p.Y + tileH);
            dl.AddRectFilled(min, mx, U(th.Card), 5f);
            dl.AddText(ImGui.GetFont(), f * 0.85f, new Vector2(x + 8, p.Y + 5), U(hue), label);

            var big = f * 1.3f;
            var curText = N(cur);
            var by = p.Y + 5 + f * 0.85f + 3;
            dl.AddText(ImGui.GetFont(), big, new Vector2(x + 8, by), U(th.Text), curText);
            var bigW = ImGui.CalcTextSize(curText).X * (big / f);
            var maxText = "/ " + N(max);
            dl.PushClipRect(min, mx, true);
            dl.AddText(new Vector2(x + 8 + bigW + 5, by + (big - f)), U(th.TextDim), maxText);
            dl.PopClipRect();

            var barY = p.Y + tileH - 10;
            dl.AddRectFilled(new Vector2(x + 8, barY), new Vector2(x + tileW - 8, barY + 4), U(th.FrameBg), 2f);
            var alpha = 1f;
            if (i == 0 && frac < 0.35f) alpha = 0.55f + 0.45f * (float)(0.5 + 0.5 * Math.Sin(now * 5));
            if (frac > 0)
                dl.AddRectFilled(new Vector2(x + 8, barY), new Vector2(x + 8 + (tileW - 16) * frac, barY + 4), U(hue, alpha), 2f);

            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton("##vital" + i, mx - min);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{label}: {N(cur)} / {N(max)} ({(int)Math.Round(frac * 100)}%)");

            x += tileW + gap;
        }
        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(w, tileH));
    }

    // ── Resistances ──────────────────────────────────────────────────

    private ResistView ResistOf(in ElementDef e)
    {
        var by = _panel.ByKey;
        int? Val(string k) => by.TryGetValue(k, out var s) ? s.Value : null;
        var max = Val(e.MaxKey) ?? Val(e.MaxKey2);
        var value = Val(e.Key) ?? Val(e.BaseKey);
        return new ResistView
        {
            Value = value,
            Uncapped = Val(e.UncappedKey) ?? value,
            Cap = max ?? DefaultResistCap,
            CapAssumed = max == null,
        };
    }

    private string? ResistSummary()
    {
        if (_panel.ByKey.Count == 0) return null;
        var parts = new string[Elements.Length];
        for (var i = 0; i < Elements.Length; i++)
        {
            var r = ResistOf(Elements[i]);
            parts[i] = r.Value.HasValue ? T("", r.Value.Value) : "0";
        }
        return string.Join(" / ", parts);
    }

    private void DrawResists(StatsUiState state, PanelTheme th, double now)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var tileH = f * 4.9f;
        const float gap = 6f;
        var tileW = (w - gap * 3) / 4;
        var anyAssumed = false;
        var x = p.X;

        for (var i = 0; i < Elements.Length; i++)
        {
            ref readonly var e = ref Elements[i];
            var r = ResistOf(e);
            var has = r.Value.HasValue;
            var v = r.Value ?? 0;
            var over = Math.Max(0, (r.Uncapped ?? v) - r.Cap);
            var negative = v < 0;
            var capped = has && v >= r.Cap;
            anyAssumed |= has && r.CapAssumed;
            var pinned = state.PinnedStatKeys.Contains(e.Key);
            var selected = state.SelectedStatKey == e.Key;

            var min = new Vector2(x, p.Y);
            var mx = new Vector2(x + tileW, p.Y + tileH);

            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton("##res" + i, mx - min);
            var hovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) PanelApply(SelectStat(selected ? null : e.Key), now);
            if (hovered)
                ImGui.SetTooltip($"{e.Label} resistance: {(has ? v + "%" : "no stat on the character (0%)")}  -  cap {r.Cap}%{(r.CapAssumed ? " (assumed)" : "")}{(over > 0 ? $"  -  {over}% over cap" : "")}\n{e.Key}\nClick to select, pin with the pin icon");

            // Pin hit area (top-right), drawn over the tile button
            var pinMin = new Vector2(mx.X - 18, p.Y + 3);
            ImGui.SetCursorScreenPos(pinMin);
            ImGui.InvisibleButton("##respin" + i, new Vector2(15, 15));
            var pinHovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) PanelApply(SetStatPinned(e.Key, !pinned), now);
            if (pinHovered) ImGui.SetTooltip(pinned ? "Unpin" : "Pin (shared with the Claude app)");

            dl.AddRectFilled(min, mx, U(hovered ? th.Tile : th.Card), 5f);
            if (selected)
            {
                var glow = now < _panel.HighlightUntil ? (float)(0.5 + 0.5 * Math.Sin((now - _panel.HighlightUntil) * 8)) : 0f;
                dl.AddRectFilled(min, mx, U(ToneAccent, 0.08f + glow * 0.12f), 5f);
                dl.AddRect(min, mx, U(ToneAccent, 0.9f), 5f, ImDrawFlags.None, 1.5f);
            }

            dl.AddText(ImGui.GetFont(), small, new Vector2(x + 7, p.Y + 5), U(e.Hue), e.Label);
            if (pinned || hovered || pinHovered)
                DrawPinGlyph(dl, new Vector2(pinMin.X + 7.5f, pinMin.Y + 7.5f), pinned ? U(ToneWarn) : U(th.TextDim, pinHovered ? 1f : 0.7f));

            var big = f * 1.3f;
            var valCol = !has ? th.TextDim : negative ? ToneBad : th.Text;
            dl.AddText(ImGui.GetFont(), big, new Vector2(x + 7, p.Y + 5 + small + 3), U(valCol), T("", v, "%"));

            string status; Vector4 tone;
            if (!has) { status = "no stat"; tone = th.TextDim; }
            else if (negative) { status = "below zero"; tone = ToneBad; }
            else if (capped) { status = over > 0 ? "capped +" + over : "capped"; tone = ToneOk; }
            else { status = (r.Cap - v) + " to cap"; tone = ToneWarn; }
            dl.PushClipRect(min, mx, true);
            dl.AddText(ImGui.GetFont(), small, new Vector2(x + 7, p.Y + 5 + small + 3 + big + 2), U(tone), status);
            dl.PopClipRect();

            // Bar with the cap tick
            var barY = p.Y + tileH - 10;
            var bx0 = x + 7; var bx1 = mx.X - 7; var bw = bx1 - bx0;
            dl.AddRectFilled(new Vector2(bx0, barY), new Vector2(bx1, barY + 4), U(th.FrameBg), 2f);
            if (negative)
            {
                dl.AddRectFilled(new Vector2(bx0, barY), new Vector2(bx0 + bw * Math.Min(100, -v) / 100f, barY + 4), U(ToneBad), 2f);
            }
            else if (has)
            {
                var fill = Math.Clamp(Math.Min(v, r.Cap) / 100f, 0f, 1f);
                if (fill > 0) dl.AddRectFilled(new Vector2(bx0, barY), new Vector2(bx0 + bw * fill, barY + 4), U(e.Hue), 2f);
                if (over > 0)
                {
                    var o0 = bx0 + bw * Math.Clamp(r.Cap / 100f, 0f, 1f);
                    var o1 = bx0 + bw * Math.Clamp(Math.Min(r.Cap + over, 100) / 100f, 0f, 1f);
                    dl.AddRectFilled(new Vector2(o0, barY), new Vector2(o1, barY + 4), U(e.Hue, 0.45f), 2f);
                }
            }
            var tickX = bx0 + bw * Math.Clamp(r.Cap / 100f, 0f, 1f);
            dl.AddLine(new Vector2(tickX, barY - 2), new Vector2(tickX, barY + 6), U(th.Text, r.CapAssumed ? 0.45f : 0.8f), 1f);

            x += tileW + gap;
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(w, tileH));

        if (anyAssumed)
        {
            var note = "75% cap assumed - no maximum_*_resistance stat";
            var nw = ImGui.CalcTextSize(note).X * (f * 0.8f / f);
            var np = ImGui.GetCursorScreenPos();
            dl.AddText(ImGui.GetFont(), f * 0.8f, new Vector2(np.X + w - nw, np.Y - 3), U(th.TextDim, 0.85f), note);
            ImGui.Dummy(new Vector2(w, f * 0.8f - 2));
        }
    }

    // ── Pinned chips ─────────────────────────────────────────────────

    private void DrawPinned(StatsUiState state, PanelTheme th, double now)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;

        if (state.PinnedStatKeys.Count == 0)
        {
            dl.AddText(ImGui.GetFont(), small, new Vector2(p.X + 4, p.Y + 2), U(th.TextDim),
                "Nothing pinned. Hover a stat and click its pin, or ask Claude to pin one.");
            ImGui.Dummy(new Vector2(w, small + 4));
            return;
        }

        var chipH = f + 8;
        const float gap = 5f, pad = 7f;
        var x = p.X; var y = p.Y;
        for (var i = 0; i < state.PinnedStatKeys.Count; i++)
        {
            var key = state.PinnedStatKeys[i];
            _panel.ByKey.TryGetValue(key, out var s);
            var label = ChipLabel(key);
            var value = s != null ? FmtStat(key, s.Value) : "-";
            var hasChange = _panel.Changes.TryGetValue(key, out var ch) && now - ch.at < DeltaVisibleSec;
            var delta = hasChange ? Signed(ch.delta) : "";

            var lw = ImGui.CalcTextSize(label).X;
            var vw = ImGui.CalcTextSize(value).X;
            var dw = hasChange ? ImGui.CalcTextSize(delta).X * (small / f) + 4 : 0;
            var cw = pad + lw + 6 + vw + dw + 6 + 10 + pad;
            if (x > p.X && x + cw > p.X + w) { x = p.X; y += chipH + gap; }

            var min = new Vector2(x, y);
            var mx = new Vector2(x + cw, y + chipH);
            var selected = state.SelectedStatKey == key;

            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton("##chip" + i, mx - min);
            var hovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) PanelApply(SelectStat(selected ? null : key), now);
            if (hovered) ImGui.SetTooltip($"{key}{(s?.Text != null ? "\n" + s.Text : "")}{(s == null ? "\nNot on the character right now (0)" : "")}");

            var xMin = new Vector2(mx.X - pad - 10, y + (chipH - 12) * 0.5f);
            ImGui.SetCursorScreenPos(xMin);
            ImGui.InvisibleButton("##unpin" + i, new Vector2(12, 12));
            var xHovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) PanelApply(SetStatPinned(key, false), now);
            if (xHovered) ImGui.SetTooltip("Unpin");

            dl.AddRectFilled(min, mx, U(hovered ? th.Tile : th.Card), chipH * 0.5f);
            if (selected) dl.AddRect(min, mx, U(ToneAccent, 0.9f), chipH * 0.5f, ImDrawFlags.None, 1.5f);
            else dl.AddRect(min, mx, U(th.Border, 0.6f), chipH * 0.5f);

            var tx = x + pad;
            dl.AddText(new Vector2(tx, y + 4), U(th.TextDim), label);
            tx += lw + 6;
            if (hasChange && now - ch.at < FlashSec)
            {
                var t = (float)((now - ch.at) / FlashSec);
                dl.AddRectFilled(new Vector2(tx - 2, y + 3), new Vector2(tx + vw + 2, y + chipH - 3), U(ch.delta > 0 ? ToneOk : ToneBad, 0.3f * (1 - t)), 3f);
            }
            dl.AddText(new Vector2(tx, y + 4), U(s == null ? th.TextDim : th.Text), value);
            tx += vw + 4;
            if (hasChange)
                dl.AddText(ImGui.GetFont(), small, new Vector2(tx, y + 4 + (f - small) * 0.5f), U(ch.delta > 0 ? ToneOk : ToneBad), delta);

            DrawCloseGlyph(dl, new Vector2(xMin.X + 6, xMin.Y + 6), 3f, U(xHovered ? th.Text : th.TextDim, xHovered ? 1f : 0.8f));

            x += cw + gap;
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(w, y + chipH - p.Y));
    }

    private string ChipLabel(string key)
    {
        foreach (var e in Elements)
        {
            if (key == e.Key) return e.Label + " res";
            if (key == e.BaseKey) return "Base " + e.Label.ToLowerInvariant() + " res";
            if (key == e.UncappedKey) return "Uncapped " + e.Label.ToLowerInvariant() + " res";
            if (key == e.MaxKey || key == e.MaxKey2) return "Max " + e.Label.ToLowerInvariant() + " res";
        }
        var h = Humanize(key);
        return h.Length > 26 ? h[..24] + ".." : h;
    }

    // ── Table ────────────────────────────────────────────────────────

    private void DrawStatTable(StatsUiState state, PanelTheme th, double now)
    {
        var f = ImGui.GetFontSize();
        var dl = ImGui.GetWindowDrawList();

        // Toolbar: search + keys toggle
        if (!_panel.FilterActive && _panel.FilterDraft != state.Filter) _panel.FilterDraft = state.Filter;
        var keysLabel = "Keys";
        var keysW = ImGui.CalcTextSize(keysLabel).X + 18;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - keysW - ImGui.GetStyle().ItemSpacing.X);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, U(th.Card));
        if (ImGui.InputTextWithHint("##statfilter", "Search key or in-game text...", ref _panel.FilterDraft, 100))
            PanelApply(SetStatsFilter(_panel.FilterDraft, null), now);
        ImGui.PopStyleColor();
        _panel.FilterActive = ImGui.IsItemActive();
        if (_panel.FilterDraft.Length > 0)
        {
            // Clear glyph inside the box
            var iMin = ImGui.GetItemRectMin(); var iMax = ImGui.GetItemRectMax();
            var cx = iMax.X - 12; var cy = (iMin.Y + iMax.Y) * 0.5f;
            ImGui.SetCursorScreenPos(new Vector2(cx - 8, cy - 8));
            ImGui.InvisibleButton("##clearfilter", new Vector2(16, 16));
            var hov = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) { _panel.FilterDraft = ""; PanelApply(SetStatsFilter("", null), now); }
            DrawCloseGlyph(dl, new Vector2(cx, cy), 3.5f, U(hov ? th.Text : th.TextDim));
            ImGui.SetCursorScreenPos(new Vector2(iMax.X, iMin.Y));
        }
        ImGui.SameLine();
        var showKeys = Settings.StatsPanelShowKeys.Value;
        ImGui.PushStyleColor(ImGuiCol.Button, U(showKeys ? th.Header : th.Card));
        ImGui.PushStyleColor(ImGuiCol.Text, U(showKeys ? th.Text : th.TextDim));
        if (ImGui.Button(keysLabel, new Vector2(keysW, 0))) Settings.StatsPanelShowKeys.Value = !showKeys;
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show Stats.dat keys instead of the in-game text (this panel only)");

        // Category chips
        DrawCategoryChips(state, th, now);

        // Table height: the rest of the window minus the detail card
        var detailH = state.SelectedStatKey != null ? DetailHeight(state.SelectedStatKey) + ImGui.GetStyle().ItemSpacing.Y : 0;
        var tableH = Math.Max(f * 5.5f, ImGui.GetContentRegionAvail().Y - detailH);

        var padY = 3f;
        var rowH = f + padY * 2;
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4, padY));
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, U(th.Border, 0.7f));
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, U(th.Card));
        var flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.NoPadOuterX;
        if (ImGui.BeginTable("##stats", 4, flags, new Vector2(0, tableH)))
        {
            ImGui.TableSetupColumn("##cat", ImGuiTableColumnFlags.WidthFixed, f * 2.4f);
            ImGui.TableSetupColumn("Stat", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, f * 4.8f);
            ImGui.TableSetupColumn("Delta", ImGuiTableColumnFlags.WidthFixed, f * 3.4f);
            ImGui.TableSetupScrollFreeze(0, 1);

            // Header row with shared sorting
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers, rowH);
            ImGui.TableSetColumnIndex(0);
            SortHeader(state, th, "cat", "category", now, rowH, false);
            ImGui.TableSetColumnIndex(1);
            SortHeader(state, th, showKeys ? "Key" : "Stat", "key", now, rowH, false);
            ImGui.TableSetColumnIndex(2);
            SortHeader(state, th, "Value", "value", now, rowH, true);
            ImGui.TableSetColumnIndex(3);
            var hp = ImGui.GetCursorScreenPos();
            dl = ImGui.GetWindowDrawList();
            dl.AddText(ImGui.GetFont(), f * 0.85f, new Vector2(hp.X + 2, hp.Y + (f - f * 0.85f) * 0.5f), U(th.TextDim), "Delta");
            ImGui.Dummy(new Vector2(1, f));

            var rows = _panel.Rows;
            var count = rows.Count;
            if (count == 0)
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowH);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextColored(th.TextDim, GameController.InGame
                    ? (state.Filter.Length > 0 || state.Category != "all" ? "No stats match the filter." : "No stats yet.")
                    : "Not in game.");
            }
            else
            {
                var selIdx = state.SelectedStatKey == null ? -1 : rows.FindIndex(s => s.Key == state.SelectedStatKey);
                var viewH = tableH - rowH;
                if (_panel.ScrollToSelected)
                {
                    _panel.ScrollToSelected = false;
                    if (selIdx >= 0) ImGui.SetScrollY(Math.Max(0, selIdx * rowH - (viewH - rowH) * 0.5f));
                }
                var scroll = ImGui.GetScrollY();
                var first = Math.Clamp((int)Math.Floor(scroll / rowH) - 1, 0, count);
                var last = Math.Clamp((int)Math.Ceiling((scroll + viewH) / rowH) + 1, first, count);

                if (first > 0) ImGui.TableNextRow(ImGuiTableRowFlags.None, first * rowH);
                for (var i = first; i < last; i++) DrawStatRow(state, th, rows[i], i, rowH, showKeys, now);
                if (last < count) ImGui.TableNextRow(ImGuiTableRowFlags.None, (count - last) * rowH);
            }
            ImGui.EndTable();
        }
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
    }

    private void DrawCategoryChips(StatsUiState state, PanelTheme th, double now)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var chipH = small + 8;
        const float gap = 4f, pad = 7f;
        var x = p.X; var y = p.Y;
        var total = 0;
        foreach (var c in _panel.CategoryCounts) total += c;

        for (var i = -1; i < StatCategories.All.Length; i++)
        {
            var cat = i < 0 ? "all" : StatCategories.All[i];
            var count = i < 0 ? total : _panel.CategoryCounts[i];
            if (i >= 0 && count == 0 && state.Category != cat) continue;
            var label = i < 0 ? "All" : CatLabel(cat);
            var countText = T("", count);
            var lw = ImGui.CalcTextSize(label).X * (small / f);
            var cw2 = ImGui.CalcTextSize(countText).X * (small / f);
            var cw = pad + lw + 4 + cw2 + pad;
            if (x > p.X && x + cw > p.X + w) { x = p.X; y += chipH + gap; }

            var active = state.Category == cat;
            var min = new Vector2(x, y);
            var mx = new Vector2(x + cw, y + chipH);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton("##cat_" + cat, mx - min);
            var hovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked() && !active) PanelApply(SetStatsFilter(null, cat), now);

            dl.AddRectFilled(min, mx, active ? U(th.Header) : U(hovered ? th.Tile : th.Card), chipH * 0.5f);
            if (i >= 0)
            {
                dl.AddCircleFilled(new Vector2(x + pad, y + chipH * 0.5f), 2.5f, U(CategoryHue(cat)));
                dl.AddText(ImGui.GetFont(), small, new Vector2(x + pad + 6, y + 4), U(active ? th.Text : th.TextDim), label);
                dl.AddText(ImGui.GetFont(), small, new Vector2(x + pad + 6 + lw + 4, y + 4), U(th.TextDim, active ? 0.9f : 0.6f), countText);
                cw += 6;
                mx.X += 6;
                dl.AddRectFilled(new Vector2(mx.X - 6, y), mx, active ? U(th.Header) : U(hovered ? th.Tile : th.Card), chipH * 0.5f, ImDrawFlags.RoundCornersRight);
            }
            else
            {
                dl.AddText(ImGui.GetFont(), small, new Vector2(x + pad, y + 4), U(active ? th.Text : th.TextDim), label);
                dl.AddText(ImGui.GetFont(), small, new Vector2(x + pad + lw + 4, y + 4), U(th.TextDim, active ? 0.9f : 0.6f), countText);
            }
            x += cw + gap;
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(w, y + chipH - p.Y));
    }

    private void SortHeader(StatsUiState state, PanelTheme th, string label, string by, double now, float rowH, bool right)
    {
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var active = state.SortBy == by;

        ImGui.InvisibleButton("##sort_" + by, new Vector2(Math.Max(w, 8), f));
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked())
            PanelApply(SetStatsView(by, active ? !state.SortDesc : by == "value", null), now);
        if (hovered) ImGui.SetTooltip(by == "category" ? "Sort by category (shared with the Claude app)" : $"Sort by {by} (shared with the Claude app)");

        var col = U(active || hovered ? th.Text : th.TextDim);
        var tw = ImGui.CalcTextSize(label).X * (small / f);
        var arrowW = active ? 10f : 0f;
        var tx = right ? p.X + w - tw - arrowW : p.X + 2;
        var ty = p.Y + (f - small) * 0.5f;
        if (by == "category")
        {
            // three stacked dots stand for "category"
            for (var k = 0; k < 3; k++)
                dl.AddCircleFilled(new Vector2(p.X + 6 + k * 5, p.Y + f * 0.5f), 1.8f, col);
            tx = p.X + 20;
        }
        else
        {
            dl.AddText(ImGui.GetFont(), small, new Vector2(tx, ty), col, label);
        }
        if (active)
        {
            var ax = tx + (by == "category" ? -2 : tw + 3);
            var ay = p.Y + f * 0.5f;
            if (state.SortDesc)
                dl.AddTriangleFilled(new Vector2(ax, ay - 2), new Vector2(ax + 6, ay - 2), new Vector2(ax + 3, ay + 2), U(ToneAccent));
            else
                dl.AddTriangleFilled(new Vector2(ax, ay + 2), new Vector2(ax + 6, ay + 2), new Vector2(ax + 3, ay - 2), U(ToneAccent));
        }
    }

    private void DrawStatRow(StatsUiState state, PanelTheme th, StatDto s, int i, float rowH, bool showKeys, double now)
    {
        var f = ImGui.GetFontSize();
        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowH);
        var selected = s.Key == state.SelectedStatKey;
        var pinned = state.PinnedStatKeys.Contains(s.Key);
        var hasChange = _panel.Changes.TryGetValue(s.Key, out var ch) && now - ch.at < DeltaVisibleSec;

        // Row background: zebra, selection (pulsing when selected elsewhere), or a fading change flash
        uint bg = 0;
        if (selected)
        {
            var glow = now < _panel.HighlightUntil ? (float)(0.5 + 0.5 * Math.Sin((now - _panel.HighlightUntil) * 8)) : 0f;
            bg = U(ToneAccent, 0.16f + glow * 0.18f);
        }
        else if (hasChange && now - ch.at < FlashSec)
        {
            var t = (float)((now - ch.at) / FlashSec);
            bg = U(ch.delta > 0 ? ToneOk : ToneBad, 0.28f * (1 - t));
        }
        else if ((i & 1) == 1) bg = U(th.Text, 0.03f);
        if (bg != 0) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, bg);

        ImGui.TableSetColumnIndex(0);
        var cellMin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, U(th.Text, 0.06f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, U(th.Text, 0.10f));
        ImGui.PushStyleColor(ImGuiCol.Header, 0);
        if (ImGui.Selectable("##row" + i, selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap, new Vector2(0, f)))
            PanelApply(SelectStat(selected ? null : s.Key), now);
        ImGui.PopStyleColor(3);
        var rowHovered = ImGui.IsItemHovered();
        if (ImGui.BeginPopupContextItem("##ctx" + i))
        {
            if (ImGui.MenuItem(pinned ? "Unpin" : "Pin")) PanelApply(SetStatPinned(s.Key, !pinned), now);
            if (ImGui.MenuItem("Copy key")) { ImGui.SetClipboardText(s.Key); _panel.CopiedAt = now; }
            if (ImGui.MenuItem(selected ? "Deselect" : "Select")) PanelApply(SelectStat(selected ? null : s.Key), now);
            ImGui.EndPopup();
        }

        var cy = cellMin.Y + f * 0.5f;
        dl.AddCircleFilled(new Vector2(cellMin.X + 6, cy), 3f, U(CategoryHue(s.Category)));

        var pinMin = new Vector2(cellMin.X + 13, cy - 7);
        ImGui.SetCursorScreenPos(pinMin);
        ImGui.InvisibleButton("##pin" + i, new Vector2(14, 14));
        var pinHovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked()) PanelApply(SetStatPinned(s.Key, !pinned), now);
        if (pinned || rowHovered || pinHovered)
            DrawPinGlyph(dl, new Vector2(pinMin.X + 7, pinMin.Y + 7), pinned ? U(ToneWarn) : U(th.TextDim, pinHovered ? 1f : 0.55f));
        if (pinHovered) ImGui.SetTooltip(pinned ? "Unpin" : "Pin (shared with the Claude app)");
        else if (rowHovered)
            ImGui.SetTooltip($"{s.Key}\nid {s.Id}  -  {s.Category}{(s.IsLocal == true ? "  -  local" : "")}{(s.Text != null ? "\n" + s.Text : "")}\nClick to select, right-click for actions");

        ImGui.TableSetColumnIndex(1);
        var label = showKeys ? s.Key : (s.Text ?? Humanize(s.Key));
        if (!showKeys && s.Text == null) ImGui.TextColored(th.TextDim with { W = 1f }, label);
        else ImGui.TextUnformatted(label);

        ImGui.TableSetColumnIndex(2);
        var vt = FmtStat(s.Key, s.Value);
        var vw = ImGui.CalcTextSize(vt).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - vw));
        if (s.Value < 0) ImGui.TextColored(ToneBad, vt);
        else ImGui.TextUnformatted(vt);

        ImGui.TableSetColumnIndex(3);
        if (hasChange)
        {
            var age = now - ch.at;
            var alpha = age < DeltaVisibleSec - 10 ? 1f : (float)Math.Clamp((DeltaVisibleSec - age) / 10, 0.15, 1);
            ImGui.TextColored((ch.delta > 0 ? ToneOk : ToneBad) with { W = alpha }, Signed(ch.delta));
        }
    }

    // ── Detail ───────────────────────────────────────────────────────

    private float DetailHeight(string key)
    {
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var isResist = ResistElementIndex(key) >= 0;
        var present = _panel.ByKey.ContainsKey(key);
        // meta line (with the pin pill) + title + value/key line + optional absent note + optional layer chips + padding
        return 6 + (small + 6) + 4 + (f + 4) + (f * 1.5f + 4) + (present ? 0 : small + 4) + (isResist ? small + 6 + 4 : 0) + 6;
    }

    private static int ResistElementIndex(string key)
    {
        for (var i = 0; i < Elements.Length; i++) if (Elements[i].Owns(key)) return i;
        return -1;
    }

    /// <summary>
    /// Everything about the selected stat in a compact card: category and Stats.dat record details,
    /// in-game text, value with its recent delta, the key (click to copy), resistance layers.
    /// </summary>
    private void DrawDetail(StatsUiState state, PanelTheme th, double now)
    {
        var key = state.SelectedStatKey!;
        if (_panel.DetailKey != key || !ReferenceEquals(_panel.DetailSnap, _panel.SnapRef))
        {
            _panel.DetailKey = key;
            _panel.DetailSnap = _panel.SnapRef;
            _panel.Detail = BuildStatDetail(key);
        }
        var detail = _panel.Detail;
        _panel.ByKey.TryGetValue(key, out var s);

        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var h = DetailHeight(key);
        var min = p; var mx = new Vector2(p.X + w, p.Y + h);
        var remote = now - _panel.RemoteAt < 4 && _panel.RemoteWhat.Contains("selection");
        var font = ImGui.GetFont();

        dl.AddRectFilled(min, mx, U(th.Card), 5f);
        dl.AddRect(min, mx, U(ToneAccent, remote ? 0.6f : 0.3f), 5f);

        var x = p.X + 10; var y = p.Y + 6;
        var lineH = small + 6;
        var category = s?.Category ?? detail?.Stat?.Category ?? StatCategories.For(key);

        // Right side of the meta line: pin pill, then the close glyph
        var pinned = state.PinnedStatKeys.Contains(key);
        var cx = mx.X - 16; var cy = y + lineH * 0.5f;
        ImGui.SetCursorScreenPos(new Vector2(cx - 8, cy - 8));
        ImGui.InvisibleButton("##detclose", new Vector2(16, 16));
        var closeHov = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked()) PanelApply(SelectStat(null), now);
        if (closeHov) ImGui.SetTooltip("Clear selection");
        DrawCloseGlyph(dl, new Vector2(cx, cy), 3.5f, U(closeHov ? th.Text : th.TextDim));

        var pinLabel = pinned ? "Pinned" : "Pin";
        var pillW = ImGui.CalcTextSize(pinLabel).X * (small / f) + 26;
        var pillMin = new Vector2(cx - 14 - pillW, y);
        var pillMax = new Vector2(cx - 14, y + lineH);
        ImGui.SetCursorScreenPos(pillMin);
        ImGui.InvisibleButton("##detpin", pillMax - pillMin);
        var pinHov = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked()) PanelApply(SetStatPinned(key, !pinned), now);
        if (pinHov) ImGui.SetTooltip(pinned ? "Unpin (shared with the Claude app)" : "Pin (shared with the Claude app)");
        dl.AddRectFilled(pillMin, pillMax, pinned ? U(ToneWarn, pinHov ? 0.3f : 0.18f) : U(th.Tile, pinHov ? 1f : 0.8f), lineH * 0.5f);
        if (!pinned) dl.AddRect(pillMin, pillMax, U(th.Border, 0.7f), lineH * 0.5f);
        DrawPinGlyph(dl, new Vector2(pillMin.X + 10, cy), pinned ? U(ToneWarn) : U(th.TextDim));
        dl.AddText(font, small, new Vector2(pillMin.X + 18, y + 3), U(pinned ? ToneWarn : th.Text), pinLabel);

        // Meta line (left): category, who selected it, record details
        var ty = y + 3;
        dl.AddCircleFilled(new Vector2(x + 3, cy), 3f, U(CategoryHue(category)));
        var metaX = x + 11;
        var catLabel = category.ToUpperInvariant();
        dl.AddText(font, small, new Vector2(metaX, ty), U(th.TextDim), catLabel);
        metaX += ImGui.CalcTextSize(catLabel).X * (small / f) + 8;
        dl.PushClipRect(min, new Vector2(pillMin.X - 6, mx.Y), true);
        if (remote)
        {
            var tag = "selected by Claude";
            dl.AddText(font, small, new Vector2(metaX, ty), U(ToneAccent), tag);
            metaX += ImGui.CalcTextSize(tag).X * (small / f) + 8;
        }
        var meta = "";
        if (detail?.RecordType != null) meta += detail.RecordType + " record";
        if (detail?.IsWeaponLocal != null) meta += (meta.Length > 0 ? "  -  " : "") + (detail.IsWeaponLocal == true ? "weapon-local" : "global");
        if (s != null) meta += (meta.Length > 0 ? "  -  " : "") + "id " + s.Id;
        if (detail?.Error != null) meta = detail.Error;
        dl.AddText(font, small, new Vector2(metaX, ty), U(th.TextDim, 0.8f), meta);
        dl.PopClipRect();
        y += lineH + 4;

        // Title: the in-game text, or the humanised key
        dl.PushClipRect(min, new Vector2(mx.X - 10, mx.Y), true);
        dl.AddText(new Vector2(x, y), U(th.Text), s?.Text ?? Humanize(key));
        dl.PopClipRect();
        y += f + 4;

        // Value, recent delta and the key (click to copy) on one line, baseline-aligned to the big value
        var big = f * 1.5f;
        var valText = s != null ? FmtStat(key, s.Value) : "-";
        dl.AddText(font, big, new Vector2(x, y), U(s == null ? th.TextDim : s.Value < 0 ? ToneBad : th.Text), valText);
        var vx = x + ImGui.CalcTextSize(valText).X * (big / f) + 10;
        var baseY = y + big - f - 1;
        if (_panel.Changes.TryGetValue(key, out var ch) && now - ch.at < DeltaVisibleSec)
        {
            var dt = Signed(ch.delta);
            dl.AddText(new Vector2(vx, baseY), U(ch.delta > 0 ? ToneOk : ToneBad), dt);
            vx += ImGui.CalcTextSize(dt).X + 5;
            var agoText = Ago(now - ch.at);
            dl.AddText(font, small, new Vector2(vx, y + big - small - 1), U(th.TextDim), agoText);
            vx += ImGui.CalcTextSize(agoText).X * (small / f) + 10;
        }
        var copied = now - _panel.CopiedAt < 1.5;
        var kw = ImGui.CalcTextSize(key).X;
        var keyRight = mx.X - 10;
        ImGui.SetCursorScreenPos(new Vector2(vx, baseY - 1));
        ImGui.InvisibleButton("##keycopy", new Vector2(Math.Max(10, Math.Min(keyRight - vx, kw + f + 8)), f + 2));
        var keyHov = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked()) { ImGui.SetClipboardText(key); _panel.CopiedAt = now; }
        if (keyHov) ImGui.SetTooltip(key + "\nClick to copy the Stats.dat key");
        dl.PushClipRect(new Vector2(vx, min.Y), new Vector2(keyRight, mx.Y), true);
        dl.AddText(new Vector2(vx, baseY), U(keyHov ? th.Text : th.TextDim), key);
        if (vx + kw + f + 4 <= keyRight)
        {
            DrawCopyGlyph(dl, new Vector2(vx + kw + 6, baseY + 2), f - 4, U(copied ? ToneOk : keyHov ? th.Text : th.TextDim, 0.9f));
            if (copied) dl.AddText(font, small, new Vector2(vx + kw + 6 + f, baseY + (f - small) * 0.5f), U(ToneOk), "copied");
        }
        dl.PopClipRect();
        y += big + 4;

        if (s == null)
        {
            dl.AddText(font, small, new Vector2(x, y), U(th.TextDim), "Not on the character right now; it counts as 0 until something grants it.");
            y += small + 4;
        }

        // Resistance layers as one row of chips (base, total, uncapped, cap); click one to select that layer
        var ei = ResistElementIndex(key);
        if (ei >= 0)
        {
            ref readonly var e = ref Elements[ei];
            var r = ResistOf(e);
            var by = _panel.ByKey;
            var layers = new (string label, string key, int? value, bool assumed)[4];
            layers[0] = ("Base", e.BaseKey, by.TryGetValue(e.BaseKey, out var b) ? b.Value : null, false);
            layers[1] = ("Total", e.Key, by.TryGetValue(e.Key, out var t) ? t.Value : null, false);
            layers[2] = ("Uncapped", e.UncappedKey, by.TryGetValue(e.UncappedKey, out var u) ? u.Value : null, false);
            layers[3] = ("Cap", e.MaxKey, r.Cap, r.CapAssumed);
            var chipH = small + 6;
            var lx = x;
            for (var k = 0; k < 4; k++)
            {
                var (label, lkey, lv, assumed) = layers[k];
                var valueText = lv == null ? "-" : T("", lv.Value, assumed ? "% assumed" : "%");
                var lw = ImGui.CalcTextSize(label).X * (small / f);
                var vw = ImGui.CalcTextSize(valueText).X * (small / f);
                var cw = 7 + lw + 4 + vw + 7;
                var cmin = new Vector2(lx, y); var cmax = new Vector2(lx + cw, y + chipH);
                var isThis = lkey == key || (k == 3 && key == e.MaxKey2);
                ImGui.SetCursorScreenPos(cmin);
                ImGui.InvisibleButton("##layer" + k, cmax - cmin);
                var hov = ImGui.IsItemHovered();
                if (hov)
                    ImGui.SetTooltip(assumed ? "No maximum_*_resistance stat exposed; the default 75% cap is assumed"
                        : lv == null ? lkey + "\nnot on the character" : lkey + (isThis ? "" : "\nClick to select"));
                if (ImGui.IsItemClicked() && lv != null && !assumed && !isThis) PanelApply(SelectStat(lkey), now);
                dl.AddRectFilled(cmin, cmax, isThis ? U(e.Hue, 0.2f) : U(th.Tile, hov ? 1f : 0.8f), chipH * 0.5f);
                if (isThis) dl.AddRect(cmin, cmax, U(e.Hue, 0.8f), chipH * 0.5f);
                dl.AddText(font, small, new Vector2(lx + 7, y + 3), U(th.TextDim), label);
                dl.AddText(font, small, new Vector2(lx + 7 + lw + 4, y + 3), U(lv == null || assumed ? th.TextDim : th.Text), valueText);
                lx += cw + 4;
            }
            y += chipH + 4;
        }

        ImGui.SetCursorScreenPos(new Vector2(p.X, mx.Y));
        ImGui.Dummy(new Vector2(w, 0));
    }

    // ── Formatting ───────────────────────────────────────────────────

    // Panel text is rebuilt every frame from values that rarely change: cache the strings (bounded) so drawing the
    // panel doesn't allocate (it was ~9 KB per frame; garbage becomes GC pauses at high fps).
    private static readonly Dictionary<int, string> NCache = new();
    private static readonly Dictionary<(string, int, string), string> TextCache = new();
    private static readonly Dictionary<(int, int, int), string> VitalsCache = new();
    private static readonly Dictionary<string, string> CatLabelCache = new();

    private static string N(int v)
    {
        if (NCache.TryGetValue(v, out var s)) return s;
        if (NCache.Count > 4096) NCache.Clear();
        return NCache[v] = v.ToString("#,0", CultureInfo.InvariantCulture);
    }

    private static string T(string pre, int v, string suf = "")
    {
        if (TextCache.TryGetValue((pre, v, suf), out var s)) return s;
        if (TextCache.Count > 8192) TextCache.Clear();
        return TextCache[(pre, v, suf)] = pre + v.ToString(CultureInfo.InvariantCulture) + suf;
    }

    private static string CatLabel(string cat)
    {
        if (CatLabelCache.TryGetValue(cat, out var s)) return s;
        return CatLabelCache[cat] = char.ToUpperInvariant(cat[0]) + cat[1..];
    }

    private static bool IsPercentKey(string key) => key.EndsWith('%') || key.Contains("_%_");

    private static string FmtStat(string key, int value) => IsPercentKey(key) ? N(value) + "%" : N(value);

    private static string Signed(int v) => v > 0 ? "+" + N(v) : N(v);

    private static string Ago(double sec) => sec < 2 ? "just now" : sec < 60 ? $"{(int)Math.Round(sec)} s ago" : $"{(int)Math.Round(sec / 60)} min ago";

    /// <summary>"base_fire_damage_resistance_%" -> "Base fire damage resistance" (cached; the unit moves to the value).</summary>
    private string Humanize(string key)
    {
        if (_panel.Human.TryGetValue(key, out var h)) return h;
        var s = key.EndsWith("_%", StringComparison.Ordinal) ? key[..^2] : key;
        s = s.Replace('_', ' ').Trim();
        if (s.Length > 0) s = char.ToUpperInvariant(s[0]) + s[1..];
        if (_panel.Human.Count > 4000) _panel.Human.Clear();
        _panel.Human[key] = s;
        return s;
    }

    private static Vector4 CategoryHue(string category) => category switch
    {
        StatCategories.Vitals => HueLife,
        StatCategories.Resistances => HueFire,
        StatCategories.Defense => HueEs,
        StatCategories.Offense => HueLightning,
        StatCategories.Charges => HueChaos,
        StatCategories.Movement => HueCold,
        _ => ToneNeutral,
    };

    // ── Drawing primitives ───────────────────────────────────────────

    private static Vector4 Hex(uint rgb, float a = 1f) =>
        new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);

    private static uint U(Vector4 c) => ImGui.GetColorU32(c);

    private static uint U(Vector4 c, float alpha) => ImGui.GetColorU32(new Vector4(c.X, c.Y, c.Z, alpha));

    private static float Luma(Vector4 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    private static Vector4 Shift(Vector4 c, float d) =>
        new(Math.Clamp(c.X + d, 0f, 1f), Math.Clamp(c.Y + d, 0f, 1f), Math.Clamp(c.Z + d, 0f, 1f), c.W);

    /// <summary>
    /// Card fill a step off the window colour, pushed past the control background so inputs on the
    /// card still read as recessed (same idea as ExileMaps' SurfaceFill).
    /// </summary>
    private static Vector4 SurfaceFill(Vector4 window, Vector4 frame, float step = 0.055f, float margin = 0.05f)
    {
        var dir = Luma(window) > 0.5f ? -1f : 1f;
        var c = Shift(window, dir * step);
        var gap = (Luma(c) - Luma(frame)) * dir;
        var fill = gap >= margin ? c : Shift(c, dir * (margin - gap));
        fill.W = Math.Max(fill.W, 0.9f);
        return fill;
    }

    /// <summary>A push-pin: round head, short needle. 12 px tall around <paramref name="c"/>.</summary>
    private static void DrawPinGlyph(ImDrawListPtr dl, Vector2 c, uint col)
    {
        dl.AddCircleFilled(new Vector2(c.X, c.Y - 2.5f), 3.2f, col);
        dl.AddRectFilled(new Vector2(c.X - 3.5f, c.Y - 0.5f), new Vector2(c.X + 3.5f, c.Y + 1f), col);
        dl.AddLine(new Vector2(c.X, c.Y + 1f), new Vector2(c.X, c.Y + 5.5f), col, 1.2f);
    }

    private static void DrawCloseGlyph(ImDrawListPtr dl, Vector2 c, float r, uint col)
    {
        dl.AddLine(new Vector2(c.X - r, c.Y - r), new Vector2(c.X + r, c.Y + r), col, 1.3f);
        dl.AddLine(new Vector2(c.X - r, c.Y + r), new Vector2(c.X + r, c.Y - r), col, 1.3f);
    }

    /// <summary>Two offset rounded rectangles: the usual "copy" icon.</summary>
    private static void DrawCopyGlyph(ImDrawListPtr dl, Vector2 p, float size, uint col)
    {
        var s = size * 0.7f;
        dl.AddRect(new Vector2(p.X + size - s, p.Y + size - s), new Vector2(p.X + size, p.Y + size), col, 1.5f);
        dl.AddRect(p, new Vector2(p.X + s, p.Y + s), col, 1.5f);
    }
}
