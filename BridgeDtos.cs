using System.Collections.Generic;
using Newtonsoft.Json;

namespace WhatsAnAiBridge;

// ── Root response ────────────────────────────────────────────────────

public class BridgeResponse
{
    [JsonProperty("player", NullValueHandling = NullValueHandling.Ignore)]
    public PlayerDto? Player { get; set; }

    [JsonProperty("stats", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, int>? Stats { get; set; }

    [JsonProperty("buffProbe", NullValueHandling = NullValueHandling.Ignore)]
    public List<BuffProbeDto>? BuffProbe { get; set; }

    [JsonProperty("area", NullValueHandling = NullValueHandling.Ignore)]
    public AreaDto? Area { get; set; }

    [JsonProperty("npcDialog", NullValueHandling = NullValueHandling.Ignore)]
    public NpcDialogDto? NpcDialog { get; set; }

    [JsonProperty("mapData", NullValueHandling = NullValueHandling.Ignore)]
    public MapDataDto? MapData { get; set; }

    [JsonProperty("ui", NullValueHandling = NullValueHandling.Ignore)]
    public UiDto? Ui { get; set; }

    [JsonProperty("stashTabs", NullValueHandling = NullValueHandling.Ignore)]
    public List<StashTabDto>? StashTabs { get; set; }

    [JsonProperty("entities", NullValueHandling = NullValueHandling.Ignore)]
    public List<EntityDto>? Entities { get; set; }

    // Deep query fields
    [JsonProperty("filter", NullValueHandling = NullValueHandling.Ignore)]
    public string? Filter { get; set; }

    [JsonProperty("matchCount", NullValueHandling = NullValueHandling.Ignore)]
    public int? MatchCount { get; set; }

    [JsonProperty("query")]
    public string Query { get; set; } = "";

    [JsonProperty("timestamp")]
    public string Timestamp { get; set; } = "";
}

// ── Player ───────────────────────────────────────────────────────────

public class PlayerDto
{
    [JsonProperty("path")]
    public string Path { get; set; } = "";

    [JsonProperty("hp")]
    public int Hp { get; set; }

    [JsonProperty("maxHp")]
    public int MaxHp { get; set; }

    [JsonProperty("es")]
    public int Es { get; set; }

    [JsonProperty("maxEs")]
    public int MaxEs { get; set; }

    [JsonProperty("mana")]
    public int Mana { get; set; }

    [JsonProperty("maxMana")]
    public int MaxMana { get; set; }

    [JsonProperty("pos")]
    public float[] Pos { get; set; } = [];

    [JsonProperty("rotation", NullValueHandling = NullValueHandling.Ignore)]
    public float? Rotation { get; set; }

    [JsonProperty("buffs", NullValueHandling = NullValueHandling.Ignore)]
    public List<BuffDto>? Buffs { get; set; }

    [JsonProperty("skills", NullValueHandling = NullValueHandling.Ignore)]
    public List<SkillDto>? Skills { get; set; }

    [JsonProperty("actor", NullValueHandling = NullValueHandling.Ignore)]
    public ActorDto? Actor { get; set; }
}

// ── Buff ─────────────────────────────────────────────────────────────

public class BuffDto
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("charges")]
    public int Charges { get; set; }

    [JsonProperty("timer")]
    public float Timer { get; set; }

    [JsonProperty("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
    public string? Description { get; set; }

    [JsonProperty("stacks")]
    public int Stacks { get; set; }

    [JsonProperty("maxTime")]
    public float MaxTime { get; set; }

    [JsonProperty("sourceEntityId")]
    public uint SourceEntityId { get; set; }

    [JsonProperty("sourceSkillId")]
    public int SourceSkillId { get; set; }

    [JsonProperty("sourceName", NullValueHandling = NullValueHandling.Ignore)]
    public string? SourceName { get; set; }
}

// ── Skill ────────────────────────────────────────────────────────────

public class SkillDto
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("internalName", NullValueHandling = NullValueHandling.Ignore)]
    public string? InternalName { get; set; }

    [JsonProperty("displayName", NullValueHandling = NullValueHandling.Ignore)]
    public string? DisplayName { get; set; }

