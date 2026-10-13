using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.RunLog;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>A server over a scripted runtime with the pieces the console's read routes draw on: the run log, the learning records, the saved batch state, intake's state file and a profile. Tests
/// seed the records an earlier batch would have left and read the routes back.</summary>
internal sealed class ConsoleRig : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public const string Commit = "abc1234def5678abc1234def5678abc1234def56";

    private ConsoleRig(TestServer server, JsonlLearningStore learning, string batches, string intake, DrivenRun driven, Profile profile, string root)
    {
        (Server, Learning, BatchDirectory, IntakePath, Driven, Profile, Root) = (server, learning, batches, intake, driven, profile, root);
    }

    public TestServer Server { get; }

    public JsonlLearningStore Learning { get; }

    public string BatchDirectory { get; }

    public string IntakePath { get; }

    public DrivenRun Driven { get; }

    public Profile Profile { get; }

    public string Root { get; }

    public JsonlRunLog Log => Server.Log;

    public HttpClient Owner => Server.Http;

    /// <param name="profile">The profile, from the server's root directory; the default is a profile with driven sessions off.</param>
    /// <param name="services">The container engine and friends, from the root; null: driven sessions are off (the server has the saved state and the learning records only).</param>
    /// <param name="accountsFor">The accounts from the server's root directory, for an account whose rules name a path under it; wins over <paramref name="accounts"/>.</param>
    /// <param name="hold">The scripted runtime holds every run unfinished.</param>
    public static async Task<ConsoleRig> StartAsync(IReadOnlyList<AccountSettings>? accounts = null, Func<string, Profile>? profile = null, Func<string, bool>? secretPresent = null,
        Func<string, DrivenServices>? services = null, Func<string, IReadOnlyList<AccountSettings>>? accountsFor = null, bool hold = false)
    {
        var clock = new ManualTimeProvider();
        clock.Advance(Now - clock.GetUtcNow());
        string root = "", batches = "", intake = "";
        DrivenRun? driven = null;
        JsonlLearningStore? learning = null;
        Profile? built = null;
        var runtime = new ScriptedRuntime(Runs.DraftReply);
        if (hold)
            runtime.Hold = new();
        var server = await TestServer.StartAsync(runtime, accounts: accounts, clock: clock,
            driven: (dir, log) =>
            {
                learning = new JsonlLearningStore(Path.Combine(dir, "learning.jsonl"));
                return driven = new DrivenRun(built!, log, Repo.Root, services?.Invoke(dir), batches, learning);
            },
            // The server's settings are made before its driven run, so the profile is made here.
            tweak: (dir, settings) =>
            {
                (root, batches, intake) = (dir, Path.Combine(dir, "batches"), Path.Combine(dir, "intake", "github.json"));
                built = profile?.Invoke(dir) ?? Runs.Profile(dir);
                return settings with { Profile = built, SecretPresent = secretPresent, IntakeStatePath = intake, Accounts = accountsFor?.Invoke(dir) ?? settings.Accounts };
            });
        return new ConsoleRig(server, learning!, batches, intake, driven!, built!, root);
    }

    /// <summary>A key for <paramref name="account"/>; the account must be declared.</summary>
    public HttpClient As(string account) => Server.As(Server.Keys.Create(account, "test").Key);

    public ValueTask DisposeAsync()
    {
        Driven.Dispose();
        return Server.DisposeAsync();
    }

    public static async Task<JsonElement> Get(HttpClient http, string path)
    {
        var res = await http.GetAsync(path);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{path}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<HttpStatusCode> Status(HttpClient http, string path) => (await http.GetAsync(path)).StatusCode;

    /// <summary>The body as the server sent it, for assertions that something never appears in it.</summary>
    public static async Task<string> Text(HttpClient http, string path)
    {
        var res = await http.GetAsync(path);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{path}: {(int)res.StatusCode} {text}");
        return text;
    }
}

/// <summary>What one task of a seeded batch ended as.</summary>
/// <param name="Status">The word in the <c>driven-batch</c> artifact: completed, failed, needs_input, not_started or cancelled.</param>
internal sealed record SeedTask(string Id, string Status = "completed", string? Goal = null, string? PrUrl = null, string? Branch = null, ErrorCode? Error = null, string[]? Questions = null,
    IReadOnlyList<Artifact>? Artifacts = null, string? Detail = null, DrivenTask? Asked = null, bool NoRun = false);

internal static class ConsoleSeed
{
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static string TaskRun(string batch, string task) => $"{batch}-{task}";

