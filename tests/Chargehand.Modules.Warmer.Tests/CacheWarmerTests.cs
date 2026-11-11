using System.Text.Json;
using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Warmer.Api;

namespace Chargehand.Modules.Warmer.Tests;

[Collection("process-environment")]
public sealed class CacheWarmerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "chargehand-warmer-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteEventLog log;
    private readonly FakeRunner runner = new();

    public CacheWarmerTests()
    {
        Directory.CreateDirectory(root);
        log = SqliteEventLog.Open(Path.Combine(root, "events.db"));
    }

    public void Dispose()
    {
        log.Dispose();
        Directory.Delete(root, true);
    }

    private sealed class FakeRunner : IStepRunner
    {
        public List<string> Ran { get; } = [];

        public Func<WarmStep, int> Exit { get; set; } = _ => 0;

        public Task<int> RunAsync(WarmStep step, CancellationToken ct)
        {
            Ran.Add(step.Name);
            return Task.FromResult(Exit(step));
        }
    }

    private CacheWarmer NewWarmer() => new(log, Path.Combine(root, "checkouts"), runner);

    private string Checkout(string name, params string[] files)
    {
        var dir = Path.Combine(root, "checkouts", name);
        Directory.CreateDirectory(dir);
        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, file))!);
            File.WriteAllText(Path.Combine(dir, file), "");
        }
        return dir;
    }

    private async Task<StoredEvent> Emit(string id, string type, string? checkout, string scope = "global")
    {
        var data = checkout is null ? "{}" : JsonSerializer.Serialize(new Dictionary<string, string> { ["checkout"] = checkout }, WarmerTestJson.Default.DictionaryStringString);
        var envelope = $$"""{"specversion":"1.0","id":"{{id}}","source":"chargehand/orchestrator","type":"{{type}}","time":"2026-11-11T10:00:00.000Z","scope":"{{scope}}","data":{{data}}}""";
        var seqs = await log.AppendAsync([new NewEvent("chargehand/orchestrator", id, type, null, scope, null, "2026-11-11T10:00:00.000Z", envelope)], default);
        return (await log.ReadAsync(new EventFilter(After: seqs[0] - 1, Limit: 1), default))[0];
    }

    private async Task<IReadOnlyList<JsonElement>> Warmed() =>
        [.. (await log.ReadAsync(new EventFilter(Type: CacheWarmer.WarmedType), default)).Select(e => JsonDocument.Parse(e.Json).RootElement.Clone())];

    [Theory]
    [InlineData("chargehand.worktree.created")]
    [InlineData("chargehand.branch.queued")]
    public async Task Both_trigger_events_warm_the_checkout(string type)
    {
        Checkout("repo-1/abc", "pnpm-lock.yaml");
        var warmed = await NewWarmer().WarmAsync(await Emit("e1", type, "repo-1/abc"), default);
        Assert.True(warmed);
        Assert.Equal(["pnpm-install"], runner.Ran);
    }

    [Fact]
    public async Task Other_events_are_not_jobs()
    {
        Checkout("repo-1/abc", "pnpm-lock.yaml");
        Assert.False(await NewWarmer().WarmAsync(await Emit("e1", "chargehand.tool.called", "repo-1/abc"), default));
        Assert.Empty(runner.Ran);
        Assert.Empty(await Warmed());
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/etc")]
    [InlineData("a/../../b")]
    [InlineData("")]
    public async Task A_checkout_outside_the_root_is_refused(string checkout)
    {
        Directory.CreateDirectory(Path.Combine(root, "outside"));
        File.WriteAllText(Path.Combine(root, "outside", "pnpm-lock.yaml"), "");
        Assert.False(await NewWarmer().WarmAsync(await Emit("e1", CacheWarmer.CreatedType, checkout), default));
        Assert.Empty(runner.Ran);
    }

    [Fact]
    public async Task A_missing_data_field_is_not_a_job()
    {
        Assert.False(await NewWarmer().WarmAsync(await Emit("e1", CacheWarmer.CreatedType, null), default));
        Assert.Empty(runner.Ran);
    }

    [Fact]
    public async Task A_step_whose_result_exists_is_a_hit_and_is_not_run()
    {
        Checkout("r/c", "pnpm-lock.yaml", "node_modules/.modules.yaml");
        await NewWarmer().WarmAsync(await Emit("e1", CacheWarmer.CreatedType, "r/c"), default);
        Assert.Empty(runner.Ran);
        var data = (await Warmed()).Single().GetProperty("data");
        Assert.Equal("hit", data.GetProperty("outcome").GetString());
        Assert.Equal("hit", data.GetProperty("steps")[0].GetProperty("result").GetString());
    }

    [Fact]
    public async Task A_step_that_runs_and_exits_zero_is_a_miss()
    {
        Checkout("r/c", "pnpm-lock.yaml");
        await NewWarmer().WarmAsync(await Emit("e1", CacheWarmer.CreatedType, "r/c"), default);
        var data = (await Warmed()).Single().GetProperty("data");
        Assert.Equal("miss", data.GetProperty("outcome").GetString());
        Assert.Equal("pnpm-install", data.GetProperty("steps")[0].GetProperty("name").GetString());
        Assert.True(data.GetProperty("steps")[0].GetProperty("ms").GetInt64() >= 0);
        Assert.True(data.GetProperty("ms").GetInt64() >= 0);
    }

    [Fact]
    public async Task A_failed_step_is_logged_and_the_next_still_runs()
    {
        Checkout("r/c", "App.slnx", "pnpm-lock.yaml");
        runner.Exit = step => step.Name == "dotnet-restore" ? 1 : 0;
        await NewWarmer().WarmAsync(await Emit("e1", CacheWarmer.CreatedType, "r/c"), default);
        Assert.Equal(["dotnet-restore", "pnpm-install"], runner.Ran);
        var data = (await Warmed()).Single().GetProperty("data");
        Assert.Equal("failed", data.GetProperty("outcome").GetString());
        Assert.Equal(["failed", "miss"], data.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("result").GetString()!).ToArray());
    }

    [Fact]
    public async Task A_missing_checkout_is_logged_as_skipped()
    {
        await NewWarmer().WarmAsync(await Emit("e1", CacheWarmer.CreatedType, "nope/none"), default);
        Assert.Equal("skipped", (await Warmed()).Single().GetProperty("data").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task The_result_event_names_the_checkout_and_keeps_the_trigger_scope()
    {
        Checkout("r/c", "pnpm-lock.yaml");
        await NewWarmer().WarmAsync(await Emit("e1", CacheWarmer.CreatedType, "r/c", "project:Egoushka/a"), default);
        var warmed = (await Warmed()).Single();
        Assert.Equal("chargehand/warmer", warmed.GetProperty("source").GetString());
        Assert.Equal("project:Egoushka/a", warmed.GetProperty("scope").GetString());
        Assert.Equal("r/c", warmed.GetProperty("data").GetProperty("checkout").GetString());
    }

    [Fact]
    public async Task Warming_the_same_trigger_twice_logs_once()
    {
        Checkout("r/c", "pnpm-lock.yaml");
        var trigger = await Emit("e1", CacheWarmer.CreatedType, "r/c");
        var warmer = NewWarmer();
        await warmer.WarmAsync(trigger, default);
        await warmer.WarmAsync(trigger, default);
        Assert.Single(await Warmed());
    }

    [Fact]
    public async Task The_loop_warms_events_that_arrive_after_it_starts_and_not_before()
    {
        Checkout("old/c", "pnpm-lock.yaml");
        Checkout("new/c", "pnpm-lock.yaml");
        await Emit("before", CacheWarmer.CreatedType, "old/c");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var loop = NewWarmer().RunAsync(stop.Token);
        await Emit("after", CacheWarmer.CreatedType, "new/c");
        while ((await Warmed()).Count == 0)
            await Task.Delay(20, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        Assert.Equal("new/c", (await Warmed()).Single().GetProperty("data").GetProperty("checkout").GetString());
    }

    [Fact]
    public async Task A_runner_that_throws_is_logged_as_failed()
    {
        Checkout("r/c", "pnpm-lock.yaml");
        var warmer = new CacheWarmer(log, Path.Combine(root, "checkouts"), new ThrowingRunner());
        await warmer.WarmAsync(await Emit("e1", CacheWarmer.CreatedType, "r/c"), default);
        Assert.Equal("failed", (await Warmed()).Single().GetProperty("data").GetProperty("outcome").GetString());
        Assert.Equal("no such program", (await Warmed()).Single().GetProperty("data").GetProperty("steps")[0].GetProperty("error").GetString());
    }

    private sealed class ThrowingRunner : IStepRunner
    {
        public Task<int> RunAsync(WarmStep step, CancellationToken ct) => throw new InvalidOperationException("no such program");
    }

    [Fact]
    public async Task Missing_browsers_are_a_miss_not_a_hit()
    {
        Checkout("r/c", "package.json");
        File.WriteAllText(Path.Combine(root, "checkouts", "r/c", "package-lock.json"), "");
        File.WriteAllText(Path.Combine(root, "checkouts", "r/c", "package.json"), "{\"devDependencies\":{\"@playwright/test\":\"1\"}}");
        Directory.CreateDirectory(Path.Combine(root, "checkouts", "r/c", "node_modules"));
        File.WriteAllText(Path.Combine(root, "checkouts", "r/c", "node_modules", ".package-lock.json"), "");
        var empty = Path.Combine(root, "no-browsers");
        Directory.CreateDirectory(empty);
        var before = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", empty);
        try
        {
            await NewWarmer().WarmAsync(await Emit("e1", CacheWarmer.CreatedType, "r/c"), default);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", before);
        }
        Assert.Equal("miss", (await Warmed()).Single().GetProperty("data").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_single_segment_name_is_refused()
    {
        Checkout("lonely", "pnpm-lock.yaml");
        Assert.False(await NewWarmer().WarmAsync(await Emit("e1", CacheWarmer.CreatedType, "lonely"), default));
        Assert.Empty(runner.Ran);
    }

    [Fact]
    public async Task A_symlink_that_leaves_the_root_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(root, "outside"));
        File.WriteAllText(Path.Combine(root, "outside", "pnpm-lock.yaml"), "");
        Directory.CreateDirectory(Path.Combine(root, "checkouts", "r"));
        Directory.CreateSymbolicLink(Path.Combine(root, "checkouts", "r", "c"), Path.Combine(root, "outside"));
        Assert.False(await NewWarmer().WarmAsync(await Emit("e1", CacheWarmer.CreatedType, "r/c"), default));
        Assert.Empty(runner.Ran);
    }

    [Fact]
    public async Task A_sibling_root_with_the_same_prefix_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(root, "checkouts-evil", "r", "c"));
        File.WriteAllText(Path.Combine(root, "checkouts-evil", "r", "c", "pnpm-lock.yaml"), "");
        Assert.False(await NewWarmer().WarmAsync(await Emit("e1", CacheWarmer.CreatedType, "../checkouts-evil/r/c"), default));
        Assert.Empty(runner.Ran);
    }

}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class WarmerTestJson : System.Text.Json.Serialization.JsonSerializerContext;
