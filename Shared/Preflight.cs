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
    private static ReflectionPreflightDto? _preflight;
    private static readonly object PreflightLock = new();
    private const string PreflightFile = "reflection-preflight.json";

    private string? ProcessPreflightMethod(string method, JToken? p)
    {
        if (method != "reflection.preflight") return null;
        var simulate = p?["simulateBroken"]?.Value<string>();
        if (simulate != null)
        {
            // A test of the failure path: one link resolved under a wrong name, reported, never cached or saved.
            var link = HudLinks.All.FirstOrDefault(l => l.Id == simulate);
            if (link == null) return Serialize(Err("bad_request", $"No link '{simulate}'. Links: {string.Join(", ", HudLinks.All.Select(l => l.Id))}"));
            lock (PreflightLock)
            {
                link.SimulatedName = link.Member + "_simulatedRename";
                try { var r = RunPreflight(null, true); r.Simulated = $"{simulate} resolved as {link.SimulatedName}"; return Serialize(r); }
                finally { link.SimulatedName = null; }
            }
        }
        var report = p?["refresh"]?.Value<bool>() == true ? PreflightAtLoad() : Preflight();
        return Serialize(report);
    }

    /// <summary>The report of this load (checked on first use if the load-time check hasn't run).</summary>
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
                Ok = r.Ok, Broken = r.Broken, NotNow = r.NotNow, Blocked = r.Blocked, Problems = r.Problems,
            };
        }
        catch (Exception ex) { return new ReflectionPreflightDto { Problems = { $"preflight failed: {ex.GetType().Name}: {ex.Message}" } }; }
    }

    /// <summary>Check every link now, compare with the stamp saved by the last check, save, and say it loudly when something broke.</summary>
    private ReflectionPreflightDto PreflightAtLoad()
    {
        lock (PreflightLock)
        {
            var path = string.IsNullOrEmpty(_bridgeDir) ? null : Path.Combine(_bridgeDir, PreflightFile);
            PreflightStateDto? prev = null;
            try { if (path != null && File.Exists(path)) prev = JsonConvert.DeserializeObject<PreflightStateDto>(File.ReadAllText(path)); }
            catch (Exception ex) { LogQuiet($"[Preflight] previous {PreflightFile} unreadable ({ex.Message}): treated as a first check"); }

            var r = RunPreflight(prev, false);
            _preflight = r;
            try
            {
                if (path != null)
                    File.WriteAllText(path, JsonConvert.SerializeObject(new PreflightStateDto
                    {
                        Stamp = r.Stamp, CheckedAt = r.CheckedAt,
                        Broken = r.Links!.Where(l => l.Status == "broken").Select(l => l.Id).ToList(),
                    }, Formatting.Indented));
            }
            catch (Exception ex) { LogQuiet($"[Preflight] saving {PreflightFile} failed: {ex.Message}"); }

            var change = r.ChangedFrom != null ? $"HUD build changed ({r.ChangedFrom} -> {r.Stamp}): " : "";
            if (r.Broken + r.Blocked > 0)
                LogFault($"[Preflight] {change}{r.Broken} reflection link(s) broken, {r.Blocked} blocked: {string.Join("; ", r.Problems.Take(r.Broken + r.Blocked))}. Details: MCP bridge_status (hello.reflection) or reflection.preflight.", "preflight");
            else
                LogQuiet($"[Preflight] {change}{r.Targets} reflection links ok ({r.NotNow} not set yet) in {r.Ms:F1} ms");
            return r;
        }
    }

    private ReflectionPreflightDto RunPreflight(PreflightStateDto? prev, bool simulated)
    {
        var sw = Stopwatch.StartNew();
        var rows = new List<LinkCheckDto>();
        foreach (var link in HudLinks.All)
        {
            try { rows.Add(link.Check()); }
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
        if (prev != null && !simulated)
            foreach (var id in prev.Broken.Where(id => rows.Any(x => x.Id == id && x.Status == "ok")))
                r.Problems.Add($"{id} works again (broken at the previous check)");
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
