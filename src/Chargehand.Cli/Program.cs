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
using Chargehand.Kernel;
using Chargehand.Mcp;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Memory.Api;
using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.OpenCode;
using Chargehand.Prompts;
using Chargehand.RunLog;
using Chargehand.Runtime;
using Chargehand.Runtimes.ClaudeCode.Api;
using Chargehand.Server;
using Chargehand.Verification;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

const string Usage = """
    usage: chargehand [--profile profiles/local.json] <command>
      run                          reads request/v1 on stdin, writes result/v1 on stdout
      serve                        HTTP /v1/runs and MCP /v1/mcp (profile http; 127.0.0.1 by default)
      mcp                          the MCP tool over stdio, for a client that starts chargehand itself
      verify <result.json> --public-key <key.pem>
                                   checks a signed result offline; exit 0 valid, 1 invalid or unsigned, 2 usage
      egress --listen <ip:port> --allow <host,*.suffix,...>
                                   the allowlist proxy of a driven-session batch's egress container (CONNECT to port 443 only)
      runner --listen <ip:port> --images <digest,...> --egress-image <digest>
                                   the service in front of the container engine for driven sessions (key in CHARGEHAND_RUNNER_KEY)
      runs adherence <stream.jsonl>...
                                   whether driven sessions kept to the change skill's steps, from their stored streams
      runs kill --all              removes every driven-session container and batch network by label (works with the server down)
      learn report [--since <days>d|<time>]
                                   what finished driven tasks taught, per repository and size class: spend, completion, governor checkpoints,
                                   how their pull requests ended (reads learning.jsonl next to the run log; the server writes it)
      watch <run-id> [--url <server>]
                                   one line per event of a run on a server until its result; exit 0 completed, 1 failed, 3 needs input
                                   (server: --url or CHARGEHAND_URL, key: CHARGEHAND_API_KEY, else the profile's http block)
      cancel <run-id> [--url <server>]
                                   asks the server to cancel a run
      accounts add <account> [--label <text>] | list | revoke <key-id>
                                   keys of the accounts in the profile's http.accounts; a new key is printed once (ADR 0050)
      show <run-id>                prints a run and its calls from the run log
      why <path>:<line> [--repo <dir>] [--stream <stream.jsonl>]
                                   which run wrote that line (git blame, the commit's Chargehand-* trailers, then the run log's goal and the
                                   batch records' session); with --stream, the session messages that edited the file. Works without a profile
      reconcile <run-id>           reads gateway spend rows (JSONL) on stdin, prints own vs gateway cost
      cache <run-id>               cache report: reads, writes and hit rate per call; the first block that changed
      routes [run-log.jsonl ...]   routing report per preset, node kind and model; suggestions only
      score <run-id> <0-1> [name]  records a hand score for a run (name defaults to quality)
      eval seed <cell> <run-id>... proposes eval items (JSONL on stdout) from runs in the log, for review
      eval support <examples.jsonl> runs the support judge over labelled claims and prints agreement and every miss
      eval score-claims <claims.jsonl> --repo <dir> --commit <sha> [--out <verdicts.jsonl>]   resolves each claim's path:line locators at the commit and judges the cited text
      eval push <cell>             pushes reviewed items (JSONL on stdin) to the cell's Langfuse dataset
      eval gate <base> <change> [--cells a,b] [--changed-files f] [--pr-body f] [--name n] [--cells-file f]
                                   [--change-cells-file f] [--allow-uncovered]
                                   paired runs of the base and change prompts/ and presets/; exit 1 when blocked
      prompts sync                 pushes prompt blocks to Langfuse prompt management
      extensions check [--preset <name>] [--probe <query>]
                                   connects each mcp_servers entry, checks the memory mappings and the presets' services
                                   against the tools it lists; one line per item, exit 1 on a problem; --probe also runs one recall
      bench run <taskset.json> [--dry-run|--baseline] [--only <ids>] [--remote <template>] [--max-tokens <n>] [--max-usd <n>] [--out <dir>]
                                   measures driven batches on a task set of public repositories: pass rate, honest stops, spend, governor decisions
                                   (--dry-run and --baseline call no model; `chargehand bench run` alone prints its full usage)
      bench report <results.json> [<other.json>]
                                   renders a benchmark's results again, or compares two runs
      init [<repo path>] [--profile <out path>] [--force]
                                   reads a repository's marker files, prints its ecosystems, the proposed context.verify and the
                                   package hosts the driven egress allowlist lacks, and writes a starter profile (never over a file without --force)
      doctor [--profile <path>]    checks a setup, one ok/warn/fail line per check with the fix; exit 1 when a check fails
      incident <repo path> [--commit <ref>]
                                   reads an alert on stdin (Alertmanager or Grafana webhook JSON, or text) and prints a request/v1 for the incident preset; pipe it to run
      release-notes <repo path> --from <tag or commit> [--to <ref>]
                                   prints a request/v1 for the release-notes preset, the range's commits as inputs (git only); pipe it to run
    prompts/ and presets/ come from the current directory when it has both (a checkout), else from the install; the
    run log is the profile's run_log, else runs/run-log.jsonl in a checkout, else a per-user file (README).
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
// `bench run <set> --profile <path>` reads like the other verbs' global option.
if (argv is ["bench", ..] && argv.IndexOf("--profile") is var benchProfileAt and >= 0 && benchProfileAt + 1 < argv.Count)
{
    profilePath = argv[benchProfileAt + 1];
    argv.RemoveRange(benchProfileAt, 2);
}
// Offline and profile-free: a verifier holds a result and a public key, nothing else (ADR 0036).
if (argv is ["verify", .. var verifyArgs])
    return Chargehand.Signing.VerifyCli.Run(verifyArgs, Console.Out, Console.Error);
// Reads stored session streams only (ADR 0039).
if (argv is ["runs", "adherence", .. var adherenceArgs])
    return Chargehand.Modules.Sessions.Api.AdherenceCli.Run(adherenceArgs, Console.Out, Console.Error);
// A client of a running server: it reads the profile only for what --url and the environment leave out.
if (argv is ["watch" or "cancel", var watchRun, .. var watchOptions])
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    var (client, problem) = Chargehand.Watch.WatchCli.Connect(watchOptions, Environment.GetEnvironmentVariable, ProfileServer);
    if (client is null)
    {
        Console.Error.WriteLine(problem);
        return 2;
    }
    using (client)
        return argv[0] == "watch"
            ? await Chargehand.Watch.WatchCli.WatchAsync(client, watchRun, Console.Out, Console.Error, stop.Token)
            : await Chargehand.Watch.WatchCli.CancelAsync(client, watchRun, Console.Out, Console.Error, stop.Token);
}
// Profile-free too: the egress container of a driven-session batch holds an allowlist and nothing else (ADR 0039).
if (argv is ["egress", .. var egressArgs])
{
    using var stop = new CancellationTokenSource();
    using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, _ => stop.Cancel());
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    return await Chargehand.Modules.Gateway.Api.EgressCli.RunAsync(egressArgs, Console.Error, stop.Token);
}
// The runner service holds the container engine's socket so `serve` never has to (ADR 0039); it is profile-free as well.
if (argv is ["runner", .. var runnerArgs])
{
    using var stop = new CancellationTokenSource();
    using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, _ => stop.Cancel());
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    return await Chargehand.Server.RunnerCli.RunAsync(runnerArgs, Console.Error, stop.Token);
}
// The command a driven-session container runs (ADR 0039); not listed in the usage: it is not for a person to type.
if (argv is ["session", .. var sessionArgs])
{
    using var stop = new CancellationTokenSource();
    using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, _ => stop.Cancel());
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    return await Chargehand.Modules.Sessions.Api.SessionCli.RunAsync(sessionArgs, Console.Error, stop.Token);
}
// What a fresh verification container runs (ADR 0039); not for a person to type.
if (argv is ["verify-branch", .. var verifyBranchArgs])
{
    using var stop = new CancellationTokenSource();
    using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, _ => stop.Cancel());
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    return await Chargehand.Driven.VerifyBranchCli.RunAsync(verifyBranchArgs, Console.Out, Console.Error, stop.Token);
}
// Benchmarks (ADR 0052): a report is a file read; a run reads the profile for the images, the allowlist and the server it may reach.
if (argv is ["bench", "report", .. var benchReportArgs])
    return Chargehand.Bench.BenchCli.Report(benchReportArgs, Console.Out, Console.Error);
if (argv is ["bench", "run", .. var benchRunArgs])
{
    Profile benchProfile;
    try
    {
        benchProfile = Profile.Load(profilePath);
    }
    catch (ChargehandException e)
    {
        Console.Error.WriteLine($"{profilePath}: {e.Message}");
        Console.Error.WriteLine(e.Action);
        return 2;
    }
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    return await Chargehand.Bench.BenchCli.RunAsync(benchRunArgs, benchProfile, DrivenPresetAllow(), BenchLimits(), () => BenchEngines(benchProfile),
        connectArgs => Chargehand.Watch.WatchCli.Connect(connectArgs, Environment.GetEnvironmentVariable, ProfileServer), Environment.GetEnvironmentVariable,
        Directory.GetCurrentDirectory(), Console.Out, Console.Error, stop.Token);
}
if (argv is ["bench", ..])
{
    Console.Error.WriteLine(Chargehand.Bench.BenchCli.Usage);
    return 2;
}
// Profile-free: init writes the profile, and doctor reports one that does not load.
if (argv is ["init", .. var initArgs])
    return Chargehand.Onboarding.InitCli.Run(initArgs, Directory.GetCurrentDirectory(), profilePath, DrivenPresetAllow(), Console.Out, Console.Error);
// Profile-free: it maps an alert to a request for `run` (CHARGEHAND-192).
if (argv is ["incident", .. var incidentArgs])
    return await Chargehand.Incident.IncidentCli.RunAsync(incidentArgs, Directory.GetCurrentDirectory(), Console.In, Console.Out, Console.Error, DateTimeOffset.UtcNow, CancellationToken.None);
// Profile-free: it reads a repository's git log and prints a request for `run` (CHARGEHAND-191).
if (argv is ["release-notes", .. var releaseNotesArgs])
    return await Chargehand.ReleaseNotes.ReleaseNotesCli.RunAsync(releaseNotesArgs, Directory.GetCurrentDirectory(), Console.Out, Console.Error, CancellationToken.None);
// Reads git blame and, where it can, the run log (ADR 0074): with no profile or no log it still answers from the commit's trailers.
if (argv is ["why", .. var whyArgs])
{
    var whySetting = Profile.Load(profilePath).RunLog;
    var (_, whyLogPath) = InstallPaths.Resolve(whySetting, Directory.GetCurrentDirectory(), AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify));
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    return await Chargehand.Modules.Tasks.Api.WhyCli.RunAsync(whyArgs, new JsonlRunLog(whyLogPath), Path.Combine(Path.GetDirectoryName(Path.GetFullPath(whyLogPath))!, "records"),
        Directory.GetCurrentDirectory(), Console.Out, Console.Error, stop.Token);
}
if (argv is ["doctor", .. var doctorArgs])
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    return await Chargehand.Onboarding.DoctorCli.RunAsync(doctorArgs, profilePath, new Chargehand.Onboarding.SystemDoctorEnvironment(), DrivenPresetAllow(), Console.Out, Console.Error, stop.Token);
}
if (argv.Count == 0 || argv[0] is not ("run" or "serve" or "mcp" or "show" or "reconcile" or "cache" or "routes" or "score" or "eval" or "prompts" or "extensions" or "runs" or "accounts" or "learn"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

Profile profile;
try
{
    profile = Profile.Load(profilePath);
}
catch (ChargehandException e)
{
    Console.Error.WriteLine($"{profilePath}: {e.Message}");
    Console.Error.WriteLine(e.Action);
    return 2;
}
var (root, runLogPath) = InstallPaths.Resolve(profile.RunLog, Directory.GetCurrentDirectory(), AppContext.BaseDirectory,
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify));
var runLog = new JsonlRunLog(runLogPath);
// What finished driven tasks taught (ADR 0045): the server appends to it, `learn report` reads it.
var learning = new Chargehand.Modules.Oversight.Api.JsonlLearningStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runLogPath))!, "learning.jsonl"));
// One pool for memory and services (ADR 0034). Connections open on first use, so a command that uses neither never connects; disposed when the process ends.
await using var mcpPool = new McpConnectionPool(profile.McpServers ?? new Dictionary<string, McpServerSettings>(), profile.Secret);
var services = new ServiceResolver(mcpPool);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

switch (argv)
{
    case ["run"]:
        return await Run();
    case ["serve"]:
        return await Serve();
    case ["mcp"]:
        return await Mcp();
    case ["accounts", .. var accountArgs]:
        return AccountsCli.Run(accountArgs, profile, AccountsCli.KeysFile(runLogPath), Console.Out, Console.Error);
    case ["show", var id]:
        return await Show(id);
    case ["runs", "kill", "--all"]:
        return await Chargehand.Modules.Runners.Api.RunsCli.KillAllAsync(new Chargehand.Modules.Runners.Api.DockerCliEngine(), Console.Out, Console.Error, ct);
    case ["learn", "report", .. var learnOptions]:
        return await Chargehand.Modules.Oversight.Api.LearningCli.RunAsync(learnOptions, learning, Console.Out, Console.Error, DateTimeOffset.UtcNow, ct);
    case ["reconcile", var id]:
        return await Reconcile(id);
    case ["cache", var id]:
        var calls = (await runLog.ReadAsync(id, ct)).Calls;
        Console.Write(CacheReport.Build(calls));
        return calls.Count == 0 ? 1 : 0;
    case ["routes", .. var logs]:
        var data = await Task.WhenAll((logs.Count == 0 ? [runLogPath] : logs).Select(l => new JsonlRunLog(l).ReadAllAsync(ct)));
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
    case ["eval", "support", var examplesFile]:
        return await EvalSupport(examplesFile);
    case ["eval", "score-claims", var claimsFile, .. var scoreOptions]:
        return await EvalScoreClaims(claimsFile, scoreOptions);
    case ["eval", "gate", var baseRoot, var changeRoot, .. var options]:
        return await EvalGate(Path.GetFullPath(baseRoot), Path.GetFullPath(changeRoot), options);
    case ["prompts", "sync"]:
        return await SyncPrompts();
    case ["extensions", "check", .. var options]:
        return await CheckExtensions(options);
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

/// <summary>The hosts presets/driven.yaml lets a session reach; none when the preset cannot be read (init and doctor then treat every package host as missing).</summary>
IReadOnlyList<string> DrivenPresetAllow()
{
    var (installRoot, _) = InstallPaths.Resolve(null, Directory.GetCurrentDirectory(), AppContext.BaseDirectory, "");
    try
    {
        return Preset.Load(Path.Combine(installRoot, "presets"), "driven").Driven?.Allow ?? [];
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        return [];
    }
}

/// <summary>The server and key the profile's http block names, for a client verb that was given neither.</summary>
(string Url, string Key)? ProfileServer()
{
    try
    {
        var p = Profile.Load(profilePath);
        return p.Http is { } http ? ($"http://{(http.Listen is "0.0.0.0" or "::" ? "127.0.0.1" : http.Listen)}:{http.Port}", p.Secret(http.ApiKeySecret)) : null;
    }
    catch (Exception e) when (e is ChargehandException or InvalidOperationException)
    {
        return null;
    }
}

/// <summary>The container limits of the driven preset, so a benchmark's acceptance check gets what a session's own test run got.</summary>
Chargehand.Bench.AcceptanceLimits BenchLimits()
{
    var (installRoot, _) = InstallPaths.Resolve(null, Directory.GetCurrentDirectory(), AppContext.BaseDirectory, "");
    try
    {
        return Preset.Load(Path.Combine(installRoot, "presets"), "driven").Driven is { } d ? new(d.MemoryMb, d.Cpus, d.Pids) : new(4096, 2, 512);
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        return new(4096, 2, 512);
    }
}

/// <summary>The engine a benchmark's acceptance checks start containers with: the runner service when the profile names one, else this machine's Docker.</summary>
(Chargehand.Modules.Runners.Api.IContainerEngine, Chargehand.Modules.Runners.Api.IWorkspaceEngine, Chargehand.Modules.Runners.Api.IOutVolumeEngine) BenchEngines(Profile p)
{
    // A benchmark's acceptance checks read the server's checkouts, so they use a runner that shares its disk (ADR 0058).
    if (p.Driven?.ResolvedRunners().FirstOrDefault(r => r.Workspace == Chargehand.Modules.Runners.Api.WorkspaceMode.Shared) is { } runner)
    {
        var http = new HttpClient { BaseAddress = new Uri(runner.Url) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.Secret(runner.ApiKeySecret));
        var client = new Chargehand.Modules.Runners.Api.RunnerClient(http);
        return (client, client, client);
    }
    var docker = new Chargehand.Modules.Runners.Api.DockerCliEngine();
    return (docker, docker, docker);
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
        var orchestrator = new Orchestrator(profile.WithLaunchDirectory(Directory.GetCurrentDirectory()), runtime, runtimeVersion, root, runLog, await PromptVersions(), Memory(), services, enhancer: Enhancer());
        result = await orchestrator.RunAsync(request, ct);
    }
    catch (ChargehandException e)
    {
        // The runtime refused before a run started (not running, another version): no prompt was sent, so the chain is empty.
        result = new ResultContract("result/v1", RunIds.New(), "run", ActivityTraceId.CreateRandom().ToHexString(),
            new PromptChain([], new AsSent("", "", "", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), ResultStatus.Failed, e.Message, [], [], [], [e.Message], 0,
            new Usage(0, 0, 0, 0, 0), e.Error);
    }
    tracing?.ForceFlush(10_000);

    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
    return result.Status switch { ResultStatus.Completed => 0, ResultStatus.NeedsInput => 3, _ => 1 };
}

async Task<int> EvalSupport(string examplesFile)
{
    var (runtime, _) = await Connect();
    var outcomes = await SupportEval.RunAsync(runtime, Orchestrator.ParseModel(profile.IntakeModel), SupportEval.Load(examplesFile), ct);
    Console.WriteLine($"judge model: {profile.IntakeModel ?? "the runtime's default"}");
    Console.WriteLine(SupportEval.Report(outcomes));
    return outcomes.All(o => o.Got is not null) ? 0 : 1;
}

async Task<int> EvalScoreClaims(string claimsFile, IReadOnlyList<string> options)
{
    string? Option(string name) => options.SkipWhile(o => o != name).Skip(1).FirstOrDefault();
    if (Option("--repo") is not { } repo || Option("--commit") is not { } commit)
    {
        Console.Error.WriteLine("usage: eval score-claims <claims.jsonl> --repo <dir> --commit <sha> [--out <verdicts.jsonl>]");
        return 2;
    }
    var (runtime, _) = await Connect();
    var scope = new EvidenceScope(Path.GetFullPath(repo), commit, new HashSet<string>(), new HashSet<string>(), "", []);
    var scored = await ClaimScorer.ScoreAsync(runtime, Orchestrator.ParseModel(profile.IntakeModel), ClaimScorer.ReadClaims(File.ReadLines(claimsFile)), scope, ct);
    if (Option("--out") is { } outFile)
        File.WriteAllLines(outFile, ClaimScorer.ToJsonl(scored));
    Console.WriteLine($"judge model: {profile.IntakeModel ?? "the runtime's default"}");
    Console.WriteLine(ClaimScorer.Report(scored));
    return scored.Any(s => s.Outcome == ClaimOutcome.Unchecked) ? 1 : 0;
}

/// <summary>Driven sessions (ADR 0039): null while the profile has them off, so nothing about containers is read or connected.</summary>
Chargehand.Driven.DrivenRun? DrivenRunFor(Orchestrator orchestrator)
{
    // Batch state and the run-token key sit next to the run log (ADR 0043).
    var stateDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runLogPath))!, "batches");
    // What the operator console reads about a batch (events, sessions, governor decisions, checks) sits there too, one directory per batch.
    var records = new BatchRecords(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runLogPath))!, "records"));
    if (profile.Driven is not { Enabled: true } driven)
        return new Chargehand.Driven.DrivenRun(profile, runLog, root, null, learning: learning, records: records);
    Chargehand.Modules.Runners.Api.IContainerEngine engine;
    Chargehand.Modules.Runners.Api.IWorkspaceEngine workspace;
    Chargehand.Modules.Runners.Api.IHostSource hostSource;
    IReadOnlyList<Chargehand.Driven.RunnerSite>? sites = null;
    Chargehand.Modules.Runners.Api.RunnerClient RunnerClientFor(Chargehand.Modules.Runners.Api.DrivenRunnerEntry runner)
    {
        // The client times each call out itself (a control call, or a transfer of repository size), so the HttpClient's 100 seconds must not cut a transfer short (ADR 0058).
        var http = new HttpClient { BaseAddress = new Uri(runner.Url), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", profile.Secret(runner.ApiKeySecret));
        return new Chargehand.Modules.Runners.Api.RunnerClient(http);
    }
    var bundles = new Chargehand.Modules.Runners.Api.RepositoryBundles(Path.Combine(profile.WorkerRoot, ".driven", "bundles"));
    if (driven.ResolvedRunners() is [var runner, ..])
    {
        var client = RunnerClientFor(runner);
        (engine, workspace, hostSource) = (client, client, client);
    }
    else
    {
        var docker = new Chargehand.Modules.Runners.Api.DockerCliEngine();
        (engine, workspace, hostSource) = (docker, docker, new Chargehand.Modules.Runners.Api.HostProbe());
    }
    // Both engines move files through a volume with a helper started from the session image; without an image no batch starts anyway.
    Chargehand.Modules.Sessions.Api.ISessionVolumes volumes = engine is Chargehand.Modules.Runners.Api.IOutVolumeEngine files && driven.Images is { Count: > 0 } images
        ? new Chargehand.Modules.Sessions.Api.EngineSessionVolumes(files, images[0])
        : new Chargehand.Modules.Sessions.Api.UnavailableSessionVolumes();
    // A profile that names its runners (ADR 0058) gets a pool: each runner has its own client, and its volumes' helper starts the image for its platform.
    if (driven.NamesRunners)
        sites = [.. driven.ResolvedRunners().Select(entry =>
        {
            var client = RunnerClientFor(entry);
            return new Chargehand.Driven.RunnerSite(entry, client, entry.Workspace == Chargehand.Modules.Runners.Api.WorkspaceMode.Bundle ? new Chargehand.Modules.Runners.Api.BundleWorkspaceEngine(client, client, bundles) : client, driven.Images is { Count: > 0 } list
                ? new Chargehand.Modules.Sessions.Api.EngineSessionVolumes(client, driven.ImageFor(list[0], entry.Platform))
                : new Chargehand.Modules.Sessions.Api.UnavailableSessionVolumes(), client);
        })];
    // The planning and retro runs belong to the account of the batch they serve (ADR 0050).
    Chargehand.Modules.Batches.Api.PlanRunner childRun = async (request, runId, parentRunId, token) =>
        await orchestrator.RunAsync(request, token, runId, parentRunId: parentRunId, actor: (await runLog.ReadAsync(parentRunId, token)).Start?.Actor);
    return new Chargehand.Driven.DrivenRun(profile, runLog, root, new Chargehand.Driven.DrivenServices(engine, workspace, volumes,
        push => new Chargehand.Modules.Forge.Api.GitHubPullRequests(new HttpClient { BaseAddress = GitHubApiRoot(driven) }, push.TokenAsync),
        driven.TaskSource is { } source ? new Chargehand.Mcp.McpTaskSource(source, mcpPool) : null,
        orchestrator.CheckoutForDrivenAsync, new Chargehand.Verification.GitEvidenceResolver(), profile.Secret, orchestrator.DrivenSupportCheck, Host: hostSource,
        Integration: IntegrationHost(driven), ForgeFor: (remote, token) => new Chargehand.Modules.Forge.Api.GitLabForge(
            new HttpClient { BaseAddress = Chargehand.Modules.Forge.Api.GitLabForge.ApiRoot(remote.Host!, driven.Forge?.BaseUrl) }, token),
        Planner: new Chargehand.Modules.Batches.Api.ModelPlanner(childRun, driven.Planning?.Preset ?? "cheap"),
        Retro: new Chargehand.Modules.Batches.Api.ModelRetro(childRun, driven.Learning?.RetroPreset ?? "cheap"), Runners: sites,
        AppToken: AppTokens(driven) is { } appTokens ? (remote, token) => appTokens.GetAsync(remote.Owner, remote.Name, token) : null),
        stateDirectory, learning, records);
}

// GitHub's API root: api.github.com, or the Enterprise host the profile's driven.forge names. The E2E switch exists for scripts/driven-e2e.sh only.
Uri GitHubApiRoot(Chargehand.Config.DrivenSettings driven) =>
    new(Environment.GetEnvironmentVariable("CHARGEHAND_E2E_GITHUB_API") ?? (driven.Forge is { Kind: "github", BaseUrl: { } baseUrl } ? baseUrl.TrimEnd('/') + "/" : "https://api.github.com/"));

// The App's installation tokens for pushing (CHARGEHAND-247), when driven.integrator names the App; the source lives as long as the process, so a token is kept until shortly before it expires.
Chargehand.Modules.Forge.Api.GitHubInstallationTokens? AppTokens(Chargehand.Config.DrivenSettings driven) =>
    driven.Integrator is { AppId: { } appId, PrivateKeySecret: { } privateKeySecret, InstallationId: var installationId }
        ? new Chargehand.Modules.Forge.Api.GitHubInstallationTokens(new HttpClient { BaseAddress = GitHubApiRoot(driven) }, appId, installationId, () => profile.Secret(privateKeySecret), TimeProvider.System)
        : null;

// The App's token source lives as long as the process, so a token is fetched once an hour at most (ADR 0046). The key is read from the secret store only when a token is needed.
Chargehand.Modules.Forge.Api.IIntegrationHost? IntegrationHost(Chargehand.Config.DrivenSettings driven)
{
    if (driven.Integrator is not { AppId: { } appId, PrivateKeySecret: { } privateKeySecret, InstallationId: var installationId })
        return null;
    var api = new HttpClient { BaseAddress = GitHubApiRoot(driven) };
    return new Chargehand.Modules.Forge.Api.GitHubIntegrationHost(api,
        new Chargehand.Modules.Forge.Api.GitHubInstallationTokens(api, appId, installationId, () => profile.Secret(privateKeySecret), TimeProvider.System));
}

// GitHub intake (ADR 0051): null unless the profile has automation.github. It reads and comments as the integrator's GitHub App; its state sits next to the run log.
Chargehand.Automation.GitHubIntake? GitHubIntake()
{
    if (profile.Automation?.Github is not { } github || profile.Driven is not { Integrator: { AppId: { } appId, PrivateKeySecret: { } privateKeySecret, InstallationId: var installationId } } driven)
        return null;
    var api = new HttpClient { BaseAddress = GitHubApiRoot(driven) };
    var tokens = new Chargehand.Modules.Forge.Api.GitHubInstallationTokens(api, appId, installationId, () => profile.Secret(privateKeySecret), TimeProvider.System);
    var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runLogPath))!, "intake");
    var state = Chargehand.Automation.IntakeState.Load(Path.Combine(directory, "github.json"));
    // The App's repositories (discover) and those whose checkout cannot be written are cloned under worker_root and fetched with the App's token, on this host only.
    var clones = new Chargehand.Automation.ManagedClones(profile.ClonesRoot, tokens.GetAsync);
    return new Chargehand.Automation.GitHubIntake(github, new Chargehand.Automation.GitHubIntakeHost(api, tokens), runLog, new Chargehand.Automation.GitRepositoryHeads(Console.Error.WriteLine, clones), state,
        integrator: new Chargehand.Modules.Handover.Api.IntegratorPolicy(driven.Integrator), sources: Chargehand.Automation.IntakeSources.Load(Path.Combine(directory, "sources.json")), clones: clones);
}

// Scheduled work (ADR 0068): null unless the profile has automation.schedules. Its state sits next to the run log. flaky-tests reads CI history with the forge credential the driven run reads pull requests with.
Func<Chargehand.Automation.IMorningSource, Chargehand.Automation.Scheduler>? Schedules(Chargehand.Driven.DrivenRun? driven)
{
    if (profile.Automation?.Schedules is not { } schedules)
        return null;
    var state = Chargehand.Automation.ScheduleState.Load(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runLogPath))!, "schedules", "state.json"));
    var flakes = driven is null ? null : new Chargehand.Automation.ForgeFlakeHistory(driven.ReadHost, profile.Driven?.Forge, warn: Console.Error.WriteLine);
    return morning => new Chargehand.Automation.Scheduler(schedules, runLog, new Chargehand.Automation.GitRepositoryHeads(Console.Error.WriteLine), state, new Chargehand.Automation.HttpScheduleNotifier(), morning,
        flakes: flakes);
}

// Plane intake (ADR 0067): null unless the profile has automation.plane. The key is read from the secret store at each call; its state sits next to the run log.
Chargehand.Automation.PlaneIntake? PlaneIntake()
{
    if (profile.Automation?.Plane is not { Url: { } url, ApiKeySecret: { } apiKeySecret, Workspace: { } workspace } plane)
        return null;
    var api = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") };
    var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runLogPath))!, "intake");
    var state = Chargehand.Automation.PlaneState.Load(Path.Combine(directory, "plane.json"));
    return new Chargehand.Automation.PlaneIntake(plane, new Chargehand.Automation.PlaneHost(api, workspace, () => profile.Secret(apiKeySecret)), runLog,
        new Chargehand.Automation.GitRepositoryHeads(Console.Error.WriteLine), state, sources: Chargehand.Automation.IntakeSources.Load(Path.Combine(directory, "plane-sources.json")));
}

// The console's account view says whether a credential's secret item resolves, never its value.
bool SecretResolves(string item)
{
    try
    {
        _ = profile.Secret(item);
        return true;
    }
    catch (InvalidOperationException)
    {
        return false;
    }
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
    if (Chargehand.ClaudeCode.AccountRuntimes.Unsupported(runtime, http.Accounts) is { } unsupported)
    {
        Console.Error.WriteLine(unsupported);
        return 2;
    }
    // An account's runs use its own model credential, or are refused (ADR 0053).
    Func<string?, IWorkerRuntime>? runtimeFor = http.Accounts is { Count: > 0 } accounts
        ? new Chargehand.ClaudeCode.AccountRuntimes(runtime, new AccountCredentials(accounts), profile.Secret).For : null;
    var orchestrator = new Orchestrator(profile, runtime, runtimeVersion, root, runLog, await PromptVersions(), Memory(), services, enhancer: Enhancer(), runtimeFor: runtimeFor);
    var drivenRun = DrivenRunFor(orchestrator);
    var app = ChargehandServer.Create(new ServerSettings(http.Port, profile.Secret(http.ApiKeySecret), Path.Combine(root, "presets"),
        http.Listen, http.AllowedHosts, http.MaxConcurrentRuns, profile.Driven?.MaxSessionCalls ?? 2,
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runLogPath))!, "batches"), http.Accounts, AccountsCli.KeysFile(runLogPath), Ui: http.Ui,
        Profile: profile, SecretPresent: SecretResolves, IntakeStatePath: Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runLogPath))!, "intake", "github.json"),
        PlaneStatePath: Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runLogPath))!, "intake", "plane.json"),
        InboxDismissalsPath: Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runLogPath))!, "inbox", "dismissed.json"), Access: http.CloudflareAccess, ProxySignIn: http.ProxySignIn),
        orchestrator, runLog, drivenRun, GitHubIntake(), Schedules(drivenRun), PlaneIntake());
    await app.StartAsync(ct);
    Console.Error.WriteLine($"chargehand serve: {string.Join(", ", app.Urls)} (/v1/runs, MCP /v1/mcp{(http.Ui ? ", console /console/" : "")})");
    await app.WaitForShutdownAsync(ct);
    return 0;
}

/// <summary>The orchestrate tool over stdio (ADR 0027): stdout carries only MCP messages, so nothing here writes to it.</summary>
async Task<int> Mcp()
{
    using var tracing = Tracing();
    var (runtime, runtimeVersion) = await Connect();
    var orchestrator = new Orchestrator(profile.WithLaunchDirectory(Directory.GetCurrentDirectory()), runtime, runtimeVersion, root, runLog, await PromptVersions(), Memory(), services, enhancer: Enhancer());
    using var host = ChargehandServer.CreateStdio(Path.Combine(root, "presets"), orchestrator, Console.OpenStandardInput(), Console.OpenStandardOutput());
    // The transport stops the host when the client closes stdin.
    await host.RunAsync(ct);
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
    IReadOnlyList<string> fresh = [];
    // The change's own cells only recognise files new in the change (EvalRunner.NewInChange); they never set a tolerance.
    if (Option("--change-cells-file") is { } changeCells && File.Exists(changeCells))
        (uncovered, fresh) = EvalRunner.NewInChange(uncovered, EvalCell.Load(changeCells), f => File.Exists(Path.Combine(baseRoot, f)));
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
    var allowUncovered = options.Contains("--allow-uncovered");
    var blockedByUncovered = uncovered.Count > 0 && !allowUncovered;
    foreach (var v in verdicts.Where(v => v is { Items: 0, Blocked: false }))
        Console.WriteLine($"::warning title=prompt-ci::{v.Cell}: {v.Reason}");
    foreach (var f in uncovered)
        Console.WriteLine($"uncovered: {f} (no eval cell gates it{(blockedByUncovered ? "" : "; allowed")})");
    foreach (var f in fresh)
        Console.WriteLine($"new: {f} (no base to compare; the change's eval cell gates it once merged)");
    var blocked = verdicts.Where(v => v.Blocked).ToList();
    var description = Gate.Describe(verdicts, uncovered, fresh, allowUncovered);
    // Last line for scripts/prompt-ci.sh: the commit status (a status description holds at most 140 characters).
    Console.WriteLine($"prompt-ci: {(blocked.Count > 0 || blockedByUncovered ? "failure" : "success")}: {(description.Length > 140 ? description[..137] + "..." : description)}");
    return blocked.Count > 0 || blockedByUncovered ? 1 : 0;
}

EvalCell Cell(string name) =>
    EvalCell.Load(Path.Combine(root, "evals", "cells.json")).FirstOrDefault(c => c.Name == name) ?? throw new ArgumentException($"no eval cell {name} in evals/cells.json");

async Task<(IWorkerRuntime Runtime, string Version)> Connect()
{
    var kind = RuntimeSelector.Select(profile.Runtime, Environment.GetEnvironmentVariable("CHARGEHAND_RUNTIME"), RuntimeSelector.OnPath, profile.RuntimeBlocks);
    if (kind == RuntimeKind.ClaudeCode)
    {
        var cc = profile.ClaudeCode ?? ClaudeCodeSettings.Detect(item =>
        {
            try { profile.Secret(item); return true; }
            catch (InvalidOperationException) { return false; }
        });
        var credential = (cc.ApiKeySecret, cc.OauthTokenSecret) switch
        {
            ({ } key, null) => new ClaudeCodeCredential(profile.Secret(key), Subscription: false),
            (null, { } token) => new ClaudeCodeCredential(profile.Secret(token), Subscription: true),
            (null, null) => ClaudeCodeCredential.CliLogin,
            _ => throw new ChargehandException(ErrorCode.InvalidRequest, "claude_code takes at most one of api_key_secret and oauth_token_secret",
                "Set one of claude_code.api_key_secret and claude_code.oauth_token_secret in the profile, not both."),
        };
        var headers = cc.CustomHeadersSecret is { } headersItem ? cc.CustomHeaders(profile.Secret(headersItem)) : null;
        var claude = await ClaudeCodeWorkerRuntime.ConnectAsync(cc.Binary, cc.Version, credential, ct, cc.BaseUrl is null ? null : new Uri(cc.BaseUrl), headers);
        return (claude, claude.Version);
    }
    IOpenCodeClient client;
    if (profile.Opencode is { } oc)
        client = new OpenCodeClient(new HttpClient { BaseAddress = new Uri(oc.Url) }, profile.Secret(oc.PasswordSecret));
    else
    {
        // No opencode block (ADR 0030): chargehand starts its own server and stops it when this process exits.
        var server = await OpenCodeServerProcess.StartAsync("opencode", OpenCodeServerProcess.DefaultState(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify)), ct);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => server.Dispose();
        client = server.Client();
    }
    var runtime = await OpenCodeWorkerRuntime.ConnectAsync(client, profile.Opencode?.Version ?? OpenCodeWorkerRuntime.PinnedVersion, ct);
    return (runtime, runtime.Version);
}

MemoryStack? Memory() => MemoryStacks.From(profile, mcpPool);

Chargehand.Enhancement.IPromptEnhancer? Enhancer() => PromptEnhancers.From(profile, mcpPool);

/// <summary>Setup mistakes before a run: <c>--preset</c> narrows the services check to one preset, <c>--probe</c> adds one real recall per memory.</summary>
async Task<int> CheckExtensions(IReadOnlyList<string> options)
{
    string? preset = null, probe = null;
    for (var i = 0; i < options.Count; i += 2)
    {
        var value = i + 1 < options.Count ? options[i + 1] : null;
        switch (options[i])
        {
            case "--preset" when value is not null:
                preset = value;
                break;
            case "--probe" when value is not null:
                probe = value;
                break;
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }
    IReadOnlyList<Preset> presets;
    IReadOnlyList<string> presetProblems;
    try
    {
        (presets, presetProblems) = ExtensionsCheck.LoadPresets(Path.Combine(root, "presets"), preset);
    }
    catch (ChargehandException e)
    {
        Console.Error.WriteLine(e.Message);
        Console.Error.WriteLine(e.Action);
        return 2;
    }
    ExtensionsCheckResult result;
    try
    {
        result = await ExtensionsCheck.RunAsync(profile, presets, mcpPool, probe, ct);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("extensions check: cancelled");
        return 1;
    }
    foreach (var line in result.Lines.Concat(presetProblems))
        Console.WriteLine(line);
    return result.Ok && presetProblems.Count == 0 ? 0 : 1;
}

async Task<int> Show(string runId)
{
    var (start, run, calls) = await runLog.ReadAsync(runId, ct);
    if (start is null && run is null && calls.Count == 0)
    {
        Console.Error.WriteLine($"no run {runId} in {runLogPath}");
        return 1;
    }
    if (run is null && start is not null)
        Console.WriteLine($"{start.RunId}  preset={start.Request.Context.Preset}  status={(start.OwnerAlive() ? "running" : "lost")}  started={start.Started:u}  pid={start.Pid}");
    if (run is not null)
        Console.WriteLine($"{run.RunId}  preset={run.Preset}  intake={run.IntakeAction ?? "needs_input"}  status={run.Result.Status}  " +
                          $"{(run.Finished - run.Started).TotalSeconds:0.0}s  usd={run.Result.Usage.Usd}  claims={run.Result.Claims.Count}  open={run.Result.OpenQuestions.Count}");
    foreach (var line in run?.Extensions?.Lines() ?? [])
        Console.WriteLine(line);
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
