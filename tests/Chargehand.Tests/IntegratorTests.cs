using Chargehand.Config;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>A pull request on a pretend GitHub: the snapshot a test sets (or a script computes) plus a record of what the integrator did to it.</summary>
internal sealed class FakeIntegrationHost : IIntegrationHost
{
    public List<string> Calls { get; } = [];

    public List<string> Comments { get; } = [];

    public PrSnapshot Current { get; set; } = Snapshot();

    /// <summary>Computes the snapshot for each read, from the host's own record of what was done; null: <see cref="Current"/>.</summary>
    public Func<FakeIntegrationHost, PrSnapshot>? Script { get; set; }

    public int BehindBy { get; set; }

    public bool UpdateMovesHead { get; set; } = true;

    public Exception? UpdateFails { get; set; }

    public Exception? AutoMergeRefused { get; set; }

    public Exception? ReadFails { get; set; }

    public string StatusAfterReady { get; set; } = "BLOCKED";

    public Dictionary<long, string> Logs { get; } = [];

    public static PrSnapshot Snapshot(string head = "aaaaaaa1", bool draft = true, string status = "DRAFT", bool autoMerge = false, bool merged = false, IReadOnlyList<CheckStatus>? checks = null) =>
        new("PR_node", 7, head, "main", draft, merged, false, status, autoMerge, checks ?? [], []);

    public Task<PrSnapshot> GetAsync(RemoteRepository repository, int number, CancellationToken ct)
    {
        Calls.Add("get");
        if (ReadFails is not null)
            throw ReadFails;
        var snapshot = Script?.Invoke(this) ?? Current;
        return Task.FromResult(snapshot with { Comments = [.. snapshot.Comments, .. Comments] });
    }

    public Task<int> BehindByAsync(RemoteRepository repository, string baseBranch, string branch, CancellationToken ct)
    {
        Calls.Add($"behind {baseBranch} {branch}");
        return Task.FromResult(BehindBy);
    }

    public Task UpdateBranchAsync(RemoteRepository repository, int number, string expectedHeadSha, CancellationToken ct)
    {
        Calls.Add($"update-branch {expectedHeadSha}");
        if (UpdateFails is not null)
            throw UpdateFails;
        if (UpdateMovesHead)
            Current = Current with { HeadSha = "bbbbbbb2" };
        BehindBy = 0;
        return Task.CompletedTask;
    }

    public Task MarkReadyAsync(RemoteRepository repository, string nodeId, CancellationToken ct)
    {
        Calls.Add("ready");
        Current = Current with { IsDraft = false, MergeStatus = StatusAfterReady };
        return Task.CompletedTask;
    }

    public Task EnableAutoMergeAsync(RemoteRepository repository, string nodeId, string headSha, CancellationToken ct)
    {
        Calls.Add($"auto-merge {headSha}");
        if (AutoMergeRefused is not null)
            throw AutoMergeRefused;
        Current = Current with { AutoMerge = true };
        return Task.CompletedTask;
    }

    public Task MergeNowAsync(RemoteRepository repository, int number, string headSha, CancellationToken ct)
    {
        Calls.Add($"merge {headSha}");
        Current = Current with { Merged = true };
        return Task.CompletedTask;
    }

    public Task CommentAsync(RemoteRepository repository, int number, string body, CancellationToken ct)
    {
        Calls.Add("comment");
        Comments.Add(body);
        return Task.CompletedTask;
    }

    /// <summary>The job ids each re-run was asked for; a re-run turns the failed checks pending.</summary>
    public List<IReadOnlyList<long>> Reruns { get; } = [];

    public Exception? RerunFails { get; set; }

    public Task<int> RerunFailedJobsAsync(RemoteRepository repository, IReadOnlyList<long> jobIds, CancellationToken ct)
    {
        Calls.Add("rerun");
        if (RerunFails is not null)
            throw RerunFails;
        Reruns.Add(jobIds);
        Current = Current with { Checks = [.. Current.Checks.Select(c => jobIds.Contains(c.JobId ?? -1) ? c with { Outcome = CheckOutcome.Pending } : c)] };
        return Task.FromResult(jobIds.Count);
    }

