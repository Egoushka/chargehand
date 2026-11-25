using Chargehand.Automation;
using Chargehand.Modules.Pool.Api;

namespace Chargehand.Tests;

public sealed class BoardProducerTests : IDisposable
{
    private const string Toml = """
        # comment
        [kind.fix]
        runner = "chargehand"
        model_class = "build"
        gates = ["engineering", "security"]
        env = { isolation = "worktree", data = "any", check = "fast-tier" }
        budget_usd = 3.0
        max_minutes = 60
        night = true

        [kind.ui]
        runner = "chargehand"
        model_class = "build"
        gates = ["engineering", "ui-design"]
        budget_usd = 6.0
        max_minutes = 120
        night = false

        [kind.release]
        runner = "desk:release"
        model_class = "build"
        gates = []
        night = false
        """;

    private readonly TempDir _dir = new();
    private readonly SqlitePoolStore _store;
    private readonly TaskPool _pool;
    private readonly ManualTimeProvider _time = new();
    private readonly FakeBoard _board = new();
    private readonly FakeNight _night = new();

    public BoardProducerTests()
    {
        _store = SqlitePoolStore.Open(Path.Combine(_dir.Path, "pool.db"));
        _pool = new TaskPool(_store, new TaskPoolSettings(), [], new NoStarter(), _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private BoardProducer Producer() => new(WorkKinds.Parse(Toml), _board, _pool, _night, _time, _ => { });

    private static BoardItem Item(string id, string work, string state = "planned", string repo = "owner/app") => new(id, "Title " + id, "Ask " + id, state, work, repo);

    [Fact]
    public void Parse_reads_the_kinds_shape()
    {
        var kinds = WorkKinds.Parse(Toml);
        Assert.Equal(3, kinds.Count);
        var fix = kinds["fix"];
        Assert.Equal(("chargehand", "build", 3.0m, 60, true), (fix.Runner, fix.ModelClass, fix.BudgetUsd, fix.MaxMinutes, fix.Night));
        Assert.Equal(["engineering", "security"], fix.Gates);
        Assert.False(kinds["ui"].Night);
        Assert.Equal("desk:release", kinds["release"].Runner);
    }

    [Fact]
    public async Task A_planned_item_becomes_a_pool_item_with_class_gates_and_budget()
    {
        _board.Items["fix"] = [Item("b-1", "fix")];
        Assert.Equal(1, await Producer().ProduceAsync(CancellationToken.None));
        var item = Assert.Single(await _pool.ItemsAsync(null, CancellationToken.None));
        Assert.Equal("board", item.Source);
        Assert.Equal("owner/app", item.Repository);
        Assert.Equal("board-b-1", item.Area);
        Assert.Contains("Model class: build", item.Body);
        Assert.Contains("Gates: engineering, security", item.Body);
        Assert.Contains("Budget: $3.0", item.Body);
        Assert.Contains("Ask b-1", item.Body);
    }

    [Fact]
    public async Task Other_runners_states_and_items_without_a_repository_are_left()
    {
        _board.Items["fix"] = [Item("b-2", "fix", "active"), Item("b-3", "fix", repo: "")];
        _board.Items["release"] = [Item("b-4", "release")];
        Assert.Equal(0, await Producer().ProduceAsync(CancellationToken.None));
        Assert.Empty(await _pool.ItemsAsync(null, CancellationToken.None));
        Assert.DoesNotContain("release", _board.Asked);
    }

    [Fact]
    public async Task In_the_night_window_a_day_only_kind_waits()
    {
        _board.Items["fix"] = [Item("b-5", "fix")];
        _board.Items["ui"] = [Item("b-6", "ui")];
        _night.Open = true;
        Assert.Equal(1, await Producer().ProduceAsync(CancellationToken.None));
        Assert.Equal("board-b-5", Assert.Single(await _pool.ItemsAsync(null, CancellationToken.None)).Area);
        _night.Open = false;
        Assert.Equal(1, await Producer().ProduceAsync(CancellationToken.None));
        Assert.Equal(2, (await _pool.ItemsAsync(null, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Producing_twice_enqueues_once()
    {
        _board.Items["fix"] = [Item("b-7", "fix")];
        Assert.Equal(1, await Producer().ProduceAsync(CancellationToken.None));
        Assert.Equal(0, await Producer().ProduceAsync(CancellationToken.None));
        Assert.Single(await _pool.ItemsAsync(null, CancellationToken.None));
    }

    private sealed class NoStarter : IPoolStarter
    {
        public Task<string?> StartAsync(PoolStartRequest request, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class FakeBoard : IBoardSource
    {
        public Dictionary<string, BoardItem[]> Items { get; } = [];
        public List<string> Asked { get; } = [];

        public Task<IReadOnlyList<BoardItem>> ListAsync(string kind, CancellationToken ct)
        {
            Asked.Add(kind);
            return Task.FromResult<IReadOnlyList<BoardItem>>(Items.GetValueOrDefault(kind, []));
        }
    }

    private sealed class FakeNight : INightWindow
    {
        public bool Open { get; set; }
        public bool IsOpen(DateTimeOffset now) => Open;
    }
}
