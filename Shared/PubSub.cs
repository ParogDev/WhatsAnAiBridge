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
///   AIBridge.Publish   Func&lt;string kind, string json, string&gt;  -> {ok, seq, subscribers: [{who, kinds, seenAgoSec}]}
///                      (the TCP subscribers that read this kind in the last 20 s: none means nobody will handle it now)
///   AIBridge.Subscribe Func&lt;string subscriber, string kindsCsv, Action&lt;string&gt;, string&gt;: the handler gets each matching
///                      event as JSON, on the thread that published it (a bridge request thread or the publisher's):
///                      keep it cheap and thread-safe (queue it). Calling again with the same subscriber replaces it
///                      (a reloaded plugin); a handler that throws is dropped.
/// TCP:
///   pubsub.events {since?, kinds?, limit?, subscriber?}   events after since, each with its claims; subscriber marks the
///                                                         caller as listening to kinds (what Publish reports)
///   pubsub.claim {seq, stage?: handle, by?}               exactly once per (event, stage): the second caller gets claimed
///   pubsub.release {seq, stage?, reason?}                 hands a claim back (e.g. the handler could not start)
///   pubsub.publish {kind, data, source?}                  an agent's event (a reply), delivered to the plugins' subscriptions
///   pubsub.status {}
/// Every event {seq, kind, at, t, source, data} is appended to &lt;BridgeDirectory&gt;\pubsub\events.jsonl (the sequence and
/// the claims continue from it after a restart: a press made just before a HUD restart is still claimable) and, while
/// observing, copied onto the observer timeline as kind = the event's kind with pubSeq (same clock as hud / agent).
/// </summary>
public partial class WhatsAnAiBridge
{
    public const string PublishMethodName = "AIBridge.Publish", SubscribeMethodName = "AIBridge.Subscribe";
    private const int PubRing = 2000;
    private const long PubTailBytes = 8L * 1024 * 1024;
    private const double PubListenerFreshSec = 20;
    private const int PubObserverDataMax = 4000;

    private readonly object _pubLock = new();
    private readonly List<JObject> _pubEvents = new();
    private readonly Dictionary<string, JObject> _pubClaims = new();   // "<seq>:<stage>" -> {by, at, stage}
    private readonly Dictionary<string, (HashSet<string> kinds, Action<string> handler)> _pubLocal = new();
    private readonly Dictionary<string, (HashSet<string> kinds, DateTime seen, string? who)> _pubRemote = new();
    private long _pubSeq;
    private bool _pubLoaded;
    private string? _pubWhy;

    private string PubFile => Path.Combine(_bridgeDir, "pubsub", "events.jsonl");

