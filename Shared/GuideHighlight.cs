using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// In-game highlights: an agent points at what the user should look at or click - items in the inventory or the visible
/// stash tab (by name), any UI element (walker path) or a screen area - with an emphasis tier and an optional order
/// (a sequence: "1 then 2 then 3"). Targets are re-resolved every 100 ms so they follow the UI; GuideHighlightDraw.cs
/// draws them. Read-only: nothing is clicked.
///   guide.highlight {targets: [{item | path | panel+child | rect:[x,y,w,h], label?, tier?: primary|secondary|context, order?,
///                               ask?, key?}], title?, current?, durationSec?, clear?}
///   guide.highlight_advance {}      the next step of a sequence becomes current
///   guide.highlight_state {}        targets with their resolved rects (what the user sees), and each target's answer
///   guide.verdicts {since?, limit?} the user's answers to asked targets (Yes / No / Not sure, clicked in game), with a seq
/// Verdicts: a target with `ask` (a yes/no question about the agent's guess, e.g. "Is this the Keth stop?") shows Yes /
/// No / Not sure controls next to it. The click records a verdict {id, key, ask, label, answer, at, rect, locator,
/// highlightTitle}, kept in memory, appended to <BridgeDirectory>\verdicts\verdicts.jsonl and, while observing, put
/// on the observer's agent lane (method user.verdict). The target then stops asking.
/// Layers: every session draws in its own layer (HighlightState per owner: the session id, or "flow" / "queue" for what
/// the HUD drives itself, or "anon" for a connection that never said hello). A guide.highlight replaces only the
/// caller's own targets and clear=true clears only them (clear + force clears every layer), so another session's call
/// never removes a question it asked. The overlay draws every layer together; each keeps its own title, sequence and
/// current step. Reads report the caller's layer at the top level plus `layers`, the combined view.
/// Nothing lingers (the user: "this here is obnoxious and shouldn't stick around at all"), whatever the agent asked for:
///   - Lifetime: an unanswered ask and a context target last durationSec, else HlDefaultLifeSec (25 s), then fade out
///     and expire (state: expired; an expired ask no longer counts as pending). Primary and secondary targets stay until
///     cleared, replaced or durationSec.
///   - Step: a session's highlight set while its own guide step is active (waiting / detected / settling) goes with that
///     step: when the card moves to another step, finishes (captured, done, failed...), is cleared or dismissed.
///   - Big panels: while an NPC dialogue or a large / fullscreen panel is open, a box that is not inside it is "about
///     something else": context and secondary ones overlapping it are hidden (covered), and every unanswered ask about
///     something else shrinks to a small pill at the screen edge (docked). Boxes inside the panel are untouched.
/// </summary>
public partial class WhatsAnAiBridge
{
    internal sealed class HighlightTarget
    {
        public string? Item;          // name / base / path substring, matched in the player inventory and the visible stash tab
        public string? Path;          // walker path to a UI element
        public string? Panel;         // text inside a top-level IngameUi panel (e.g. "Stash Tab Settings"): stable when child indexes shift
        public int[]? Child;          // child indexes inside that panel, e.g. [0,1,7,1,11,1]
        public string? Text;          // exact label of a visible element (stash tab, button, row): every visible match
        public string? Within;        // optional: limit the text search to the panel containing this text
        public string? Action;        // optional cue drawn on the target: click | rightclick
        public string? UntilCond;     // optional: step done when checked | unchecked (checkbox, PoE2 +0x60A) | gone (target disappears)
        public string? ClipTo;        // optional walker path to a container: only parts of the target inside its rect count (e.g. the visible part of a scrolling tab row)
        public string[]? Rel;         // optional navigation from the matched element: "^" = parent, "n" = child n (e.g. a label's checkbox: ["^","1"])
        public float[]? Rect;         // fixed screen rect x, y, w, h
        public string? Label;
        public string Tier = "primary";
        public int? Order;
        public string? Ask;           // a yes/no question about this target ("Is this the Keth stop?"): Yes / No / Not sure controls appear next to it
        public string? Key;           // caller's correlation key for the verdict (e.g. "worldmap.stop.10=G2_4_1"); default id: hl<rev>.<index>
        public string? Answer;        // yes | no | skip once the user clicked
        public DateTime? AnsweredAt;
        public DateTime? EndsAt;      // default lifetime (an unanswered ask, a context target): expires then unless durationSec was given
        public bool Expired;          // its lifetime ran out: no longer resolved, drawn or asked
    }

    /// <summary>One drawable box (an item target can resolve to several).</summary>
    /// <remarks><c>EndTicks</c>: UTC ticks when it is gone (its lifetime or the layer's durationSec; 0 = no end), so the
    /// overlay fades it out over the last <see cref="HlFadeSec"/>. <c>Covered</c>: a context / secondary box over an open
    /// big panel it is not part of - not drawn. <c>Docked</c>: an unanswered ask while a big panel it is not part of is open -
    /// drawn as a small pill at the screen edge instead of the question strip.</remarks>
    internal readonly record struct HighlightBox(float X, float Y, float W, float H, string? Label, string Tier, int? Order, int TargetIndex,
        string? Action = null, string? Ask = null, string? Answer = null, bool Panned = false, float PanX = 0, float PanY = 0,
        long EndTicks = 0, bool Covered = false, bool Docked = false);

