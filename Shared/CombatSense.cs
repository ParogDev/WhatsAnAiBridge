using System;

namespace WhatsAnAiBridge;

/// <summary>
/// A cheap "is the user fighting right now" detector for the attention manager (GuidePanel.cs, rule 1 of
/// research\display-attention.md): the agent's surfaces never compete with gameplay for attention.
/// Sampled at most every 250 ms, cached between samples, and allocation-free: one pass over the HUD's monster list
/// (ValidEntitiesByType[Monster]) looking for the first alive hostile within CombatRangeGrid grid units of the
/// player (Entity.DistancePlayer, cached grid positions; the distance is checked first so Life is only read for
/// monsters that are near). Town and hideout are always safe. A short hold (CombatHoldSec after the last hostile
/// left) keeps the result from flickering between packs.
/// Game-agnostic: GameController, EntityType and the area flags come from the per-game GlobalUsings.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const double CombatSampleSec = 0.25, CombatHoldSec = 3.0;
    private const float CombatRangeGrid = 60f;

    /// <summary>
    /// What the detector saw at its last sample. InCombat holds for CombatHoldSec after the last hostile; Safe is
    /// the raw view (town / hideout, or no hostile near at the last sample), without the hold.
    /// </summary>
    private sealed class CombatState
    {
        public double SampledAt = -1e9;
        public double LastHostileAt = -1e9;
        public bool HostileNear;
        public bool SafeArea = true;
        public bool InCombat;
        public string? LastError;
    }

    private readonly CombatState _combat = new();

    /// <summary>The cached combat view for this frame (sampled when the last sample is older than 250 ms).</summary>
    private (bool inCombat, bool safe) CombatSense(double now)
    {
        var c = _combat;
        if (now - c.SampledAt >= CombatSampleSec)
        {
            c.SampledAt = now;
            CombatSample(c);
            if (c.HostileNear) c.LastHostileAt = now;
            c.InCombat = now - c.LastHostileAt < CombatHoldSec;
        }
        return (c.InCombat, c.SafeArea || !c.HostileNear);
    }

    private void CombatSample(CombatState c)
    {
        try
        {
            var area = GameController.InGame ? GameController.Area?.CurrentArea : null;
            var safe = area == null || area.IsTown || area.IsHideout;
            var near = false;
            if (!safe && GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Monster, out var monsters))
            {
                foreach (var e in monsters)
                {
                    if (e == null || e.DistancePlayer > CombatRangeGrid) continue;
                    if (!e.IsHostile || !e.IsAlive) continue;
                    near = true;
                    break;
                }
            }
            c.SafeArea = safe;
            c.HostileNear = near;
        }
        catch (Exception ex)
        {
            // A read that fails (an entity leaving mid-pass) counts as "not fighting" for this sample; log once.
            c.SafeArea = true;
            c.HostileNear = false;
            if (c.LastError != ex.Message) { c.LastError = ex.Message; LogError($"[CombatSense] {ex.Message}"); }
        }
    }
}
