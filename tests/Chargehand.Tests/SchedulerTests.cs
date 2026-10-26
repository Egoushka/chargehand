using Chargehand.Automation;
using Chargehand.Contracts;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

internal sealed class FakeNotifier : IScheduleNotifier
{
    public List<(string Url, string Title, string Body)> Posted { get; } = [];

    public bool Fail { get; set; }

    public Task PostAsync(string url, string title, string body, CancellationToken ct)
    {
        if (Fail)
            throw new IntakeException("the notification service answered 503", 503);
        Posted.Add((url, title, body));
        return Task.CompletedTask;
    }
}

internal sealed class FakeMorning : IMorningSource
{
    public MorningData Data { get; set; } = new(0, 0, 0, 0, 0, 0, 0m, 0, []);

    public DateTimeOffset? Since { get; private set; }

    public Task<MorningData> ReadAsync(DateTimeOffset since, CancellationToken ct)
    {
        Since = since;
        return Task.FromResult(Data);
    }
}

/// <summary>A run log that holds what the scheduler's runs wrote: a start, and a run record once a test ends it.</summary>
internal sealed class ScheduleLog : IRunLog
{
    private readonly Dictionary<string, StartRecord> _starts = [];
    private readonly Dictionary<string, RunRecord> _runs = [];

    public void Start(string run, RunRequest request, string? boot = null) => _starts[run] = new StartRecord(run, DateTimeOffset.UtcNow, "trace", request, Environment.ProcessId, BootId: boot ?? ProcessBoot.Id);

    public void End(string run, ResultContract result) => _runs[run] = new RunRecord(run, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "default", null, null, result);

    public void Forget(string run) => _starts.Remove(run);

    public Task AppendAsync(StartRecord record, CancellationToken ct) => Task.CompletedTask;

    public Task AppendAsync(CallRecord record, CancellationToken ct) => Task.CompletedTask;

    public Task AppendAsync(RunRecord record, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<RunSummary>> ListAsync(RunListQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<RunSummary>>([]);

    public Task<RunEntry> ReadAsync(string runId, CancellationToken ct) => Task.FromResult(new RunEntry(_starts.GetValueOrDefault(runId), _runs.GetValueOrDefault(runId), []));
}

/// <summary>The scheduler wired to fakes, with its state in a file so a restart can be tried. The clock starts on Thursday 2026-01-01 00:00 UTC.</summary>
internal sealed class ScheduleRig : IDisposable
{
    public const string Url = "https://ntfy.example/secret-topic";

    private readonly TempDir _dir = new();
    private AutomationSchedules _settings;

    public ScheduleRig(params ScheduleEntry[] entries) : this(new AutomationSchedules(entries, Url))
    {
    }

    public ScheduleRig(AutomationSchedules settings)
    {
        _settings = settings;
        Starter = new RecordingStarter(Log);
        Scheduler = Build();
    }

    public ScheduleLog Log { get; } = new();

    public RecordingStarter Starter { get; }

    public ScheduleHeads Heads { get; } = new();

    public FakeNotifier Notifier { get; } = new();

    public FakeMorning Morning { get; } = new();

    public FakeFlakes Flakes { get; } = new();

    public ManualTimeProvider Time { get; } = new();

    public List<string> Warnings { get; } = [];

    public Scheduler Scheduler { get; private set; }

    public string StateFile => Path.Combine(_dir.Path, "schedules", "state.json");

    public ScheduleState State => ScheduleState.Load(StateFile);

    private ISecurityAlerts? _alerts;

    private ManagedClones? _clones;

    private Scheduler Build() => new(_settings, Log, Heads, ScheduleState.Load(StateFile), Notifier, Morning, Time, Warnings.Add, Flakes, _alerts, _clones);

    /// <summary>The managed clones read-only checkouts fall back to; the scheduler is built again on the same state.</summary>
    public void UseClones(ManagedClones clones)
    {
        _clones = clones;
        Restart();
    }

    /// <summary>The alerts reader of <c>security-alerts</c>, or none (no App); the scheduler is built again on the same state.</summary>
    public void UseAlerts(ISecurityAlerts? alerts)
    {
        _alerts = alerts;
        Restart();
    }

    /// <summary>A new process on the same state file.</summary>
    public void Restart() => Scheduler = Build();

    /// <summary>A new process on the same state file whose profile lists other entries.</summary>
    public void Restart(params ScheduleEntry[] entries)
    {
        _settings = new AutomationSchedules(entries, Url);
        Restart();
    }

    public Task Tick() => Scheduler.TickAsync(Starter, default);

    public async Task Wake(TimeSpan by)
    {
        Time.Advance(by);
        await Tick();
    }

    public static ScheduleEntry Docs(string at = "daily 04:00", int? cap = null, string id = "docs") =>
        new(id, at, Template: ScheduleTemplates.DocDrift, Repository: new AutomationRepository("/srv/repos/repo"), MaxPerMonth: cap);

    public static ScheduleEntry Deps(string at = "daily 04:00", int? cap = null) =>
        new("deps", at, Template: ScheduleTemplates.DependencyUpgrades, Repository: new AutomationRepository("/srv/repos/repo"), MaxPerMonth: cap);

    public static ScheduleEntry Report(string at = "daily 06:00") => new("morning", at, Template: ScheduleTemplates.MorningReport);

    public static ResultContract Result(ResultStatus status = ResultStatus.Completed, string summary = "Two findings.", IReadOnlyList<Claim>? claims = null, IReadOnlyList<Evidence>? evidence = null) =>
        new("result/v1", "run", "worker", new string('0', 32), new PromptChain([], new AsSent("1", "build", "p/m", "2026-01-01")), status, summary, claims ?? [], evidence ?? [], [], [], 0.8, new Usage(0, 0, 0, 0, 0));

    public void Dispose() => _dir.Dispose();
}

internal sealed class FakeFlakes : IFlakeHistory
{
    public FlakeHistory? History { get; set; }

    public List<string> Read { get; } = [];

    public Task<FlakeHistory?> ReadAsync(string checkoutPath, CancellationToken ct)
    {
        Read.Add(checkoutPath);
        return Task.FromResult(History);
    }
}

internal sealed class ScheduleHeads : IRepositoryHeads
{
    public string Commit { get; set; } = "abc1234def5678abc1234def5678abc1234def56";

    public string? Fail { get; set; }

    /// <summary>Checkouts that cannot be written, and what fetching into them says.</summary>
    public Dictionary<string, ReadOnlyCheckoutException> ReadOnly { get; } = [];

    /// <summary>The paths asked for a commit, in order.</summary>
    public List<string> Asked { get; } = [];

    public Task<string> CurrentCommitAsync(string path, CancellationToken ct)
    {
        Asked.Add(path);
        return ReadOnly.TryGetValue(path, out var e) ? throw e : Fail is null ? Task.FromResult(Commit) : throw new IntakeException(Fail);
    }

    public Task<string> FetchPullHeadAsync(string path, int number, CancellationToken ct) => throw new NotSupportedException("schedules never fetch a pull request");
}

internal sealed class RecordingStarter(ScheduleLog log) : IBatchStarter
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
