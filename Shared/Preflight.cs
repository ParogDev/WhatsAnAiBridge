using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>One row of the reflection preflight.</summary>
public class LinkCheckDto
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    /// <summary>ok | broken (gone: a HUD / runtime / Harmony update) | not_now (there, its value isn't set yet) | blocked (needs a broken link).</summary>
    [JsonProperty("status")] public string Status { get; set; } = "";
    [JsonProperty("type")] public string Type { get; set; } = "";
    [JsonProperty("member")] public string Member { get; set; } = "";
    [JsonProperty("expected")] public string Expected { get; set; } = "";
    /// <summary>Broken: what the type has instead. Ok: the member found when it was matched by shape.</summary>
    [JsonProperty("found", NullValueHandling = NullValueHandling.Ignore)] public string? Found { get; set; }
    [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)] public string? Note { get; set; }
    /// <summary>What stops working while it is broken.</summary>
    [JsonProperty("feature")] public string Feature { get; set; } = "";
}

/// <summary>reflection.preflight: every declared reflection target (HudLinks) checked against this HUD build.</summary>
public class ReflectionPreflightDto
{
    /// <summary>HUD assembly version + module id and the .NET runtime: what the targets live in.</summary>
    [JsonProperty("stamp")] public string Stamp { get; set; } = "";
    /// <summary>The stamp of the previous check saved in the bridge directory, when it differs (a HUD or .NET update).</summary>
    [JsonProperty("changedFrom", NullValueHandling = NullValueHandling.Ignore)] public string? ChangedFrom { get; set; }
    [JsonProperty("checkedAt")] public DateTime CheckedAt { get; set; }
    [JsonProperty("ms")] public double Ms { get; set; }
    [JsonProperty("targets")] public int Targets { get; set; }
    [JsonProperty("ok")] public int Ok { get; set; }
    [JsonProperty("broken")] public int Broken { get; set; }
    [JsonProperty("notNow")] public int NotNow { get; set; }
    [JsonProperty("blocked")] public int Blocked { get; set; }
    /// <summary>"id: what broke" for every row that isn't ok, broken first.</summary>
    [JsonProperty("problems")] public List<string> Problems { get; set; } = new();
    /// <summary>Links broken at the previous load's check that are ok now (not problems).</summary>
    [JsonProperty("recovered", NullValueHandling = NullValueHandling.Ignore)] public List<string>? Recovered { get; set; }
    [JsonProperty("links", NullValueHandling = NullValueHandling.Ignore)] public List<LinkCheckDto>? Links { get; set; }
    [JsonProperty("simulated", NullValueHandling = NullValueHandling.Ignore)] public string? Simulated { get; set; }
}

/// <summary>What the bridge saves between loads to tell a new build from the last one checked.</summary>
public class PreflightStateDto
{
    [JsonProperty("stamp")] public string Stamp { get; set; } = "";
    [JsonProperty("checkedAt")] public DateTime CheckedAt { get; set; }
    [JsonProperty("broken")] public List<string> Broken { get; set; } = new();
}

/// <summary>
/// HUD-update drift preflight. Every private/internal reflection target in <see cref="HudLinks"/> is checked when the
/// bridge loads (each HUD start, each bridge reload) against the build stamp saved by the previous check: a changed
/// stamp means a HUD or .NET update, and is reported as such. Broken links are named one by one (type,
/// member, expected shape, what is there instead) in hello's <c>reflection</c> (MCP bridge_status, hud_health_report)
/// and in full by <c>reflection.preflight</c>; a break is also logged as a fault at load.
/// </summary>
public partial class WhatsAnAiBridge
{
    // Instance state, never static: all of it belongs to this load of the bridge (a reload is a new instance), so no
    // cached report, baseline or "already written" mark can leak from one load into the next.
    private ReflectionPreflightDto? _preflight;
    private PreflightStateDto? _preflightPrev;
    private bool _preflightPrevRead;
    /// <summary>What this load last wrote to <see cref="PreflightFile"/> (stamp + broken ids; null = not written yet this
    /// load) and last logged: unchanged = not again.</summary>
    private string? _preflightSavedKey, _preflightLoggedLine;
    private readonly object PreflightLock = new();
    private const string PreflightFile = "reflection-preflight.json";