    /// <summary>The user's answer to an asked target, as stored (verdicts.jsonl, guide.verdicts, the observer's agent lane).</summary>
    internal sealed class HighlightVerdict
    {
        [JsonProperty("seq")] public long Seq;
        [JsonProperty("id")] public string Id = "";
        [JsonProperty("key", NullValueHandling = NullValueHandling.Ignore)] public string? Key;
        [JsonProperty("ask")] public string Ask = "";
        [JsonProperty("label", NullValueHandling = NullValueHandling.Ignore)] public string? Label;
        [JsonProperty("answer")] public string Answer = "";        // yes | no | skip
        [JsonProperty("at")] public DateTime At;
        [JsonProperty("rect", NullValueHandling = NullValueHandling.Ignore)] public float[]? Rect;   // the target's first box when answered
        [JsonProperty("item", NullValueHandling = NullValueHandling.Ignore)] public string? Item;
        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)] public string? Path;
        [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)] public string? Text;
        [JsonProperty("panel", NullValueHandling = NullValueHandling.Ignore)] public string? Panel;
        [JsonProperty("child", NullValueHandling = NullValueHandling.Ignore)] public int[]? Child;
        [JsonProperty("highlightTitle", NullValueHandling = NullValueHandling.Ignore)] public string? HighlightTitle;
        [JsonProperty("highlightRev")] public int HighlightRev;
        [JsonProperty("target")] public int Target;
        [JsonProperty("layer", NullValueHandling = NullValueHandling.Ignore)] public string? Layer;     // the layer's owner key
        [JsonProperty("session", NullValueHandling = NullValueHandling.Ignore)] public string? Session; // the asking session's id
        [JsonProperty("who", NullValueHandling = NullValueHandling.Ignore)] public string? Who;         // its label
    }

    /// <summary>One layer as the overlay draws it this frame (a consistent copy).</summary>
    internal sealed record HighlightLayerView(string Owner, int Slot, string? Who, List<HighlightBox> Boxes, string? Title, int? Current, DateTime Since, int Rev);

    internal sealed class HighlightState
    {
        public string Owner = "";           // layer key: session id | flow | queue | anon
        public int Slot;                    // small stable number for the overlay's window ids
        public List<HighlightTarget> Targets = new();
        public string? Title;
        public int? Current;              // order of the current step (sequences)
        public DateTime Since = DateTime.MinValue;
        public DateTime? Until;
        public int Rev;
        public List<HighlightBox> Boxes = new();
        public List<int> Missing = new();  // target indexes that resolved to nothing
        public bool Auto = true;            // sequences follow the user: a later step appearing, a met condition or the current target leaving moves on
        public Dictionary<int, bool> WasFound = new();   // order -> found at the last resolution
        public string? Session, Who;        // the session that set it (Sessions.cs), or the one a flow / queued step belongs to
        public int? StepGen;                // the guide step it goes with (GuideState.StepGen), null = none
        public int Pending => Targets.Count(t => t.Ask != null && t.Answer == null && !t.Expired);
    }

    private const string HlAnon = "anon", HlFlow = "flow", HlQueue = "queue";
    /// <summary>How long an unanswered ask or a context target stays without durationSec; the overlay fades it over the last HlFadeSec.</summary>
    internal const double HlDefaultLifeSec = 25, HlFadeSec = 1.5;
    private readonly object _hlLock = new();
    private readonly List<HighlightState> _hlLayers = new();   // drawing order: oldest layer first
    private int _hlRevSeq;                                        // revs are unique across layers (verdict ids hl<rev>.<index>)
    private DateTime _hlLastResolve = DateTime.MinValue;

    private HighlightState? HlLayerLocked(string owner)
    {
        foreach (var l in _hlLayers) if (l.Owner == owner) return l;
        return null;
    }

    private HighlightState HlLayerOrNewLocked(string owner)
    {
        if (HlLayerLocked(owner) is { } l) return l;
        var slot = 0;
        while (_hlLayers.Any(x => x.Slot == slot)) slot++;
        l = new HighlightState { Owner = owner, Slot = slot };
        _hlLayers.Add(l);
        return l;
    }

    /// <summary>The layer key of the request being served: its session, else anon (a connection that never said hello).</summary>
    private string HlOwnerOf(SessionInfo? me) => me?.Id ?? HlAnon;

    private string? ProcessHighlightMethod(string method, JToken? p) => method switch
    {
        "guide.highlight" => SafeMemory(() => HighlightSet(p)),
        "guide.highlight_advance" => SafeMemory(() => HighlightAdvance(HlOwnerOf(CurrentSession()))),
        "guide.highlight_state" => SafeMemory(() => HighlightStateJson(HlOwnerOf(CurrentSession()))),
        "guide.verdicts" => SafeMemory(() => VerdictsJson(p)),
        _ => null,
    };

    // ── Verdicts ─────────────────────────────────────────────────────

    private const int VerdictsKeep = 300;
    private readonly object _verdictLock = new();
    private readonly List<HighlightVerdict> _verdicts = new();
    private long _verdictSeq;
    private bool _verdictsLoaded;
    private string VerdictsFile => Path.Combine(_bridgeDir, "verdicts", "verdicts.jsonl");

    /// <summary>Continue from the file once: the sequence must not restart after a HUD restart (an agent may wait with since=).</summary>
    private void VerdictsLoadLocked()
    {
        if (_verdictsLoaded) return;
        _verdictsLoaded = true;
        try
        {
            if (!File.Exists(VerdictsFile)) return;
            foreach (var line in File.ReadLines(VerdictsFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                HighlightVerdict? v = null;
                try { v = JsonConvert.DeserializeObject<HighlightVerdict>(line); } catch { }
                if (v == null) continue;
                _verdicts.Add(v);
                if (v.Seq > _verdictSeq) _verdictSeq = v.Seq;
            }
            if (_verdicts.Count > VerdictsKeep) _verdicts.RemoveRange(0, _verdicts.Count - VerdictsKeep);
        }
        catch (Exception ex) { LogError($"[GuideHighlight] reading {VerdictsFile}: {ex.Message}"); }
    }

    /// <summary>
    /// Main thread (the overlay's Yes / No / Not sure click): record the user's answer to an asked target. Ignored when
    /// the target does not ask or was answered already. The highlight rev does not change (the overlay would re-enter).
    /// </summary>
    internal void HighlightAnswer(string owner, int targetIndex, string answer)
    {
        if (answer is not ("yes" or "no" or "skip")) return;
        HighlightVerdict v;
        lock (_hlLock)
        {
            if (HlLayerLocked(owner) is not { } hl || targetIndex < 0 || targetIndex >= hl.Targets.Count) return;
            var t = hl.Targets[targetIndex];
            if (t.Ask == null || t.Answer != null || t.Expired) return;
            t.Answer = answer;
            t.AnsweredAt = DateTime.UtcNow;
            for (var i = 0; i < hl.Boxes.Count; i++)
                if (hl.Boxes[i].TargetIndex == targetIndex) hl.Boxes[i] = hl.Boxes[i] with { Answer = answer };
            var box = hl.Boxes.FirstOrDefault(b => b.TargetIndex == targetIndex);
            v = new HighlightVerdict
            {
                Id = t.Key ?? $"hl{hl.Rev}.{targetIndex}", Key = t.Key, Ask = t.Ask, Label = t.Label, Answer = answer, At = t.AnsweredAt.Value,
                Rect = box.W > 0 ? [box.X, box.Y, box.W, box.H] : null,
                Item = t.Item, Path = t.Path, Text = t.Text, Panel = t.Panel, Child = t.Child,
                HighlightTitle = hl.Title, HighlightRev = hl.Rev, Target = targetIndex,
                Layer = hl.Owner, Session = hl.Session, Who = hl.Who,
            };
        }
        string line;
        lock (_verdictLock)
        {
            VerdictsLoadLocked();
            v.Seq = ++_verdictSeq;
            _verdicts.Add(v);
            if (_verdicts.Count > VerdictsKeep) _verdicts.RemoveRange(0, _verdicts.Count - VerdictsKeep);
            line = JsonConvert.SerializeObject(v);
        }
        var file = VerdictsFile;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.AppendAllText(file, line + Environment.NewLine); }
            catch (Exception ex) { LogError($"[GuideHighlight] writing {file}: {ex.Message}"); }
        });
        // While observing, the answer is an event on the agent lane (method user.verdict), so observe_wait can wake on it.
        try { if (Obs().Enabled) ObsEmit(new JObject { ["kind"] = "agent", ["method"] = "user.verdict", ["params"] = JObject.FromObject(v) }); } catch { }
    }

    /// <summary>
    /// guide.verdicts {since?, limit?}: the answers after since (oldest first, every layer's, each with its layer and who),
    /// the newest seq, what the caller's layers still ask (its own, plus a flow or queued step it started), and `layers`:
    /// every layer with its pending count (the combined view).
    /// </summary>
    private JObject VerdictsJson(JToken? p)
    {
        var me = CurrentSession();
        var owner = HlOwnerOf(me);
        var since = p?["since"]?.Value<long>() ?? 0;
        var limit = Math.Clamp(p?["limit"]?.Value<int>() ?? 100, 1, VerdictsKeep);
        JArray list; long seq;
        lock (_verdictLock)
        {
            VerdictsLoadLocked();
            seq = _verdictSeq;
            list = new JArray(_verdicts.Where(v => v.Seq > since).Take(limit).Select(JObject.FromObject));
        }
        var asked = new JArray();
        int pending = 0, pendingAll = 0, rev = 0;
        JArray layers;
        lock (_hlLock)
        {
            foreach (var l in _hlLayers)
            {
                pendingAll += l.Pending;
                if (!HlMine(l, owner, me?.Label)) continue;
                if (l.Owner == owner) rev = l.Rev;
                pending += l.Pending;
                for (var i = 0; i < l.Targets.Count; i++)
                {
                    var t = l.Targets[i];
                    if (t.Ask == null) continue;
                    asked.Add(new JObject
                    {
                        ["index"] = i, ["id"] = t.Key ?? $"hl{l.Rev}.{i}", ["key"] = t.Key, ["ask"] = t.Ask, ["label"] = t.Label,
                        ["answer"] = t.Answer, ["expired"] = t.Expired, ["onScreen"] = l.Boxes.Any(b => b.TargetIndex == i), ["layer"] = l.Owner, ["highlightRev"] = l.Rev,
                    });
                }
            }
            layers = HlLayersJsonLocked(owner, me?.Label);
        }
        return new JObject
        {
            ["ok"] = true, ["seq"] = seq, ["highlightRev"] = rev, ["verdicts"] = list, ["asked"] = asked, ["pending"] = pending,
            ["pendingAll"] = pendingAll, ["layer"] = owner, ["layers"] = layers, ["file"] = VerdictsFile,
        };
    }

    /// <summary>A layer counts as the caller's: its own, or a flow / queued step the same session started (matched by label).</summary>
    private static bool HlMine(HighlightState l, string owner, string? label) =>
        l.Owner == owner || (label != null && l.Owner is HlFlow or HlQueue && l.Who == label);

    /// <summary>The combined view: every layer, oldest first, with whose it is and what it still asks.</summary>
    private JArray HlLayersJsonLocked(string owner, string? label) => new(_hlLayers.Select(l => new JObject
    {
        ["layer"] = l.Owner, ["session"] = l.Session, ["who"] = l.Who, ["mine"] = HlMine(l, owner, label),
        ["rev"] = l.Rev, ["title"] = l.Title, ["current"] = l.Current, ["targets"] = l.Targets.Count,
        ["found"] = l.Boxes.Count, ["asked"] = l.Targets.Count(t => t.Ask != null), ["pending"] = l.Pending,
        ["until"] = l.Until?.ToString("O"), ["withStep"] = l.StepGen != null,
        ["expired"] = l.Targets.Count(t => t.Expired),
    }));

    /// <summary>guide.highlight from a request: the caller's own layer (its session, else anon).</summary>
    internal JObject HighlightSet(JToken? p)
    {
        var me = CurrentSession();   // before the lock (Sessions.cs lock order)
        return HighlightSetLayer(HlOwnerOf(me), me?.Id, me?.Label, p);
    }

    /// <summary>
    /// Replace or clear one layer. Other layers are never touched, so a question another session asked survives any call;
    /// only clear + force clears every layer (the user asked for a clean screen). The HUD's own highlights use the
    /// layers "flow" (a running guided flow) and "queue" (a queued step being recorded), owned by whoever started them.
    /// </summary>
    internal JObject HighlightSetLayer(string owner, string? session, string? who, JToken? p)
    {
        if (p?["clear"]?.Value<bool>() == true || p?["targets"] is not JArray arr)
        {
            lock (_hlLock)
            {
                if (p?["clear"]?.Value<bool>() == true && p["force"]?.Value<bool>() == true)
                {
                    var names = new JArray(_hlLayers.Select(l => l.Who ?? l.Owner));
                    var lost = _hlLayers.Sum(l => l.Pending);
                    _hlLayers.Clear();
                    return new JObject
                    {
                        ["ok"] = true, ["cleared"] = true, ["all"] = true, ["clearedLayers"] = names,   // names; "layers" is the combined view's objects
                        ["note"] = lost > 0 ? $"{lost} unanswered question(s) were removed with them." : null,
                    };
                }
                if (HlLayerLocked(owner) is { } mine) _hlLayers.Remove(mine);
                var others = _hlLayers.Where(l => l.Owner != owner).ToList();
                return new JObject
                {
                    ["ok"] = true, ["cleared"] = true, ["layer"] = owner,
                    ["note"] = others.Count > 0
                        ? $"Cleared yours; {others.Count} other layer(s) stay on screen ({string.Join(", ", others.Select(l => (l.Who ?? l.Owner) + (l.Pending > 0 ? $": {l.Pending} question(s) unanswered" : "")))}). clear + force clears them too."
                        : null,
                };
            }
        }
        var targets = new List<HighlightTarget>();
        var n = 0;
        foreach (var t in arr.OfType<JObject>())
        {
            if (++n > 40) break;
            if (ParseTarget(t) is not { } ht)
                return Err("bad_target", $"Target {n - 1}: needs item (name), text (element label), path (UI element), panel (+ child) or rect [x,y,w,h].");
            targets.Add(ht);
        }
        // A session's highlight goes with its own active guide step, if any (read before the lock: never nest the two).
        var step = session != null ? GuideStepNow() : default;
        var dur = p?["durationSec"]?.Value<double>();
        var now = DateTime.UtcNow;
        if (dur is not > 0)
            foreach (var t in targets)
                if (t.Ask != null || t.Tier == "context") t.EndsAt = now.AddSeconds(HlDefaultLifeSec);
        lock (_hlLock)
        {
            var hl = HlLayerOrNewLocked(owner);
            hl.Session = session; hl.Who = who;
            hl.StepGen = step.Active && step.Session == session ? step.Gen : null;
            hl.Targets = targets;
            hl.Boxes = new(); hl.Missing = new();
            hl.Auto = p?["auto"]?.Value<bool>() != false;
            hl.WasFound.Clear();
            hl.Title = Clip(p?["title"]?.ToString(), 80);
            var orders = targets.Where(t => t.Order != null).Select(t => t.Order!.Value).OrderBy(o => o).ToList();
            hl.Current = p?["current"]?.Type == JTokenType.Integer ? p["current"]!.Value<int>() : orders.Count > 0 ? orders[0] : null;
            hl.Since = now;
            hl.Until = dur is > 0 ? now.AddSeconds(Math.Min(dur.Value, 3600)) : null;
            hl.Rev = ++_hlRevSeq;
            _hlLastResolve = DateTime.MinValue;
        }
        ResolveHighlights(force: true);
        return HighlightStateJson(owner);
    }

    private JObject HighlightAdvance(string owner)
    {
        lock (_hlLock)
        {
            if (HlLayerLocked(owner) is not { } hl)
                return Err("no_highlight", _hlLayers.Count > 0
                    ? $"You have no highlight; the {_hlLayers.Count} on screen belong to {string.Join(", ", _hlLayers.Select(l => l.Who ?? l.Owner))} (advance moves only your own)."
                    : "No highlight is set.");
            var orders = hl.Targets.Where(t => t.Order != null).Select(t => t.Order!.Value).Distinct().OrderBy(o => o).ToList();
            if (orders.Count == 0) return Err("no_sequence", "Your highlight has no ordered targets.");
            var next = orders.FirstOrDefault(o => o > (hl.Current ?? int.MinValue), int.MaxValue);
            hl.Current = next == int.MaxValue ? null : next;   // past the last step: all done
            hl.Since = DateTime.UtcNow;
            hl.Rev = ++_hlRevSeq;
        }
        return HighlightStateJson(owner);
    }

    /// <summary>The caller's layer at the top level (as before layers existed), plus `layers`: every layer on screen.</summary>
    private JObject HighlightStateJson(string owner)
    {
        var label = CurrentWho();   // before the lock (Sessions.cs lock order)
        lock (_hlLock)
        {
            long verdictSeq;
            lock (_verdictLock) verdictSeq = _verdictSeq;
            var hl = HlLayerLocked(owner);
            var boxes = hl?.Boxes ?? HlNoBoxes;
            var others = _hlLayers.Count(l => l.Owner != owner);
            return new JObject
            {
                ["ok"] = true, ["layer"] = owner, ["rev"] = hl?.Rev ?? 0, ["title"] = hl?.Title, ["current"] = hl?.Current, ["who"] = hl?.Who,
                ["until"] = hl?.Until?.ToString("O"), ["withStep"] = hl?.StepGen != null,
                ["bigPanels"] = _hlPanels.Count,   // open NPC dialogue / large / fullscreen panels: boxes not inside them are covered or docked
                ["verdictSeq"] = verdictSeq,   // await_verdicts since=: answers after this call
                ["targets"] = new JArray((hl?.Targets ?? []).Select((t, i) => new JObject
                {
                    ["index"] = i, ["item"] = t.Item, ["path"] = t.Path, ["text"] = t.Text, ["within"] = t.Within, ["action"] = t.Action, ["panel"] = t.Panel, ["child"] = t.Child == null ? null : new JArray(t.Child), ["rect"] = t.Rect == null ? null : new JArray(t.Rect),
                    ["label"] = t.Label, ["tier"] = t.Tier, ["order"] = t.Order,
                    ["ask"] = t.Ask, ["key"] = t.Key, ["answer"] = t.Answer, ["answeredAt"] = t.AnsweredAt?.ToString("O"),
                    ["endsAt"] = t.EndsAt?.ToString("O"), ["expired"] = t.Expired,
                    ["found"] = boxes.Count(b => b.TargetIndex == i),
                })),
                ["boxes"] = new JArray(boxes.Select(b =>
                {
                    var o = new JObject { ["target"] = b.TargetIndex, ["rect"] = new JArray(b.X, b.Y, b.W, b.H) };
                    if (b.Covered) o["covered"] = true;
                    if (b.Docked) o["docked"] = true;
                    return o;
                })),
                ["worldMapPan"] = boxes.Any(b => b.Panned) ? _wmPanSource : null,
                ["layers"] = HlLayersJsonLocked(owner, label),
                ["note"] = hl == null
                    ? others > 0 ? $"You have no highlight; {others} other layer(s) are on screen (layers)." : null
                    : hl.Missing.Count > 0 ? $"{hl.Missing.Count} target(s) not on screen right now (panel closed, item not visible, or expired); they appear when visible." : null,
            };
        }
    }

    private static readonly List<HighlightLayerView> HlNoLayers = new();
    private static readonly List<HighlightBox> HlNoBoxes = new();

    /// <summary>A consistent copy of every layer with boxes, for drawing (taken once per frame, main thread; never mutate
    /// the result). Boxes on the world map move with its pan, read raw this frame: targets re-resolve only every 100 ms
    /// and the HUD's rects trail a drag.</summary>
    internal List<HighlightLayerView> HighlightSnapshot()
    {
        List<HighlightLayerView> views;
        lock (_hlLock)
        {
            if (_hlLayers.Count == 0) return HlNoLayers;
            views = new(_hlLayers.Count);
            foreach (var l in _hlLayers)
                if (l.Boxes.Count > 0) views.Add(new(l.Owner, l.Slot, l.Who, l.Boxes.ToList(), l.Title, l.Current, l.Since, l.Rev));
        }
        foreach (var v in views)
        {
            var boxes = v.Boxes;
            if (boxes.Any(b => b.Panned) && FreshPan(_wmLastPan) is { Addr: not 0 } now)
                for (var i = 0; i < boxes.Count; i++)
                    if (boxes[i].Panned)
                        boxes[i] = boxes[i] with { X = boxes[i].X + (now.PanX - boxes[i].PanX) * now.Sx, Y = boxes[i].Y + (now.PanY - boxes[i].PanY) * now.Sy };
        }
        return views;
    }

    /// <summary>Every layer's drawn boxes in one list (covered ones are not drawn; no copy for a single layer with none
    /// covered; never mutate the result).</summary>
    internal static List<HighlightBox> HighlightBoxesOf(List<HighlightLayerView> views)
    {
        if (views.Count == 0) return HlNoBoxes;
        if (views.Count == 1 && !views[0].Boxes.Exists(b => b.Covered)) return views[0].Boxes;
        var all = new List<HighlightBox>();
        foreach (var v in views) foreach (var b in v.Boxes) if (!b.Covered) all.Add(b);
        return all;
    }

    /// <summary>Main thread, from Render: re-resolve every layer's targets to screen rects every 100 ms (they follow the UI).</summary>
    private void ResolveHighlights(bool force = false)
    {
        List<(HighlightState layer, int rev, List<HighlightTarget> targets, long until)> work;
        var step = GuideStepNow();   // before the lock: never nest the guide and highlight locks
        lock (_hlLock)
        {
            var utc = DateTime.UtcNow;
            if (_hlLayers.Count == 0) return;
            // An unanswered ask's lifetime ends once answered (the answer and its mark stay); a context target's never does.
            foreach (var l in _hlLayers)
                foreach (var t in l.Targets)
                    if (!t.Expired && t.EndsAt is { } e && utc > e && (t.Answer == null || t.Tier == "context")) t.Expired = true;
            _hlLayers.RemoveAll(l => (l.Until is { } u && utc > u)                                         // durationSec ran out
                                     || (l.StepGen is { } g && (g != step.Gen || !step.Active))           // its guide step is over
                                     || (l.Targets.Count > 0 && l.Targets.TrueForAll(t => t.Expired)));   // everything in it expired
            if (_hlLayers.Count == 0) return;
            if (!force && (utc - _hlLastResolve).TotalMilliseconds < 100) return;
            _hlLastResolve = utc;
            work = new(_hlLayers.Count);
            foreach (var l in _hlLayers) if (l.Targets.Count > 0) work.Add((l, l.Rev, l.Targets.ToList(), l.Until?.Ticks ?? 0));
        }
        var panels = HlBigPanels();
        foreach (var (layer, rev, targets, until) in work)
        {
            var boxes = new List<HighlightBox>();
            var missing = new List<int>();
            var conditionMet = new Dictionary<int, bool>();   // target index -> its 'until' holds now
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                int before = boxes.Count;
                if (t.Expired) { missing.Add(i); continue; }
                var end = t.EndsAt is { } e && (t.Answer == null || t.Tier == "context") ? e.Ticks : 0;
                if (until != 0 && (end == 0 || until < end)) end = until;
                try
                {
                    var found = ResolveTarget(t);
                    var pan = _wmLastPan;   // the pan these boxes were placed with (HighlightSnapshot moves them by the change since)
                    foreach (var (_, x, y, w, h, panned) in found)
                    {
                        var (covered, docked) = HlPanelRule(panels, x, y, w, h, t);
                        boxes.Add(new(x, y, w, h, t.Label, t.Tier, t.Order, i, t.Action, t.Ask, t.Answer, panned, pan.PanX, pan.PanY, end, covered, docked));
                    }
                    // Checkbox state (PoE2: byte at +0x60A of the checkbox element, see finding ui.poe2.checkbox-checked).
                    if (t.UntilCond is "checked" or "unchecked" && found.FirstOrDefault(f => f.e != null).e is { } cb)
                        conditionMet[i] = IsChecked(cb) == (t.UntilCond == "checked");
                }
                catch { }
                if (boxes.Count == before) missing.Add(i);
            }
            lock (_hlLock)
            {
                // Replaced, advanced or cleared meanwhile: these boxes belong to targets that are gone.
                if (layer.Rev != rev || !_hlLayers.Contains(layer)) continue;
                layer.Boxes = boxes;
                layer.Missing = missing;
                if (layer.Auto) AutoAdvanceLocked(layer, targets, boxes, conditionMet);
            }
        }
    }

    // Open NPC dialogue / large / fullscreen panels as screen rects, refreshed with each resolve (main thread).
    private List<(float x, float y, float w, float h)> _hlPanels = new();

    /// <summary>The open big panels now: the NPC dialogue, IngameUi.LargePanels and FullscreenPanels (main thread, at the
    /// 100 ms resolve; IsVisible walks the parent chain, ~8 us per panel).</summary>
    private List<(float x, float y, float w, float h)> HlBigPanels()
    {
        var list = new List<(float, float, float, float)>();
        try
        {
            var ui = GameController.IngameState?.IngameUi;
            if (ui != null)
            {
                Add(ui.NpcDialog);
                if (ui.LargePanels is { } large) foreach (var e in large) Add(e);
                if (ui.FullscreenPanels is { } full) foreach (var e in full) Add(e);
            }
        }
        catch { }
        return _hlPanels = list;

        void Add(UiElement? e)
        {
            if (e == null || e.Address == 0 || !e.IsVisible) return;
            var r = e.GetClientRect();
            if (r.Width > 0 && r.Height > 0) list.Add((r.X, r.Y, r.Width, r.Height));
        }
    }

    /// <summary>
    /// A box against the open big panels: inside one (4 px slack) it is part of what the user is looking at and stays
    /// as it is. Otherwise, while one is open, a context / secondary box overlapping one is covered (hidden) and an
    /// unanswered ask is docked (a small pill at the screen edge).
    /// </summary>
    private static (bool covered, bool docked) HlPanelRule(List<(float x, float y, float w, float h)> panels, float x, float y, float w, float h, HighlightTarget t)
    {
        if (panels.Count == 0) return (false, false);
        const float slack = 4f;
        var overlaps = false;
        foreach (var p in panels)
        {
            if (x >= p.x - slack && y >= p.y - slack && x + w <= p.x + p.w + slack && y + h <= p.y + p.h + slack) return (false, false);
            if (x < p.x + p.w && x + w > p.x && y < p.y + p.h && y + h > p.y) overlaps = true;
        }
        return (overlaps && t.Tier is "context" or "secondary", t.Ask != null && t.Answer == null);
    }

    /// <summary>A string field, or null when missing, JSON null or empty (empty must never mean "match anything").</summary>
    private static string? S(JToken? v) => v == null || v.Type == JTokenType.Null || string.IsNullOrEmpty(v.ToString()) ? null : v.ToString();

    /// <summary>A target from JSON (highlights and flows share the shape); null when it names nothing to find.</summary>
    internal static HighlightTarget? ParseTarget(JObject t)
    {
        var ht = new HighlightTarget
        {
            Item = S(t["item"]), Path = S(t["path"]), Panel = S(t["panel"]), Text = S(t["text"]), Within = S(t["within"]),
            Action = t["action"]?.ToString() is "click" or "rightclick" ? t["action"]!.ToString() : null,
            UntilCond = t["until"]?.ToString() is "checked" or "unchecked" or "gone" ? t["until"]!.ToString() : null,
            ClipTo = S(t["clipTo"]),
            Rel = t["rel"] is JArray rel ? rel.Select(v => v.ToString()).ToArray() : null,
            Child = t["child"] is JArray ch ? ch.Select(v => v.Value<int>()).ToArray() : t["child"]?.ToString() is { Length: > 0 } cs ? cs.Split('.', ',').Select(int.Parse).ToArray() : null,
            Label = Clip(t["label"]?.ToString(), 40),
            Tier = t["tier"]?.ToString() is "secondary" or "context" ? t["tier"]!.ToString() : "primary",
            Order = t["order"]?.Type == JTokenType.Integer ? t["order"]!.Value<int>() : null,
            Ask = Clip(S(t["ask"]), 80),
            Key = Clip(S(t["key"]), 80),
        };
        if (t["rect"] is JArray r && r.Count == 4) ht.Rect = r.Select(v => v.Value<float>()).ToArray();
        return ht.Item == null && ht.Path == null && ht.Rect == null && ht.Panel == null && ht.Text == null ? null : ht;
    }

    /// <summary>Follow "^" (parent) / "n" (child n) from an element; null when a step doesn't exist.</summary>
    private static UiElement? Navigate(UiElement e, string[] rel)
    {
        UiElement? cur = e;
        foreach (var s in rel)
        {
            if (cur == null) return null;
            if (s == "^") cur = cur.Parent;
            else if (int.TryParse(s, out var i)) { var cs = cur.Children; cur = i >= 0 && i < cs.Count ? cs[i] : null; }
            else return null;
        }
        return cur;
    }

    /// <summary>A target's visible boxes now (element when it is a UI element), clipped to its clipTo container if any.</summary>
    /// <summary>Why the last ResolveTarget / condition came up empty or false: the FIRST link that failed, by name (main thread only).
    /// Offsets and UI trees change with patches: a failure must name its broken link, not surface ten calls later.</summary>
    internal string? Why;

    /// <remarks><c>panned</c>: the box sits on the world map's pan container, placed with the pan read at resolve time;
    /// <see cref="HighlightSnapshot"/> moves it with the pan every frame.</remarks>
    internal List<(UiElement? e, float x, float y, float w, float h, bool panned)> ResolveTarget(HighlightTarget t)
    {
        var list = new List<(UiElement?, float, float, float, float, bool)>();
        Why = null;
        if (t.Rect != null) list.Add((null, t.Rect[0], t.Rect[1], t.Rect[2], t.Rect[3], false));
        else if (t.Item != null) foreach (var r in FindItemRects(t.Item)) list.Add((null, r.x, r.y, r.w, r.h, false));
        else
        {
            List<UiElement> elements;
            if (t.Text != null) elements = FindByText(t.Text, t.Within);
            else if (t.Panel != null) elements = PanelChild(t.Panel, t.Child) is { } pe ? [pe] : [];
            else
            {
                var obj = new ExpressionWalker(GameController).Resolve(t.Path!, out var err);
                elements = obj is UiElement we ? [we] : [];
                if (err != null) Why = $"path {t.Path}: {err}";
                else if (obj == null) Why = $"path {t.Path}: resolved to null";
                else if (obj is not UiElement) Why = $"path {t.Path}: is a {obj.GetType().Name}, not a UI element";
            }
            if (t.Rel != null && elements.Count > 0)
            {
                var moved = elements.Select(e => Navigate(e, t.Rel)).Where(e => e != null).Select(e => e!).ToList();
                if (moved.Count == 0) Why = $"rel [{string.Join(",", t.Rel)}] from {elements.Count} match(es): a step does not exist (UI tree changed?)";
                elements = moved;
            }
            if (elements.Count > 0 && elements.All(e => !e.IsVisible))
                Why ??= t.Rel != null ? $"{elements.Count} element(s) after rel [{string.Join(",", t.Rel)}], none visible" : $"{elements.Count} match(es), none visible";
            var pan = elements.Count > 0 ? WorldMapPan() : default;
            foreach (var e in elements)
            {
                if (!e.IsVisible) continue;
                var r = e.GetClientRect();
                if (r.Width <= 0 || r.Height <= 0) continue;
                var panned = OnWorldMap(e, pan, out var x, out var y);
                list.Add((e, panned ? x : r.X, panned ? y : r.Y, r.Width, r.Height, panned));
            }
        }
        if (t.ClipTo != null)
        {
            // Keep only boxes whose centre lies inside the container (a tab scrolled out of the row is not "in view").
            if (new ExpressionWalker(GameController).Resolve(t.ClipTo, out _) is UiElement c && c.IsVisible)
            {
                var cr = c.GetClientRect();
                if (OnWorldMap(c, WorldMapPan(), out var cx, out var cy)) { cr.X = cx; cr.Y = cy; }
                list.RemoveAll(b => b.Item2 + b.Item4 / 2 < cr.X || b.Item2 + b.Item4 / 2 > cr.X + cr.Width
                                    || b.Item3 + b.Item5 / 2 < cr.Y || b.Item3 + b.Item5 / 2 > cr.Y + cr.Height);
            }
            else { list.Clear(); Why = $"clipTo {t.ClipTo}: container not found or not visible"; }
            if (list.Count == 0) Why ??= "outside the clipTo container (e.g. scrolled out of view)";
        }
        if (list.Count == 0) Why ??= t.Item != null ? $"no visible item matches '{t.Item}' (inventory / visible stash tab)" : "not found";
        return list;
    }


    /// <summary>The world map's pan as one resolve (or one frame) sees it: default when the map is closed or off.</summary>
    internal readonly record struct WorldMapPanState(long Addr, float ParentX, float ParentY, float Sx, float Sy, float PanX, float PanY);

    // Pan calibration, per opening of the map (the container's address): the offset of the two pan floats inside
    // WorldMap[0], found by matching its Position at rest. Main thread only.
    private long _wmCalAddr;
    private int? _wmCalOffset;
    private string? _wmCalBroken;
    private Vector2N _wmCalLastPos;
    private DateTime _wmCalSince;
    /// <summary>Where the pan comes from now, for guide.highlight_state (null: no box on the world map).</summary>
    private string? _wmPanSource;

    /// <summary>
    /// The world map (Travel and waypoint Teleport views) pans its container WorldMap[0] by screen width / 2560 horizontally
    /// (the game's 2560x1600 layout stretched to the screen) and screen height / 1600 vertically, but the HUD scales every
    /// rect by H/1600 on both axes, so boxes under it drift sideways by pan.X * (W/2560 - H/1600) (finding
    /// ui.worldmap.pan-x-underscaled; 31.7 px at the Act 2 map's pan limit on 1920x1080). And GetClientRect() sums cached
    /// element memory, which trails a drag on both axes (Whats A Route: 9.4 px avg, 52 px max while dragging). So the pan
    /// is read raw (leaf backend, no cache) from WorldMap[0] + the calibrated offset; until calibrated, or when that is
    /// broken (reason in <see cref="_wmPanSource"/>), the cached Position stands in. Per game: <see cref="WorldMapPanOffset"/>.
    /// </summary>
    private WorldMapPanState WorldMapPan()
    {
        if (WorldMapPanOffset is not { } hint) return default;
        var wm = GameController.IngameState?.IngameUi?.WorldMap;
        if (wm == null || wm.Address == 0 || !wm.IsVisible || wm.ChildCount == 0) return default;
        var pan = wm.GetChildAtIndex(0);
        if (pan == null || pan.Address == 0) return default;
        var cam = GameController.IngameState!.Camera;
        var sy = cam.Height / 1600f;
        if (sy <= 0) return default;
        var pos = pan.Position;
        if (_wmCalAddr != pan.Address) { _wmCalAddr = pan.Address; _wmCalOffset = null; _wmCalBroken = null; _wmCalSince = DateTime.UtcNow; _wmCalLastPos = new(float.NaN); }
        if (_wmCalOffset == null && _wmCalBroken == null) CalibratePan(pan.Address, new Vector2N(pos.X, pos.Y), hint);
        _wmCalLastPos = new(pos.X, pos.Y);
        var r = wm.GetClientRect();
        var fresh = FreshPan(new(pan.Address, r.X, r.Y, cam.Width / 2560f, sy, pos.X, pos.Y));
        _wmPanSource = _wmCalOffset is { } off ? $"raw WorldMap[0] +0x{off:X}"
            : $"cached WorldMap[0].Position (trails drags): {_wmCalBroken ?? "calibrating, waiting for the map to rest"}";
        return _wmLastPan = fresh;
    }

    /// <summary>The pan the last resolve placed world-map boxes with.</summary>
    private WorldMapPanState _wmLastPan;

    /// <summary>At rest (Position unchanged since the last resolve), find the two pan floats in WorldMap[0]: the per-game
    /// hint first, else a scan of its first 0x400 bytes. Gives up after 3 s with the reason (the offset moved).</summary>
    private void CalibratePan(long addr, Vector2N pos, int hint)
    {
        static bool Same(float a, float b) => MathF.Abs(a - b) < 0.01f;
        if (!Same(pos.X, _wmCalLastPos.X) || !Same(pos.Y, _wmCalLastPos.Y))
        {
            if ((DateTime.UtcNow - _wmCalSince).TotalSeconds > 30) _wmCalBroken = "the map never came to rest for 30 s";
            return;
        }
        if (Same(RawRead<float>(addr + hint), pos.X) && Same(RawRead<float>(addr + hint + 4), pos.Y)) { _wmCalOffset = hint; return; }
        if (pos.X != 0 || pos.Y != 0)   // (0, 0) matches any zeroed pair: only the hint can be trusted then
            for (var off = 0; off + 8 <= 0x400; off += 4)
                if (Same(RawRead<float>(addr + off), pos.X) && Same(RawRead<float>(addr + off + 4), pos.Y)) { _wmCalOffset = off; return; }
        if ((DateTime.UtcNow - _wmCalSince).TotalSeconds > 3)
            _wmCalBroken = $"no float pair equal to WorldMap[0].Position ({pos.X:F1}, {pos.Y:F1}) in its first 0x400 bytes " +
                           $"(hint +0x{hint:X} reads {RawRead<float>(addr + hint):F1}, {RawRead<float>(addr + hint + 4):F1}): the pan moved";
    }

    /// <summary><paramref name="p"/> with the pan read raw now when calibrated (else as given).</summary>
    private WorldMapPanState FreshPan(WorldMapPanState p) =>
        p.Addr != 0 && p.Addr == _wmCalAddr && _wmCalOffset is { } off
            ? p with { PanX = RawRead<float>(p.Addr + off), PanY = RawRead<float>(p.Addr + off + 4) }
            : p;

    /// <summary>
    /// Whether <paramref name="e"/> is inside the pan container, and if so its true top-left: parentX + pan.X * W/2560 +
    /// local.X * H/1600, parentY + (pan.Y + local.Y) * H/1600, where local = the Positions from e up to (not including)
    /// WorldMap[0] (fixed per node, so the cache doesn't matter there).
    /// </summary>
    private static bool OnWorldMap(UiElement e, WorldMapPanState pan, out float x, out float y)
    {
        x = y = 0;
        if (pan.Addr == 0) return false;
        float lx = 0, ly = 0;
        UiElement? p = e;
        for (var depth = 0; p != null && p.Address != 0 && depth < 48; depth++, p = p.Parent)
        {
            if (p.Address == pan.Addr)
            {
                x = pan.ParentX + pan.PanX * pan.Sx + lx * pan.Sy;
                y = pan.ParentY + (pan.PanY + ly) * pan.Sy;
                return true;
            }
            var lp = p.Position;
            lx += lp.X;
            ly += lp.Y;
        }
        return false;
    }
    /// <summary>PoE2 checkbox state: byte at +0x60A of the checkbox element (finding ui.poe2.checkbox-checked).</summary>
    internal bool IsChecked(UiElement checkbox) => GameController.Memory.Read<byte>(checkbox.Address + 0x60A) == 1;

    /// <summary>
    /// Sequences follow what the user does: a later step whose target appears (a dialog opened) becomes current; the
    /// current step is done when its 'until' holds (checked / unchecked) or, for 'gone' or no condition, when its target
    /// disappears (the dialog closed after Confirm). Past the last step: all done.
    /// </summary>
    private void AutoAdvanceLocked(HighlightState hl, List<HighlightTarget> targets, List<HighlightBox> boxes, Dictionary<int, bool> conditionMet)
    {
        var orders = targets.Where(t => t.Order != null).Select(t => t.Order!.Value).Distinct().OrderBy(o => o).ToList();
        if (orders.Count == 0) return;
        var found = orders.ToDictionary(o => o, o => boxes.Any(b => b.Order == o));
        var first = hl.WasFound.Count == 0;
        int? next = null;
        if (!first && hl.Current is { } cur)
        {
            // 1. A later step appeared.
            foreach (var o in orders.Where(o => o > cur))
                if (found[o] && !hl.WasFound.GetValueOrDefault(o)) { next = o; break; }
            // 2. The current step's condition holds, or its target left.
            if (next == null)
            {
                var curTargets = targets.Select((t, i) => (t, i)).Where(x => x.t.Order == cur).ToList();
                var met = curTargets.Any(x => conditionMet.GetValueOrDefault(x.i));
                var gone = hl.WasFound.GetValueOrDefault(cur) && !found[cur] && curTargets.All(x => x.t.UntilCond is null or "gone");
                if (met || gone) next = orders.FirstOrDefault(o => o > cur, int.MinValue) is var n && n != int.MinValue ? n : -1;
            }
            if (next != null)
            {
                hl.Current = next == -1 ? null : next;   // -1: past the last step, all done
                hl.Since = DateTime.UtcNow;
                hl.Rev = ++_hlRevSeq;
            }
        }
        hl.WasFound = found;
    }

    /// <summary>Visible elements whose text is exactly <paramref name="text"/> (case-insensitive), in the panel containing
    /// <paramref name="within"/> or in every visible top-level panel. Bounded walk.</summary>
    private List<UiElement> FindByText(string text, string? within)
    {
        var roots = new List<UiElement>();
        if (within != null)
        {
            // within: a walker path (starts with GameController) or text inside a top-level panel
            if (within.StartsWith("GameController", StringComparison.Ordinal)) { if (new ExpressionWalker(GameController).Resolve(within, out _) is UiElement w && w.IsVisible) roots.Add(w); }
            else if (PanelChild(within, null) is { } p) roots.Add(p);
        }
        else if (GameController.IngameState?.IngameUi?.Children is { } kids)
            roots.AddRange(kids.Where(k => k != null && k.Address != 0 && k.IsVisibleLocal));
        if (roots.Count == 0)
        {
            Why = within == null ? "no visible top-level panel" : within.StartsWith("GameController", StringComparison.Ordinal)
                ? $"within {within}: not found or not visible" : $"within: no open panel contains the text '{within}'";
            return [];
        }
        var hits = new List<UiElement>();
        var queue = new Queue<(UiElement e, int d)>(roots.Select(r => (r, 0)));
        int visited = 0;
        while (queue.Count > 0 && visited++ < 6000 && hits.Count < 10)
        {
            var (e, d) = queue.Dequeue();
            string? t = null;
            try { t = e.Text; } catch { }
            if (t != null && string.Equals(t.Trim(), text, StringComparison.OrdinalIgnoreCase)) hits.Add(e);
            if (d >= 16) continue;
            try { foreach (var c in e.Children) if (c != null && c.IsVisibleLocal) queue.Enqueue((c, d + 1)); } catch { }
        }
        if (hits.Count == 0) Why = $"text '{text}' not found in {(within ?? "the open panels")} ({visited} visible elements searched)";
        return hits;
    }

    // Panel lookups by text: address -> texts, refreshed when the panel's address changes (cheap: only visible top-level panels).
    private readonly Dictionary<long, List<string>> _hlPanelTexts = new();

    /// <summary>The visible top-level IngameUi panel whose texts contain <paramref name="text"/>, then its child at <paramref name="child"/>.</summary>
    private UiElement? PanelChild(string text, int[]? child)
    {
        var kids = GameController.IngameState?.IngameUi?.Children;
        if (kids == null) { Why = "IngameUi.Children unavailable"; return null; }
        string? brokenAt = null;
        foreach (var p in kids)
        {
            if (p == null || p.Address == 0 || !p.IsVisibleLocal) continue;
            if (!_hlPanelTexts.TryGetValue(p.Address, out var texts))
            {
                if (_hlPanelTexts.Count > 200) _hlPanelTexts.Clear();
                _hlPanelTexts[p.Address] = texts = PanelTexts(p, 6, 12);
            }
            if (!texts.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase))) continue;
            UiElement? e = p;
            var walked = new List<int>();
            foreach (var i in child ?? [])
            {
                var cs = e.Children;
                if (i < 0 || i >= cs.Count || cs[i] == null)
                {
                    brokenAt = $"panel '{text}' is open, but child [{string.Join(",", walked.Append(i))}] does not exist " +
                               $"(element at [{string.Join(",", walked)}] has {cs.Count} children): its layout changed";
                    e = null;
                    break;
                }
                walked.Add(i);
                e = cs[i];
            }
            if (e != null) return e;
        }
        Why = brokenAt ?? $"panel '{text}' is not open";
        return null;
    }

    /// <summary>Visible items whose base name, unique name or metadata path contains the query (player inventory, visible stash tab).</summary>
    private IEnumerable<(float x, float y, float w, float h)> FindItemRects(string query)
    {
        // Typed access (no reflection: NormalInventoryItem declares Item more than once, so GetProperty("Item") throws).
        var ui = GameController.IngameState.IngameUi;
        var found = new List<(float, float, float, float)>();
        try
        {
            var inv = ui.InventoryPanel[InventoryIndex.PlayerInventory];
            if (inv?.IsVisible == true && inv.VisibleInventoryItems is { } items)
                foreach (var it in items) AddIfMatch(it?.Item, it?.GetClientRect());
        }
        catch { }
        try
        {
            var st = ui.StashElement;
            if (st?.IsVisible == true && st.VisibleStash?.VisibleInventoryItems is { } items)
                foreach (var it in items) AddIfMatch(it?.Item, it?.GetClientRect());
        }
        catch { }
        return found;

        void AddIfMatch(Entity? entity, RectangleF? rect)
        {
            if (entity == null || rect is not { } r || r.Width <= 0 || r.Height <= 0) return;
            var name = entity.GetComponent<Base>()?.Name ?? "";
            var unique = "";
            try { unique = entity.GetComponent<Mods>()?.UniqueName ?? ""; } catch { }
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase) || unique.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (entity.Path?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                found.Add((r.X, r.Y, r.Width, r.Height));
        }
    }

    /// <summary>Drawn by GuideHighlightDraw.cs: every layer together, each with its own title and sequence.</summary>
    partial void DrawHighlightsImpl(List<HighlightLayerView> layers);

    private void DrawHighlights()
    {
        ResolveHighlights();
        var layers = HighlightSnapshot();
        if (layers.Count == 0) return;
        DrawHighlightsImpl(layers);
    }
}
