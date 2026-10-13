using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>A GitHub that holds issues, label events and comments in memory. Comments intake posts come back from <see cref="CommentsSinceAsync"/> as the App's own, as GitHub would list them.</summary>
internal sealed class FakeGitHub : IIntakeHost
{
    private long _next = 1000;
    private readonly HashSet<int> _pullQueued = [];

    public List<IntakeIssue> Issues { get; } = [];

    public Dictionary<int, LabelEvent> LabelEvents { get; } = [];

    public List<IntakeComment> Comments { get; } = [];

    public List<(int Issue, string Body)> Posted { get; } = [];

    public int EventReads { get; private set; }

    public bool CommentsFail { get; set; }

    public string? FailFor { get; set; }

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    private void Check(string repository)
    {
        if (repository == FailFor)
            throw new IntakeException("GitHub answered 500 to the request", 500);
    }

    public Task<IReadOnlyList<IntakeIssue>> LabeledAsync(string repository, string label, CancellationToken ct)
    {
        Check(repository);
        return Task.FromResult<IReadOnlyList<IntakeIssue>>([.. Issues.Where(i => i.Labels.Contains(label, StringComparer.OrdinalIgnoreCase))]);
    }

    public Task<LabelEvent?> LastLabelEventAsync(string repository, int number, string label, CancellationToken ct)
    {
        EventReads++;
        return Task.FromResult(LabelEvents.GetValueOrDefault(number));
    }

    public Task<IntakeIssue> GetAsync(string repository, int number, CancellationToken ct) => Task.FromResult(Issues.Single(i => i.Number == number));