    public Task<string> JobLogTailAsync(RemoteRepository repository, long jobId, int maxChars, CancellationToken ct)
    {
        Calls.Add($"log {jobId}");
        return Logs.TryGetValue(jobId, out var log) ? Task.FromResult(log) : throw new IntegrationException("no log");
    }
}

/// <summary>ADR 0046: after a task's handover opened a draft pull request on an opted-in repository, the integrator brings the branch up to date, marks it ready and merges by policy. It
/// merges only through GitHub's rules, never a partial result, and asks where the policy says ask.</summary>
public class IntegratorTests
{
    private static readonly RemoteRepository Repo = new("o", "r");

    private static Integrator Make(FakeIntegrationHost host, string mode = "merge", string[]? alwaysAsk = null) =>
        new(new IntegratorPolicy(new DrivenIntegrator(1, 2, "k", new Dictionary<string, string> { ["o/r"] = mode }, alwaysAsk)), host, delay: (_, _) => Task.CompletedTask);

    private static IntegrationTarget Target(bool partial = false) => new(Repo, 7, "chargehand/run1", partial, "Add a retry");

    [Fact]
    public async Task A_draft_on_a_merge_repository_is_marked_ready_and_auto_merge_is_enabled_for_its_head()
    {
        var host = new FakeIntegrationHost();
        List<string> told = [];

        var result = await Make(host).IntegrateAsync(Target(), told.Add, default);

        Assert.Equal(IntegrationAction.AutoMergeEnabled, result.Action);
        Assert.Equal(["get", "behind main chargehand/run1", "ready", "get", "auto-merge aaaaaaa1"], host.Calls);
        Assert.Equal(["marked ready for review", result.Detail], told);
        Assert.Contains("squash", result.Detail);
    }

    [Fact]
    public async Task A_branch_behind_its_base_is_updated_first_and_the_merge_is_pinned_to_the_new_head()
    {
        var host = new FakeIntegrationHost { BehindBy = 2 };

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.AutoMergeEnabled, result.Action);
        Assert.Equal(["get", "behind main chargehand/run1", "update-branch aaaaaaa1", "get", "ready", "get", "auto-merge bbbbbbb2"], host.Calls);
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

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.Merged, result.Action);
        Assert.Equal("merge aaaaaaa1", host.Calls[^1]);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("auto-merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_GitHub_refuses_auto_merge_on_a_pull_request_that_became_mergeable_it_is_merged_by_the_same_rules()
    {
        var host = new FakeIntegrationHost { AutoMergeRefused = new IntegrationException("Pull request is in clean status") };
        host.Script = h => h.Calls.Contains("ready") && h.Calls.Count(c => c == "get") >= 3 ? h.Current with { MergeStatus = "CLEAN" } : h.Current;

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.Merged, result.Action);
        Assert.Equal("merge aaaaaaa1", host.Calls[^1]);
    }

    [Fact]
    public async Task A_refusal_of_auto_merge_on_a_blocked_pull_request_is_a_failed_result_not_a_merge()
    {
        var host = new FakeIntegrationHost { AutoMergeRefused = new IntegrationException("Auto merge is not allowed for this repository") };

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.Failed, result.Action);
        Assert.Contains("not allowed", result.Detail);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("merge ", StringComparison.Ordinal));
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
    public async Task Auto_merge_that_is_already_on_is_not_enabled_again()
    {
        var host = new FakeIntegrationHost { Current = FakeIntegrationHost.Snapshot(draft: false, status: "BLOCKED", autoMerge: true) };

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.AutoMergeEnabled, result.Action);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("auto-merge", StringComparison.Ordinal));
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
        Assert.Contains("auto-merge", Make(host).Describe(Repo, partial: false));
        Assert.Contains("up to 3 attempts", Make(host).Describe(Repo, partial: false));
        Assert.Contains("asks first", Make(host, "ask").Describe(Repo, partial: false));
        Assert.Null(Make(host).Describe(Repo, partial: true));
    }
}