    public static Artifact Inline(string kind, object content)
    {
        var text = JsonSerializer.Serialize(content, Snake);
        return new Artifact(kind, "application/json", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), Content: text);
    }

    public static ResultContract Result(string runId, ResultStatus status, string summary, IReadOnlyList<Artifact>? artifacts = null, ResultError? error = null, IReadOnlyList<string>? questions = null,
        Usage? usage = null, string? actor = null) =>
        new("result/v1", runId, "session", new string('0', 32), new PromptChain([], new AsSent("claude-code/2.1.283", "session", "m", "2026-10-15")), status, summary, [], [], artifacts ?? [], questions ?? [], 0.7,
            usage ?? new Usage(1000, 200, 3000, 400, null), error, Actor: actor);

    public static StartRecord Start(string runId, RunRequest request, string? actor, DateTimeOffset at, string? parent = null) =>
        new(runId, at, new string('0', 32), request, Environment.ProcessId, parent, ProcessBoot.Id, actor);

    public static RunRecord Finish(StartRecord start, ResultContract result, DateTimeOffset at) =>
        new(start.RunId, start.Started, at, start.Request.Context.Preset, null, null, result, Actor: start.Actor);

    public static RunRequest BatchRequest(params DrivenTask[] tasks) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", ConsoleRig.Commit)),
            Driven: new RequestDriven([.. tasks], MaxParallel: 2, MaxTokensTotal: 5_000_000, MaxUsdTotal: 4m));

    /// <summary>A finished batch as the run log keeps it: the batch's start and run record, and a start and run record for each task that ran.</summary>
    public static async Task<string> BatchAsync(ConsoleRig rig, string batchId, string? actor, params SeedTask[] tasks) =>
        await BatchAsync(rig.Log, batchId, actor, ConsoleRig.Now.AddHours(-2), tasks);

    public static async Task<string> BatchAsync(JsonlRunLog log, string batchId, string? actor, DateTimeOffset at, params SeedTask[] tasks)
    {
        var request = BatchRequest([.. tasks.Select(t => t.Asked ?? new DrivenTask(t.Id, Goal: t.Goal ?? $"goal of {t.Id}"))]);
        var start = Start(batchId, request, actor, at);
        await log.AppendAsync(start, default);
        foreach (var t in tasks.Where(t => !t.NoRun))
        {
            var run = TaskRun(batchId, t.Id);
            var taskStart = Start(run, request with { Text = t.Goal ?? $"goal of {t.Id}", Driven = null }, actor, at.AddMinutes(1), batchId);
            await log.AppendAsync(taskStart, default);
            var status = t.Status switch { "completed" => ResultStatus.Completed, "needs_input" => ResultStatus.NeedsInput, _ => ResultStatus.Failed };
            await log.AppendAsync(Finish(taskStart, Result(run, status, $"summary of {t.Id}", t.Artifacts, t.Error is { } code ? new ResultError(code, t.Detail ?? "it failed", false, "send it again") : null, t.Questions, actor: actor), at.AddMinutes(30)), default);
        }
        var rows = tasks.Select(t => new
        {
            id = t.Id,
            run_id = t.NoRun ? null : TaskRun(batchId, t.Id),
            status = t.Status,
            branch = t.Branch,
            pr_url = t.PrUrl,
            usd = (decimal?)null,
            tokens = 1200,
            error_code = t.Error is { } e ? JsonNamingPolicy.SnakeCaseLower.ConvertName(e.ToString()) : null,
            detail = t.Detail,
        });
        var allDone = tasks.All(t => t.Status == "completed");
        var result = Result(batchId, allDone ? ResultStatus.Completed : ResultStatus.Failed, allDone ? "All tasks ended in a draft pull request." : "some tasks did not produce a pull request",
            [Inline("driven-batch", new { credential_delivery = "token_exchange", tasks = rows })], allDone ? null : new ResultError(ErrorCode.TasksIncomplete, "some tasks did not produce a pull request", false, "send them again"),
            actor: actor) with
        { Usage = new Usage(2400, 0, 0, 0, null) };
        await log.AppendAsync(Finish(start, result, at.AddHours(1)), default);
        return batchId;
    }

    public static LearningRecord Learned(string batch, string task, string outcome = LearningOutcome.Completed, string? actor = null, string repository = "o/r", string size = "M", DateTimeOffset? at = null,
        LearningPr? pr = null, bool integrityFlag = false, int continuations = 0, IReadOnlyList<GovernorCount>? governor = null, string? mode = "observe", decimal? usd = 1.5m,
        BudgetSource? source = null) =>
        new(batch, task, TaskRun(batch, task), at ?? ConsoleRig.Now.AddHours(-1), repository, size, "caller", new LearningBudget(4m, source ?? new BudgetSource("history", 22, 80, "repository")),
            new LearningSpend(usd, false, 1000, 200, 3000, 400, 31, continuations), outcome, outcome == LearningOutcome.Completed ? "completed" : "turn_limit", mode, governor ?? [], integrityFlag, pr, actor);

    public static LearningPr Pull(int number, string name = "r", string owner = "o", string? head = "aaaa") =>
        new($"https://example.test/{owner}/{name}/pull/{number}", number, new RemoteRepository(owner, name), head);

    public static Task LearnAsync(ConsoleRig rig, LearningRecord record) => rig.Learning.AppendAsync(record, default);

    public static Task OutcomeAsync(ConsoleRig rig, LearningRecord record, string outcome) =>
        rig.Learning.AppendAsync(new PrOutcomeRecord(record.RunId, ConsoleRig.Now.AddMinutes(-10), outcome, record.Pr?.HeadSha), default);
}
