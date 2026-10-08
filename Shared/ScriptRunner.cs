using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Run C# inside the HUD (Roslyn scripting, which both HUDs ship): for agents to test a theory
/// against the live object model with real code - LINQ over entities, reflection into internals,
/// calls into other plugins (via dynamic) - instead of chaining eval paths.
///
/// Off by default (setting "Allow C# scripts"): it is arbitrary code in the HUD process.
/// Flow: script.run compiles on a worker thread (never the HUD's main thread), then runs the
/// script on the main thread at the start of the next Render (like plugin code) or, with
/// thread=worker, right away on the worker (fine for reads; the watchdog serves bridge reads
/// off-thread the same way). script.result polls the job. A script can't be aborted: an endless
/// loop on the main thread freezes the HUD until it is restarted.
///
/// Scripts see a prelude: GameController, Log(object) for printed output, and the game's
/// namespaces imported. The value of the last expression is the result (serialized like
/// eval_path results). Identical code reuses its compiled script, so re-runs don't load new
/// assemblies; each distinct script stays loaded until the HUD restarts.
/// </summary>
public partial class WhatsAnAiBridge
{
    private const int MaxScriptChars = 20_000;
    private const int MaxScriptJobs = 20;
    private const int MaxCompiledScripts = 64;

    private readonly ConcurrentDictionary<string, ScriptJob> _scriptJobs = new();
    private readonly ConcurrentQueue<ScriptJob> _mainThreadScripts = new();
    private readonly ConcurrentDictionary<string, Script<object>> _compiledScripts = new();
    private ScriptOptions? _scriptOptions;
    private int _preludeLines;

    private sealed class ScriptJob
    {
        public string Id = Guid.NewGuid().ToString("N")[..12];
        public string Code = "";
        public bool OnMainThread = true;
        public int TimeoutMs = 10_000;
        public DateTime CreatedAt = DateTime.UtcNow;
        public volatile string Status = "compiling";
        public Script<object>? Script;
        public ScriptResultDto Result = new();
    }

    private string? ProcessScriptMethod(string method, JToken? p)
    {
        switch (method)
        {
            case "script.run":
                return Serialize(StartScript(p?["code"]?.Value<string>(), p?["thread"]?.Value<string>(), p?["timeoutMs"]?.Value<int>()));
            case "script.result":
            {
                var id = p?["id"]?.Value<string>() ?? "";
                return _scriptJobs.TryGetValue(id, out var job)
                    ? Serialize(Snapshot(job))
                    : Serialize(new ScriptResultDto { Status = "unknown", Error = "unknown_job", Message = $"No script job '{id}' (only the last {MaxScriptJobs} are kept)." });
            }
            default:
                return null;
        }
    }

    private ScriptResultDto StartScript(string? code, string? thread, int? timeoutMs)
    {
        if (!Settings.AllowCSharpScripts.Value)
            return new ScriptResultDto
            {
                Status = "rejected", Error = "scripts_disabled",
                Message = "C# scripts are off. Enable 'Allow C# scripts' in the Whats An AI Bridge settings (Dev Loop section).",
            };
        if (string.IsNullOrWhiteSpace(code))
            return new ScriptResultDto { Status = "rejected", Error = "missing_code" };
        if (code.Length > MaxScriptChars)
            return new ScriptResultDto { Status = "rejected", Error = "too_long", Message = $"Scripts are limited to {MaxScriptChars} characters." };

        var job = new ScriptJob
        {
            Code = code,
            OnMainThread = !string.Equals(thread, "worker", StringComparison.OrdinalIgnoreCase),
            TimeoutMs = Math.Clamp(timeoutMs ?? 10_000, 100, 120_000),
        };
        job.Result.Id = job.Id;
        job.Result.Thread = job.OnMainThread ? "main" : "worker";
        _scriptJobs[job.Id] = job;
        foreach (var old in _scriptJobs.Values.OrderByDescending(j => j.CreatedAt).Skip(MaxScriptJobs).ToList())
            _scriptJobs.TryRemove(old.Id, out _);

        Task.Run(() => CompileAndMaybeRun(job));
        return Snapshot(job);
    }

