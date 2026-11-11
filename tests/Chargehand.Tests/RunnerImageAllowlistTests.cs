using System.Net;
using Chargehand.Config;
using Chargehand.Driven;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Server;
using Chargehand.Workspace;

namespace Chargehand.Tests;

/// <summary>Issue #425: the runner says which session images it starts, the System page shows a runner whose allowlist lacks one, and the server's preflight tells the log.</summary>
public class RunnerImageAllowlistTests
{
    private const string Key = "runner-test-key";
    private static readonly string Image = "registry.test/session@sha256:" + new string('a', 64);
    private static readonly string Old = "registry.test/session@sha256:" + new string('b', 64);

    private sealed class Runner(IReadOnlyList<string>? images) : IHostSource, IImageAllowlistSource
    {
        public Task<HostSnapshot?> ReadAsync(CancellationToken ct) => Task.FromResult<HostSnapshot?>(new HostSnapshot(8000, 16000, null, null, 0.5, 4));

        public Task<IReadOnlyList<string>?> ReadImagesAsync(CancellationToken ct) => Task.FromResult(images);
    }

    private static DrivenServices Services(DrivenFakes.FakeEngine engine, IHostSource host, IReadOnlyList<RunnerSite>? runners = null) =>
        new(engine, engine, new DrivenFakes.FakeVolumes(_ => SessionStatus.Completed), _ => throw new NotSupportedException(), null,
            (_, _, _) => Task.FromResult(new DrivenCheckout("/srv/checkouts/repo", ConsoleRig.Commit, "https://github.com/o/r.git", "main")), new DrivenRunTests.NoEvidenceProblems(),
            name => $"value-of-{name}", Host: host, Runners: runners);

    [Fact]
    public async Task The_images_route_serves_the_allowlist_to_the_client_and_needs_the_key()
    {
        var app = RunnerServer.Create(new RunnerSettings(0, Key, new RunnerPolicy([Image, Old], Old)), new DrivenFakes.FakeEngine());
        await app.StartAsync();
        var address = new Uri(app.Urls.First());
        using var http = new HttpClient { BaseAddress = address };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/images")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", Key);

        Assert.Equal([Image, Old], await new RunnerClient(http).ReadImagesAsync(default));
        await app.DisposeAsync();
    }

    [Fact]
    public async Task A_runner_older_than_the_images_route_reads_as_unknown()
    {
        using var http = new HttpClient(new NotFound()) { BaseAddress = new Uri("http://runner.internal:4310") };

        Assert.Null(await new RunnerClient(http).ReadImagesAsync(default));
    }

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"not found"}""") });
    }

    [Fact]
    public async Task System_shows_the_runner_whose_allowlist_lacks_the_session_image()
    {
        var engine = new DrivenFakes.FakeEngine();
        await using var rig = await ConsoleRig.StartAsync(profile: root => Runs.Profile(root) with
        {
            Driven = new DrivenSettings(true, Images: [Image], Runner: new DrivenRunner("http://runner.internal:4310", "runner-key-item")),
        }, services: _ => Services(engine, new Runner([Old])));

        var only = (await ConsoleRig.Get(rig.Owner, "/v1/system")).GetProperty("runners").EnumerateArray().Single();

        Assert.True(only.GetProperty("reachable").GetBoolean());
        Assert.Contains($"the session image {Image} is not on the runner's allowlist", only.GetProperty("image_mismatch").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task System_shows_which_runner_of_a_pool_lacks_the_session_image()
    {
        var box = new DrivenFakes.FakeEngine();
        var laptop = new DrivenFakes.FakeEngine();
        var boxEntry = new DrivenRunnerEntry("box", "http://box.internal:4310", "runner-key-item");
        var laptopEntry = new DrivenRunnerEntry("laptop", "http://laptop.internal:4310", "runner-key-item");
        await using var rig = await ConsoleRig.StartAsync(profile: root => Runs.Profile(root) with { Driven = new DrivenSettings(true, Images: [Image], Runners: [boxEntry, laptopEntry]) },
            services: _ => Services(box, new Runner([Old]),
            [
                new RunnerSite(boxEntry, box, box, new DrivenFakes.FakeVolumes(_ => SessionStatus.Completed), new Runner([Old])),
                new RunnerSite(laptopEntry, laptop, laptop, new DrivenFakes.FakeVolumes(_ => SessionStatus.Completed), new Runner([Image])),
            ]));

        var runners = (await ConsoleRig.Get(rig.Owner, "/v1/system")).GetProperty("runners").EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!);

        Assert.Contains("is not on the runner's allowlist", runners["box"].GetProperty("image_mismatch").GetString(), StringComparison.Ordinal);
        Assert.False(runners["laptop"].TryGetProperty("image_mismatch", out _));
    }

    [Fact]
    public async Task The_preflight_tells_a_new_mismatch_once_and_when_it_clears()
    {
        IReadOnlyDictionary<string, string?> found = new Dictionary<string, string?> { ["box"] = "the session image x is not on the runner's allowlist (--images)", ["laptop"] = null };
        List<string> lines = [];
        var preflight = new ImagePreflight(ImagePreflight.Every, _ => Task.FromResult(found), lines.Add);

        await preflight.PassAsync(default);
        await preflight.PassAsync(default);
        found = new Dictionary<string, string?> { ["box"] = null };
        await preflight.PassAsync(default);

        Assert.Equal(2, lines.Count);
        Assert.Contains("runner 'box': the session image x is not on the runner's allowlist", lines[0], StringComparison.Ordinal);
        Assert.Contains("runner 'box' carries the session images again", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_server_finds_each_runners_mismatch_for_its_preflight()
    {
        var engine = new DrivenFakes.FakeEngine();
        using var root = new TempDir();
        var profile = Runs.Profile(root.Path) with { Driven = new DrivenSettings(true, Images: [Image], Runner: new DrivenRunner("http://runner.internal:4310", "runner-key-item")) };
        using var driven = new DrivenRun(profile, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), root.Path, Services(engine, new Runner([Old])), Path.Combine(root.Path, "state"));

        var found = await driven.ImageMismatchesAsync(default);

        Assert.Contains("is not on the runner's allowlist", found[DrivenSettings.DefaultRunnerName], StringComparison.Ordinal);
    }
}