    public Task<IReadOnlyList<IntakeComment>> CommentsSinceAsync(string repository, DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IntakeComment>>([.. Comments.Where(c => c.UpdatedAt >= since)]);

    public Dictionary<int, IntakePull> Pulls { get; } = [];

    public List<ReviewComment> ReviewComments { get; } = [];

    public int PullReads { get; private set; }

    /// <summary>A pull request GitHub answers 500 for once the first reads (the queueing) are done.</summary>
    public int? PullFails { get; set; }

    public Task<IntakePull> GetPullAsync(string repository, int number, CancellationToken ct)
    {
        PullReads++;
        if (PullFails == number && PullReads > 1 && _pullQueued.Contains(number))
            throw new IntakeException("GitHub answered 500 to the pull request", 500);
        _pullQueued.Add(number);
        return Task.FromResult(Pulls.TryGetValue(number, out var pull) ? pull : throw new IntakeException("GitHub answered 404 to the pull request", 404));
    }

    public Task<IReadOnlyList<ReviewComment>> ReviewCommentsSinceAsync(string repository, DateTimeOffset since, CancellationToken ct)
    {
        Check(repository);
        return Task.FromResult<IReadOnlyList<ReviewComment>>([.. ReviewComments.Where(c => c.UpdatedAt >= since)]);
    }

    public Task<IReadOnlyList<string>> CommentBodiesAsync(string repository, int number, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([.. Comments.Where(c => c.IssueNumber == number).OrderBy(c => c.Id).Select(c => c.Body)]);

    public long AddReview(int pull, string author, string body, string path, int? line, bool reply, DateTimeOffset at)
    {
        var id = _next++;
        ReviewComments.Add(new ReviewComment(id, pull, author, body, path, line, reply, at));
        return id;
    }

    public Task<long> CommentAsync(string repository, int number, string body, CancellationToken ct)
    {
        if (CommentsFail)
            throw new IntakeException("GitHub answered 500 to the comment", 500);
        var id = _next++;
        Posted.Add((number, body));
        Comments.Add(new IntakeComment(id, number, "chargehand[bot]", body, Now()));
        return Task.FromResult(id);
    }

    public long Add(int issue, string author, string body, DateTimeOffset at)
    {
        var id = _next++;
        Comments.Add(new IntakeComment(id, issue, author, body, at));
        return id;
    }
}

internal sealed class FakeStarter(FakeRunLog log) : IBatchStarter
{
    public List<RunRequest> Started { get; } = [];

    public string? Refuse { get; set; }

    public Task<BatchStart> StartAsync(RunRequest request, CancellationToken ct)
    {
        if (Refuse is not null)
            return Task.FromResult(new BatchStart(null, Refuse));
        Started.Add(request);
        var run = $"run-{Started.Count}";
        log.Start(run, request);
        return Task.FromResult(new BatchStart(run));
    }
}

internal sealed class FakeHeads : IRepositoryHeads
{
    public string Commit { get; set; } = "abc1234def5678abc1234def5678abc1234def56";

    public Task<string> CurrentCommitAsync(string path, CancellationToken ct) => Task.FromResult(Commit);
}

internal sealed class FakeRunLog : IRunLog
{
    private readonly Dictionary<string, StartRecord> _starts = [];
    private readonly Dictionary<string, RunRecord> _runs = [];

    public void Start(string run, RunRequest request) => _starts[run] = new StartRecord(run, DateTimeOffset.UtcNow, "trace", request, Environment.ProcessId, BootId: ProcessBoot.Id);

    /// <summary>Ends a batch with the outcomes of its tasks, as the driver would write them.</summary>
    public void End(string run, params TaskOutcome[] outcomes) =>
        _runs[run] = new RunRecord(run, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "driven", null, null, DrivenResult.BuildBatch(run, "trace", "", new BatchOutcome(outcomes, null), "environment", false, "1.0"));

    /// <summary>The run record of a task's own session, with the questions it asked.</summary>
    public void Asked(string run, params string[] questions) =>
        _runs[run] = new RunRecord(run, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "driven", null, null,
            DrivenResult.BuildBatch(run, "trace", "", new BatchOutcome([new TaskOutcome("t", TaskState.NeedsInput)], null), "environment", false, "1.0") with { OpenQuestions = questions });

    /// <summary>The own run of a task, as the driver writes it: a child of its batch, with the branch it pushed.</summary>
    public void TaskRun(string run, string batch, string branch, string summary = "Added the retry.")
    {
        _starts[run] = new StartRecord(run, DateTimeOffset.UtcNow, "trace", Runs.DraftRequest(), Environment.ProcessId, batch, ProcessBoot.Id);
        var result = DrivenResult.BuildBatch(run, "trace", "", new BatchOutcome([IntakeRig.Pr()], null), "environment", false, "1.0");
        _runs[run] = new RunRecord(run, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "driven", null, null, result with
        {
            Summary = summary,
            Artifacts = [Inline("branch", new { branch, commit = "def5678", @base = "abc1234def5678abc1234def5678abc1234def56", pushed = true })],
        });
    }

    private static readonly System.Text.Json.JsonSerializerOptions SnakeCase = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };

    private static Artifact Inline(string kind, object value)
    {
        var content = System.Text.Json.JsonSerializer.Serialize(value, SnakeCase);
        return new Artifact(kind, "application/json", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))), Content: content);
    }

    public void Forget(string run) => _starts.Remove(run);

    public Task AppendAsync(StartRecord record, CancellationToken ct) => Task.CompletedTask;

    public Task AppendAsync(CallRecord record, CancellationToken ct) => Task.CompletedTask;

    public Task AppendAsync(RunRecord record, CancellationToken ct) => Task.CompletedTask;

    public Task<RunEntry> ReadAsync(string runId, CancellationToken ct) => Task.FromResult(new RunEntry(_starts.GetValueOrDefault(runId), _runs.GetValueOrDefault(runId), []));

    public Task<IReadOnlyList<RunSummary>> ListAsync(RunListQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<RunSummary>>([.. _starts.Values.Select(start =>
    {
        _runs.TryGetValue(start.RunId, out var run);
        return new RunSummary("run-summary/v1", start.RunId, run is null ? RunState.Running : RunStatus.StateOf(run.Result.Status), start.Request.Context.Preset, start.Started, start.ParentRunId,
            Branch: Branch(run?.Result));
    }).OrderByDescending(r => r.StartedAt).Take(query.Limit)]);

    private static string? Branch(ResultContract? result)
    {
        if (result?.Artifacts.FirstOrDefault(a => a.Kind == "branch")?.Content is not { } content)
            return null;
        using var doc = System.Text.Json.JsonDocument.Parse(content);
        return doc.RootElement.GetProperty("branch").GetString();
    }
}

