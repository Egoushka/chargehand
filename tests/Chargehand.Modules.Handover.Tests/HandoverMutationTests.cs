using Chargehand.Contracts;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Modules.Handover.Tests;

using Handover = Chargehand.Modules.Handover.Api.Handover;

/// <summary>Issue 788: the tests of a change must kill a share of the mutants of its changed lines, or the branch is held back.</summary>
public class HandoverMutationTests
{
    /// <summary>A mutation tester that answers <paramref name="report"/>.</summary>
    private sealed class FakeMutation(MutationReport report) : IMutationTester
    {
        public List<MutationRun> Calls { get; } = [];

        public Task<MutationReport> MutateAsync(MutationRun run, CancellationToken ct)
        {
            Calls.Add(run);
            return Task.FromResult(report);
        }
    }

    private static string StrykerTail(string score) => $"All mutants have been tested, and your mutation score has been calculated\nThe final mutation score is {score} %\n";

    /// <summary>A change to <c>src/Retry.cs</c> and its test; <paramref name="assertion"/> is the test's body.</summary>
    private static HandoverTests.Fixture CodeChange(string assertion) => new(file: "src/Retry.cs", edit: w =>
    {
        Directory.CreateDirectory(Path.Combine(w, "tests"));
        File.WriteAllText(Path.Combine(w, "tests", "RetryTests.cs"), $"public class RetryTests {{ [Fact] public void Retries() {{ {assertion} }} }}\n");
    });

    [Fact]
    public async Task A_change_whose_test_asserts_nothing_is_held_back()
    {
        using var f = CodeChange("new Retry().Run();");
        var mutation = new FakeMutation(StrykerMutationTester.Read(0, StrykerTail("0.00")));
        var prs = new HandoverTests.FakePullRequests();

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), prs, mutation: mutation).RunAsync(f.Input(with: i => i with { MutationShare = 0.6 }), default);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Equal(ErrorCode.VerificationFailed, outcome.Error);
        Assert.Contains("killed 0% of the mutants", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal(["src/Retry.cs"], mutation.Calls.Single().Files);
        Assert.Equal(f.BaseCommit, mutation.Calls.Single().BaseCommit);
        Assert.Empty(prs.Created);
        Assert.Equal("", f.RemoteRef("chargehand/run1"));
    }

    [Fact]
    public async Task A_change_with_a_real_test_passes()
    {
        using var f = CodeChange("Assert.Equal(2, new Retry().Run());");
        var mutation = new FakeMutation(StrykerMutationTester.Read(0, StrykerTail("83.33")));
        var prs = new HandoverTests.FakePullRequests();

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), prs, mutation: mutation).RunAsync(f.Input(with: i => i with { MutationShare = 0.6 }), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Single(mutation.Calls);
        Assert.Single(prs.Created);
    }

    [Fact]
    public async Task A_mutation_run_that_cannot_finish_holds_the_branch_back()
    {
        using var f = CodeChange("new Retry().Run();");
        var mutation = new FakeMutation(StrykerMutationTester.Read(1, "error: build failed"));

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests(), mutation: mutation).RunAsync(f.Input(with: i => i with { MutationShare = 0.6 }), default);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Contains("the mutation check could not run", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_outcome_records_the_score_against_the_threshold()
    {
        using var f = CodeChange("Assert.Equal(2, new Retry().Run());");
        var mutation = new FakeMutation(new MutationReport(true, 0.71, ""));

        var passed = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests(), mutation: mutation).RunAsync(f.Input(with: i => i with { MutationShare = 0.6 }), default);

        Assert.Equal(new MutationOutcome(true, 0.71, 0.6, null), passed.Mutation);

        using var g = CodeChange("new Retry().Run();");
        var weak = new FakeMutation(new MutationReport(true, 0.2, ""));
        var refused = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests(), mutation: weak).RunAsync(g.Input(with: i => i with { MutationShare = 0.6 }), default);

        Assert.Equal(new MutationOutcome(true, 0.2, 0.6, null), refused.Mutation);
    }

    [Fact]
    public async Task A_run_that_could_not_finish_records_why()
    {
        using var f = CodeChange("new Retry().Run();");
        var mutation = new FakeMutation(new MutationReport(false, null, "the mutation run timed out"));

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests(), mutation: mutation).RunAsync(f.Input(with: i => i with { MutationShare = 0.6 }), default);

        Assert.Equal(new MutationOutcome(false, null, 0.6, "the mutation run timed out"), outcome.Mutation);
    }

    [Fact]
    public async Task A_change_with_no_production_file_has_no_mutation_record()
    {
        using var f = new HandoverTests.Fixture();
        var mutation = new FakeMutation(new MutationReport(true, 0, ""));

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests(), mutation: mutation).RunAsync(f.Input(with: i => i with { MutationShare = 0.6 }), default);

        Assert.Null(outcome.Mutation);
    }

    [Fact]
    public async Task No_share_no_check()
    {
        using var f = CodeChange("new Retry().Run();");
        var mutation = new FakeMutation(new MutationReport(true, 0, ""));

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests(), mutation: mutation).RunAsync(f.Input(), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Empty(mutation.Calls);
    }

    [Fact]
    public async Task A_change_with_no_csharp_production_file_is_not_mutated()
    {
        using var f = new HandoverTests.Fixture();
        var mutation = new FakeMutation(new MutationReport(true, 0, ""));

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests(), mutation: mutation).RunAsync(f.Input(with: i => i with { MutationShare = 0.6 }), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Empty(mutation.Calls);
    }

    [Fact]
    public async Task Failing_tests_are_refused_before_any_mutation()
    {
        using var f = CodeChange("new Retry().Run();");
        var mutation = new FakeMutation(new MutationReport(true, 1, ""));

        var outcome = await new Handover(new HandoverTests.FakeVerifier(exit: 1), new HandoverTests.FakePullRequests(), mutation: mutation).RunAsync(f.Input(with: i => i with { MutationShare = 0.6 }), default);

        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Contains("the tests failed", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(mutation.Calls);
    }

    [Theory]
    [InlineData("The final mutation score is 75.50 %", true, 0.755)]
    [InlineData("The final mutation score is NaN %", true, null)]
    [InlineData("It looks like all mutants were filtered out. No mutants found.", true, null)]
    [InlineData("Build failed", false, null)]
    public void Stryker_output_is_read(string output, bool ran, double? score)
    {
        var report = StrykerMutationTester.Read(0, output);

        Assert.Equal(ran, report.Ran);
        Assert.Equal(score, report.Score is { } s ? Math.Round(s, 3) : null);
    }

    [Theory]
    [InlineData("src/Retry.cs", true)]
    [InlineData("tests/RetryTests.cs", false)]
    [InlineData("src/Retry.Tests/RetryTests.cs", false)]
    [InlineData("src/retry.ts", false)]
    public void Only_csharp_production_files_are_mutated(string path, bool mutated) =>
        Assert.Equal(mutated, Handover.MutatedFiles([path]).Count == 1);

    [Fact]
    public void The_stryker_command_mutates_since_the_base_commit()
    {
        var argv = StrykerMutationTester.Argv("abc1234");

        Assert.Contains("--since:abc1234", argv[^1], StringComparison.Ordinal);
        Assert.Contains($"--version {StrykerMutationTester.Version}", argv[^1], StringComparison.Ordinal);
    }
}
