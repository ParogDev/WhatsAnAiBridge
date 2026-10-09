using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Waits for something worth measuring: another player starting to walk nearby, so overlay tracking can be captured on a
/// moving target without the user (or an agent) moving. Checked once per frame while armed (positions the HUD already
/// cached this frame; no extra reads). A player counts as moving after 3 frames in a row above minSpeed world units/s
/// (a walk is ~300-450). The local player is ignored.
/// motion.watch {range? (world units, default 900), minSpeed? (default 200), on? (default true)}; motion.state {since?}
/// -> the latest event {seq, entityId, name, speed, screen:[x,y]} once one is seen.
/// </summary>
public partial class WhatsAnAiBridge
{
    private sealed class MotionWatchState
    {
        public bool On;
        public float Range = 900, MinSpeed = 200;
        public readonly Dictionary<long, (Vector3 pos, DateTime at, int streak)> Last = new();
        public JObject? Latest;
        public int Seq;
    }

    private readonly MotionWatchState _motion = new();

    private string? ProcessMotionMethod(string method, JToken? p) => method switch
    {
        "motion.watch" => SafeMemory(() =>
        {
            _motion.On = p?["on"]?.Value<bool>() ?? true;
            _motion.Range = Math.Clamp(p?["range"]?.Value<float>() ?? 900, 50, 5000);
            _motion.MinSpeed = Math.Clamp(p?["minSpeed"]?.Value<float>() ?? 200, 20, 5000);
            _motion.Last.Clear();
            return new JObject { ["on"] = _motion.On, ["range"] = _motion.Range, ["minSpeed"] = _motion.MinSpeed, ["seq"] = _motion.Seq };
        }),
        "motion.state" => SafeMemory(() =>
        {
            var since = p?["since"]?.Value<int>() ?? 0;
            return _motion.Latest is { } l && _motion.Seq > since
                ? l
                : new JObject { ["on"] = _motion.On, ["seq"] = _motion.Seq, ["event"] = null };
        }),
        _ => null,
    };

    private void MotionTick()
    {
        if (!_motion.On || !GameController.InGame) return;
        try
        {
            var me = GameController.Player;
            var now = DateTime.UtcNow;
            var seen = new HashSet<long>();
            var players = GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Player, out var list) ? list : [];
            foreach (var e in players)
            {
                if (e.Address == me?.Address || e.DistancePlayer * 10.87f > _motion.Range) continue;
                var r = e.GetComponent<Render>();
                if (r == null) continue;
                var pos = RenderPosNum(r);
                seen.Add(e.Id);
                if (_motion.Last.TryGetValue(e.Id, out var last))
                {
                    var dt = (now - last.at).TotalSeconds;
                    if (dt <= 0) continue;
                    var speed = Vector2.Distance(new Vector2(pos.X, pos.Y), new Vector2(last.pos.X, last.pos.Y)) / dt;
                    var streak = speed >= _motion.MinSpeed ? last.streak + 1 : 0;
                    _motion.Last[e.Id] = (pos, now, streak);
                    if (streak == 3)
                    {
                        var s = GameController.IngameState.Camera.WorldToScreen(pos);
                        _motion.Latest = new JObject
                        {
                            ["seq"] = ++_motion.Seq, ["entityId"] = e.Id, ["name"] = e.RenderName ?? e.Path, ["speed"] = Math.Round(speed),
                            ["screen"] = new JArray(Math.Round(s.X), Math.Round(s.Y)), ["at"] = now.ToString("o"),
                        };
                    }
                }
                else _motion.Last[e.Id] = (pos, now, 0);
            }
            foreach (var gone in _motion.Last.Keys.Where(k => !seen.Contains(k)).ToList()) _motion.Last.Remove(gone);
        }
        catch { }
    }
}
