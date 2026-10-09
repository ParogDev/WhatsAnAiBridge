using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Player stats + the shared stats-view state (pins, filter, selection, sort).
/// All bridge methods here run on the main thread (Tick), as does the ImGui panel, so no locking.
/// Same API on PoE1 and PoE2: Stats component, Files.Stats.recordsById, Files.StatDescriptions.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int MaxPinnedStats = 64;
    private const int MaxPageSize = 200;
    private static readonly TimeSpan StatsSnapshotTtl = TimeSpan.FromMilliseconds(250);

    private List<StatDto>? _statsSnapshot;
    private DateTime _statsSnapshotAt = DateTime.MinValue;
    private readonly Dictionary<(int id, int value), string?> _statTextCache = new();
    private readonly Dictionary<int, StatDto> _statDtoById = new();

    private StatsUiState StatsUi => Settings.StatsUi ??= new StatsUiState();

    // ── Snapshot ─────────────────────────────────────────────────────

    /// <summary>Player's current stats, cached for <see cref="StatsSnapshotTtl"/> so polling is cheap.</summary>
    private List<StatDto> GetStatsSnapshot()
    {
        var now = DateTime.UtcNow;
        if (_statsSnapshot != null && now - _statsSnapshotAt < StatsSnapshotTtl)
            return _statsSnapshot;

        var statsComp = GameController.Player?.GetComponent<Stats>();
        _statsSnapshotAt = now;
        // The dictionary costs ~60-150 us per read: skip it while the raw stat bytes are unchanged (StatsRaw.cs).
        if (_statsSnapshot != null && StatsUnchangedRaw(statsComp?.Address ?? 0, now)) return _statsSnapshot;
        var stats = statsComp?.StatDictionary;
        StatsRawAfterRead(statsComp?.Address ?? 0, stats, now);
        // Unchanged stats keep the same list: consumers (the panel's row index and sort) skip work on the same reference,
        // and nothing is allocated while the character stands still.
        if (_statsSnapshot != null && SameStats(_statsSnapshot, stats)) return _statsSnapshot;

        var result = new List<StatDto>(stats?.Count ?? 0);
        if (stats != null)
        {
            var records = GameController.Files.Stats?.recordsById;
            foreach (var kv in stats)
            {
                var id = (int)kv.Key;
                if (_statDtoById.TryGetValue(id, out var known) && known.Value == kv.Value) { result.Add(known); continue; }
                var record = records != null && records.TryGetValue(id, out var r) ? r : null;
                var key = record?.Key ?? kv.Key.ToString();
                var dto = new StatDto
                {
                    Id = id,
                    Key = key,
                    Value = kv.Value,
                    Text = TranslateStat(kv.Key, kv.Value),
                    Category = StatCategories.For(key),
                    IsLocal = record?.IsLocal == true ? true : null,
                };
                _statDtoById[id] = dto;
                result.Add(dto);
            }
        }

        _statsSnapshot = result;
        return result;
    }

    private static bool SameStats(List<StatDto> prev, IReadOnlyDictionary<GameStat, int>? stats)
    {
        if (stats == null) return prev.Count == 0;
        if (stats.Count != prev.Count) return false;
        var i = 0;
        foreach (var kv in stats)
        {
            var p = prev[i++];
            if (p.Id != (int)kv.Key || p.Value != kv.Value) return false;
        }
        return true;
    }

    private string? TranslateStat(GameStat stat, int value)
    {
        var cacheKey = ((int)stat, value);
        if (_statTextCache.TryGetValue(cacheKey, out var cached)) return cached;

        string? text = null;
        try
        {
            text = CleanStatText(GameController.Files.StatDescriptions?.TranslateMod(new Dictionary<GameStat, int> { [stat] = value }));
        }
        catch { }

        if (_statTextCache.Count > 20_000) _statTextCache.Clear();
        _statTextCache[cacheKey] = text;
        return text;
    }

    private static readonly System.Text.RegularExpressions.Regex StatLinkMarkup =
        new(@"\[(?:[^\]|]*\|)?([^\]]*)\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Translator output to display text: stats without a description come back as
    /// "&lt;unknown Name:value&gt;" (treated as no text), and PoE2 descriptions embed links as
    /// "[Target|Label]" or "[Label]" (reduced to the label).
    /// </summary>
    private static string? CleanStatText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("<unknown", StringComparison.Ordinal)) return null;
        return StatLinkMarkup.Replace(text, "$1").Trim();
    }

    private VitalsDto? BuildVitals()
    {
        var player = GameController.Player;
        var life = player?.GetComponent<Life>();
        if (life == null) return null;
        return new VitalsDto
        {
            Hp = life.CurHP,
            MaxHp = life.MaxHP,
            Es = life.CurES,
            MaxEs = life.MaxES,
            Mana = life.CurMana,
            MaxMana = life.MaxMana,
            WeaponSet = ActiveWeaponSet(player!),
        };
    }

    // ── Bridge methods ───────────────────────────────────────────────

    /// <summary>Routes "stats.*" JSON-RPC methods. Returns null when the method isn't a stats method.</summary>
    private string? ProcessStatsMethod(string method, JToken? p)
    {
        switch (method)
        {
            case "stats.ui_state":
                return Serialize(BuildStatsUiState(p?["sinceRev"]?.Value<long?>()));

            case "stats.page":
                return Serialize(BuildStatsPage(
                    filter: p?["filter"]?.Value<string>(),
                    category: p?["category"]?.Value<string>(),
                    pinnedOnly: p?["pinnedOnly"]?.Value<bool?>() ?? false,
                    page: p?["page"]?.Value<int?>() ?? 0,
                    pageSize: p?["pageSize"]?.Value<int?>() ?? 50,
                    sortBy: p?["sortBy"]?.Value<string>(),
                    sortDesc: p?["sortDesc"]?.Value<bool?>()));

            case "stats.get":
                return Serialize(BuildStatDetail(p?["key"]?.Value<string>() ?? ""));

            case "stats.set_pinned":
                return Serialize(SetStatPinned(p?["key"]?.Value<string>() ?? "", p?["pinned"]?.Value<bool?>() ?? true,
                    p?["expectedRev"]?.Value<long?>()));

            case "stats.set_filter":
                return Serialize(SetStatsFilter(p?["text"]?.Value<string>(), p?["category"]?.Value<string>(),
                    p?["expectedRev"]?.Value<long?>()));

            case "stats.select":
                return Serialize(SelectStat(p?["key"]?.Value<string>(), p?["expectedRev"]?.Value<long?>()));

            case "stats.set_view":
                return Serialize(SetStatsView(p?["sortBy"]?.Value<string>(), p?["sortDesc"]?.Value<bool?>(),
                    p?["panelOpen"]?.Value<bool?>(), p?["expectedRev"]?.Value<long?>()));

            default:
                return null;
        }
    }

    private StatsUiStateResponse BuildStatsUiState(long? sinceRev)
    {
        var state = StatsUi;
        var unchanged = sinceRev.HasValue && sinceRev.Value == state.Rev;
        return new StatsUiStateResponse
        {
            Game = GameId,
            Rev = state.Rev,
            Unchanged = unchanged ? true : null,
            State = unchanged ? null : state.Clone(),
            Vitals = GameController.InGame ? BuildVitals() : null,
            InGame = GameController.InGame,
        };
    }

    private StatsPageResponse BuildStatsPage(string? filter, string? category, bool pinnedOnly, int page, int pageSize,
        string? sortBy, bool? sortDesc)
    {
        var state = StatsUi;
        var all = GameController.InGame ? GetStatsSnapshot() : new List<StatDto>();
        var pinnedSet = new HashSet<string>(state.PinnedStatKeys, StringComparer.Ordinal);

        // Arguments override the shared state for this read only (they do not mutate it).
        filter ??= state.Filter;
        category = string.IsNullOrEmpty(category) ? state.Category : category;
        sortBy = string.IsNullOrEmpty(sortBy) ? state.SortBy : sortBy;
        var desc = sortDesc ?? state.SortDesc;
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(0, page);

        IEnumerable<StatDto> q = all;
        if (pinnedOnly) q = q.Where(s => pinnedSet.Contains(s.Key));
        if (!string.IsNullOrEmpty(category) && category != "all") q = q.Where(s => s.Category == category);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter.Trim();
            q = q.Where(s => s.Key.Contains(f, StringComparison.OrdinalIgnoreCase)
                             || (s.Text?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        q = (sortBy, desc) switch
        {
            ("value", false) => q.OrderBy(s => s.Value).ThenBy(s => s.Key),
            ("value", true) => q.OrderByDescending(s => s.Value).ThenBy(s => s.Key),
            ("key", false) => q.OrderBy(s => s.Key),
            ("key", true) => q.OrderByDescending(s => s.Key),
            (_, false) => q.OrderBy(s => Array.IndexOf(StatCategories.All, s.Category)).ThenBy(s => s.Key),
            (_, true) => q.OrderByDescending(s => Array.IndexOf(StatCategories.All, s.Category)).ThenBy(s => s.Key),
        };

        var filtered = q.ToList();
        var byKey = all.ToDictionary(s => s.Key, StringComparer.Ordinal);

        return new StatsPageResponse
        {
            Game = GameId,
            Rev = state.Rev,
            Total = filtered.Count,
            Page = page,
            PageSize = pageSize,
            Items = filtered.Skip(page * pageSize).Take(pageSize).Select(s => WithPinned(s, pinnedSet)).ToList(),
            Categories = all.GroupBy(s => s.Category).ToDictionary(g => g.Key, g => g.Count()),
            // Pinned stats always included (even when filtered out / absent), in pin order.
            Pinned = state.PinnedStatKeys
                .Select(k => byKey.TryGetValue(k, out var s) ? WithPinned(s, pinnedSet) : AbsentStat(k))
                .ToList(),
        };
    }

    private static StatDto WithPinned(StatDto s, HashSet<string> pinned) => new()
    {
        Id = s.Id, Key = s.Key, Value = s.Value, Text = s.Text, Category = s.Category, IsLocal = s.IsLocal,
        Pinned = pinned.Contains(s.Key) ? true : null,
    };

    /// <summary>A pinned stat the player currently doesn't have (value 0).</summary>
    private StatDto AbsentStat(string key)
    {
        var record = FindStatRecord(key);
        return new StatDto
        {
            Id = record?.ID ?? 0,
            Key = key,
            Value = 0,
            Category = StatCategories.For(key),
            Pinned = true,
        };
    }

    private StatDetailResponse BuildStatDetail(string key)
    {
        var record = FindStatRecord(key);
        if (record == null)
            return new StatDetailResponse { Game = GameId, Error = $"unknown_stat: '{key}' is not a Stats.dat key in this {GameId} build" };

        var snapshot = GameController.InGame ? GetStatsSnapshot() : new List<StatDto>();
        var current = snapshot.FirstOrDefault(s => s.Key == key);
        var pinned = StatsUi.PinnedStatKeys.Contains(key);
        return new StatDetailResponse
        {
            Game = GameId,
            Present = current != null,
            Stat = current != null
                ? WithPinned(current, pinned ? new HashSet<string> { key } : new HashSet<string>())
                : new StatDto { Id = record.ID, Key = key, Category = StatCategories.For(key), Pinned = pinned ? true : null },
            RecordType = record.Type.ToString(),
            IsWeaponLocal = record.IsWeaponLocal,
        };
    }

    private StatsDat.StatRecord? FindStatRecord(string key)
    {
        try
        {
            var records = GameController.Files.Stats?.records;
            return records != null && records.TryGetValue(key, out var r) ? r : null;
        }
        catch { return null; }
    }

    // ── Mutators (shared by bridge methods and the ImGui panel) ──────

    private StatsMutationResponse Mutate(long? expectedRev, Func<StatsUiState, (bool changed, string? error, string? message)> apply)
    {
        var state = StatsUi;
        if (expectedRev.HasValue && expectedRev.Value != state.Rev)
            return new StatsMutationResponse
            {
                Ok = false, Rev = state.Rev, State = state.Clone(), Error = "rev_mismatch",
                Message = $"expectedRev {expectedRev} but current rev is {state.Rev}; re-read state and retry",
            };

        var (changed, error, message) = apply(state);
        if (error != null)
            return new StatsMutationResponse { Ok = false, Rev = state.Rev, State = state.Clone(), Error = error, Message = message };

        if (changed) state.Rev++;
        return new StatsMutationResponse { Ok = true, Rev = state.Rev, State = state.Clone() };
    }

    internal StatsMutationResponse SetStatPinned(string key, bool pinned, long? expectedRev = null) =>
        Mutate(expectedRev, s =>
        {
            if (string.IsNullOrWhiteSpace(key)) return (false, "invalid_argument", "key is required");
            var has = s.PinnedStatKeys.Contains(key);
            if (pinned == has) return (false, null, null); // idempotent
            if (pinned)
            {
                if (FindStatRecord(key) == null) return (false, "unknown_stat", $"'{key}' is not a Stats.dat key in this {GameId} build");
                if (s.PinnedStatKeys.Count >= MaxPinnedStats) return (false, "too_many_pins", $"at most {MaxPinnedStats} pinned stats");
                s.PinnedStatKeys.Add(key);
            }
            else
            {
                s.PinnedStatKeys.Remove(key);
            }
            return (true, null, null);
        });

    internal StatsMutationResponse SetStatsFilter(string? text, string? category, long? expectedRev = null) =>
        Mutate(expectedRev, s =>
        {
            if (category != null && category != "all" && !StatCategories.All.Contains(category))
                return (false, "invalid_argument", $"category must be 'all' or one of: {string.Join(", ", StatCategories.All)}");
            var changed = false;
            if (text != null && text != s.Filter) { s.Filter = text.Length > 100 ? text[..100] : text; changed = true; }
            if (category != null && category != s.Category) { s.Category = category; changed = true; }
            return (changed, null, null);
        });

    internal StatsMutationResponse SelectStat(string? key, long? expectedRev = null) =>
        Mutate(expectedRev, s =>
        {
            if (string.IsNullOrEmpty(key)) key = null;
            if (key != null && FindStatRecord(key) == null)
                return (false, "unknown_stat", $"'{key}' is not a Stats.dat key in this {GameId} build");
            if (key == s.SelectedStatKey) return (false, null, null);
            s.SelectedStatKey = key;
            return (true, null, null);
        });

    internal StatsMutationResponse SetStatsView(string? sortBy, bool? sortDesc, bool? panelOpen, long? expectedRev = null) =>
        Mutate(expectedRev, s =>
        {
            if (sortBy != null && sortBy is not ("key" or "value" or "category"))
                return (false, "invalid_argument", "sortBy must be key, value or category");
            var changed = false;
            if (sortBy != null && sortBy != s.SortBy) { s.SortBy = sortBy; changed = true; }
            if (sortDesc.HasValue && sortDesc.Value != s.SortDesc) { s.SortDesc = sortDesc.Value; changed = true; }
            if (panelOpen.HasValue && panelOpen.Value != s.PanelOpen) { s.PanelOpen = panelOpen.Value; changed = true; }
            return (changed, null, null);
        });
}
