using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Plugin pub/sub: other plugins publish events through the bridge, agents and dispatchers read and claim them over TCP,
/// and replies flow back to the plugins that subscribed. The first user is Whats A Route's F8 feedback loop: a press is a
/// "feedback" event, the MCP's dispatcher claims it once and starts a fresh Claude Code session, whose answer comes back
/// as "feedback.reply" and shows above the route card.
///
/// In-process (PluginBridge, no compile-linking; the delegates use BCL types only):
///   AIBridge.Publish   Func&lt;string kind, string json, string&gt;  -> {ok, seq, subscribers: [{subscriber, who, seenAgoSec}]}
///                      (the TCP subscribers that read this kind in the last 20 s: none means nobody will handle it now).
///                      After the bridge reloads, the old delegate answers bridge_reloaded: look the method up again.
///   AIBridge.Subscribe Func&lt;string subscriber, string kindsCsv, Action&lt;string&gt;?, string&gt;: the handler gets each matching
///                      event as JSON, on the thread that published it (a bridge request thread or the publisher's):
///                      keep it cheap and thread-safe (queue it). The same subscriber replaces (a reloaded plugin); a null
///                      handler unsubscribes (call it from OnClose); a handler that throws is dropped.
/// TCP:
///   pubsub.events {since?, kinds?, limit?, subscriber?}   events after since, each with its claims; next = the since for
///                                                         the next page; subscriber marks the caller as listening to kinds
///                                                         (Publish reports listeners of the kind by name only)
///   pubsub.claim {seq, stage?: handle, by?, ttlSec?}      exactly once per (event, stage): the second caller gets claimed.
///                                                         ttlSec: the claim lapses then (a claimer that died), else forever
///   pubsub.release {seq, stage?, by?, reason?, force?}    hands a claim back; only its holder (by), unless force
///   pubsub.publish {kind, data, source?}                  an agent's event (a reply), delivered to the plugins' subscriptions
///   pubsub.status {}
/// Every event {seq, kind, at, t, source, data} is appended to &lt;BridgeDirectory&gt;\pubsub\events.jsonl (the sequence and
/// the claims continue from it after a restart: a press made just before a HUD restart is still claimable) and, while
/// observing, copied onto the observer timeline as kind = the event's kind with pubSeq (same clock as hud / agent). The
/// observer's own kinds and pubsub.* are reserved.
/// </summary>
public partial class WhatsAnAiBridge
{
    public const string PublishMethodName = "AIBridge.Publish", SubscribeMethodName = "AIBridge.Subscribe";
    private const int PubRing = 2000;
    private const long PubTailBytes = 8L * 1024 * 1024;
    private const double PubListenerFreshSec = 20;
    private const int PubObserverDataMax = 4000;
    private static readonly HashSet<string> PubReservedKinds = new(StringComparer.Ordinal)
        { "layer", "layer.noisy", "ui", "area", "level", "entity", "hud", "agent", "server", "server.noisy" };

    private readonly object _pubLock = new();
    private readonly List<JObject> _pubEvents = new();
    private readonly Dictionary<long, Dictionary<string, JObject>> _pubClaims = new();   // event seq -> stage -> {by, at, stage, claimSeq, until?}
    private readonly Dictionary<string, (HashSet<string> kinds, Action<string> handler)> _pubLocal = new();
    private readonly Dictionary<string, (HashSet<string> kinds, DateTime seen, string? who)> _pubRemote = new();
    private long _pubSeq, _pubAppends;
    private bool _pubLoaded, _pubClosed;
    private string? _pubWhy;

    private string PubFile => Path.Combine(_bridgeDir, "pubsub", "events.jsonl");

    /// <summary>From Initialise: the log read off the main thread. The two PluginBridge methods other plugins call
    /// (PubPublishFromPlugin, PubSubscribeLocal) are [BridgeExport]s, saved by BridgeExports.Register in Initialise.</summary>
    private void PubRegister()
    {
        System.Threading.ThreadPool.QueueUserWorkItem(_ => { lock (_pubLock) PubLoadLocked(); });
    }

    /// <summary>From OnClose / hot reload: delegates other plugins still hold answer bridge_reloaded instead of writing a log
    /// with a sequence the next load also hands out.</summary>
    private void PubClose()
    {
        lock (_pubLock) { _pubClosed = true; _pubLocal.Clear(); }
    }

