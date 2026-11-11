using Chargehand.Modules.Runners.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Runners.Tests;

/// <summary>Issue #425: a runner whose allowlist lacks the session image is found before a task is admitted, not as a 403 inside the task.</summary>
public class ImagePreflightTests
{
    private const string Image = "registry.example/session@sha256:aaaa";
    private const string Old = "registry.example/session@sha256:bbbb";

    private sealed class Runner(IReadOnlyList<string>? images) : IHostSource, IImageAllowlistSource
    {
        public IReadOnlyList<string>? Images { get; set; } = images;
        public int ImageReads;

        public Task<HostSnapshot?> ReadAsync(CancellationToken ct) => Task.FromResult<HostSnapshot?>(new HostSnapshot(9000, 16000, 0, 0, 0.1, 4));

        public Task<IReadOnlyList<string>?> ReadImagesAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref ImageReads);
            return Task.FromResult(Images);
        }
    }

    private static PoolSite Site(string name, Runner runner, IReadOnlyList<string>? images = null) =>
        new(new DrivenRunnerEntry(name, $"http://{name}:4310", "key"), runner, images ?? [Image]);

    private static RunnerPool Pool(params PoolSite[] sites) => new(sites, new DrivenAdmission(0), TimeSpan.FromMilliseconds(20), new ManualTimeProvider());

    [Fact]
    public async Task A_pool_whose_only_runner_lacks_the_session_image_refuses_the_task_at_once_with_the_reason()
    {
        using var pool = Pool(Site("box", new Runner([Old])));

        var decision = await pool.TryAdmitAsync(1024, null, default);

        Assert.False(decision.Admit);
        Assert.True(decision.Refuse);
        Assert.Contains($"box: the session image {Image} is not on the runner's allowlist", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runner_that_lacks_the_image_is_passed_over_for_one_that_has_it_and_the_status_says_why()
    {
        using var pool = Pool(Site("box", new Runner([Old])), Site("laptop", new Runner([Image, Old])));

        var decision = await pool.TryAdmitAsync(1024, null, default);
        var statuses = (await pool.StatusAsync(default)).ToDictionary(s => s.Site.Name);

        Assert.Equal(("laptop", true), (decision.Runner, decision.Admit));
        Assert.Contains("is not on the runner's allowlist", statuses["box"].ImageMismatch, StringComparison.Ordinal);
        Assert.Null(statuses["laptop"].ImageMismatch);
    }

    [Fact]
    public async Task A_runner_that_cannot_say_or_a_profile_with_no_image_is_not_judged()
    {
        using (var old = Pool(Site("box", new Runner(null))))
            Assert.True((await old.TryAdmitAsync(1024, null, default)).Admit);
        var unjudged = new Runner([Old]);
        using (var none = Pool(Site("box", unjudged, images: [])))
            Assert.True((await none.TryAdmitAsync(1024, null, default)).Admit);
        Assert.Equal(0, unjudged.ImageReads);
    }

    [Fact]
    public async Task A_runner_restarted_with_the_image_takes_tasks_again()
    {
        var time = new ManualTimeProvider();
        var runner = new Runner([Old]);
        using var pool = new RunnerPool([Site("box", runner)], new DrivenAdmission(0), TimeSpan.FromMilliseconds(20), time);
        Assert.True((await pool.TryAdmitAsync(1024, null, default)).Refuse);

        runner.Images = [Image];
        time.Advance(RunnerPool.HealthTtl + TimeSpan.FromSeconds(1));

        Assert.True((await pool.TryAdmitAsync(1024, null, default)).Admit);
    }

    [Fact]
    public async Task One_runner_whose_allowlist_lacks_the_image_refuses_admission_with_the_reason()
    {
        using var admission = new HostAdmission(new Runner([Old]), new DrivenAdmission(0), images: [Image]);

        var decision = await admission.TryAdmitAsync(1024, default);

        Assert.False(decision.Admit);
        Assert.True(decision.Refuse);
        Assert.Contains($"the session image {Image} is not on the runner's allowlist (--images)", decision.Detail, StringComparison.Ordinal);
        Assert.Equal(0, admission.Running);
        Assert.True((await new HostAdmission(new Runner([Image]), new DrivenAdmission(0), images: [Image]).TryAdmitAsync(1024, default)).Admit);
    }

    [Fact]
    public void Missing_names_every_image_the_allowlist_lacks_and_nothing_when_it_cannot_compare()
    {
        Assert.Null(ImageAllowlist.Missing([Image], [Image, Old]));
        Assert.Null(ImageAllowlist.Missing([Image], null));
        Assert.Null(ImageAllowlist.Missing([], [Old]));
        Assert.Contains($"the session images {Image}, x@sha256:cc are not", ImageAllowlist.Missing([Image, "x@sha256:cc", Old], [Old]), StringComparison.Ordinal);
    }

    [Fact]
    public void Each_runner_is_checked_for_the_image_its_platform_starts()
    {
        var variants = new Dictionary<string, IReadOnlyDictionary<string, string>> { [Image] = new Dictionary<string, string> { ["linux/arm64"] = Old } };

        Assert.Equal([Old], DrivenRunnerSet.SessionImagesFor([Image], variants, "linux/arm64"));
        Assert.Equal([Image], DrivenRunnerSet.SessionImagesFor([Image, Image], variants, "linux/amd64"));
    }
}
