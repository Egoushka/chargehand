using Chargehand.Modules.Pool.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Pool.Tests;

/// <summary>A pool on a temp file with a manual clock, a recording starter and settable checks.</summary>
internal sealed class PoolKit : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("chargehand-pool-").FullName;

    public PoolKit(params IPoolCheck[] checks)
        : this(new TaskPoolSettings(), checks)
    {
    }

    public PoolKit(TaskPoolSettings settings, params IPoolCheck[] checks)
    {
        Settings = settings;
        Store = SqlitePoolStore.Open(DbPath);
        Pool = new TaskPool(Store, settings, checks, Starter, Time);
    }

    public TaskPoolSettings Settings { get; }

    public string DbPath => Path.Combine(_dir, "pool.db");

    public SqlitePoolStore Store { get; private set; }

    public TaskPool Pool { get; private set; }

    public ManualTimeProvider Time { get; } = new();

    public FakeStarter Starter { get; } = new();

    public async Task<PoolItem> AddAsync(string area, string title, int priority = 0, int? pullRequest = null, string repository = "owner/repo", string? body = null)
    {
        var result = await Pool.EnqueueAsync(new EnqueueRequest(repository, area, title, body ?? "body of " + title, priority, pullRequest), CancellationToken.None);
        Assert.Null(result.Refusal);
        Time.Advance(TimeSpan.FromSeconds(1));
        return result.Item!.Item;
    }

    public void Reopen(params IPoolCheck[] checks)
    {
        Store.Dispose();
        Store = SqlitePoolStore.Open(DbPath);
        Pool = new TaskPool(Store, Settings, checks, Starter, Time);
    }

    public void Dispose()
    {
        Store.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // agent-checks: allow empty-catch -- a temp directory left behind by a file the OS still holds is harmless to the test run
        }
    }
}

internal sealed class FakeStarter : IPoolStarter
{
    public List<PoolStartRequest> Requests { get; } = [];

    public string? Refusal { get; set; }

    /// <summary>When set, only this area is refused, with <see cref="Refusal"/>.</summary>
    public string? RefusedArea { get; set; }

    /// <summary>When set, starting this area throws.</summary>
    public string? ThrowingArea { get; set; }

    public Task<string?> StartAsync(PoolStartRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (ThrowingArea == request.Scope.Area)
            throw new DirectoryNotFoundException("the checkout is gone");
        return Task.FromResult(RefusedArea is null || RefusedArea == request.Scope.Area ? Refusal : null);
    }
}

internal sealed class FakeCheck(string name) : IPoolCheck
{
    public string? Detail { get; set; }

    public string Name => name;

    public bool Throws { get; set; }

    public Task<string?> HoldAsync(PoolScope scope, CancellationToken ct) => Throws ? throw new InvalidOperationException("no answer") : Task.FromResult(Detail);
}
