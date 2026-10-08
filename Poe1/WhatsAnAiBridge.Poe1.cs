using System;
using System.Collections.Generic;
using System.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// PoE1 (ExileCore) side of the plugin: everything whose API or game knowledge differs from PoE2.
/// The PoE2 twin is Poe2/WhatsAnAiBridge.Poe2.cs - keep the two member lists identical.
/// </summary>
public partial class WhatsAnAiBridge
{
    internal const string GameId = "poe1";

    /// <summary>Namespaces the eval/describe walker may traverse.</summary>
    internal static readonly string[] ApiNamespaces = ["ExileCore", "GameOffsets", "SharpDX"];

    /// <summary>Features this game's HUD cannot provide (reported by hello and in responses).</summary>
    internal static readonly string[] UnsupportedFeatures = [];

    public override Job Tick()
    {
        ProcessPendingRequests();
        return default;
    }

    // ── Mechanical API differences ───────────────────────────────────

    private static Color Rgba(int r, int g, int b, int a = 255) => new(r, g, b, a);

    private static float[] GridXY(Entity e) => [MathF.Round(e.GridPosNum.X), MathF.Round(e.GridPosNum.Y)];

    private static float[] RenderPos(Render r) => [Round1(r.PosNum.X), Round1(r.PosNum.Y), Round1(r.PosNum.Z)];

    private static float[] RenderBounds(Render r) => [Round1(r.BoundsNum.X), Round1(r.BoundsNum.Y), Round1(r.BoundsNum.Z)];

    private static BeamDto? BuildBeam(Beam beam)
    {
        var bs = beam.BeamStartNum;
        var be = beam.BeamEndNum;
        return new BeamDto
        {
            Start = [Round1(bs.X), Round1(bs.Y), Round1(bs.Z)],
            End = [Round1(be.X), Round1(be.Y), Round1(be.Z)],
        };
    }

    private static void PopulateSkillNames(SkillDto dto, ActorSkill s)
    {
        var activeSkill = s.EffectsPerLevel?.SkillGemWrapper?.ActiveSkill;
        if (activeSkill == null) return;
        if (!string.IsNullOrEmpty(activeSkill.InternalName)) dto.InternalName = activeSkill.InternalName;
        if (!string.IsNullOrEmpty(activeSkill.DisplayName)) dto.DisplayName = activeSkill.DisplayName;
    }

    private static uint? StashAffinity(ServerStashTab tab) => (uint)tab.Affinity;

    private static int? ActiveWeaponSet(Entity player) => null;

    // ── Game-specific UI panels ──────────────────────────────────────

    private static void PopulateGamePanels(UiDto dto, IngameUIElements ui)
    {
        dto.MapDeviceWindow = ui.MapDeviceWindow?.IsVisible == true;
        dto.VillageRewardWindow = ui.VillageRewardWindow?.IsVisible == true;
        dto.MercenaryEncounterWindow = ui.MercenaryEncounterWindow?.IsVisible == true;
        dto.ZanaMissionChoice = ui.ZanaMissionChoice?.IsVisible == true;
    }

    // ── BUFF PROBE (raw memory dump, PoE1 layout) ─────────────────────
    // Offsets were reverse-engineered against the PoE1 buff layout; meaningless on PoE2.

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

    private static Dictionary<string, object> ReadTreeNode(IMemory mem, long ptr)
    {
        var node = new Dictionary<string, object>();
        for (int off = 0; off <= 0x38; off += 4)
        {
            try { node[$"0x{off:X2}"] = mem.Read<int>(ptr + off); }
            catch { node[$"0x{off:X2}"] = "ERR"; }
        }
        return node;
    }
}
