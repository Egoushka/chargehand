using Chargehand.Automation;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>A Plane that holds states and work items in memory and records what intake writes.</summary>
internal sealed class FakePlane : IPlaneHost
{
    public List<PlaneWorkflowState> States { get; } = [new("s-todo", "Todo"), new("s-agent", "Agent"), new("s-doing", "In Progress"), new("s-review", "In Review"), new("s-failed", "Failed")];

    public List<PlaneItem> Items { get; } = [];

    public List<(string Item, string Text)> Comments { get; } = [];

    public List<(string Item, string State)> Moves { get; } = [];

    public bool CommentsFail { get; set; }

    public bool Down { get; set; }

    public Task<string> ProjectIdentifierAsync(string project, CancellationToken ct) => Task.FromResult("ABC");

    public Task<IReadOnlyList<PlaneWorkflowState>> StatesAsync(string project, CancellationToken ct) => Down ? throw new IntakeException("Plane answered 500 to the states", 500) : Task.FromResult<IReadOnlyList<PlaneWorkflowState>>(States);

    public Task<IReadOnlyList<PlaneItem>> ItemsInStateAsync(string project, string stateId, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlaneItem>>([.. Items.Where(i => i.StateId == stateId)]);

    public Task<PlaneItem> GetAsync(string project, string itemId, CancellationToken ct) => Task.FromResult(Items.Single(i => i.Id == itemId));

    public Task CommentAsync(string project, string itemId, string text, CancellationToken ct)
    {
        if (CommentsFail)
            throw new IntakeException("Plane answered 500 to the comment", 500);
        Comments.Add((itemId, text));
        return Task.CompletedTask;
    }

    public Task SetStateAsync(string project, string itemId, string stateId, CancellationToken ct)
    {
        Moves.Add((itemId, stateId));
        var at = Items.FindIndex(i => i.Id == itemId);
        Items[at] = Items[at] with { StateId = stateId, UpdatedAt = Items[at].UpdatedAt.AddSeconds(1) };
        return Task.CompletedTask;
    }
}

/// <summary>Plane intake wired to fakes, with its state in a file so a restart can be tried.</summary>
internal sealed class PlaneRig : IDisposable
{
    public const string Project = "proj-1";

    private readonly TempDir _dir = new();

    public PlaneRig(AutomationPlane? settings = null)
    {
        Settings = settings ?? Defaults();
        Starter = new FakeStarter(Log);
        Intake = Build();
    }

    public static AutomationPlane Defaults(int concurrent = 2, int perDay = 10, string? failed = "Failed") => new("https://plane.example.test", "plane-key", "ws", new Dictionary<string, PlaneProject> { [Project] = new("/srv/repos/repo", "octo/repo") },
        "Agent", "In Progress", "In Review", failed, MaxConcurrent: concurrent, MaxStartsPerDay: perDay);

    public AutomationPlane Settings { get; }

    public FakePlane Plane { get; } = new();

    public FakeRunLog Log { get; } = new();

    public FakeStarter Starter { get; }

    public FakeHeads Heads { get; } = new();

    public ManualTimeProvider Time { get; } = new();

    public List<string> Warnings { get; } = [];

    public PlaneIntake Intake { get; private set; }

    private string StatePath => Path.Combine(_dir.Path, "intake", "plane.json");

    public PlaneState State => PlaneState.Load(StatePath);

    /// <summary>The state file itself, for the console's queues.</summary>
    public string StateFile => StatePath;

    private string SourcesPath => Path.Combine(_dir.Path, "intake", "plane-sources.json");

    /// <summary>The switches as a new process would read them.</summary>
    public IntakeSources Sources => IntakeSources.Load(SourcesPath);

    private ManagedClones? _clones;

    private PlaneIntake Build() => new(Settings, Plane, Log, Heads, PlaneState.Load(StatePath), Time, Warnings.Add, IntakeSources.Load(SourcesPath), _clones);

    /// <summary>The managed clones read-only checkouts fall back to; intake is built again on the same state.</summary>
    public void UseClones(ManagedClones clones)
    {
        _clones = clones;
        Restart();
    }

    /// <summary>A new process on the same state file.</summary>
    public void Restart() => Intake = Build();

    public Task Poll() => Intake.PollAsync(Starter, default);

    public PlaneItem Item(int sequence, string state = "s-agent", string name = "Add a retry", string description = "The client gives up on the first error.") =>
        AddItem(new PlaneItem($"item-{sequence}", name, description, state, sequence, Time.GetUtcNow()));

    private PlaneItem AddItem(PlaneItem item)
    {
        Plane.Items.RemoveAll(i => i.Id == item.Id);
        Plane.Items.Add(item);
        return item;
    }

    /// <summary>A person moves an item to another state: its update time changes.</summary>
    public void Move(string id, string state)
    {
        Time.Advance(TimeSpan.FromSeconds(5));
        var at = Plane.Items.FindIndex(i => i.Id == id);
        Plane.Items[at] = Plane.Items[at] with { StateId = state, UpdatedAt = Time.GetUtcNow() };
    }

    public static Chargehand.Modules.Tasks.Api.TaskOutcome Pr(int sequence, string url = "https://github.example.test/octo/repo/pull/9") => IntakeRig.Pr($"plane-abc-{sequence}", url);

    public void Dispose() => _dir.Dispose();
}