    private string? ProcessPubSubMethod(string method, JToken? p) => method switch
    {
        "pubsub.events" => SafeMemory(() => PubEvents(p)),
        "pubsub.claim" => SafeMemory(() => PubClaim(p)),
        "pubsub.release" => SafeMemory(() => PubRelease(p)),
        "pubsub.publish" => SafeMemory(() => PubPublishRemote(p)),
        "pubsub.status" => SafeMemory(PubStatus),
        _ => null,
    };

    /// <summary>Continue from the file once: the sequence and the claims must survive a HUD restart.</summary>
    private void PubLoadLocked()
    {
        if (_pubLoaded) return;
        _pubLoaded = true;
        try
        {
            // The rotated log fills what the current one lacks (right after a rotation the current one is near empty).
            var currentBytes = File.Exists(PubFile) ? new FileInfo(PubFile).Length : 0;
            var rotated = Path.ChangeExtension(PubFile, ".1.jsonl");
            var lines = Enumerable.Empty<string>();
            if (currentBytes < PubTailBytes && File.Exists(rotated)) lines = lines.Concat(JournalTail(rotated, PubTailBytes - currentBytes));
            if (currentBytes > 0) lines = lines.Concat(JournalTail(PubFile, PubTailBytes));
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject e;
                try { e = JObject.Parse(line); } catch { continue; }
                var seq = e["seq"]?.Value<long>() ?? 0;
                if (seq > _pubSeq) _pubSeq = seq;
                PubApplyClaimLocked(e);
                _pubEvents.Add(e);
                if (_pubEvents.Count > PubRing * 2) _pubEvents.RemoveRange(0, PubRing);
            }
            PubTrimLocked();
        }
        catch (Exception ex) { _pubWhy = $"reading {PubFile}: {ex.Message}"; LogFault($"[PubSub] {_pubWhy}"); }
    }

    /// <summary>The ring keeps the newest events; claims of events that left it go with them.</summary>
    private void PubTrimLocked()
    {
        if (_pubEvents.Count > PubRing) _pubEvents.RemoveRange(0, _pubEvents.Count - PubRing);
        if (_pubEvents.Count == 0) return;
        var oldest = _pubEvents[0]["seq"]!.Value<long>();
        foreach (var seq in _pubClaims.Keys.Where(s => s < oldest).ToList()) _pubClaims.Remove(seq);
    }

    private void PubApplyClaimLocked(JObject e)
    {
        var kind = e["kind"]?.ToString();
        if (kind is not ("pubsub.claim" or "pubsub.release")) return;
        var d = e["data"] as JObject;
        if (d?["ref"]?.Type != JTokenType.Integer) return;
        var seq = d["ref"]!.Value<long>();
        var stage = d["stage"]?.ToString() ?? "handle";
        if (kind == "pubsub.claim")
        {
            if (!_pubClaims.TryGetValue(seq, out var stages)) _pubClaims[seq] = stages = new Dictionary<string, JObject>();
            stages[stage] = new JObject { ["by"] = d["by"], ["at"] = e["at"], ["stage"] = stage, ["claimSeq"] = e["seq"], ["until"] = d["until"] };
        }
        else if (_pubClaims.TryGetValue(seq, out var stages))
        {
            stages.Remove(stage);
            if (stages.Count == 0) _pubClaims.Remove(seq);
        }
    }

    /// <summary>A live claim of (seq, stage): null when there is none or its ttl ran out.</summary>
    private JObject? PubHeldLocked(long seq, string stage) =>
        _pubClaims.TryGetValue(seq, out var stages) && stages.TryGetValue(stage, out var c) && !PubLapsed(c) ? c : null;

    private static bool PubLapsed(JObject claim) =>
        claim["until"]?.Type == JTokenType.String && DateTime.TryParse(claim["until"]!.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var u)
        && u.ToUniversalTime() < DateTime.UtcNow;

    /// <summary>Adds an event (seq, time, file) and returns it with the local handlers to call after the lock.</summary>
    private (JObject e, List<(string name, Action<string> handler)> handlers) PubAddLocked(string kind, JObject data, string? source)
    {
        PubLoadLocked();
        var e = new JObject
        {
            ["seq"] = ++_pubSeq, ["kind"] = kind, ["at"] = DateTime.UtcNow.ToString("O"),
            ["t"] = Math.Round(ObsClock.Elapsed.TotalMilliseconds, 1), ["source"] = source, ["data"] = data,
        };
        _pubEvents.Add(e);
        PubApplyClaimLocked(e);
        PubTrimLocked();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PubFile)!);
            if (++_pubAppends % 200 == 0 && File.Exists(PubFile) && new FileInfo(PubFile).Length > PubTailBytes)
                File.Move(PubFile, Path.ChangeExtension(PubFile, ".1.jsonl"), overwrite: true);   // one generation kept, like the observer journal
            File.AppendAllText(PubFile, e.ToString(Formatting.None) + "\n");   // rare events (a few a minute): a synchronous append keeps the order
            _pubWhy = null;
        }
        catch (Exception ex) { _pubWhy = $"writing {PubFile}: {ex.Message}"; }
        var handlers = _pubLocal.Where(kv => kv.Value.kinds.Contains(kind) || kv.Value.kinds.Contains("*")).Select(kv => (kv.Key, kv.Value.handler)).ToList();
        return (e, handlers);
    }

    /// <summary>Publish, then outside the lock the observer copy and the local handlers.</summary>
    private JObject PubPublish(string kind, JObject data, string? source)
    {
        JObject e; List<(string name, Action<string> handler)> handlers; JArray listeners, plugins;
        lock (_pubLock)
        {
            (e, handlers) = PubAddLocked(kind, data, source);
            listeners = PubListenersLocked(kind);
            // The plugins it reaches by name (a "*" watcher handles nothing), so a publisher can tell "no plugin listens".
            plugins = new JArray(_pubLocal.Where(kv => kv.Value.kinds.Contains(kind)).Select(kv => kv.Key));
            // Presses, replies and route events build the F8 work items (Work.cs; applied under the sessions lock). Enqueued
            // under this lock (lock-free queue, no other lock taken): the inbox is in seq order, and a press is in it before
            // any poller can read it and reply.
            WorkEnqueue(kind, e);
        }
        try
        {
            if (Obs().Enabled)
            {
                var text = data.ToString(Formatting.None);
                ObsEmit(new JObject
                {
                    ["kind"] = kind, ["pubSeq"] = e["seq"], ["source"] = source,
                    ["data"] = text.Length <= PubObserverDataMax ? data.DeepClone() : new JObject { ["clipped"] = true, ["bytes"] = text.Length },
                });
            }
        }
        catch { }
        var json = e.ToString(Formatting.None);
        foreach (var (name, handler) in handlers)
        {
            try { handler(json); }
            catch (Exception ex)
            {
                lock (_pubLock) { if (_pubLocal.TryGetValue(name, out var cur) && cur.handler == handler) _pubLocal.Remove(name); }
                LogFault($"[PubSub] subscriber {name} threw on {kind} and was dropped (it subscribes again when it loads): {ex.Message}");
            }
        }
        return new JObject { ["ok"] = true, ["seq"] = e["seq"], ["kind"] = kind, ["subscribers"] = listeners, ["plugins"] = plugins, ["fileError"] = _pubWhy };
    }

    private JArray PubListenersLocked(string kind)
    {
        var now = DateTime.UtcNow;
        return new JArray(_pubRemote
            .Where(kv => (now - kv.Value.seen).TotalSeconds <= PubListenerFreshSec && kv.Value.kinds.Contains(kind))   // by name: a watcher of every kind handles nothing
            .Select(kv => new JObject { ["subscriber"] = kv.Key, ["who"] = kv.Value.who, ["seenAgoSec"] = Math.Round((now - kv.Value.seen).TotalSeconds, 1) }));
    }

    private static HashSet<string> PubKinds(IEnumerable<string>? kinds)
    {
        var set = new HashSet<string>((kinds ?? []).Select(k => k.Trim()).Where(k => k.Length > 0), StringComparer.Ordinal);
        if (set.Count == 0) set.Add("*");
        return set;
    }

    private static string? PubKindRefused(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? "kind is empty"
        : kind.StartsWith("pubsub.", StringComparison.Ordinal) ? $"'{kind}': pubsub.* kinds are the bridge's own"
        : PubReservedKinds.Contains(kind) ? $"'{kind}' is one of the observer's own kinds ({string.Join(", ", PubReservedKinds)})"
        : null;

    // ── PluginBridge ─────────────────────────────────────────────────

    #pragma warning disable HXB007 // spans the method name (where HXB007 is reported); "AIBridge.*" predates the PluginName.Method convention, other plugins look it up by this string
    [BridgeExport(PublishMethodName)]
    private string PubPublishFromPlugin(string kind, string json)
    #pragma warning restore HXB007
    {
        try
        {
            if (_pubClosed) return Err("bridge_reloaded", $"This {PublishMethodName} belongs to a bridge load that has ended: look the method up again.").ToString(Formatting.None);
            if (PubKindRefused(kind) is { } refused) return Err("bad_kind", $"{PublishMethodName}: {refused}.").ToString(Formatting.None);
            JObject data;
            try { data = string.IsNullOrWhiteSpace(json) ? new JObject() : JToken.Parse(json) is JObject o ? o : new JObject { ["value"] = JToken.Parse(json) }; }
            catch (JsonException ex) { return Err("bad_json", $"{PublishMethodName}({kind}): json does not parse: {ex.Message}").ToString(Formatting.None); }
            return PubPublish(kind, data, data["source"]?.ToString() ?? "plugin").ToString(Formatting.None);
        }
        catch (Exception ex) { return Err("publish_failed", ex.Message).ToString(Formatting.None); }
    }

    #pragma warning disable HXB007 // spans the method name (where HXB007 is reported); "AIBridge.*" predates the PluginName.Method convention, other plugins look it up by this string
    [BridgeExport(SubscribeMethodName)]
    private string PubSubscribeLocal(string subscriber, string kindsCsv, Action<string>? handler)
    #pragma warning restore HXB007
    {
        if (string.IsNullOrWhiteSpace(subscriber))
            return Err("bad_subscription", $"{SubscribeMethodName}(subscriber, kindsCsv, handler): subscriber is required.").ToString(Formatting.None);
        lock (_pubLock)
        {
            if (_pubClosed) return Err("bridge_reloaded", $"This {SubscribeMethodName} belongs to a bridge load that has ended: look the method up again.").ToString(Formatting.None);
            if (handler == null)
            {
                var removed = _pubLocal.Remove(subscriber);
                return new JObject { ["ok"] = true, ["subscriber"] = subscriber, ["unsubscribed"] = removed }.ToString(Formatting.None);
            }
            var kinds = PubKinds(kindsCsv?.Split(','));
            _pubLocal[subscriber] = (kinds, handler);
            return new JObject { ["ok"] = true, ["subscriber"] = subscriber, ["kinds"] = new JArray(kinds), ["seq"] = _pubSeq }.ToString(Formatting.None);
        }
    }

    // ── TCP ──────────────────────────────────────────────────────────

    private JObject PubEvents(JToken? p)
    {
        var since = p?["since"]?.Value<long>() ?? 0;
        var kinds = (p?["kinds"] as JArray)?.Select(k => k.ToString()).ToHashSet();
        if (kinds != null && (kinds.Count == 0 || kinds.Contains("*"))) kinds = null;
        var limit = Math.Clamp(p?["limit"]?.Value<int>() ?? 100, 1, 500);
        var subscriber = p?["subscriber"]?.ToString();
        lock (_pubLock)
        {
            PubLoadLocked();
            if (!string.IsNullOrWhiteSpace(subscriber)) _pubRemote[subscriber] = (PubKinds(kinds), DateTime.UtcNow, CurrentWho());
            var list = new JArray();
            var next = _pubSeq;   // every event up to here was looked at, unless the page filled up
            foreach (var e in _pubEvents)
            {
                if (e["seq"]!.Value<long>() <= since || (kinds != null && !kinds.Contains(e["kind"]!.ToString()))) continue;
                list.Add(PubWithClaimsLocked(e));
                if (list.Count >= limit) { next = e["seq"]!.Value<long>(); break; }
            }
            return new JObject
            {
                ["ok"] = true, ["seq"] = _pubSeq, ["next"] = next, ["events"] = list,
                ["oldest"] = _pubEvents.Count > 0 ? _pubEvents[0]["seq"] : null, ["file"] = PubFile, ["fileError"] = _pubWhy,
            };
        }
    }

    private JObject PubWithClaimsLocked(JObject e)
    {
        var copy = (JObject)e.DeepClone();
        if (_pubClaims.TryGetValue(e["seq"]!.Value<long>(), out var stages))
        {
            var claims = new JObject();
            foreach (var (stage, c) in stages)
            {
                var cc = (JObject)c.DeepClone();
                if (PubLapsed(c)) cc["lapsed"] = true;
                claims[stage] = cc;
            }
            if (claims.Count > 0) copy["claims"] = claims;
        }
        return copy;
    }

    private JObject PubClaim(JToken? p)
    {
        var seq = p?["seq"]?.Value<long>();
        if (seq is null or <= 0) return Err("missing_seq", "Pass seq: the event to claim (pubsub.events lists them).");
        var stage = p?["stage"]?.ToString() is { Length: > 0 } s ? s : "handle";
        var by = p?["by"]?.ToString() is { Length: > 0 } b ? b : CurrentWho() ?? "anon";
        var ttl = p?["ttlSec"]?.Value<double?>();
        lock (_pubLock)
        {
            PubLoadLocked();
            var ev = _pubEvents.FirstOrDefault(e => e["seq"]!.Value<long>() == seq);
            if (ev == null)
                return Err("unknown_event", $"No event {seq} in memory (kept: {(_pubEvents.Count > 0 ? $"{_pubEvents[0]["seq"]}..{_pubSeq}" : "none")}).");
            if (PubHeldLocked(seq.Value, stage) is { } held)
                return new JObject { ["ok"] = false, ["error"] = "claimed", ["message"] = $"Event {seq} ({stage}) was claimed by {held["by"]} at {held["at"]}.", ["claim"] = held.DeepClone() };
            var data = new JObject { ["ref"] = seq, ["stage"] = stage, ["by"] = by };
            if (ttl is > 0) data["until"] = DateTime.UtcNow.AddSeconds(ttl.Value).ToString("O");
            var (claim, _) = PubAddLocked("pubsub.claim", data, by);
            return new JObject { ["ok"] = true, ["claimSeq"] = claim["seq"], ["stage"] = stage, ["by"] = by, ["until"] = data["until"], ["event"] = PubWithClaimsLocked(ev) };
        }
    }

    private JObject PubRelease(JToken? p)
    {
        var seq = p?["seq"]?.Value<long>();
        if (seq is null or <= 0) return Err("missing_seq", "Pass seq: the claimed event.");
        var stage = p?["stage"]?.ToString() is { Length: > 0 } s ? s : "handle";
        var by = p?["by"]?.ToString() is { Length: > 0 } b ? b : CurrentWho() ?? "anon";
        var force = p?["force"]?.Value<bool>() == true;
        lock (_pubLock)
        {
            PubLoadLocked();
            if (!_pubClaims.TryGetValue(seq.Value, out var stages) || !stages.TryGetValue(stage, out var held))
                return Err("not_claimed", $"Event {seq} ({stage}) is not claimed.");
            if (!force && held["by"]?.ToString() != by && !PubLapsed(held))
                return Err("not_yours", $"Event {seq} ({stage}) is held by {held["by"]}, not {by}: only its holder releases it (or force).");
            PubAddLocked("pubsub.release", new JObject { ["ref"] = seq, ["stage"] = stage, ["by"] = by, ["reason"] = p?["reason"], ["force"] = force ? true : null }, by);
        }
        return new JObject { ["ok"] = true, ["seq"] = seq, ["stage"] = stage };
    }

    private JObject PubPublishRemote(JToken? p)
    {
        var kind = p?["kind"]?.ToString();
        if (PubKindRefused(kind) is { } refused) return Err("bad_kind", $"{refused} (claims go through pubsub.claim / pubsub.release).");
        var data = p?["data"] switch { JObject o => (JObject)o.DeepClone(), null or { Type: JTokenType.Null } => new JObject(), var v => new JObject { ["value"] = v.DeepClone() } };
        WorkStampRouteAgent(kind, data);   // route.agent: Owner / OwnerName / Hue of the calling session (Work.cs)
        return PubPublish(kind!, data, p?["source"]?.ToString() ?? CurrentWho() ?? "anon");
    }

    private JObject PubStatus()
    {
        lock (_pubLock)
        {
            PubLoadLocked();
            var now = DateTime.UtcNow;
            return new JObject
            {
                ["ok"] = true, ["seq"] = _pubSeq, ["kept"] = _pubEvents.Count, ["claims"] = _pubClaims.Sum(kv => kv.Value.Count),
                ["counts"] = JObject.FromObject(_pubEvents.GroupBy(e => e["kind"]!.ToString()).ToDictionary(g => g.Key, g => g.Count())),
                ["plugins"] = new JArray(_pubLocal.Select(kv => new JObject { ["subscriber"] = kv.Key, ["kinds"] = new JArray(kv.Value.kinds) })),
                ["listeners"] = new JArray(_pubRemote.Select(kv => new JObject
                {
                    ["subscriber"] = kv.Key, ["who"] = kv.Value.who, ["kinds"] = new JArray(kv.Value.kinds),
                    ["seenAgoSec"] = Math.Round((now - kv.Value.seen).TotalSeconds, 1), ["fresh"] = (now - kv.Value.seen).TotalSeconds <= PubListenerFreshSec,
                })),
                ["methods"] = new JArray(PublishMethodName, SubscribeMethodName), ["file"] = PubFile, ["fileError"] = _pubWhy,
            };
        }
    }
}
