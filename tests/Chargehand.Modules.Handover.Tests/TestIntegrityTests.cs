using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Handover.Tests;

/// <summary>A green run that only changed tests or build files is flagged, unless the goal asked for that work; a flagged pull request is never merged (ADR 0046).</summary>
public class TestIntegrityTests
{
    [Theory]
    [InlineData("tests/RetryTests.cs", ChangeKind.Test)]
    [InlineData("src/Chargehand.Tests/Retry.cs", ChangeKind.Test)]
    [InlineData("src/RetryTests.cs", ChangeKind.Test)]
    [InlineData("src/retry.test.ts", ChangeKind.Test)]
    [InlineData("src/retry.spec.ts", ChangeKind.Test)]
    [InlineData("pkg/retry_test.go", ChangeKind.Test)]
    [InlineData("app/test_retry.py", ChangeKind.Test)]
    [InlineData("app/conftest.py", ChangeKind.Test)]
    [InlineData("web/__snapshots__/retry.snap", ChangeKind.Test)]
    [InlineData("web/retry.snap", ChangeKind.Test)]
    [InlineData("spec/fixtures/retry.json", ChangeKind.Test)]
    [InlineData("scripts/check.sh", ChangeKind.Build)]
    [InlineData("Makefile", ChangeKind.Build)]
    [InlineData("package.json", ChangeKind.Build)]
    [InlineData("src/App/App.csproj", ChangeKind.Build)]
    [InlineData("Directory.Build.props", ChangeKind.Build)]
    [InlineData("Chargehand.slnx", ChangeKind.Build)]
    [InlineData("pyproject.toml", ChangeKind.Build)]
    [InlineData(".github/workflows/ci.yml", ChangeKind.Build)]
    [InlineData("src/Retry.cs", ChangeKind.Production)]
    [InlineData("README.md", ChangeKind.Production)]
    [InlineData("src/LatestVersion.cs", ChangeKind.Production)]
    [InlineData("src/Contest.cs", ChangeKind.Production)]
    [InlineData("src/Attestation/Signer.cs", ChangeKind.Production)]
    [InlineData("docs/scripting.md", ChangeKind.Production)]
    public void A_path_is_a_test_a_build_file_or_production(string path, ChangeKind expected) => Assert.Equal(expected, TestIntegrity.Classify(path));

    [Theory]
    [InlineData("Add a retry to the client", false)]
    [InlineData("Add tests for the retry", true)]
    [InlineData("Fix the flaky test in RetryTests", true)]
    [InlineData("Raise coverage of the parser", true)]
    [InlineData("Speed up the build", true)]
    [InlineData("Fix the CI pipeline", true)]
    [InlineData("Update the snapshot", true)]
    [InlineData("Write a spec for the API", true)]
    [InlineData("Make the retry latest-first", false)]
    [InlineData("Handle the specific case of a 429", false)]
    public void A_goal_that_mentions_test_or_build_work_asks_for_it(string goal, bool asks) => Assert.Equal(asks, TestIntegrity.GoalAsksForTestOrBuildWork(goal));

    [Theory]
    [InlineData(new[] { "tests/RetryTests.cs" }, "Add a retry", true)]
    [InlineData(new[] { "tests/RetryTests.cs", "scripts/check.sh" }, "Add a retry", true)]
    [InlineData(new[] { "scripts/check.sh" }, "Add a retry", true)]
    [InlineData(new[] { "tests/RetryTests.cs", "src/Retry.cs" }, "Add a retry", false)]
    [InlineData(new[] { "tests/RetryTests.cs" }, "Add a test for the retry", false)]
    [InlineData(new[] { "scripts/check.sh" }, "Fix the build", false)]
    [InlineData(new[] { "src/Retry.cs" }, "Add a retry", false)]
    [InlineData(new string[0], "Add a retry", false)]
    public void Only_a_change_of_nothing_but_tests_and_build_files_for_another_goal_is_flagged(string[] changed, string goal, bool flagged)
    {
        var flag = TestIntegrity.Assess(changed, goal);
        Assert.Equal(flagged, flag is not null);
        if (flag is null)
            return;
        Assert.Equal(changed.Where(p => TestIntegrity.Classify(p) == ChangeKind.Test), flag.TestPaths);
        Assert.Equal(changed.Where(p => TestIntegrity.Classify(p) == ChangeKind.Build), flag.BuildPaths);
    }

    [Fact]
    public void The_warning_names_the_counts_and_says_it_is_not_merged()
    {
        var warning = TestIntegrity.Warning(new TestIntegrityFlag(["tests/a.cs", "tests/b.cs"], ["Makefile"]));
        Assert.StartsWith("> **Warning: test integrity.**", warning);
        Assert.Contains("2 test, 1 build", warning);
        Assert.Contains("does not merge it", warning);
    }

    private static readonly RemoteRepository Repo = new("o", "r");

    private static Integrator Make(FakeIntegrationHost host, string mode = "merge") =>
        new(new IntegratorPolicy(new DrivenIntegrator(1, 2, "k", new Dictionary<string, string> { ["o/r"] = mode }, null)), host, delay: (_, _) => Task.CompletedTask);

    private static IntegrationTarget Flagged() => new(Repo, 7, "chargehand/run1", false, "Add a retry", "every changed file is a test or build file");

    [Theory]
    [InlineData("CLEAN")]
    [InlineData("BLOCKED")]
    public async Task The_integrator_never_merges_a_flagged_pull_request_on_a_merge_repository(string statusAfterReady)
    {
        var host = new FakeIntegrationHost { StatusAfterReady = statusAfterReady };
        List<string> told = [];

        var result = await Make(host).IntegrateAsync(Flagged(), told.Add, default);

        Assert.Equal(IntegrationAction.Asked, result.Action);
        Assert.Contains("test integrity", result.Detail);
        Assert.Contains("ready", host.Calls);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("auto-merge", StringComparison.Ordinal) || c.StartsWith("merge ", StringComparison.Ordinal) || c.StartsWith("update-branch", StringComparison.Ordinal));
        var comment = Assert.Single(host.Comments);
        Assert.Contains("does not merge it", comment);
        Assert.Contains("every changed file is a test or build file", comment);
    }

    [Fact]
    public async Task A_second_pass_over_a_flagged_pull_request_does_not_comment_again()
    {
        var host = new FakeIntegrationHost();
        var integrator = Make(host);

        await integrator.IntegrateAsync(Flagged(), _ => { }, default);
        await integrator.IntegrateAsync(Flagged(), _ => { }, default);

        Assert.Single(host.Comments);
    }

    [Fact]
    public async Task Watching_a_flagged_pull_request_whose_checks_pass_leaves_the_merge_to_a_person()
    {
        var host = new FakeIntegrationHost();
        host.Script = h => h.Current with { IsDraft = false, MergeStatus = "CLEAN", Checks = [new CheckStatus("ci-gate", true, CheckOutcome.Passed, null)] };

        var result = await Make(host).FollowAsync(Flagged(), (_, _) => Task.FromResult(new CiFixResult(false, "")), _ => { }, default);

        Assert.Equal(IntegrationAction.Asked, result.Action);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("auto-merge", StringComparison.Ordinal) || c.StartsWith("merge ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unflagged_pull_request_on_the_same_repository_is_still_merged()
    {
        var host = new FakeIntegrationHost { StatusAfterReady = "CLEAN" };

        var result = await Make(host).IntegrateAsync(Flagged() with { TestIntegrity = null }, _ => { }, default);

        Assert.Equal(IntegrationAction.Merged, result.Action);
    }
}
