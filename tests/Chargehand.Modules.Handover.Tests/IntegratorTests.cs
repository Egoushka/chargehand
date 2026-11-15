using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Handover.Tests;

/// <summary>ADR 0046: after a task's handover opened a draft pull request on an opted-in repository, the integrator brings the branch up to date, marks it ready and merges by policy. It
/// merges only through GitHub's rules, never a partial result, and asks where the policy says ask.</summary>
public class IntegratorTests
{
    private static readonly RemoteRepository Repo = new("o", "r");

    private static Integrator Make(FakeIntegrationHost host, string mode = "merge", string[]? alwaysAsk = null) =>
        new(new IntegratorPolicy(new DrivenIntegrator(1, 2, "k", new Dictionary<string, string> { ["o/r"] = mode }, alwaysAsk)), host, delay: (_, _) => Task.CompletedTask);

    /// <summary>Handover, then chargehand's clean review of the head it left.</summary>
    private static async Task<IntegrationResult> ApprovedAsync(FakeIntegrationHost host)
    {
        await Make(host).IntegrateAsync(Target(), _ => { }, default);
        host.Current = host.Current with { Checks = Green };
        return await Make(host).ApproveAsync(Target(), "aaaaaaa1", default);
    }

    private static readonly CheckStatus[] Green = [new("ci", true, CheckOutcome.Passed, 1)];

    private static IntegrationTarget Target(bool partial = false) => new(Repo, 7, "chargehand/run1", partial, "Add a retry");

    [Fact]
    public async Task Handover_marks_ready_but_does_not_enable_auto_merge()
    {
        var host = new FakeIntegrationHost();
        List<string> told = [];

        var result = await Make(host).IntegrateAsync(Target(), told.Add, default);

        Assert.Equal(IntegrationAction.AwaitingReview, result.Action);
        Assert.Equal(["get", "behind main chargehand/run1", "ready", "get"], host.Calls);
        Assert.Equal(["marked ready for review", result.Detail], told);
    }

