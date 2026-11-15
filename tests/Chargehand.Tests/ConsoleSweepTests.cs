using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Pool.Api;
using Chargehand.Runtimes.ClaudeCode.Api;

namespace Chargehand.Tests;

/// <summary>The sweep's routes and host (owner key only), the owner's overrides, and the server's composition of the sweep.</summary>
public sealed class ConsoleSweepTests
{
    private static readonly AccountSettings Alice = new("alice", Driven: true, AllowServerCredential: true);

    private static IntakeRig NewRig() => new(new AutomationGitHub(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author]));

    private sealed class GateSorter : ISweepSorter
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SortResult> SortAsync(IntakeIssue issue, string repository, string? claudeMd, CancellationToken ct)
        {
            await Gate.Task.WaitAsync(ct);
            return new SortResult(SweepVerdict.NeedsOwner, "M", "Needs a look.");
        }
    }

    private static Task<TestServer> Start(IntakeRig rig, GateSorter sorter, SweepSettings? sweep = null, decimal? hold = null) =>
        TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [Alice], intake: rig.Intake,
            sweep: new SweepParts(rig.GitHub, rig.Heads, rig.State, rig.Settings.Repositories!, new HashSet<string> { IntakeRig.Author }, sorter, sweep ?? new SweepSettings(), Path.Combine(Path.GetTempPath(), "sweep-" + Guid.NewGuid().ToString("N"))),
            tweak: (root, settings) => settings with
            {
                Pool = new TaskPoolSettings(Database: Path.Combine(root, "pool.db")),
                Profile = (settings.Profile ?? Runs.Profile(root)) with
                {
                    Automation = new AutomationSettings(rig.Settings, Sweep: sweep),
                    ClaudeCode = hold is null ? null : new ClaudeCodeSettings("1", OauthTokenSecret: "t", ApiFallbackKeySecret: "api", ApiFallbackDailyUsd: 6, SubscriptionHoldPercent: hold),
                },
            });

    private static async Task<JsonElement> Get(HttpClient http, string path) => await http.GetFromJsonAsync<JsonElement>(path);

    private static async Task<JsonElement> UntilLast(HttpClient http)
    {
        for (var i = 0; i < 100; i++)
        {
            var body = await Get(http, "/v1/sweep");
            if (body.TryGetProperty("last", out _))
                return body;
            await Task.Delay(50);
        }
        throw new TimeoutException("the sweep did not finish");
    }

    [Fact]
    public async Task Post_during_a_sweep_returns_the_running_id()
    {
        using var rig = NewRig();
        rig.GitHub.OpenIssuesByRepo[IntakeRig.Repo] = [new IntakeIssue(1, "Plain", "", IntakeRig.Author, false, [], DateTimeOffset.UnixEpoch)];
        var sorter = new GateSorter();
        await using var server = await Start(rig, sorter);

        var first = await server.Http.PostAsync("/v1/sweep", null);
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var again = (await (await server.Http.PostAsync("/v1/sweep", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var during = await Get(server.Http, "/v1/sweep");
        sorter.Gate.SetResult();
        var after = await UntilLast(server.Http);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal((id, id), (again, during.GetProperty("running").GetProperty("id").GetString()));
        Assert.Equal(id, after.GetProperty("last").GetProperty("id").GetString());
        Assert.False(after.TryGetProperty("running", out _));
    }

    [Fact]
    public async Task Get_counts_the_pools_running_items_and_lists_what_needs_you()
    {
        using var rig = NewRig();
        rig.GitHub.OpenIssuesByRepo[IntakeRig.Repo] = [new IntakeIssue(1, "Plain", "", IntakeRig.Author, false, [], DateTimeOffset.UnixEpoch)];
        var sorter = new GateSorter();
        sorter.Gate.SetResult();
        await using var server = await Start(rig, sorter);
        using (var store = SqlitePoolStore.Open(Path.Combine(server.WorkerRoot, "pool.db")))
        {
            await store.EnqueueAsync(new EnqueueRequest(IntakeRig.Repo, "docs", "Other", "b", Issue: 9), 100, DateTimeOffset.UtcNow, CancellationToken.None);
            await store.StartWorkerAsync(new PoolScope(IntakeRig.Repo, "docs"), "w1", 90_000, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        await server.Http.PostAsync("/v1/sweep", null);
        var body = await UntilLast(server.Http);

        Assert.Equal(1, body.GetProperty("last").GetProperty("repositories")[0].GetProperty("running").GetInt32());
        var item = Assert.Single(body.GetProperty("needs_you_items").EnumerateArray());
        Assert.Equal((IntakeRig.Repo, 1, "Plain", "Needs a look.", $"https://github.com/{IntakeRig.Repo}/issues/1"),
            (item.GetProperty("repository").GetString(), item.GetProperty("issue").GetInt32(), item.GetProperty("title").GetString(), item.GetProperty("reason").GetString(), item.GetProperty("url").GetString()));
        Assert.False(body.GetProperty("last").TryGetProperty("needs_you_items", out _));
    }

    [Fact]
    public async Task Non_owner_gets_403()
    {
        using var rig = NewRig();
        await using var server = await Start(rig, new GateSorter());
        using var alice = server.As(server.Keys.Create("alice", "test").Key);

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsync("/v1/sweep", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/v1/sweep")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/v1/sweep/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync("/v1/sweep/settings", new { enabled = true })).StatusCode);
    }

    [Fact]
    public async Task Without_a_pool_the_routes_say_the_sweep_is_not_configured()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));

        Assert.False((await Get(server.Http, "/v1/sweep")).GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await server.Http.PostAsync("/v1/sweep", null)).StatusCode);
    }

    [Fact]
    public async Task The_server_builds_the_sweep_from_the_profile_and_get_shows_its_schedule()
    {
        using var rig = NewRig();
        await using var server = await Start(rig, new GateSorter(), new SweepSettings(Enabled: true, At: ["08:00"], TimeZone: "Europe/Berlin"));

        var body = await Get(server.Http, "/v1/sweep");
        var settings = await Get(server.Http, "/v1/sweep/settings");

        Assert.Equal(("sweep/v1", true, true, "Europe/Berlin"), (body.GetProperty("contract_version").GetString(), body.GetProperty("configured").GetBoolean(), body.GetProperty("schedule_on").GetBoolean(), body.GetProperty("time_zone").GetString()));
        Assert.True(body.TryGetProperty("next", out _));
        Assert.Equal(("sweep-settings/v1", true), (settings.GetProperty("contract_version").GetString(), settings.GetProperty("enabled").GetProperty("effective").GetBoolean()));
        Assert.Equal(["08:00"], settings.GetProperty("at").GetProperty("effective").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task The_owners_overrides_win_over_the_profile_at_the_next_tick_and_a_bad_body_is_refused()
    {
        using var rig = NewRig();
        await using var server = await Start(rig, new GateSorter(), new SweepSettings(Enabled: true, At: ["08:00"]));

        var put = await server.Http.PutAsJsonAsync("/v1/sweep/settings", new { enabled = false, at = (string[])["09:30"], days = (string[])["monday", "friday"], repositories = new Dictionary<string, bool> { [IntakeRig.Repo] = false } });
        var settings = await put.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal((true, false, false), (settings.GetProperty("enabled").GetProperty("profile").GetBoolean(), settings.GetProperty("enabled").GetProperty("override").GetBoolean(), settings.GetProperty("enabled").GetProperty("effective").GetBoolean()));
        Assert.Equal(["09:30"], settings.GetProperty("at").GetProperty("effective").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["monday", "friday"], settings.GetProperty("days").GetProperty("effective").EnumerateArray().Select(e => e.GetString()));
        Assert.False(settings.GetProperty("repositories").GetProperty(IntakeRig.Repo).GetProperty("effective").GetBoolean());
        Assert.False(rig.Intake.Sources.SweepOn(IntakeRig.Repo));
        foreach (var bad in new object[] { new { at = (string[])["25:00"] }, new { days = (string[])["someday"] }, new { subscription_hold_percent = 0 }, new { repositories = new Dictionary<string, bool> { ["o/unknown"] = true } } })
            Assert.Equal(HttpStatusCode.BadRequest, (await server.Http.PutAsJsonAsync("/v1/sweep/settings", bad)).StatusCode);
        var cleared = await (await server.Http.PutAsJsonAsync("/v1/sweep/settings", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(cleared.GetProperty("enabled").TryGetProperty("override", out _));
    }

    [Fact]
    public async Task Hold_percent_override_reaches_the_fallback_gate()
    {
        using var rig = NewRig();
        await using var server = await Start(rig, new GateSorter(), hold: 50m);
        var settings = new ClaudeCodeSettings("1", OauthTokenSecret: "t", ApiFallbackKeySecret: "api", ApiFallbackDailyUsd: 6, SubscriptionHoldPercent: 50m);
        var price = new Chargehand.Modules.Ledger.Api.ModelPrice(1m, 2m, 3m, 4m);
        decimal? Hold(Func<decimal?>? over) =>
            Chargehand.Driven.FallbackSetup.Of(settings, "m", _ => price, priced: false, new ApiFallback(6m, null), n => n, "run-1", over)!.HoldPercent;

        Assert.Equal(50m, Hold(() => rig.Intake.Sources.Sweep.HoldPercent));
        var put = await server.Http.PutAsJsonAsync("/v1/sweep/settings", new { subscription_hold_percent = 80 });
        var body = (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("subscription_hold_percent");

        Assert.Equal(80m, Hold(() => rig.Intake.Sources.Sweep.HoldPercent));
        Assert.Equal((50m, 80m, 80m), (body.GetProperty("profile").GetDecimal(), body.GetProperty("override").GetDecimal(), body.GetProperty("effective").GetDecimal()));
    }

    private sealed class HostRig : IDisposable
    {
        private readonly TempDir _dir = new();
        private readonly SqlitePoolStore _store;

        public HostRig(SweepSettings profile)
        {
            _store = SqlitePoolStore.Open(Path.Combine(_dir.Path, "pool.db"));
            var pool = new TaskPool(_store, new TaskPoolSettings(), [], new NoStarter(), Time);
            Sources = new IntakeSources();
            GitHub.OpenIssuesByRepo[IntakeRig.Repo] = [new IntakeIssue(1, "Plain", "", IntakeRig.Author, false, [], DateTimeOffset.UnixEpoch)];
            Host = new SweepHost(profile, Sources, new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") },
                repositories => new IssueSweep(profile, repositories, GitHub, Sorter, new SweepState(Path.Combine(_dir.Path, "sweep.json")), pool, new Events(), new IntakeState(), new FakeHeads(),
                    new HashSet<string> { IntakeRig.Author }, new NoSplit(), Time), Time, Path.Combine(_dir.Path, "last.json"));
        }

        public ManualTimeProvider Time { get; } = new();

        public FakeGitHub GitHub { get; } = new();

        public GateSorter Sorter { get; } = new();

        public IntakeSources Sources { get; }

        public SweepHost Host { get; }

        public void Dispose()
        {
            Sorter.Gate.TrySetResult();
            Host.WhenIdleAsync().GetAwaiter().GetResult();
            _store.Dispose();
            _dir.Dispose();
        }
    }

    private sealed class NoStarter : IPoolStarter
    {
        public Task<string?> StartAsync(PoolStartRequest request, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class NoSplit : IEpicSplitter
    {
        public Task<int> SplitAsync(string repository, IntakeIssue epic, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class Events : Chargehand.Modules.Events.Api.IEventLog
    {
        public Chargehand.Modules.Events.Api.EventSignal Signal { get; } = new();

        public Task<IReadOnlyList<long>> AppendAsync(IReadOnlyList<Chargehand.Modules.Events.Api.NewEvent> batch, CancellationToken ct) => Task.FromResult<IReadOnlyList<long>>([]);

        public Task<IReadOnlyList<Chargehand.Modules.Events.Api.StoredEvent>> ReadAsync(Chargehand.Modules.Events.Api.EventFilter filter, CancellationToken ct) => Task.FromResult<IReadOnlyList<Chargehand.Modules.Events.Api.StoredEvent>>([]);
    }

    [Fact]
    public void Schedule_off_or_day_excluded_starts_nothing_and_post_still_runs()
    {
        using var rig = new HostRig(new SweepSettings(Enabled: false, At: ["08:00"]));
        rig.Time.Advance(new DateTimeOffset(2026, 11, 16, 8, 5, 0, TimeSpan.Zero) - rig.Time.GetUtcNow());   // a Monday
        Assert.Null(rig.Host.Tick());
        rig.Time.Advance(TimeSpan.FromHours(24));
        Assert.Null(rig.Host.Tick());

        rig.Sources.SetSweep(new IntakeSources.SweepOverrides(Enabled: true, Days: [DayOfWeek.Friday]));   // on, but not on a Wednesday
        rig.Time.Advance(TimeSpan.FromHours(24));
        Assert.Null(rig.Host.Tick());
        Assert.Null(rig.Host.Next(rig.Time.GetUtcNow()) is { } next && next.DayOfWeek != DayOfWeek.Friday ? "next is not on a Friday" : null);

        var id = rig.Host.Start("manual");

        Assert.Equal(id, rig.Host.Current?.Id);
    }

    [Fact]
    public void A_slot_due_while_the_schedule_is_on_starts_one_sweep_and_one_only()
    {
        using var rig = new HostRig(new SweepSettings(Enabled: true, At: ["08:00"]));
        rig.Time.Advance(new DateTimeOffset(2026, 11, 16, 7, 59, 0, TimeSpan.Zero) - rig.Time.GetUtcNow());
        Assert.Null(rig.Host.Tick());
        rig.Time.Advance(TimeSpan.FromMinutes(2));

        var id = rig.Host.Tick();

        Assert.NotNull(id);
        Assert.Null(rig.Host.Tick());
        Assert.Equal(id, rig.Host.Current?.Id);
    }
}
