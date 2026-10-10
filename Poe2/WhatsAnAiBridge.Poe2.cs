using System;
using System.Collections.Generic;

namespace WhatsAnAiBridge;

/// <summary>
/// PoE2 (ExileCore2) side of the plugin: everything whose API or game knowledge differs from PoE1.
/// The PoE1 twin is Poe1/WhatsAnAiBridge.Poe1.cs - keep the two member lists identical.
/// Anything PoE2 cannot provide is reported as unsupported, never faked as false/0.
/// </summary>
public partial class WhatsAnAiBridge
{
    internal const string GameId = "poe2";

    /// <summary>Namespaces the eval/describe walker may traverse.</summary>
    internal static readonly string[] ApiNamespaces = ["ExileCore2", "GameOffsets2"];

    /// <summary>Features this game's HUD cannot provide (reported by hello and in responses).</summary>
    internal static readonly string[] UnsupportedFeatures =
    [
        "buffProbe: raw buff memory offsets were reverse-engineered for PoE1 only",
        "ui.mapDeviceWindow: no MapDeviceWindow on ExileCore2 IngameUIElements",
        "ui.villageRewardWindow: Settlers (PoE1 league) UI",
        "ui.mercenaryEncounterWindow: Mercenaries (PoE1 league) UI",
        "ui.zanaMissionChoice: PoE1 atlas UI",
        "stashTabs.affinity: ServerStashTab has no Affinity on ExileCore2",
    ];

    public override void Tick()
    {
        ProcessPendingRequests();
    }

    // ── Mechanical API differences ───────────────────────────────────

    private static Color Rgba(int r, int g, int b, int a = 255) => Color.FromArgb(a, r, g, b);

    private static float[] GridXY(Entity e) => [MathF.Round(e.GridPos.X), MathF.Round(e.GridPos.Y)];

    private static float[] RenderPos(Render r) => [Round1(r.Pos.X), Round1(r.Pos.Y), Round1(r.Pos.Z)];
    private static System.Numerics.Vector3 RenderPosNum(Render r) => r.Pos;

    private static float[] RenderBounds(Render r) => [Round1(r.Bounds.X), Round1(r.Bounds.Y), Round1(r.Bounds.Z)];

    private static BeamDto? BuildBeam(Beam beam)
    {
        var bs = beam.BeamStart;
        var be = beam.BeamEnd;
        return new BeamDto
        {
            Start = [Round1(bs.X), Round1(bs.Y), Round1(bs.Z)],
            End = [Round1(be.X), Round1(be.Y), Round1(be.Z)],
        };
    }

    private static void PopulateSkillNames(SkillDto dto, ActorSkill s)
    {
        // PoE2 exposes the internal name directly; display name comes via GrantedEffect.ActiveSkill.
        if (!string.IsNullOrEmpty(s.InternalName)) dto.InternalName = s.InternalName;
        var activeSkill = s.EffectsPerLevel?.GrantedEffect?.ActiveSkill;
        if (activeSkill != null && !string.IsNullOrEmpty(activeSkill.DisplayName))
            dto.DisplayName = activeSkill.DisplayName;
    }

    private static uint? StashAffinity(ServerStashTab tab)
    {
        MarkUnsupported("stashTabs.affinity");
        return null;
    }

    private static int? ActiveWeaponSet(Entity player) => player.GetComponent<Stats>()?.ActiveWeaponSetIndex;

    /// <summary>Whether highlight boxes under the world map pan container get the horizontal stretch correction
    /// (Shared WorldMapPan; finding ui.worldmap.pan-x-underscaled, measured on PoE2 2026-10-10).</summary>
    private static readonly bool WorldMapPanCorrected = true;

    // ── Game-specific UI panels ──────────────────────────────────────

    private static void PopulateGamePanels(UiDto dto, IngameUIElements ui)
    {
        // PoE1-only panels stay null (omitted) and are listed as unsupported instead.
        MarkUnsupported("ui.mapDeviceWindow");
        MarkUnsupported("ui.villageRewardWindow");
        MarkUnsupported("ui.mercenaryEncounterWindow");
        MarkUnsupported("ui.zanaMissionChoice");
    }

    // ── BUFF PROBE ───────────────────────────────────────────────────

    private List<BuffProbeDto>? BuildBuffProbe()
    {
        MarkUnsupported("buffProbe");
        return null;
    }
}
