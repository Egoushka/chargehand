using Chargehand.Automation;
using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Pool.Api;

namespace Chargehand.Tests;

public sealed class IssueSweepTests : IDisposable
{
    private const string App = "owner/app";
    private const string Infra = "owner/infra";

    private readonly TempDir _dir = new();
    private readonly FakeGitHub _github = new();
    private readonly FakeSorter _sorter = new();
    private readonly FakeSplitter _splitter = new();
    private readonly FakeEvents _events = new();
    private readonly SqlitePoolStore _store;
    private readonly TaskPool _pool;
    private readonly ManualTimeProvider _time = new();

    public IssueSweepTests()
    {
        _store = SqlitePoolStore.Open(Path.Combine(_dir.Path, "pool.db"));
        _pool = new TaskPool(_store, new TaskPoolSettings(), [], new NoStarter(), _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private IssueSweep Sweep(int max = 200, params string[] repositories) => new(
        new SweepSettings(Enabled: true, MaxSortsPerSweep: max),
        (repositories.Length == 0 ? [App, Infra] : repositories).ToDictionary(r => r, r => new AutomationRepository(Path.Combine(_dir.Path, r.Replace('/', '-')))),
        _github, _sorter, new SweepState(Path.Combine(_dir.Path, "sweep.json")), _pool, _events, new IntakeState(), new FakeHeads(),
        new HashSet<string> { "alice", "chargehand[bot]" }, _splitter, _time);

    private static IntakeIssue Issue(int n, string title = "Plain", string body = "", params string[] labels) =>
        new(n, title, body, "alice", false, labels, DateTimeOffset.UnixEpoch.AddMinutes(n));

    private void Seed()
    {
        _github.OpenIssuesByRepo[App] = [Issue(1, labels: "you"), Issue(2, body: "Blocked by #99"), Issue(3, body: "Blocked by #98"), Issue(4, "Epic"), Issue(5, labels: "agent:skip")];
        _github.OpenIssuesByRepo[Infra] = [Issue(1), Issue(2), Issue(3), Issue(4), Issue(5, labels: "in-progress")];
        _github.RepoIssueStates[(App, 99)] = IssueState.Open;
        _github.RepoIssueStates[(App, 98)] = IssueState.Closed;
        _sorter.Verdicts["Epic"] = SweepVerdict.Epic;
    }

    [Fact]
    public async Task Ten_issues_two_repositories()
    {
        Seed();
        var report = await Sweep().RunAsync("manual", CancellationToken.None);

        Assert.Equal(6, _sorter.Calls);
        Assert.Equal(5, (await _pool.ItemsAsync(null, CancellationToken.None)).Count);
        Assert.Equal(1, _splitter.Calls);
        Assert.Equal(5, report.Repositories.Sum(r => r.Enqueued));
        Assert.Contains((App, 2, SweepLabels.Blocked), _github.LabelAdds);
        Assert.DoesNotContain(_github.LabelAdds, a => a.Repository == App && a.Number == 2 && a.Label.StartsWith("size:", StringComparison.Ordinal));
        Assert.Contains((App, 3, SweepLabels.Ready), _github.LabelAdds);
        Assert.Contains((App, 4, SweepLabels.Epic), _github.LabelAdds);
        Assert.Contains((Infra, 1, "size:M"), _github.LabelAdds);
        Assert.DoesNotContain(_github.LabelAdds, a => a.Repository == App && a.Number is 1 or 5);
        Assert.All(_github.RepoComments, c => Assert.StartsWith("<!-- chargehand-sweep -->", c.Body));
        Assert.Equal(1, _events.Count("chargehand.sweep.started"));
        Assert.Equal(1, _events.Count("chargehand.sweep.finished"));
    }

    [Fact]
    public async Task Second_sweep_unchanged_makes_no_calls()
    {
        Seed();
        var sweep = Sweep();
        await sweep.RunAsync("manual", CancellationToken.None);
        var calls = _sorter.Calls;
        var comments = _github.RepoComments.Count;

        var second = await sweep.RunAsync("schedule", CancellationToken.None);

        Assert.Equal(calls, _sorter.Calls);
        Assert.Equal(0, second.Repositories.Sum(r => r.Enqueued));
        Assert.Equal(comments, _github.RepoComments.Count);
    }

    [Fact]
    public async Task Every_sweep_syncs_the_labels_of_each_repository()
    {
        Seed();
        await Sweep().RunAsync("manual", CancellationToken.None);

        Assert.Equal(SweepLabels.All.Count, _github.SyncedLabels.Count(s => s.Repository == App));
        Assert.Equal(SweepLabels.All.Count, _github.SyncedLabels.Count(s => s.Repository == Infra));
    }

    [Fact]
    public async Task Unreadable_blocker_does_not_block()
    {
        _github.OpenIssuesByRepo[App] = [Issue(1, body: "Depends on #7")];

        await Sweep(repositories: App).RunAsync("manual", CancellationToken.None);

        Assert.Equal(1, _sorter.Calls);
        Assert.Contains((App, 1, SweepLabels.Ready), _github.LabelAdds);
        Assert.Contains("owner/app#7", Assert.Single(_github.RepoComments).Body);
    }

    [Fact]
    public async Task A_failing_repository_is_a_problem_and_others_continue()
    {
        Seed();
        _github.SweepFailFor.Add(Infra);

        var report = await Sweep().RunAsync("manual", CancellationToken.None);

        Assert.NotNull(report.Repositories.Single(r => r.Repository == Infra).Problem);
        Assert.Null(report.Repositories.Single(r => r.Repository == App).Problem);
        Assert.DoesNotContain(_github.LabelAdds, a => a.Repository == Infra);
        Assert.DoesNotContain(_github.RepoComments, c => c.Repository == Infra);
        Assert.NotEmpty(_github.LabelAdds);
        Assert.Equal(1, _events.Count("chargehand.sweep.problem"));
    }

    [Fact]
    public async Task At_most_max_sorts_per_sweep_model_calls()
    {
        _github.OpenIssuesByRepo[App] = [.. Enumerable.Range(1, 250).Select(n => Issue(n))];

        await Sweep(repositories: App).RunAsync("manual", CancellationToken.None);

        Assert.Equal(200, _sorter.Calls);
    }

    [Fact]
    public async Task A_changed_verdict_is_commented_once_and_a_split_epic_is_left_alone()
    {
        _github.OpenIssuesByRepo[App] = [Issue(1, "Epic", "Done.\n<!-- chargehand-split -->")];
        _sorter.Verdicts["Epic"] = SweepVerdict.Epic;

        await Sweep(repositories: App).RunAsync("manual", CancellationToken.None);

        Assert.Equal(0, _splitter.Calls);
        Assert.Single(_github.RepoComments);
    }

    [Fact]
    public async Task Priority_labels_set_the_pool_priority()
    {
        _github.OpenIssuesByRepo[App] = [Issue(1, labels: "priority:urgent"), Issue(2, labels: "priority:high"), Issue(3)];

        await Sweep(repositories: App).RunAsync("manual", CancellationToken.None);

        var items = await _pool.ItemsAsync(null, CancellationToken.None);
        Assert.Equal(2, items.Single(i => i.Issue == 1).Priority);
        Assert.Equal(1, items.Single(i => i.Issue == 2).Priority);
        Assert.Equal(0, items.Single(i => i.Issue == 3).Priority);
        Assert.All(items, i => Assert.Equal($"issue-{i.Issue}", i.Area));
    }

    private sealed class NoStarter : IPoolStarter
    {
        public Task<string?> StartAsync(PoolStartRequest request, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class FakeSorter : ISweepSorter
    {
        public int Calls { get; private set; }

        public Dictionary<string, SweepVerdict> Verdicts { get; } = [];

        public Task<SortResult> SortAsync(IntakeIssue issue, string repository, string? claudeMd, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new SortResult(Verdicts.GetValueOrDefault(issue.Title, SweepVerdict.Ready), "M", "Looks right."));
        }
    }

    private sealed class FakeSplitter : IEpicSplitter
    {
        public int Calls { get; private set; }

        public Task<int> SplitAsync(string repository, IntakeIssue epic, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(3);
        }
    }

    private sealed class FakeEvents : IEventLog
    {
        private readonly List<NewEvent> _all = [];

        public EventSignal Signal { get; } = new();

        public int Count(string type) => _all.Count(e => e.Type == type);

        public Task<IReadOnlyList<long>> AppendAsync(IReadOnlyList<NewEvent> batch, CancellationToken ct)
        {
            _all.AddRange(batch);
            return Task.FromResult<IReadOnlyList<long>>([.. batch.Select((_, i) => (long)i)]);
        }

        public Task<IReadOnlyList<StoredEvent>> ReadAsync(EventFilter filter, CancellationToken ct) => Task.FromResult<IReadOnlyList<StoredEvent>>([]);
    }
}
