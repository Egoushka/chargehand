// chargehand CLI. Exit codes: 0 completed, 1 failed or denied, 2 usage error, 3 needs input.
using System.Text;
using System.Text.Json;
using Chargehand;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.OpenCode;
using Chargehand.Prompts;
using Chargehand.RunLog;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

const string Usage = """
    usage: chargehand [--profile profiles/local.json] <command>
      run                          reads request/v1 on stdin, writes result/v1 on stdout
      show <run-id>                prints a run and its calls from the run log
      reconcile <run-id>           reads gateway spend rows (JSONL) on stdin, prints own vs gateway cost
      cache <run-id>               cache report: reads, writes and hit rate per call; the first block that changed
      prompts sync                 pushes prompt blocks to Langfuse prompt management
    Paths (prompts/, presets/, the run log) are relative to the current directory.
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
if (argv.Count == 0 || argv[0] is not ("run" or "show" or "reconcile" or "cache" or "prompts"))
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
    case ["show", var id]:
        return await Show(id);
    case ["reconcile", var id]:
        return await Reconcile(id);
    case ["cache", var id]:
        var (_, calls) = await runLog.ReadAsync(id, ct);
        Console.Write(CacheReport.Build(calls));
        return calls.Count == 0 ? 1 : 0;
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
    var client = new OpenCodeClient(new HttpClient { BaseAddress = new Uri(profile.Opencode.Url) }, profile.Secret(profile.Opencode.PasswordSecret));
    var runtime = await OpenCodeWorkerRuntime.ConnectAsync(client, profile.Opencode.Version, ct);
    var orchestrator = new Orchestrator(profile, runtime, runtime.Version, root, runLog, await PromptVersions());
    var result = await orchestrator.RunAsync(request, ct);
    tracing?.ForceFlush(10_000);

    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
    return result.Status switch { ResultStatus.Completed => 0, ResultStatus.NeedsInput => 3, _ => 1 };
}

async Task<int> Show(string runId)
{
    var (run, calls) = await runLog.ReadAsync(runId, ct);
    if (run is null && calls.Count == 0)
    {
        Console.Error.WriteLine($"no run {runId} in {profile.RunLog}");
        return 1;
    }
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
    var (_, calls) = await runLog.ReadAsync(runId, ct);
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
    var lf = Langfuse() ?? throw new InvalidOperationException("profile has no telemetry settings");
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
    var lf = Langfuse();
    var versions = new Dictionary<string, int>();
    if (lf is null)
        return versions;
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

LangfusePrompts? Langfuse()
{
    if (profile.Telemetry is not { } t)
        return null;
    var baseUrl = new Uri(t.OtlpEndpoint[..t.OtlpEndpoint.IndexOf("/api/public/otel", StringComparison.Ordinal)] + "/");
    return new LangfusePrompts(baseUrl, profile.Secret(t.PublicKeySecret), profile.Secret(t.SecretKeySecret));
}

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