    [JsonProperty("canBeUsed")]
    public bool CanBeUsed { get; set; }

    [JsonProperty("isOnSkillBar")]
    public bool IsOnSkillBar { get; set; }

    [JsonProperty("cooldown")]
    public float Cooldown { get; set; }

    [JsonProperty("isUsing")]
    public bool IsUsing { get; set; }

    [JsonProperty("isUserSkill", NullValueHandling = NullValueHandling.Ignore)]
    public bool? IsUserSkill { get; set; }

    [JsonProperty("isMine", NullValueHandling = NullValueHandling.Ignore)]
    public bool? IsMine { get; set; }

    [JsonProperty("totalUses", NullValueHandling = NullValueHandling.Ignore)]
    public int? TotalUses { get; set; }

    [JsonProperty("cost", NullValueHandling = NullValueHandling.Ignore)]
    public int? Cost { get; set; }
}

// ── Actor ────────────────────────────────────────────────────────────

public class ActorDto
{
    [JsonProperty("actionId")]
    public int ActionId { get; set; }

    [JsonProperty("action")]
    public string Action { get; set; } = "";

    [JsonProperty("animationId")]
    public int AnimationId { get; set; }

    [JsonProperty("animation")]
    public string Animation { get; set; } = "";

    [JsonProperty("isMoving")]
    public bool IsMoving { get; set; }

    [JsonProperty("isAttacking")]
    public bool IsAttacking { get; set; }

    [JsonProperty("currentAction", NullValueHandling = NullValueHandling.Ignore)]
    public CurrentActionDto? CurrentAction { get; set; }
}

public class CurrentActionDto
{
    [JsonProperty("skill")]
    public string? Skill { get; set; }

    [JsonProperty("destination")]
    public int[] Destination { get; set; } = [];

    [JsonProperty("targetId", NullValueHandling = NullValueHandling.Ignore)]
    public uint? TargetId { get; set; }
}

// ── Buff probe (memory dump for reverse-engineering) ─────────────────