/// <summary>Intake wired to fakes, with its state in a file so a restart can be tried.</summary>
internal sealed class IntakeRig : IDisposable
{
    public const string Repo = "octo/repo";
    public const string Author = "alice";

    private readonly TempDir _dir = new();
    private readonly IntegratorPolicy? _policy;

    public IntakeRig(AutomationGitHub? settings = null, IntegratorPolicy? policy = null)
    {
        _policy = policy;
        Settings = settings ?? new AutomationGitHub(new Dictionary<string, AutomationRepository> { [Repo] = new("/srv/repos/repo") }, [Author]);
        Starter = new FakeStarter(Log);
        GitHub.Now = () => Time.GetUtcNow();
        Intake = Build();
    }

    public AutomationGitHub Settings { get; }

    public FakeGitHub GitHub { get; } = new();

    public FakeRunLog Log { get; } = new();

    public FakeStarter Starter { get; }

    public FakeHeads Heads { get; } = new();

    public ManualTimeProvider Time { get; } = new();

    public List<string> Warnings { get; } = [];

    public GitHubIntake Intake { get; private set; }

    public DateTimeOffset Now => Time.GetUtcNow();

    private string StatePath => Path.Combine(_dir.Path, "intake", "github.json");

    /// <summary>The state file itself, for a test that checks what is written to disk.</summary>
    public string StateFile => StatePath;

    public IntakeState State => IntakeState.Load(StatePath);

    private GitHubIntake Build() => new(Settings, GitHub, Log, Heads, IntakeState.Load(StatePath), Time, Warnings.Add, _policy);

    /// <summary>A new process on the same state file.</summary>
    public void Restart() => Intake = Build();

    public Task Poll() => Intake.PollAsync(Starter, default);

    public static IntakeIssue Issue(int number, string title = "Add a retry", string body = "The client gives up on the first error.", string author = "bob", IEnumerable<string>? labels = null,
        DateTimeOffset? updated = null) => new(number, title, body, author, false, [.. labels ?? []], updated ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>An open issue that carries the trigger label, applied by <paramref name="actor"/>.</summary>
    public IntakeIssue Labeled(int number, string actor = Author, long eventId = 1, params string[] more)
    {
        var issue = Issue(number, labels: ["chargehand", .. more], updated: Now);
        GitHub.Issues.RemoveAll(i => i.Number == number);
        GitHub.Issues.Add(issue);
        GitHub.LabelEvents[number] = new LabelEvent(eventId, actor);
        return issue;
    }

    /// <summary>An issue with no label, for a comment to mention the bot on.</summary>
    public IntakeIssue Plain(int number, string title = "Add a retry")
    {
        var issue = Issue(number, title, updated: Now);
        GitHub.Issues.Add(issue);
        return issue;
    }

    /// <summary>A pull request chargehand opened for <paramref name="branch"/> (a task's pull request), open and in the repository itself.</summary>
    public IntakePull Pull(int number, string branch = "chargehand/task-run", bool open = true, string? headRepository = null)
    {
        var pull = new IntakePull(number, branch, headRepository ?? Repo, Repo, open);
        GitHub.Pulls[number] = pull;
        GitHub.Issues.RemoveAll(i => i.Number == number);
        GitHub.Issues.Add(new IntakeIssue(number, "A pull request", "", "chargehand[bot]", true, [], Now));
        return pull;
    }

    public long Review(int pull, string author, string body, string path = "src/Retry.cs", int? line = 12, bool reply = false)
    {
        Time.Advance(TimeSpan.FromSeconds(1));
        return GitHub.AddReview(pull, author, body, path, line, reply, Now);
    }

    public long Comment(int issue, string author, string body)
    {
        Time.Advance(TimeSpan.FromSeconds(1));
        return GitHub.Add(issue, author, body, Now);
    }

    public static TaskOutcome Pr(string task = "issue-7", string url = "https://github.example.test/octo/repo/pull/9", string run = "task-run") =>
        new(task, TaskState.Completed, run, null, null, 0, 100, "chargehand/" + run, url);

    public void Dispose() => _dir.Dispose();
}