    [Fact]
    public async Task Clean_review_on_head_merges_it_pinned_to_the_reviewed_head()
    {
        var host = new FakeIntegrationHost { Current = FakeIntegrationHost.Snapshot(draft: false, status: "BLOCKED", checks: [new("ci", true, CheckOutcome.Passed, 1)]) };

        var result = await Make(host).ApproveAsync(Target(), "aaaaaaa1", default);

        Assert.Equal(IntegrationAction.Merged, result.Action);
        Assert.Contains("merge aaaaaaa1", host.Calls);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("auto-merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clean_review_merges_nothing_in_ask_mode_on_a_moved_head_with_a_failed_or_pending_check_or_a_marker()
    {
        var ready = FakeIntegrationHost.Snapshot(draft: false, status: "BLOCKED", checks: Green);
        var ask = new FakeIntegrationHost { Current = ready };
        var moved = new FakeIntegrationHost { Current = ready with { HeadSha = "ccccccc3" } };
        var failed = new FakeIntegrationHost { Current = ready with { Checks = [new("ci", true, CheckOutcome.Failed, 1)] } };

        await Make(ask, "ask").ApproveAsync(Target(), "aaaaaaa1", default);
        await Make(moved).ApproveAsync(Target(), "aaaaaaa1", default);
        await Make(failed).ApproveAsync(Target(), "aaaaaaa1", default);
        var pending = new FakeIntegrationHost { Current = ready with { Checks = [new("ci", true, CheckOutcome.Pending, 1)] } };
        var pendingResult = await Make(pending).ApproveAsync(Target(), "aaaaaaa1", default);
        var asked = new FakeIntegrationHost { Current = ready };
        asked.Comments.Add("<!-- chargehand-integration test-integrity -->");
        await Make(asked).ApproveAsync(Target(), "aaaaaaa1", default);

        Assert.Equal(IntegrationAction.AwaitingReview, pendingResult.Action);
        foreach (var host in new[] { ask, moved, failed, pending, asked })
            Assert.DoesNotContain(host.Calls, c => c.StartsWith("auto-merge", StringComparison.Ordinal) || c.StartsWith("merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_behind_its_base_is_updated_first_and_nothing_is_merged_before_its_review()
    {
        var host = new FakeIntegrationHost { BehindBy = 2 };

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);
        await Make(host).ApproveAsync(Target(), "bbbbbbb2", default);

        Assert.Equal(IntegrationAction.AwaitingReview, result.Action);
        Assert.Equal(["get", "behind main chargehand/run1", "update-branch aaaaaaa1", "get", "ready", "get", "get"], host.Calls);
    }

    [Fact]
    public async Task A_branch_whose_update_does_not_move_its_head_is_left_unmerged()
    {
        var host = new FakeIntegrationHost { BehindBy = 2, UpdateMovesHead = false };

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.NeedsAttention, result.Action);
        Assert.DoesNotContain(host.Calls, c => c is "ready" || c.StartsWith("auto-merge", StringComparison.Ordinal) || c.StartsWith("merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_that_cannot_be_updated_gets_one_comment_and_no_merge()
    {
        var host = new FakeIntegrationHost { BehindBy = 1, UpdateFails = new IntegrationException("merge conflict") };

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.NeedsAttention, result.Action);
        Assert.Contains("merge conflict", result.Detail);
        Assert.Single(host.Comments);
        Assert.DoesNotContain("ready", host.Calls);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("auto-merge", StringComparison.Ordinal) || c.StartsWith("merge ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pull_request_GitHub_calls_clean_is_squash_merged_at_its_head_and_never_through_a_bypass()
    {
        var host = new FakeIntegrationHost { StatusAfterReady = "CLEAN" };

        var result = await ApprovedAsync(host);

        Assert.Equal(IntegrationAction.Merged, result.Action);
        Assert.Equal("merge aaaaaaa1", host.Calls[^1]);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("auto-merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pull_request_that_conflicts_with_its_base_is_commented_on_once_and_not_merged()
    {
        var host = new FakeIntegrationHost { StatusAfterReady = "DIRTY" };
        var integrator = Make(host);

        var first = await integrator.IntegrateAsync(Target(), _ => { }, default);
        var second = await integrator.IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.NeedsAttention, first.Action);
        Assert.Equal(IntegrationAction.NeedsAttention, second.Action);
        Assert.Single(host.Comments);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("auto-merge", StringComparison.Ordinal) || c.StartsWith("merge ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_auto_merge_armed_earlier_is_turned_off_and_never_merges_a_head_no_review_cleared()
    {
        var host = new FakeIntegrationHost { Current = FakeIntegrationHost.Snapshot(draft: false, status: "BLOCKED", autoMerge: true, checks: Green) };

        var result = await Make(host).ApproveAsync(Target(), "", default);

        Assert.Equal(IntegrationAction.None, result.Action);
        Assert.Contains("disable-auto-merge", host.Calls);
        Assert.False(host.Current.AutoMerge);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_partial_result_is_never_touched_and_never_merged()
    {
        var host = new FakeIntegrationHost();

        var result = await Make(host).IntegrateAsync(Target(partial: true), _ => { }, default);

        Assert.Equal(IntegrationAction.None, result.Action);
        Assert.Contains("never merged", result.Detail);
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData("ask", null)]
    [InlineData("merge", "o/r")]
    public async Task An_ask_repository_gets_its_draft_marked_ready_and_a_comment_and_no_merge(string mode, string? alwaysAsk)
    {
        var host = new FakeIntegrationHost { BehindBy = 3, StatusAfterReady = "CLEAN" };

        var result = await Make(host, mode, alwaysAsk is null ? null : [alwaysAsk]).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.Asked, result.Action);
        Assert.Equal(["get", "ready", "get", "comment"], host.Calls);
        Assert.Contains("ask", Assert.Single(host.Comments));
    }

    [Fact]
    public async Task An_ask_comment_is_left_once_however_often_the_pass_repeats()
    {
        var host = new FakeIntegrationHost();
        var integrator = Make(host, "ask");

        await integrator.IntegrateAsync(Target(), _ => { }, default);
        await integrator.IntegrateAsync(Target(), _ => { }, default);

        Assert.Single(host.Comments);
    }

    [Fact]
    public async Task A_repository_that_is_not_opted_in_is_not_touched()
    {
        var host = new FakeIntegrationHost();
        var integrator = new Integrator(new IntegratorPolicy(new DrivenIntegrator(1, 2, "k", new Dictionary<string, string> { ["o/other"] = "merge" })), host);

        var result = await integrator.IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.None, result.Action);
        Assert.Empty(host.Calls);
        Assert.Null(integrator.Describe(Repo, partial: false));
    }

    [Fact]
    public async Task A_pull_request_already_merged_or_closed_is_left_alone()
    {
        var host = new FakeIntegrationHost { Current = FakeIntegrationHost.Snapshot(draft: false, merged: true) };

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.None, result.Action);
        Assert.Equal(["get"], host.Calls);
    }

    [Fact]
    public async Task A_GitHub_error_is_a_failed_result_and_not_an_exception()
    {
        var host = new FakeIntegrationHost { ReadFails = new IntegrationException("GitHub answered 502") };
        List<string> told = [];

        var result = await Make(host).IntegrateAsync(Target(), told.Add, default);

        Assert.Equal(IntegrationAction.Failed, result.Action);
        Assert.Contains("502", result.Detail);
        Assert.Equal([result.Detail], told);
    }

    [Fact]
    public void The_pull_request_body_says_what_the_policy_is_for_an_opted_in_repository_only()
    {
        var host = new FakeIntegrationHost();
        Assert.Contains("review of its head", Make(host).Describe(Repo, partial: false));
        Assert.Contains("up to 3 attempts", Make(host).Describe(Repo, partial: false));
        Assert.Contains("asks first", Make(host, "ask").Describe(Repo, partial: false));
        Assert.Null(Make(host).Describe(Repo, partial: true));
    }
}
