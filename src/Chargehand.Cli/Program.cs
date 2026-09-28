// chargehand CLI. Exit codes: 0 completed, 1 failed or denied, 2 usage error, 3 needs input.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand;
using Chargehand.ClaudeCode;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Evals;
using Chargehand.Memory;
using Chargehand.OpenCode;
using Chargehand.Prompts;
using Chargehand.RunLog;
using Chargehand.Runtime;
using Chargehand.Server;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

const string Usage = """
    usage: chargehand [--profile profiles/local.json] <command>
      run                          reads request/v1 on stdin, writes result/v1 on stdout
      serve                        HTTP /v1/runs and MCP /v1/mcp (profile http; 127.0.0.1 by default)
      show <run-id>                prints a run and its calls from the run log
      reconcile <run-id>           reads gateway spend rows (JSONL) on stdin, prints own vs gateway cost
      cache <run-id>               cache report: reads, writes and hit rate per call; the first block that changed
      routes [run-log.jsonl ...]   routing report per preset, node kind and model; suggestions only
      score <run-id> <0-1> [name]  records a hand score for a run (name defaults to quality)
      eval seed <cell> <run-id>... proposes eval items (JSONL on stdout) from runs in the log, for review
      eval push <cell>             pushes reviewed items (JSONL on stdin) to the cell's Langfuse dataset
      eval gate <base> <change> [--cells a,b] [--changed-files f] [--pr-body f] [--name n] [--cells-file f] [--allow-uncovered]
                                   paired runs of the base and change prompts/ and presets/; exit 1 when blocked
      prompts sync                 pushes prompt blocks to Langfuse prompt management
    Paths (prompts/, presets/, evals/cells.json, the run log) are relative to the current directory.
    """;

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
var argv = args.ToList();
var profilePath = Environment.GetEnvironmentVariable("CHARGEHAND_PROFILE") ?? "profiles/local.json";
if (argv.Count >= 2 && argv[0] == "--profile")
{
    profilePath = argv[1];
    argv.RemoveRange(0, 2);
}
if (argv.Count == 0 || argv[0] is not ("run" or "serve" or "show" or "reconcile" or "cache" or "routes" or "score" or "eval" or "prompts"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var profile = Profile.Load(profilePath);
var root = Directory.GetCurrentDirectory();
var runLog = new JsonlRunLog(profile.RunLog);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

switch (argv)
{
    case ["run"]:
        return await Run();
    case ["serve"]:
        return await Serve();
    case ["show", var id]:
        return await Show(id);
    case ["reconcile", var id]:
        return await Reconcile(id);
    case ["cache", var id]:
        var calls = (await runLog.ReadAsync(id, ct)).Calls;
        Console.Write(CacheReport.Build(calls));
        return calls.Count == 0 ? 1 : 0;
    case ["routes", .. var logs]:
        var data = await Task.WhenAll((logs.Count == 0 ? [profile.RunLog] : logs).Select(l => new JsonlRunLog(l).ReadAllAsync(ct)));
        Console.Write(RoutingReport.Build(new RunLogData([.. data.SelectMany(d => d.Starts)], [.. data.SelectMany(d => d.Runs)],
            [.. data.SelectMany(d => d.Calls)], [.. data.SelectMany(d => d.Scores)])));
        return 0;
    case ["score", var id, var value, .. var name]:
        await runLog.AppendAsync(new ScoreRecord(id, name.FirstOrDefault() ?? "quality", double.Parse(value, CultureInfo.InvariantCulture), DateTimeOffset.UtcNow, "hand"), ct);
        return 0;
    case ["eval", "seed", var cellName, .. var runIds]:
        foreach (var item in EvalRunner.Seed(Cell(cellName), await Task.WhenAll(runIds.Select(r => runLog.ReadAsync(r, ct)))))
            Console.WriteLine(JsonSerializer.Serialize(item, ContractJson.Options));
        return 0;
    case ["eval", "push", var cellName]:
        var cell = Cell(cellName);
        var items = new List<EvalItem>();
        while (await Console.In.ReadLineAsync(ct) is { } line)
            if (line.Trim().Length > 0)
                items.Add(JsonSerializer.Deserialize<EvalItem>(line, ContractJson.Options)!);
        await Evals().PushAsync(cell.Dataset, items, ct);
        Console.WriteLine($"{items.Count} items -> dataset {cell.Dataset}");
        return 0;
    case ["eval", "gate", var baseRoot, var changeRoot, .. var options]:
        return await EvalGate(Path.GetFullPath(baseRoot), Path.GetFullPath(changeRoot), options);
    case ["prompts", "sync"]:
        return await SyncPrompts();
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

async Task<int> Run()
{
    var input = await Console.In.ReadToEndAsync(ct);
    using var doc = JsonDocument.Parse(input);
    var errors = ContractSchemas.Validate(ContractSchemas.Request, doc.RootElement);
    if (errors.Count > 0)
    {
        Console.Error.WriteLine($"invalid request/v1: {string.Join("; ", errors)}");
        return 2;
    }
    var request = doc.RootElement.Deserialize<RunRequest>(ContractJson.Options)!;

    using var tracing = Tracing();
    ResultContract result;
    try
    {
        var (runtime, runtimeVersion) = await Connect();
        var orchestrator = new Orchestrator(profile.WithLaunchDirectory(root), runtime, runtimeVersion, root, runLog, await PromptVersions(), Memory());
        result = await orchestrator.RunAsync(request, ct);
    }
    catch (ChargehandException e)
    {
        // The runtime refused before a run started (not running, another version): no prompt was sent, so the chain is empty.
        result = new ResultContract("result/v1", Orchestrator.NewRunId(), "run", ActivityTraceId.CreateRandom().ToHexString(),
            new PromptChain([], new AsSent("", "", "", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), ResultStatus.Failed, e.Message, [], [], [], [e.Message], 0,
            new Usage(0, 0, 0, 0, 0), e.Error);
    }
    tracing?.ForceFlush(10_000);

    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
    return result.Status switch { ResultStatus.Completed => 0, ResultStatus.NeedsInput => 3, _ => 1 };
}

async Task<int> Serve()
{
    if (profile.Http is not { } http)
    {
        Console.Error.WriteLine("profile has no http settings (ADR 0018)");
        return 2;
    }
    using var tracing = Tracing();
    var (runtime, runtimeVersion) = await Connect();
    var orchestrator = new Orchestrator(profile, runtime, runtimeVersion, root, runLog, await PromptVersions(), Memory());
    var app = ChargehandServer.Create(new ServerSettings(http.Port, profile.Secret(http.ApiKeySecret), Path.Combine(root, "presets"),
        http.Listen, http.AllowedHosts), orchestrator, runLog);
    await app.StartAsync(ct);
    Console.Error.WriteLine($"chargehand serve: {string.Join(", ", app.Urls)} (/v1/runs, MCP /v1/mcp)");
    await app.WaitForShutdownAsync(ct);
    return 0;
}

/// <summary>Prompt CI (ADR 0019): the affected cells' items, paired under the base and the change, and one verdict.</summary>
async Task<int> EvalGate(string baseRoot, string changeRoot, IReadOnlyList<string> options)
{
    string? Option(string name) => options.SkipWhile(o => o != name).Skip(1).FirstOrDefault();
    // The cells (and their tolerances) come from the trusted checkout, never from the change under test.
    var all = EvalCell.Load(Option("--cells-file") ?? Path.Combine(root, "evals", "cells.json"));
    IReadOnlyList<string> uncovered = [];
    IReadOnlyList<EvalCell> cells;
    if (Option("--cells") is { } names)
        cells = [.. names.Split(',').Select(n => all.FirstOrDefault(c => c.Name == n) ?? throw new ArgumentException($"no eval cell {n}"))];
    else
        (cells, uncovered) = EvalRunner.Affected(all, Option("--changed-files") is { } f ? File.ReadAllLines(f) : []);
    var trade = Option("--pr-body") is { } body ? Gate.ParseTrade(File.ReadAllText(body)) : null;
    var name = Option("--name") ?? $"eval-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";

    using var tracing = Tracing();
    var (runtime, runtimeVersion) = await Connect();
    var langfuse = Evals();
    // Evals never use memory (ADR 0008), and each arm reads prompts/ and presets/ from its own root.
    var runner = new EvalRunner(r => new Orchestrator(profile, runtime, runtimeVersion, r, runLog, new Dictionary<string, int>()), runtime, profile.IntakeModel,
        runLog, langfuse, () => tracing?.ForceFlush(10_000), Console.Out);
    var verdicts = new List<Verdict>();
    foreach (var cell in cells)
        verdicts.Add(await runner.RunAsync(cell, await langfuse.ItemsAsync(cell.Dataset, ct), baseRoot, changeRoot, name, trade, ct));

    Console.WriteLine();
    Console.WriteLine("| cell | items | quality change | t | cost change | t | verdict |");
    Console.WriteLine("|---|---|---|---|---|---|---|");
    foreach (var v in verdicts)
        Console.WriteLine($"| {v.Cell} | {v.Items} | {v.QualityDelta:+0.000;-0.000} | {v.QualityT:0.00} | {v.CostChange:+0%;-0%} | {v.CostT:0.00} | {(v.Blocked ? "BLOCK" : "pass")}: {v.Reason} |");
    var blockedByUncovered = uncovered.Count > 0 && !options.Contains("--allow-uncovered");
    foreach (var f in uncovered)
        Console.WriteLine($"uncovered: {f} (no eval cell gates it{(blockedByUncovered ? "" : "; allowed")})");
    var blocked = verdicts.Where(v => v.Blocked).ToList();
    var description = blocked.Count > 0 ? string.Join("; ", blocked.Select(v => $"{v.Cell}: {v.Reason}"))
        : blockedByUncovered ? $"no eval cell gates {string.Join(", ", uncovered)}"
        : cells.Count == 0 ? "no prompt or preset change" : string.Join("; ", verdicts.Select(v => $"{v.Cell}: {v.Reason}"));
    // Last line for scripts/prompt-ci.sh: the commit status (a status description holds at most 140 characters).
    Console.WriteLine($"prompt-ci: {(blocked.Count > 0 || blockedByUncovered ? "failure" : "success")}: {(description.Length > 140 ? description[..137] + "..." : description)}");
    return blocked.Count > 0 || blockedByUncovered ? 1 : 0;
}

EvalCell Cell(string name) =>
    EvalCell.Load(Path.Combine(root, "evals", "cells.json")).FirstOrDefault(c => c.Name == name) ?? throw new ArgumentException($"no eval cell {name} in evals/cells.json");

async Task<(IWorkerRuntime Runtime, string Version)> Connect()
{
    var kind = RuntimeSelector.Select(profile.Runtime, Environment.GetEnvironmentVariable("CHARGEHAND_RUNTIME"), RuntimeSelector.OnPath);
    if (kind == RuntimeKind.ClaudeCode)
    {
        var cc = profile.ClaudeCode
            ?? throw new ChargehandException(ErrorCode.RuntimeUnavailable, "claude_code selected but the profile has no claude_code settings", "Add claude_code to the profile.");
        var credential = (cc.ApiKeySecret, cc.OauthTokenSecret) switch
        {
            ({ } key, null) => new ClaudeCodeCredential(profile.Secret(key), Subscription: false),
            (null, { } token) => new ClaudeCodeCredential(profile.Secret(token), Subscription: true),
            _ => throw new ChargehandException(ErrorCode.InvalidRequest, "claude_code needs exactly one of api_key_secret and oauth_token_secret",
                "Set one of claude_code.api_key_secret and claude_code.oauth_token_secret in the profile, not both."),
        };
        var claude = await ClaudeCodeWorkerRuntime.ConnectAsync(cc.Binary, cc.Version, credential, ct, cc.BaseUrl is null ? null : new Uri(cc.BaseUrl));
        return (claude, claude.Version);
    }
    var oc = profile.Opencode
        ?? throw new ChargehandException(ErrorCode.RuntimeUnavailable, "opencode selected but the profile has no opencode settings", "Add opencode to the profile.");
    var client = new OpenCodeClient(new HttpClient { BaseAddress = new Uri(oc.Url) }, profile.Secret(oc.PasswordSecret));
    var runtime = await OpenCodeWorkerRuntime.ConnectAsync(client, oc.Version, ct);
    return (runtime, runtime.Version);
}

IMemoryProvider? Memory() => profile.Memory is { } m
    ? new HindsightMemory(new HttpClient { BaseAddress = new Uri(m.Url), Timeout = TimeSpan.FromSeconds(30) }, m.ApiKeySecret is null ? null : profile.Secret(m.ApiKeySecret), m.MaxTokens)
    : null;

async Task<int> Show(string runId)
{
    var (start, run, calls) = await runLog.ReadAsync(runId, ct);
    if (start is null && run is null && calls.Count == 0)
    {
        Console.Error.WriteLine($"no run {runId} in {profile.RunLog}");
        return 1;
    }
    if (run is null && start is not null)
        Console.WriteLine($"{start.RunId}  preset={start.Request.Context.Preset}  status={(start.OwnerAlive() ? "running" : "lost")}  started={start.Started:u}  pid={start.Pid}");
    if (run is not null)
        Console.WriteLine($"{run.RunId}  preset={run.Preset}  intake={run.IntakeAction ?? "needs_input"}  status={run.Result.Status}  " +
                          $"{(run.Finished - run.Started).TotalSeconds:0.0}s  usd={run.Result.Usage.Usd}  claims={run.Result.Claims.Count}  open={run.Result.OpenQuestions.Count}");
    Console.WriteLine("node     kind     model                       prompt   cached  cache%   out+rsn  latency     usd");
    foreach (var c in calls)
        Console.WriteLine($"{c.NodeId,-8} {c.Kind,-8} {c.Model,-27} {c.PromptTokens,7} {c.Tokens?.CacheRead,8} {c.CacheRate,6:P0} {c.Tokens?.Output + c.Tokens?.Reasoning,8} {c.LatencyMs / 1000,6:0.0}s {c.Usd,8:0.000000}");
    return 0;
}

async Task<int> Reconcile(string runId)
{
    var calls = (await runLog.ReadAsync(runId, ct)).Calls;
    var rows = new List<SpendRow>();
    while (await Console.In.ReadLineAsync(ct) is { } line)
        if (line.Trim().Length > 0)
            rows.Add(JsonSerializer.Deserialize<SpendRow>(line, Profile.Json)!);
    var r = Reconciler.Match(calls, rows);
    foreach (var (call, row) in r.Matches)
        Console.WriteLine($"{call.MessageId ?? call.Kind,-32} own={call.Usd,10:0.000000} gateway={row?.Spend,10:0.000000}");
    Console.WriteLine($"own={r.OwnUsd:0.000000} gateway={r.GatewayUsd:0.000000} unmatched_calls={r.Unmatched} spend_rows={rows.Count}");
    return r.Unmatched == 0 ? 0 : 1;
}

async Task<int> SyncPrompts()
{
    using var lf = LangfuseKeys() is { } k ? new LangfusePrompts(k.BaseUrl, k.PublicKey, k.SecretKey) : throw new InvalidOperationException("profile has no telemetry settings");
    var registry = new PromptRegistry(Path.Combine(root, "prompts"));
    foreach (var file in Directory.GetFiles(Path.Combine(root, "prompts"), "*.md", SearchOption.AllDirectories).Order())
    {
        var name = Path.GetRelativePath(Path.Combine(root, "prompts"), file)[..^3].Replace('\\', '/');
        var block = registry.Get(name);
        Console.WriteLine($"{name} {block.Version} {LangfusePrompts.Label(block.Sha256)} -> langfuse v{await lf.SyncAsync(block, ct)}");
    }
    return 0;
}

async Task<IReadOnlyDictionary<string, int>> PromptVersions()
{
    var versions = new Dictionary<string, int>();
    if (LangfuseKeys() is not { } k)
        return versions;
    using var lf = new LangfusePrompts(k.BaseUrl, k.PublicKey, k.SecretKey);
    var registry = new PromptRegistry(Path.Combine(root, "prompts"));
    foreach (var name in new[] { "intake/task-spec", $"core/{Orchestrator.NodeKindName}" })
    {
        var block = registry.Get(name);
        try
        {
            if (await lf.FindAsync(block, ct) is { } v)
                versions[block.Sha256] = v;
        }
        catch (HttpRequestException)
        {
            // Linking is optional; a run never fails because the prompt mirror is unreachable.
        }
    }
    return versions;
}

/// <summary>The orchestrator's Langfuse project (from the tracing endpoint) and its key pair, or null without telemetry.</summary>
(Uri BaseUrl, string PublicKey, string SecretKey)? LangfuseKeys() =>
    profile.Telemetry is { } t
        ? (new Uri(t.OtlpEndpoint[..t.OtlpEndpoint.IndexOf("/api/public/otel", StringComparison.Ordinal)] + "/"), profile.Secret(t.PublicKeySecret), profile.Secret(t.SecretKeySecret))
        : null;

/// <summary>Eval items, dataset runs and scores live in Langfuse (ADR 0019).</summary>
LangfuseEvals Evals() => LangfuseKeys() is { } k
    ? new LangfuseEvals(new HttpClient { BaseAddress = k.BaseUrl }, k.PublicKey, k.SecretKey)
    : throw new InvalidOperationException("profile has no telemetry settings (eval items live in Langfuse)");

TracerProvider? Tracing()
{
    if (profile.Telemetry is not { } t)
        return null;
    var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{profile.Secret(t.PublicKeySecret)}:{profile.Secret(t.SecretKeySecret)}"));
    return Sdk.CreateTracerProviderBuilder()
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("chargehand", serviceVersion: typeof(Orchestrator).Assembly.GetName().Version?.ToString()))
        .AddSource(Telemetry.Source.Name)
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(t.OtlpEndpoint);
            o.Protocol = OtlpExportProtocol.HttpProtobuf;
            o.Headers = $"Authorization=Basic {basic}";
        })
        .Build();
}