public class BuffProbeDto
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("timer")]
    public float Timer { get; set; }

    [JsonProperty("address")]
    public string Address { get; set; } = "";

    [JsonProperty("raw")]
    public Dictionary<string, string> Raw { get; set; } = new();

    [JsonProperty("sv80", NullValueHandling = NullValueHandling.Ignore)]
    public StdVectorDto? Sv80 { get; set; }

    [JsonProperty("treeNode0", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, object>? TreeNode0 { get; set; }

    [JsonProperty("treeNode1", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, object>? TreeNode1 { get; set; }
}

public class StdVectorDto
{
    [JsonProperty("first")]
    public string First { get; set; } = "";

    [JsonProperty("last")]
    public string Last { get; set; } = "";

    [JsonProperty("dataSize")]
    public long DataSize { get; set; }

    [JsonProperty("dataInts", NullValueHandling = NullValueHandling.Ignore)]
    public List<object>? DataInts { get; set; }

    [JsonProperty("statPairs", NullValueHandling = NullValueHandling.Ignore)]
    public List<object>? StatPairs { get; set; }
}

public class StatPairDto
{
    [JsonProperty("statId")]
    public int StatId { get; set; }

    [JsonProperty("stat")]
    public string Stat { get; set; } = "";

    [JsonProperty("val")]
    public int Val { get; set; }
}

// ── Area ─────────────────────────────────────────────────────────────

public class AreaDto
{
    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("areaLevel")]
    public int AreaLevel { get; set; }

    [JsonProperty("act")]
    public int Act { get; set; }
}

// ── NPC Dialog ───────────────────────────────────────────────────────

public class NpcDialogDto
{
    [JsonProperty("isVisible")]
    public bool IsVisible { get; set; }

    [JsonProperty("dialogDepth")]
    public int DialogDepth { get; set; }

    [JsonProperty("npcName", NullValueHandling = NullValueHandling.Ignore)]
    public string? NpcName { get; set; }

    [JsonProperty("isLoreTalk", NullValueHandling = NullValueHandling.Ignore)]
    public bool? IsLoreTalk { get; set; }

    [JsonProperty("lines", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Lines { get; set; }

    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public string? Error { get; set; }
}

// ── Map Data ─────────────────────────────────────────────────────────

public class MapDataDto
{
    [JsonProperty("dialogDepth")]
    public int DialogDepth { get; set; }

    [JsonProperty("mapStats", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, int>? MapStats { get; set; }

    [JsonProperty("questFlags", NullValueHandling = NullValueHandling.Ignore)]
    public QuestFlagsDto? QuestFlags { get; set; }

    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public string? Error { get; set; }
}

public class QuestFlagsDto
{
    [JsonProperty("_total")]
    public int Total { get; set; }

    // Dynamic quest flag entries stored via JsonExtensionData
    [JsonExtensionData]
    public Dictionary<string, object> Flags { get; set; } = new();
}

// ── UI ───────────────────────────────────────────────────────────────

public class UiDto
{
    [JsonProperty("dialogDepth")]
    public int DialogDepth { get; set; }

    [JsonProperty("npcDialog")]
    public bool NpcDialog { get; set; }

    [JsonProperty("purchaseWindow")]
    public bool PurchaseWindow { get; set; }

    [JsonProperty("sellWindow")]
    public bool SellWindow { get; set; }

    [JsonProperty("mapDeviceWindow")]
    public bool MapDeviceWindow { get; set; }

    [JsonProperty("tradeWindow")]
    public bool TradeWindow { get; set; }

    [JsonProperty("popUpWindow")]
    public bool PopUpWindow { get; set; }

    [JsonProperty("ritualWindow")]
    public bool RitualWindow { get; set; }

    [JsonProperty("villageRewardWindow")]
    public bool VillageRewardWindow { get; set; }

    [JsonProperty("mercenaryEncounterWindow")]
    public bool MercenaryEncounterWindow { get; set; }

    [JsonProperty("zanaMissionChoice")]
    public bool ZanaMissionChoice { get; set; }

    [JsonProperty("leagueMechanicButtons", NullValueHandling = NullValueHandling.Ignore)]
    public LeagueMechanicButtonsDto? LeagueMechanicButtons { get; set; }

    [JsonProperty("visibleChildren")]
    public List<UiChildDto> VisibleChildren { get; set; } = [];

    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public string? Error { get; set; }
}

public class LeagueMechanicButtonsDto
{
    [JsonProperty("vis")]
    public bool Vis { get; set; }

    [JsonProperty("cc")]
    public int Cc { get; set; }
}

public class UiChildDto
{
    [JsonProperty("i")]
    public int I { get; set; }

    [JsonProperty("cc")]
    public int Cc { get; set; }

    [JsonProperty("t", NullValueHandling = NullValueHandling.Ignore)]
    public string? T { get; set; }

    [JsonProperty("ct", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Ct { get; set; }
}

// ── Stash tab ────────────────────────────────────────────────────────

public class StashTabDto
{
    [JsonProperty("index")]
    public int Index { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("type")]
    public string Type { get; set; } = "";

    [JsonProperty("visibleIndex")]
    public int VisibleIndex { get; set; }

    [JsonProperty("color")]
    public ColorDto Color { get; set; } = new();

    [JsonProperty("isPremium")]
    public bool IsPremium { get; set; }

    [JsonProperty("isPublic")]
    public bool IsPublic { get; set; }

    [JsonProperty("isRemoveOnly")]
    public bool IsRemoveOnly { get; set; }

    [JsonProperty("isHidden")]
    public bool IsHidden { get; set; }

    [JsonProperty("isMapSeries")]
    public bool IsMapSeries { get; set; }

    [JsonProperty("rawFlags")]
    public byte RawFlags { get; set; }

    [JsonProperty("affinity")]
    public uint Affinity { get; set; }
}

public class ColorDto
{
    [JsonProperty("r")]
    public int R { get; set; }

    [JsonProperty("g")]
    public int G { get; set; }

    [JsonProperty("b")]
    public int B { get; set; }
}

// ── Entity (shallow) ─────────────────────────────────────────────────

public class EntityDto
{
    [JsonProperty("id")]
    public uint Id { get; set; }

    [JsonProperty("deep", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Deep { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; } = "";

    [JsonProperty("path")]
    public string Path { get; set; } = "";

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("alive")]
    public bool Alive { get; set; }

    [JsonProperty("hostile")]
    public bool Hostile { get; set; }

    [JsonProperty("rarity")]
    public string Rarity { get; set; } = "";

    [JsonProperty("dist")]
    public float Dist { get; set; }

    [JsonProperty("pos")]
    public float[] Pos { get; set; } = [];

    [JsonProperty("hp")]
    public int Hp { get; set; }

    [JsonProperty("maxHp")]
    public int MaxHp { get; set; }

    // Deep fields (present only on deep-scanned entities)
    [JsonProperty("isValid", NullValueHandling = NullValueHandling.Ignore)]
    public bool? IsValid { get; set; }

    [JsonProperty("allComponents", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? AllComponents { get; set; }

    [JsonProperty("render", NullValueHandling = NullValueHandling.Ignore)]
    public RenderDto? Render { get; set; }

    [JsonProperty("positioned", NullValueHandling = NullValueHandling.Ignore)]
    public PositionedDto? Positioned { get; set; }

    [JsonProperty("animated", NullValueHandling = NullValueHandling.Ignore)]
    public AnimatedDto? Animated { get; set; }

    [JsonProperty("stateMachine", NullValueHandling = NullValueHandling.Ignore)]
    public StateMachineDto? StateMachine { get; set; }

    [JsonProperty("npc", NullValueHandling = NullValueHandling.Ignore)]
    public NpcDto? Npc { get; set; }

    [JsonProperty("life", NullValueHandling = NullValueHandling.Ignore)]
    public LifeDto? Life { get; set; }

    [JsonProperty("targetable", NullValueHandling = NullValueHandling.Ignore)]
    public TargetableDto? Targetable { get; set; }

    [JsonProperty("chest", NullValueHandling = NullValueHandling.Ignore)]
    public ChestDto? Chest { get; set; }

    [JsonProperty("omp", NullValueHandling = NullValueHandling.Ignore)]
    public OmpDto? Omp { get; set; }

    [JsonProperty("minimapIcon", NullValueHandling = NullValueHandling.Ignore)]
    public MinimapIconDto? MinimapIcon { get; set; }

    [JsonProperty("buffs", NullValueHandling = NullValueHandling.Ignore)]
    public List<BuffDto>? Buffs { get; set; }

    [JsonProperty("stats", NullValueHandling = NullValueHandling.Ignore)]
    public StatsDto? Stats { get; set; }

    [JsonProperty("actor", NullValueHandling = NullValueHandling.Ignore)]
    public ActorDto? Actor { get; set; }

    // Effect components
    [JsonProperty("beam", NullValueHandling = NullValueHandling.Ignore)]
    public BeamDto? Beam { get; set; }

    [JsonProperty("groundEffect", NullValueHandling = NullValueHandling.Ignore)]
    public GroundEffectDto? GroundEffect { get; set; }

    [JsonProperty("hasEffectPack", NullValueHandling = NullValueHandling.Ignore)]
    public bool? HasEffectPack { get; set; }

    [JsonProperty("animController", NullValueHandling = NullValueHandling.Ignore)]
    public AnimControllerDto? AnimController { get; set; }

    [JsonProperty("_end", NullValueHandling = NullValueHandling.Ignore)]
    public bool? End { get; set; }
}

// ── Deep entity sub-DTOs ─────────────────────────────────────────────

public class RenderDto
{
    [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
    public string? Name { get; set; }

    [JsonProperty("pos")]
    public float[] Pos { get; set; } = [];

    [JsonProperty("bounds")]
    public float[] Bounds { get; set; } = [];
}

public class PositionedDto
{
    [JsonProperty("grid", NullValueHandling = NullValueHandling.Ignore)]
    public int[]? Grid { get; set; }

    [JsonProperty("reaction", NullValueHandling = NullValueHandling.Ignore)]
    public int? Reaction { get; set; }

    [JsonProperty("size", NullValueHandling = NullValueHandling.Ignore)]
    public int? Size { get; set; }

    [JsonProperty("scale", NullValueHandling = NullValueHandling.Ignore)]
    public float? Scale { get; set; }

    [JsonProperty("rotation", NullValueHandling = NullValueHandling.Ignore)]
    public float? Rotation { get; set; }

    [JsonProperty("travelProgress", NullValueHandling = NullValueHandling.Ignore)]
    public float? TravelProgress { get; set; }
}

public class AnimatedDto
{
    [JsonProperty("baseEntityPath")]
    public string? BaseEntityPath { get; set; }

    [JsonProperty("baseEntityId")]
    public uint BaseEntityId { get; set; }

    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Error { get; set; }
}

public class StateMachineDto
{
    [JsonProperty("canBeTarget", NullValueHandling = NullValueHandling.Ignore)]
    public bool? CanBeTarget { get; set; }

    [JsonProperty("inTarget", NullValueHandling = NullValueHandling.Ignore)]
    public bool? InTarget { get; set; }

    [JsonProperty("states")]
    public Dictionary<string, int> States { get; set; } = new();

    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Error { get; set; }
}

public class NpcDto
{
    [JsonProperty("hasIconOverhead")]
    public bool HasIconOverhead { get; set; }

    [JsonProperty("isIgnoreHidden")]
    public bool IsIgnoreHidden { get; set; }

    [JsonProperty("isMinMapLabelVisible")]
    public bool IsMinMapLabelVisible { get; set; }
}

public class LifeDto
{
    [JsonProperty("hp")]
    public int Hp { get; set; }

    [JsonProperty("maxHp")]
    public int MaxHp { get; set; }

    [JsonProperty("es")]
    public int Es { get; set; }

    [JsonProperty("maxEs")]
    public int MaxEs { get; set; }
}

public class TargetableDto
{
    [JsonProperty("isTargetable")]
    public bool IsTargetable { get; set; }

    [JsonProperty("isTargeted")]
    public bool IsTargeted { get; set; }
}

public class ChestDto
{
    [JsonProperty("isOpened")]
    public bool IsOpened { get; set; }

    [JsonProperty("isLocked")]
    public bool IsLocked { get; set; }

    [JsonProperty("isStrongbox")]
    public bool IsStrongbox { get; set; }

    [JsonProperty("destroyAfterOpen")]
    public bool DestroyAfterOpen { get; set; }

    [JsonProperty("isLarge")]
    public bool IsLarge { get; set; }

    [JsonProperty("stompable")]
    public bool Stompable { get; set; }

    [JsonProperty("openOnDamage")]
    public bool OpenOnDamage { get; set; }
}

public class OmpDto
{
    [JsonProperty("rarity")]
    public string Rarity { get; set; } = "";

    [JsonProperty("mods", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Mods { get; set; }
}

public class MinimapIconDto
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("isVisible")]
    public bool IsVisible { get; set; }

    [JsonProperty("isHide")]
    public bool IsHide { get; set; }
}

public class StatsDto
{
    [JsonProperty("_truncated", NullValueHandling = NullValueHandling.Ignore)]
    public int? Truncated { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object> Values { get; set; } = new();
}

// ── Effect components ────────────────────────────────────────────────

public class BeamDto
{
    [JsonProperty("start")]
    public float[] Start { get; set; } = [];

    [JsonProperty("end")]
    public float[] End { get; set; } = [];
}

public class GroundEffectDto
{
    [JsonProperty("duration")]
    public float Duration { get; set; }

    [JsonProperty("maxDuration")]
    public float MaxDuration { get; set; }

    [JsonProperty("scale")]
    public float Scale { get; set; }

    [JsonProperty("sizeIncrease")]
    public float SizeIncrease { get; set; }
}

public class AnimControllerDto
{
    [JsonProperty("animId")]
    public int AnimId { get; set; }

    [JsonProperty("stage")]
    public int Stage { get; set; }

    [JsonProperty("progress")]
    public float Progress { get; set; }

    [JsonProperty("speed")]
    public float Speed { get; set; }
}

// ── Snapshot (recording) ─────────────────────────────────────────────

public class SnapshotResponse
{
    [JsonProperty("frame")]
    public int Frame { get; set; }

    [JsonProperty("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonProperty("elapsedMs")]
    public double ElapsedMs { get; set; }

    [JsonProperty("area", NullValueHandling = NullValueHandling.Ignore)]
    public AreaDto? Area { get; set; }

    [JsonProperty("player", NullValueHandling = NullValueHandling.Ignore)]
    public PlayerDto? Player { get; set; }

    [JsonProperty("entities", NullValueHandling = NullValueHandling.Ignore)]
    public List<EntityDto>? Entities { get; set; }

    [JsonProperty("_end")]
    public bool End { get; set; } = true;
}

public class SnapshotCaptureResponse
{
    [JsonProperty("file")]
    public string File { get; set; } = "";

    [JsonProperty("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonProperty("frame")]
    public int Frame { get; set; }

    [JsonProperty("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonProperty("entityCount")]
    public int EntityCount { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = "";
}

// ── Recording command responses ──────────────────────────────────────

public class RecordingStartResponse
{
    [JsonProperty("status")]
    public string Status { get; set; } = "recording_started";

    [JsonProperty("file")]
    public string File { get; set; } = "";

    [JsonProperty("intervalMs")]
    public int IntervalMs { get; set; }
}

public class RecordingStopResponse
{
    [JsonProperty("status")]
    public string Status { get; set; } = "recording_stopped";

    [JsonProperty("frames")]
    public int Frames { get; set; }

    [JsonProperty("durationMs")]
    public double DurationMs { get; set; }

    [JsonProperty("file")]
    public string File { get; set; } = "";

    [JsonProperty("sizeBytes")]
    public long SizeBytes { get; set; }
}

public class RecordingStatusResponse
{
    [JsonProperty("status")]
    public string Status { get; set; } = "";

    [JsonProperty("isRecording")]
    public bool IsRecording { get; set; }

    [JsonProperty("frames", NullValueHandling = NullValueHandling.Ignore)]
    public int? Frames { get; set; }

    [JsonProperty("durationMs", NullValueHandling = NullValueHandling.Ignore)]
    public double? DurationMs { get; set; }

    [JsonProperty("file", NullValueHandling = NullValueHandling.Ignore)]
    public string? File { get; set; }

    [JsonProperty("intervalMs", NullValueHandling = NullValueHandling.Ignore)]
    public int? IntervalMs { get; set; }
}

public class RecordingListResponse
{
    [JsonProperty("recordings")]
    public List<RecordingFileDto> Recordings { get; set; } = [];
}

public class RecordingFileDto
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonProperty("modified")]
    public string Modified { get; set; } = "";
}

public class RecordingLoadResponse
{
    [JsonProperty("status")]
    public string Status { get; set; } = "loaded";

    [JsonProperty("file")]
    public string File { get; set; } = "";

    [JsonProperty("frames")]
    public int Frames { get; set; }
}

public class RecordingFramesResponse
{
    [JsonProperty("frames")]
    public List<object> Frames { get; set; } = [];
}

public class RecordingSearchResponse
{
    [JsonProperty("term")]
    public string Term { get; set; } = "";

    [JsonProperty("matchCount")]
    public int MatchCount { get; set; }

    [JsonProperty("frames")]
    public List<int> Frames { get; set; } = [];
}

public class RecordingSummaryDto
{
    [JsonProperty("file")]
    public string File { get; set; } = "";

    [JsonProperty("totalFrames")]
    public int TotalFrames { get; set; }

    [JsonProperty("firstTimestamp")]
    public string? FirstTimestamp { get; set; }

    [JsonProperty("lastTimestamp")]
    public string? LastTimestamp { get; set; }

    [JsonProperty("uniqueEntityPaths")]
    public List<string> UniqueEntityPaths { get; set; } = [];

    [JsonProperty("uniqueBuffNames")]
    public List<string> UniqueBuffNames { get; set; } = [];
}

// ── Error response ───────────────────────────────────────────────────

public class ErrorResponse
{
    [JsonProperty("error")]
    public string Error { get; set; } = "";

    [JsonProperty("query", NullValueHandling = NullValueHandling.Ignore)]
    public string? Query { get; set; }

    [JsonProperty("file", NullValueHandling = NullValueHandling.Ignore)]
    public string? File { get; set; }

    [JsonProperty("totalFrames", NullValueHandling = NullValueHandling.Ignore)]
    public int? TotalFrames { get; set; }
}