    private void CompileAndMaybeRun(ScriptJob job)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!_compiledScripts.TryGetValue(job.Code, out var script))
            {
                var options = _scriptOptions ??= BuildScriptOptions(out _preludeLines);
                script = CSharpScript.Create<object>(ScriptPrelude + job.Code, options);
                var diagnostics = script.Compile();
                var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                job.Result.CompileMs = sw.ElapsedMilliseconds;
                if (errors.Count > 0)
                {
                    job.Result.Ok = false;
                    job.Result.Error = "compile_error";
                    job.Result.Diagnostics = errors.Take(30).Select(ToDto).ToList();
                    job.Status = "done";
                    return;
                }
                if (_compiledScripts.Count >= MaxCompiledScripts) _compiledScripts.Clear();
                _compiledScripts[job.Code] = script;
            }
            else
            {
                job.Result.CompileMs = 0;
                job.Result.Cached = true;
            }

            job.Script = script;
            if (job.OnMainThread)
            {
                job.Status = "queued";
                _mainThreadScripts.Enqueue(job);
            }
            else
            {
                Execute(job);
            }
        }
        catch (Exception ex)
        {
            job.Result.Ok = false;
            job.Result.Error = "internal_error";
            job.Result.Message = ex.Message;
            job.Status = "done";
        }
    }

    /// <summary>Called at the start of Render: runs at most one queued script per frame.</summary>
    private void RunPendingScript()
    {
        if (_mainThreadScripts.TryDequeue(out var job)) Execute(job);
    }

    private void Execute(ScriptJob job)
    {
        job.Status = "running";
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(job.TimeoutMs);
            // Runs synchronously on the calling thread. Cancellation is only observed between
            // statements Roslyn controls (and by scripts that check Ct), not inside tight loops.
            var state = job.Script!.RunAsync(cancellationToken: cts.Token).GetAwaiter().GetResult();
            job.Result.Ok = state.Exception == null;
            if (state.Exception != null) SetException(job, state.Exception);
            var ret = state.ReturnValue;
            job.Result.ReturnType = ret == null ? null : FriendlyTypeName(ret.GetType());
            job.Result.Value = ExpressionWalker.ToJson(ret);
            job.Result.Log = (state.GetVariable("__out")?.Value as List<string>)?.Take(200).ToList();
        }
        catch (Exception ex)
        {
            job.Result.Ok = false;
            SetException(job, ex is AggregateException { InnerException: { } ie } ? ie : ex);
        }
        job.Result.RunMs = sw.ElapsedMilliseconds;
        job.Status = "done";
    }

    /// <summary>
    /// C#-style type name: "List&lt;Entity&gt;", "anonymous { inGame, hp }" - not the assembly-qualified
    /// FullName, which for script types is hundreds of characters of noise.
    /// </summary>
    private static string FriendlyTypeName(Type t)
    {
        if (t.Name.Contains("AnonymousType"))
            return "anonymous { " + string.Join(", ", t.GetProperties().Select(p => p.Name)) + " }";
        if (t.IsArray) return FriendlyTypeName(t.GetElementType()!) + "[]";
        if (!t.IsGenericType) return t.Namespace is { } ns && (ns == "System" || ns.StartsWith("System.")) ? t.Name : t.FullName ?? t.Name;
        var name = t.Name[..t.Name.IndexOf('`')];
        if (name == "Nullable") return FriendlyTypeName(t.GetGenericArguments()[0]) + "?";
        return name + "<" + string.Join(", ", t.GetGenericArguments().Select(FriendlyTypeName)) + ">";
    }

    private void SetException(ScriptJob job, Exception ex)
    {
        job.Result.Error = ex is OperationCanceledException ? "timeout" : "exception";
        job.Result.Message = $"{ex.GetType().Name}: {ex.Message}";
        // Script frames show as Submission#0; keep the trace short.
        job.Result.StackTrace = string.Join("\n", (ex.StackTrace ?? "").Split('\n').Take(12));
    }

    private ScriptResultDto Snapshot(ScriptJob job)
    {
        var r = job.Result;
        r.Status = job.Status;
        return r;
    }

    private ScriptDiagnosticDto ToDto(Diagnostic d)
    {
        var pos = d.Location.GetLineSpan().StartLinePosition;
        return new ScriptDiagnosticDto
        {
            Line = pos.Line - _preludeLines + 1,
            Col = pos.Character + 1,
            Code = d.Id,
            Message = d.GetMessage(),
        };
    }

    // The prelude gives scripts their context without a globals type: Roslyn needs a file path to
    // reference a globals type's assembly, and plugin assemblies are loaded from memory when the
    // HUD avoids locking plugin DLLs.
    private const string ScriptPrelude =
        "var GameController = Core.Current.GameController;\n" +
        "var __out = new System.Collections.Generic.List<string>();\n" +
        "void Log(object o) { if (__out.Count < 1000) __out.Add(o?.ToString() ?? \"null\"); }\n";

    private static ScriptOptions BuildScriptOptions(out int preludeLines)
    {
        preludeLines = ScriptPrelude.Count(c => c == '\n');
        var hud = typeof(GameController).Assembly;
        var assemblies = new[]
            {
                typeof(object).Assembly, typeof(Enumerable).Assembly, typeof(List<>).Assembly,
                typeof(System.Numerics.Vector2).Assembly, typeof(JObject).Assembly,
                typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly, hud,
            }
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a =>
                !a.IsDynamic && !string.IsNullOrEmpty(a.Location)
                && (a.GetName().Name is { } n && (n.StartsWith("GameOffsets") || n == "ImGui.NET" || n == "System.Runtime"
                    || n == "System.Collections" || n == "System.Linq" || n == "netstandard" || n.StartsWith("SharpDX")))))
            .Distinct();
        // Import the game's common namespaces - only those this HUD build actually has.
        var gameNs = hud.GetName().Name!; // ExileCore or ExileCore2
        var wanted = new[] { "", ".PoEMemory", ".PoEMemory.Components", ".PoEMemory.MemoryObjects", ".PoEMemory.FilesInMemory",
                             ".PoEMemory.Elements", ".Shared", ".Shared.Enums", ".Shared.Helpers", ".Shared.Nodes" }
            .Select(s => gameNs + s);
        Type[] hudTypes;
        try { hudTypes = hud.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { hudTypes = e.Types.OfType<Type>().ToArray(); }
        var present = new HashSet<string>(hudTypes.Select(t => t.Namespace ?? ""));
        return ScriptOptions.Default
            .WithReferences(assemblies)
            .WithImports(new[] { "System", "System.Linq", "System.Collections.Generic", "System.Numerics", "System.Reflection" }
                .Concat(wanted.Where(present.Contains)))
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithAllowUnsafe(false)
            .WithEmitDebugInformation(false);
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────

public class ScriptResultDto
{
    [JsonProperty("id", NullValueHandling = NullValueHandling.Ignore)] public string? Id { get; set; }
    /// <summary>compiling | queued | running | done | rejected | unknown</summary>
    [JsonProperty("status")] public string Status { get; set; } = "";
    [JsonProperty("thread", NullValueHandling = NullValueHandling.Ignore)] public string? Thread { get; set; }
    [JsonProperty("ok", NullValueHandling = NullValueHandling.Ignore)] public bool? Ok { get; set; }
    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public string? Error { get; set; }
    [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)] public string? Message { get; set; }
    [JsonProperty("diagnostics", NullValueHandling = NullValueHandling.Ignore)] public List<ScriptDiagnosticDto>? Diagnostics { get; set; }
    [JsonProperty("returnType", NullValueHandling = NullValueHandling.Ignore)] public string? ReturnType { get; set; }
    [JsonProperty("value", NullValueHandling = NullValueHandling.Ignore)] public JToken? Value { get; set; }
    [JsonProperty("log", NullValueHandling = NullValueHandling.Ignore)] public List<string>? Log { get; set; }
    [JsonProperty("stackTrace", NullValueHandling = NullValueHandling.Ignore)] public string? StackTrace { get; set; }
    [JsonProperty("compileMs", NullValueHandling = NullValueHandling.Ignore)] public long? CompileMs { get; set; }
    [JsonProperty("runMs", NullValueHandling = NullValueHandling.Ignore)] public long? RunMs { get; set; }
    [JsonProperty("cached", NullValueHandling = NullValueHandling.Ignore)] public bool? Cached { get; set; }
}

public class ScriptDiagnosticDto
{
    [JsonProperty("line")] public int Line { get; set; }
    [JsonProperty("col")] public int Col { get; set; }
    [JsonProperty("code")] public string Code { get; set; } = "";
    [JsonProperty("message")] public string Message { get; set; } = "";
}
