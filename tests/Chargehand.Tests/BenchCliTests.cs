using Chargehand.Bench;
using Chargehand.Config;

namespace Chargehand.Tests;

/// <summary>The command line of <c>chargehand bench run</c>: what it refuses, what it reads from the profile, and that a dry run never touches the engine, the server or the network.</summary>
public class BenchCliTests
{
    private const string Pin = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string Image = "registry.example/session@sha256:" + new string('a', 64);

    private sealed class Run : IDisposable
    {
        public TempDir Dir { get; } = new();
        public StringWriter Out { get; } = new();
        public StringWriter Error { get; } = new();
        public int EnginesAsked { get; private set; }
        public int ServersAsked { get; private set; }
        public List<IReadOnlyList<string>> ServerArgs { get; } = [];

        public Run()
        {
            Dir.Write("set.json", $$"""
                { "schema": "bench-taskset/v1", "name": "set",
                  "repositories": [ { "name": "lib", "ecosystem": "python", "url": "https://example.com/org/lib.git", "commit": "{{Pin}}", "license": "MIT", "test": ["true"] } ],
                  "tasks": [ { "id": "a", "repository": "lib", "kind": "change", "size": "S", "goal": "Do it.", "acceptance": { "argv": ["true"] } } ] }
                """);
        }

        public string SetPath => Path.Combine(Dir.Path, "set.json");

        public Task<int> Go(Profile? profile, params string[] args) =>
            BenchCli.RunAsync(args, profile ?? new Profile("profile/v1"), ["api.anthropic.com"], new AcceptanceLimits(1024, 1, 64),
                () =>
                {
                    EnginesAsked++;
                    throw new InvalidOperationException("no engine in this test");
                },
                serverArgs =>
                {
                    ServersAsked++;
                    ServerArgs.Add(serverArgs);
                    return (null, "no server or key: pass --url <server>");
                },
                _ => null, Dir.Path, Out, Error, default);

        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public async Task A_dry_run_reads_the_set_and_starts_nothing()
    {
        using var run = new Run();
        var profile = new Profile("profile/v1", Driven: new DrivenSettings(Images: [Image], Network: new DrivenNetwork(Allow: ["pypi.org"]), Governor: new DrivenGovernor(GovernorMode.Observe)));
        Assert.Equal(0, await run.Go(profile, run.SetPath, "--dry-run"));
        Assert.Contains("dry run: nothing was started.", run.Out.ToString());
        Assert.Contains("governor mode is observe", run.Out.ToString());
        Assert.DoesNotContain("session image", run.Out.ToString());
        Assert.DoesNotContain("pypi.org to driven.network.allow", run.Out.ToString());
        Assert.Equal((0, 0), (run.EnginesAsked, run.ServersAsked));
        Assert.False(Directory.Exists(Path.Combine(run.Dir.Path, "runs")));
    }

    [Fact]
    public async Task A_dry_run_with_no_profile_notes_the_missing_image_and_hosts()
    {
        using var run = new Run();
        Assert.Equal(0, await run.Go(null, run.SetPath, "--dry-run"));
        Assert.Contains("the profile names no session image for python", run.Out.ToString());
        Assert.Contains("add pypi.org, files.pythonhosted.org to driven.network.allow", run.Out.ToString());
    }

    [Fact]
    public async Task Options_are_checked_before_anything_else()
    {
        using var run = new Run();
        Assert.Equal(2, await run.Go(null));
        Assert.Contains("usage: chargehand", run.Error.ToString());
        Assert.Equal(2, await run.Go(null, run.SetPath, "--surprise"));
        Assert.Contains("unknown option --surprise", run.Error.ToString());
        Assert.Equal(2, await run.Go(null, run.SetPath, "--only"));
        Assert.Contains("--only needs a value", run.Error.ToString());
        Assert.Equal(2, await run.Go(null, run.SetPath, "--dry-run", "--baseline"));
        Assert.Contains("pick one", run.Error.ToString());
        Assert.Equal(2, await run.Go(null, run.SetPath, "--max-tokens", "-4"));
        Assert.Equal(2, await run.Go(null, run.SetPath, "--max-usd", "lots"));
        Assert.Equal(2, await run.Go(null, run.SetPath, "--max-parallel", "9"));
        Assert.Equal(2, await run.Go(null, Path.Combine(run.Dir.Path, "missing.json"), "--dry-run"));
        Assert.Equal((0, 0), (run.EnginesAsked, run.ServersAsked));
    }

    [Fact]
    public async Task A_set_with_problems_prints_each_and_exits_two()
    {
        using var run = new Run();
        var broken = run.Dir.Write("broken.json", """{ "schema": "bench-taskset/v1", "name": "x", "repositories": [], "tasks": [] }""");
        Assert.Equal(2, await run.Go(null, broken, "--dry-run"));
        Assert.Contains("the set has no repositories", run.Error.ToString());
        Assert.Contains("the set has no tasks", run.Error.ToString());
    }

    [Fact]
    public async Task Only_with_an_unknown_task_is_a_usage_error()
    {
        using var run = new Run();
        Assert.Equal(2, await run.Go(null, run.SetPath, "--dry-run", "--only", "zzz"));
        Assert.Contains("--only names tasks the set does not have: zzz", run.Out.ToString());
    }

    [Fact]
    public async Task A_real_run_connects_to_the_server_with_the_url_given_and_stops_when_there_is_none()
    {
        using var run = new Run();
        Assert.Equal(2, await run.Go(null, run.SetPath, "--url", "http://chargehand.test", "--remote", "file:///x/{name}.git", "--max-tokens", "1000"));
        Assert.Equal(["--url", "http://chargehand.test"], Assert.Single(run.ServerArgs));
        Assert.Contains("no server or key", run.Error.ToString());
        Assert.Equal(0, run.EnginesAsked);
    }

    [Fact]
    public async Task A_baseline_asks_for_the_engine_and_not_the_server()
    {
        using var run = new Run();
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.Go(null, run.SetPath, "--baseline"));
        Assert.Equal((1, 0), (run.EnginesAsked, run.ServersAsked));
    }
}
