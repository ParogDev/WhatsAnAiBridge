using System.Collections.Generic;
using Newtonsoft.Json;

namespace WhatsAnAiBridge;

/// <summary>
/// Player-stats view state shared by every surface: the in-HUD panel, the MCP App and agents.
/// The HUD plugin is the source of truth; it persists this with its settings. Every change
/// increments <see cref="Rev"/>, so clients can poll cheaply ("anything newer than rev N?").
/// Stats are identified by their Stats.dat key (e.g. "fire_damage_resistance_%"), never by the
/// GameStat enum, whose numbering differs between PoE1/PoE2 and shifts between patches.
/// </summary>
public class StatsUiState
{
    [JsonProperty("rev")]
    public long Rev { get; set; }

    [JsonProperty("pinnedStatKeys")]
    public List<string> PinnedStatKeys { get; set; } = new();

    [JsonProperty("filter")]
    public string Filter { get; set; } = "";

    /// <summary>"all" or one of <see cref="StatCategories.All"/>.</summary>
    [JsonProperty("category")]
    public string Category { get; set; } = "all";

    [JsonProperty("selectedStatKey")]
    public string? SelectedStatKey { get; set; }

    /// <summary>"key" | "value" | "category".</summary>
    [JsonProperty("sortBy")]
    public string SortBy { get; set; } = "category";

    [JsonProperty("sortDesc")]
    public bool SortDesc { get; set; }

    [JsonProperty("panelOpen")]
    public bool PanelOpen { get; set; } = true;

    public StatsUiState Clone() => new()
    {
        Rev = Rev,
        PinnedStatKeys = new List<string>(PinnedStatKeys),
        Filter = Filter,
        Category = Category,
        SelectedStatKey = SelectedStatKey,
        SortBy = SortBy,
        SortDesc = SortDesc,
        PanelOpen = PanelOpen,
    };
}

public class StatDto
{
    /// <summary>Numeric stat id for this game build (GameStat value). Not stable across patches.</summary>
    [JsonProperty("id")]
    public int Id { get; set; }

    /// <summary>Stats.dat key - the stable identifier.</summary>
    [JsonProperty("key")]
    public string Key { get; set; } = "";

    [JsonProperty("value")]
    public int Value { get; set; }

    /// <summary>In-game translated text for this stat/value, when the game has a description for it.</summary>
    [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)]
    public string? Text { get; set; }

    [JsonProperty("category")]
    public string Category { get; set; } = "";

    [JsonProperty("isLocal", NullValueHandling = NullValueHandling.Ignore)]
    public bool? IsLocal { get; set; }

    [JsonProperty("pinned", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Pinned { get; set; }
}

public class VitalsDto
{
    [JsonProperty("hp")] public int Hp { get; set; }
    [JsonProperty("maxHp")] public int MaxHp { get; set; }
    [JsonProperty("es")] public int Es { get; set; }
    [JsonProperty("maxEs")] public int MaxEs { get; set; }
    [JsonProperty("mana")] public int Mana { get; set; }
    [JsonProperty("maxMana")] public int MaxMana { get; set; }

    /// <summary>PoE2 weapon set (0/1); omitted on PoE1.</summary>
    [JsonProperty("weaponSet", NullValueHandling = NullValueHandling.Ignore)]
    public int? WeaponSet { get; set; }
}

public class StatsUiStateResponse
{
    [JsonProperty("game")] public string Game { get; set; } = "";
    [JsonProperty("rev")] public long Rev { get; set; }

    /// <summary>True when the caller's sinceRev is current; <see cref="State"/> is then omitted.</summary>
    [JsonProperty("unchanged", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Unchanged { get; set; }

    [JsonProperty("state", NullValueHandling = NullValueHandling.Ignore)]
    public StatsUiState? State { get; set; }

    /// <summary>Always included (changes every frame, cheap).</summary>
    [JsonProperty("vitals", NullValueHandling = NullValueHandling.Ignore)]
    public VitalsDto? Vitals { get; set; }

    [JsonProperty("inGame")] public bool InGame { get; set; }
}

public class StatsPageResponse
{
    [JsonProperty("game")] public string Game { get; set; } = "";
    [JsonProperty("rev")] public long Rev { get; set; }
    [JsonProperty("total")] public int Total { get; set; }
    [JsonProperty("page")] public int Page { get; set; }
    [JsonProperty("pageSize")] public int PageSize { get; set; }
    [JsonProperty("items")] public List<StatDto> Items { get; set; } = new();

    /// <summary>Count per category over the player's full stat set (ignoring filter/paging).</summary>
    [JsonProperty("categories")] public Dictionary<string, int> Categories { get; set; } = new();

    [JsonProperty("pinned")] public List<StatDto> Pinned { get; set; } = new();
}

public class StatsMutationResponse
{
    [JsonProperty("ok")] public bool Ok { get; set; }
    [JsonProperty("rev")] public long Rev { get; set; }
    [JsonProperty("state")] public StatsUiState State { get; set; } = new();

    /// <summary>"rev_mismatch" | "unknown_stat" | "invalid_argument" | "too_many_pins".</summary>
    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public string? Error { get; set; }

    [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)]
    public string? Message { get; set; }
}

public class StatDetailResponse
{
    [JsonProperty("game")] public string Game { get; set; } = "";
    [JsonProperty("stat", NullValueHandling = NullValueHandling.Ignore)] public StatDto? Stat { get; set; }

    /// <summary>Stats.dat record type (e.g. IntValue) and flags, even when the player lacks the stat.</summary>
    [JsonProperty("recordType", NullValueHandling = NullValueHandling.Ignore)] public string? RecordType { get; set; }
    [JsonProperty("isWeaponLocal", NullValueHandling = NullValueHandling.Ignore)] public bool? IsWeaponLocal { get; set; }

    /// <summary>False when the player currently has no value for this stat (value is then 0).</summary>
    [JsonProperty("present")] public bool Present { get; set; }

    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public string? Error { get; set; }
}

public static class StatCategories
{
    public const string Vitals = "vitals";
    public const string Resistances = "resistances";
    public const string Defense = "defense";
    public const string Offense = "offense";
    public const string Charges = "charges";
    public const string Movement = "movement";
    public const string Other = "other";

    public static readonly string[] All = [Vitals, Resistances, Defense, Offense, Charges, Movement, Other];

    /// <summary>Heuristic category from the Stats.dat key. Order matters: first match wins.</summary>
    public static string For(string key)
    {
        if (key.Contains("resist")) return Resistances;
        if (key.Contains("charge")) return Charges;
        if (key.Contains("movement_velocity") || key.Contains("movement_speed")) return Movement;
        if (key.Contains("maximum_life") || key.Contains("maximum_mana") || key.Contains("maximum_energy_shield")
            || key.Contains("life_regeneration") || key.Contains("mana_regeneration") || key.Contains("spirit")
            || key.StartsWith("life_") || key.StartsWith("mana_") || key == "level")
            return Vitals;
        if (key.Contains("armour") || key.Contains("evasion") || key.Contains("block") || key.Contains("energy_shield")
            || key.Contains("deflect") || key.Contains("ward") || key.Contains("damage_taken") || key.Contains("suppress"))
            return Defense;
        if (key.Contains("damage") || key.Contains("critical") || key.Contains("attack_speed") || key.Contains("cast_speed")
            || key.Contains("accuracy") || key.Contains("projectile") || key.Contains("area_of_effect") || key.Contains("penetrat"))
            return Offense;
        return Other;
    }
}