    private string? ProcessPreflightMethod(string method, JToken? p)
    {
        if (method != "reflection.preflight") return null;
        var simulate = p?["simulateBroken"]?.Value<string>();
        if (simulate != null)
        {
            // A test of the failure path: one link resolved under a wrong name, reported, never cached or saved.
            // Only inside this check (HudLink.Check's parameter): the call sites keep resolving the real member meanwhile.
            var link = HudLinks.All.FirstOrDefault(l => l.Id == simulate);
            if (link == null) return Serialize(Err("bad_request", $"No link '{simulate}'. Links: {string.Join(", ", HudLinks.All.Select(l => l.Id))}"));
            if (link.Kind == LinkKind.FieldOfType)
                return Serialize(Err("bad_request", $"{simulate} is found by its field type, not by name: a rename can't be simulated. Pick a link found by name."));
            // Its type can't be looked at yet (Check would answer not_now before resolving anything): a simulation would
            // silently test nothing.
            if (link.NotYet is { } notYet)
                return Serialize(Err("bad_request", $"{simulate} can't be simulated now: {notYet}. Run a pipeline_trace first, or pick a link that isn't Harmony / MonoMod."));
            var r = RunPreflight(null, link);
            r.Simulated = $"{simulate} resolved as {link.SimulatedName}";
            return Serialize(r);
        }
        var report = p?["refresh"]?.Value<bool>() == true ? PreflightAtLoad() : Preflight();
        return Serialize(report);
    }

    /// <summary>The report of this load (checked on first use if the load-time check hasn't run). A check that failed is
    /// cached like any other report (<see cref="PreflightAtLoad"/>): hello never re-scans under the lock; only an explicit
    /// refresh (or the re-check after 0Harmony loads) runs it again.</summary>
    private ReflectionPreflightDto Preflight()
    {
        lock (PreflightLock) return _preflight ?? PreflightAtLoad();
    }

    /// <summary>Hello's short form: counts and problems, no rows.</summary>
    private ReflectionPreflightDto? PreflightSummary()
    {
        try
        {
            var r = Preflight();
            return new ReflectionPreflightDto
            {
                Stamp = r.Stamp, ChangedFrom = r.ChangedFrom, CheckedAt = r.CheckedAt, Ms = r.Ms, Targets = r.Targets,
                Ok = r.Ok, Broken = r.Broken, NotNow = r.NotNow, Blocked = r.Blocked, Problems = r.Problems.ToList(), Recovered = r.Recovered?.ToList(),   // copies: hello never shares the cached report's lists
                // The rows that aren't ok, typed (status field): readers classify by status, never by parsing problems.
                Links = r.Links?.Where(l => l.Status != "ok").ToList(),
            };
        }
        catch (Exception ex) { return new ReflectionPreflightDto { CheckedAt = DateTime.UtcNow, Problems = { $"preflight failed: {ex.GetType().Name}: {ex.Message}" } }; }
    }

    /// <summary>Check every link now, compare with the stamp saved by the last check, save, and say it loudly when something broke.</summary>
    private ReflectionPreflightDto PreflightAtLoad()
    {
        lock (PreflightLock)
        {
            var path = string.IsNullOrEmpty(_bridgeDir) ? null : Path.Combine(_bridgeDir, PreflightFile);
            // The previous load's file is the baseline for every check of this load (the re-check after 0Harmony loads,
            // refresh): read once, before the first write, so changedFrom and "works again" survive re-checks.
            if (!_preflightPrevRead)
            {
                _preflightPrevRead = true;
                try { if (path != null && File.Exists(path)) _preflightPrev = JsonConvert.DeserializeObject<PreflightStateDto>(File.ReadAllText(path)); }
                catch (Exception ex) { LogQuiet($"[Preflight] previous {PreflightFile} unreadable ({ex.Message}): treated as a first check"); }
            }

            ReflectionPreflightDto r;
            try { r = RunPreflight(_preflightPrev, null); }
            catch (Exception ex)
            {
                // The check itself failed: cached as this load's report (hello and reflection.preflight answer it without
                // re-scanning), re-run only by refresh=true or the re-check after 0Harmony loads. Nothing is saved: the
                // previous file stays the baseline.
                string stamp;
                try { stamp = BuildStamp(); } catch (Exception sx) { stamp = $"? ({sx.GetType().Name})"; }
                r = new ReflectionPreflightDto { Stamp = stamp, CheckedAt = DateTime.UtcNow, Problems = { $"preflight failed: {ex.GetType().Name}: {ex.Message}" } };
                _preflight = r;
                var failLine = $"[Preflight] check failed: {ex.GetType().Name}: {ex.Message}";
                if (failLine != _preflightLoggedLine) { _preflightLoggedLine = failLine; LogFault(failLine, "preflight"); }
                return r;
            }
            _preflight = r;
            // _preflightPrev stays the previous load's file on purpose (the baseline of every check of this load). The
            // file is written by the first check of every load, whatever it found, so the next load's baseline is this
            // load (never an older one that last changed something); again within this load only when the broken set
            // changes (the re-check after 0Harmony loads, a refresh). The log line likewise only when what it says changes.
            var broken = r.Links!.Where(l => l.Status == "broken").Select(l => l.Id).ToList();
            var saveKey = $"{r.Stamp}|{string.Join(",", broken)}";
            if (path != null && (_preflightSavedKey == null || saveKey != _preflightSavedKey))
            {
                try
                {
                    File.WriteAllText(path, JsonConvert.SerializeObject(new PreflightStateDto { Stamp = r.Stamp, CheckedAt = r.CheckedAt, Broken = broken }, Formatting.Indented));
                    _preflightSavedKey = saveKey;
                }
                catch (Exception ex) { LogQuiet($"[Preflight] saving {PreflightFile} failed: {ex.Message}"); }
            }

            var change = r.ChangedFrom != null ? $"HUD build changed ({r.ChangedFrom} -> {r.Stamp}): " : "";
            var bad = r.Broken + r.Blocked > 0;
            var line = bad
                ? $"[Preflight] {change}{r.Broken} reflection link(s) broken, {r.Blocked} blocked: {string.Join("; ", r.Problems.Take(r.Broken + r.Blocked))}. Details: MCP bridge_status (hello.reflection) or reflection.preflight."
                : $"[Preflight] {change}{r.Targets} reflection links ok ({r.NotNow} not set yet)";
            if (line != _preflightLoggedLine)
            {
                _preflightLoggedLine = line;
                if (bad) LogFault(line, "preflight");
                else LogQuiet($"{line} in {r.Ms:F1} ms");
            }
            return r;
        }
    }