    /// <summary>From Initialise: the two PluginBridge methods other plugins call.</summary>
    private void PubRegister()
    {
        GameController.PluginBridge.SaveMethod(PublishMethodName, (Func<string, string, string>)PubPublishFromPlugin);
        GameController.PluginBridge.SaveMethod(SubscribeMethodName, (Func<string, string, Action<string>, string>)PubSubscribeLocal);
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
            if (!File.Exists(PubFile)) return;
            foreach (var line in JournalTail(PubFile, PubTailBytes))
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
            if (_pubEvents.Count > PubRing) _pubEvents.RemoveRange(0, _pubEvents.Count - PubRing);
        }
        catch (Exception ex) { _pubWhy = $"reading {PubFile}: {ex.Message}"; LogError($"[PubSub] {_pubWhy}"); }
    }

    private void PubApplyClaimLocked(JObject e)
    {
        var kind = e["kind"]?.ToString();
        if (kind is not ("pubsub.claim" or "pubsub.release")) return;
        var d = e["data"] as JObject;
        var key = $"{d?["ref"]}:{d?["stage"] ?? "handle"}";
        if (kind == "pubsub.claim") _pubClaims[key] = new JObject { ["by"] = d?["by"], ["at"] = e["at"], ["stage"] = d?["stage"] ?? "handle", ["claimSeq"] = e["seq"] };
        else _pubClaims.Remove(key);
    }

    /// <summary>Adds an event (seq, time, file, observer copy) and returns it with the local handlers to call after the lock.</summary>
    private (JObject e, List<(string name, Action<string> handler)> handlers) PubAddLocked(string kind, JObject data, string? source)
    {
        PubLoadLocked();
        var e = new JObject
        {
            ["seq"] = ++_pubSeq, ["kind"] = kind, ["at"] = DateTime.UtcNow.ToString("O"),
            ["t"] = Math.Round(ObsClock.Elapsed.TotalMilliseconds, 1), ["source"] = source, ["data"] = data,
        };
        _pubEvents.Add(e);
        if (_pubEvents.Count > PubRing) _pubEvents.RemoveRange(0, _pubEvents.Count - PubRing);
        PubApplyClaimLocked(e);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PubFile)!);
            File.AppendAllText(PubFile, e.ToString(Formatting.None) + "\n");   // rare events: a synchronous append keeps the order
            _pubWhy = null;
        }
        catch (Exception ex) { _pubWhy = $"writing {PubFile}: {ex.Message}"; }
        var handlers = _pubLocal.Where(kv => kv.Value.kinds.Contains(kind) || kv.Value.kinds.Contains("*")).Select(kv => (kv.Key, kv.Value.handler)).ToList();
        return (e, handlers);
    }

    /// <summary>Publish outside the lock's side effects: the observer copy and the local handlers.</summary>
    private JObject PubPublish(string kind, JObject data, string? source)
    {
        JObject e; List<(string name, Action<string> handler)> handlers; JArray listeners;
        lock (_pubLock)
        {
            (e, handlers) = PubAddLocked(kind, data, source);
            listeners = PubListenersLocked(kind);
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
                LogError($"[PubSub] subscriber {name} threw on {kind} and was dropped (it subscribes again when it loads): {ex.Message}");
            }
        }
        return new JObject { ["ok"] = true, ["seq"] = e["seq"], ["kind"] = kind, ["subscribers"] = listeners, ["fileError"] = _pubWhy };
    }

    private JArray PubListenersLocked(string kind)
    {
        var now = DateTime.UtcNow;
        return new JArray(_pubRemote
            .Where(kv => (now - kv.Value.seen).TotalSeconds <= PubListenerFreshSec && (kv.Value.kinds.Contains(kind) || kv.Value.kinds.Contains("*")))
            .Select(kv => new JObject { ["subscriber"] = kv.Key, ["who"] = kv.Value.who, ["seenAgoSec"] = Math.Round((now - kv.Value.seen).TotalSeconds, 1) }));
    }

    private static HashSet<string> PubKinds(IEnumerable<string>? kinds) =>
        new((kinds ?? []).Select(k => k.Trim()).Where(k => k.Length > 0), StringComparer.Ordinal);

    // ── PluginBridge ─────────────────────────────────────────────────

    private string PubPublishFromPlugin(string kind, string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(kind)) return Err("missing_kind", "AIBridge.Publish(kind, json): kind is empty.").ToString(Formatting.None);
            if (kind.StartsWith("pubsub.", StringComparison.Ordinal)) return Err("reserved_kind", $"'{kind}': pubsub.* kinds are the bridge's own.").ToString(Formatting.None);
            JObject data;
            try { data = string.IsNullOrWhiteSpace(json) ? new JObject() : JToken.Parse(json) is JObject o ? o : new JObject { ["value"] = JToken.Parse(json) }; }
            catch (JsonException ex) { return Err("bad_json", $"AIBridge.Publish({kind}): json does not parse: {ex.Message}").ToString(Formatting.None); }
            return PubPublish(kind, data, data["source"]?.ToString() ?? "plugin").ToString(Formatting.None);
        }
        catch (Exception ex) { return Err("publish_failed", ex.Message).ToString(Formatting.None); }
    }

    private string PubSubscribeLocal(string subscriber, string kindsCsv, Action<string> handler)
    {
        if (string.IsNullOrWhiteSpace(subscriber) || handler == null)
            return Err("bad_subscription", "AIBridge.Subscribe(subscriber, kindsCsv, handler): subscriber and handler are required.").ToString(Formatting.None);
        var kinds = PubKinds(kindsCsv?.Split(','));
        lock (_pubLock)
        {
            PubLoadLocked();
            _pubLocal[subscriber] = (kinds, handler);
            return new JObject { ["ok"] = true, ["subscriber"] = subscriber, ["kinds"] = new JArray(kinds), ["seq"] = _pubSeq }.ToString(Formatting.None);
        }
    }

    // ── TCP ──────────────────────────────────────────────────────────

    private JObject PubEvents(JToken? p)
    {
        var since = p?["since"]?.Value<long>() ?? 0;
        var kinds = (p?["kinds"] as JArray)?.Select(k => k.ToString()).ToHashSet();
        var limit = Math.Clamp(p?["limit"]?.Value<int>() ?? 100, 1, 500);
        var subscriber = p?["subscriber"]?.ToString();
        lock (_pubLock)
        {
            PubLoadLocked();
            if (!string.IsNullOrWhiteSpace(subscriber)) _pubRemote[subscriber] = (PubKinds(kinds), DateTime.UtcNow, CurrentWho());
            var list = new JArray();
            foreach (var e in _pubEvents)
            {
                if (e["seq"]!.Value<long>() <= since || (kinds != null && !kinds.Contains(e["kind"]!.ToString()))) continue;
                list.Add(PubWithClaimsLocked(e));
                if (list.Count >= limit) break;
            }
            return new JObject
            {
                ["ok"] = true, ["seq"] = _pubSeq, ["events"] = list,
                ["oldest"] = _pubEvents.Count > 0 ? _pubEvents[0]["seq"] : null, ["file"] = PubFile, ["fileError"] = _pubWhy,
            };
        }
    }

    private JObject PubWithClaimsLocked(JObject e)
    {
        var copy = (JObject)e.DeepClone();
        var prefix = e["seq"] + ":";
        var claims = new JObject();
        foreach (var (key, c) in _pubClaims)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) claims[key[prefix.Length..]] = c.DeepClone();
        if (claims.Count > 0) copy["claims"] = claims;
        return copy;
    }

    private JObject PubClaim(JToken? p)
    {
        var seq = p?["seq"]?.Value<long>();
        if (seq is null or <= 0) return Err("missing_seq", "Pass seq: the event to claim (pubsub.events lists them).");
        var stage = p?["stage"]?.ToString() is { Length: > 0 } s ? s : "handle";
        var by = p?["by"]?.ToString() is { Length: > 0 } b ? b : CurrentWho() ?? "anon";
        JObject result;
        lock (_pubLock)
        {
            PubLoadLocked();
            var ev = _pubEvents.FirstOrDefault(e => e["seq"]!.Value<long>() == seq);
            if (ev == null)
                return Err("unknown_event", $"No event {seq} in memory (kept: {(_pubEvents.Count > 0 ? $"{_pubEvents[0]["seq"]}..{_pubSeq}" : "none")}).");
            if (_pubClaims.TryGetValue($"{seq}:{stage}", out var held))
                return new JObject { ["ok"] = false, ["error"] = "claimed", ["message"] = $"Event {seq} ({stage}) was claimed by {held["by"]} at {held["at"]}.", ["claim"] = held.DeepClone() };
            var (claim, _) = PubAddLocked("pubsub.claim", new JObject { ["ref"] = seq, ["stage"] = stage, ["by"] = by }, by);
            result = new JObject { ["ok"] = true, ["claimSeq"] = claim["seq"], ["stage"] = stage, ["by"] = by, ["event"] = PubWithClaimsLocked(ev) };
        }
        return result;
    }

    private JObject PubRelease(JToken? p)
    {
        var seq = p?["seq"]?.Value<long>();
        if (seq is null or <= 0) return Err("missing_seq", "Pass seq: the claimed event.");
        var stage = p?["stage"]?.ToString() is { Length: > 0 } s ? s : "handle";
        var by = CurrentWho() ?? "anon";
        lock (_pubLock)
        {
            PubLoadLocked();
            if (!_pubClaims.ContainsKey($"{seq}:{stage}")) return Err("not_claimed", $"Event {seq} ({stage}) is not claimed.");
            PubAddLocked("pubsub.release", new JObject { ["ref"] = seq, ["stage"] = stage, ["by"] = by, ["reason"] = p?["reason"] }, by);
        }
        return new JObject { ["ok"] = true, ["seq"] = seq, ["stage"] = stage };
    }

    private JObject PubPublishRemote(JToken? p)
    {
        var kind = p?["kind"]?.ToString();
        if (string.IsNullOrWhiteSpace(kind)) return Err("missing_kind", "Pass kind, e.g. feedback.reply.");
        if (kind.StartsWith("pubsub.", StringComparison.Ordinal)) return Err("reserved_kind", $"'{kind}': pubsub.* kinds are the bridge's own (use pubsub.claim / pubsub.release).");
        var data = p?["data"] switch { JObject o => (JObject)o.DeepClone(), null or { Type: JTokenType.Null } => new JObject(), var v => new JObject { ["value"] = v.DeepClone() } };
        return PubPublish(kind, data, p?["source"]?.ToString() ?? CurrentWho() ?? "anon");
    }

    private JObject PubStatus()
    {
        lock (_pubLock)
        {
            PubLoadLocked();
            var now = DateTime.UtcNow;
            return new JObject
            {
                ["ok"] = true, ["seq"] = _pubSeq, ["kept"] = _pubEvents.Count, ["claims"] = _pubClaims.Count,
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