    private ReflectionPreflightDto RunPreflight(PreflightStateDto? prev, HudLink? simulated)
    {
        var sw = Stopwatch.StartNew();
        var rows = new List<LinkCheckDto>();
        foreach (var link in HudLinks.All)
        {
            try { rows.Add(link.Check(simulated)); }
            catch (Exception ex) { rows.Add(new LinkCheckDto { Id = link.Id, Status = "broken", Type = link.TypeLabel, Member = link.Member, Expected = link.Expected, Feature = link.Feature, Found = $"checking threw {ex.GetType().Name}: {ex.Message}" }); }
        }
        var stamp = BuildStamp();
        var r = new ReflectionPreflightDto
        {
            Stamp = stamp,
            ChangedFrom = prev != null && prev.Stamp != stamp ? prev.Stamp : null,
            CheckedAt = DateTime.UtcNow,
            Targets = rows.Count,
            Ok = rows.Count(x => x.Status == "ok"),
            Broken = rows.Count(x => x.Status == "broken"),
            NotNow = rows.Count(x => x.Status == "not_now"),
            Blocked = rows.Count(x => x.Status == "blocked"),
            Links = rows,
        };
        int Order(string s) => s switch { "broken" => 0, "blocked" => 1, _ => 2 };
        foreach (var x in rows.Where(x => x.Status != "ok").OrderBy(x => Order(x.Status)))
            r.Problems.Add(x.Status switch
            {
                "broken" => $"{x.Id} BROKEN: {x.Type}: expected {x.Expected}; {x.Found} (breaks {x.Feature})",
                "blocked" => $"{x.Id} blocked: {x.Found}",
                _ => $"{x.Id} not now: {x.Note}",
            });
        // Fixed again since the last check: worth one line.
        if (prev != null && simulated == null)
            foreach (var id in prev.Broken.Where(id => rows.Any(x => x.Id == id && x.Status == "ok")))
                (r.Recovered ??= new()).Add($"{id} works again (broken at the previous load's check)");
        r.Ms = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
        return r;
    }

    /// <summary>0Harmony just loaded into the default context: its links (not_now until now) are checked.</summary>
    private void PreflightAfterHarmonyLoad()
    {
        try { PreflightAtLoad(); } catch (Exception ex) { LogFault($"[Preflight] check after 0Harmony loaded failed: {ex.GetType().Name}: {ex.Message}", "preflight"); }
    }

    /// <summary>What the targets live in: the HUD assembly (version and module id, new on every build) and .NET.</summary>
    private static string BuildStamp()
    {
        var hud = typeof(GameController).Assembly;
        var info = hud.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? hud.GetName().Version?.ToString() ?? "?";
        var mvid = hud.ManifestModule.ModuleVersionId.ToString("N")[..8];
        // No Harmony version: touching a Harmony type here would load 0Harmony into the plugin context (HudLinks.HarmonyAssembly),
        // and it ships with the bridge build anyway.
        return $"{hud.GetName().Name} {info} ({mvid}); .NET {Environment.Version}";
    }
}
