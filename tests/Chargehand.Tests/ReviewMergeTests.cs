using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>A merge happens on the intake poll, pinned to the head chargehand's own clean review (a comment of the App) covers, once the required checks are green; nothing else merges.</summary>
public class ReviewMergeTests
{
    private const string Branch = "chargehand/task-run";
    private const string Head = "1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Moved = "2222222bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly CheckStatus[] Green = [new("ci", true, CheckOutcome.Passed, 1)];

    private static readonly DrivenIntegrator Driven = new(1, 2, "k", new Dictionary<string, string> { [IntakeRig.Repo] = "merge" }, null);

    private static AutomationGitHub Settings() =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], ReviewPulls: true, AppLogin: "chargehand[bot]");

    private static (IntakeRig Rig, FakeIntegrationHost Host) Make(IReadOnlyList<CheckStatus>? checks = null, string head = Head)
    {
        var host = new FakeIntegrationHost { Current = new PrSnapshot("PR_node", 9, head, "main", false, false, false, "CLEAN", false, checks ?? Green, []) };
        var policy = new IntegratorPolicy(Driven);
        var approver = new Integrator(policy, host, delay: (_, _) => Task.CompletedTask);
        return (new IntakeRig(Settings(), policy, approver: approver), host);
    }

    private static async Task Reviewed(IntakeRig rig, double? confidence, string sha = Head)
    {
        await rig.Poll();
        rig.OpenPull(9, sha, author: "chargehand[bot]", branch: Branch);
        await rig.Poll();
        var run = $"run-{rig.Starter.Started.Count}";
        IReadOnlyList<Claim> claims = confidence is { } c ? [new Claim("The loop never ends.", ["e1"], c)] : [];
        rig.Log.Finish(run, ResultStatus.Completed, "Reviewed.", claims, confidence is null ? [] : [new Evidence("e1", EvidenceKind.File, "src/Retry.cs:12")]);
        await rig.Poll();
    }

    private static bool Merged(FakeIntegrationHost host, string sha) => host.Calls.Contains($"merge {sha}");

    [Fact]
    public async Task A_clean_review_of_the_head_is_merged_on_the_poll_pinned_to_it()
    {
        var (rig, host) = Make();
        using (rig)
        {
            await Reviewed(rig, null);
            await rig.Poll();

            Assert.True(Merged(host, Head));
            Assert.DoesNotContain(host.Calls, c => c.StartsWith("auto-merge", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_blocking_finding_merges_nothing()
    {
        var (rig, host) = Make();
        using (rig)
        {
            await Reviewed(rig, 0.9);
            await rig.Poll();

            Assert.DoesNotContain(host.Calls, c => c.StartsWith("merge", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_push_after_the_review_is_merged_only_once_its_own_review_is_clean()
    {
        var (rig, host) = Make(checks: [new("ci", true, CheckOutcome.Pending, 1)]);
        using (rig)
        {
            await Reviewed(rig, null);
            host.Current = host.Current with { Checks = Green };
            rig.OpenPull(9, Moved, author: "chargehand[bot]", branch: Branch);
            host.Current = host.Current with { HeadSha = Moved };
            await rig.Poll();

            Assert.DoesNotContain(host.Calls, c => c.StartsWith("merge", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_clean_marker_written_by_someone_else_is_not_trusted()
    {
        var (rig, host) = Make();
        using (rig)
        {
            await rig.Poll();
            rig.OpenPull(9, Head, author: "chargehand[bot]", branch: Branch);
            rig.GitHub.Comments.Add(new IntakeComment(900, 9, "mallory", PullReviews.CleanMarkerFor(Head), rig.Now));
            await rig.Poll();
            await rig.Poll();

            Assert.DoesNotContain(host.Calls, c => c.StartsWith("merge", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Checks_still_running_wait_and_the_next_poll_merges_when_they_are_green()
    {
        var (rig, host) = Make(checks: [new("ci", true, CheckOutcome.Pending, 1)]);
        using (rig)
        {
            await Reviewed(rig, null);
            await rig.Poll();
            Assert.False(Merged(host, Head));

            host.Current = host.Current with { Checks = Green };
            await rig.Poll();
            Assert.True(Merged(host, Head));
        }
    }

    [Fact]
    public async Task A_migration_found_at_approval_is_said_once_and_labelled_once()
    {
        var (rig, host) = Make();
        using (rig)
        {
            host.ChangedPaths = ["src/Db/Migrations/1_add.cs"];
            await Reviewed(rig, null);
            await rig.Poll();
            await rig.Poll();

            Assert.Equal([Integrator.NeedsYouLabel], host.Labels);
            Assert.Single(host.Comments);
            Assert.False(Merged(host, Head));
        }
    }

    [Fact]
    public async Task An_auto_merge_armed_earlier_is_turned_off_on_the_poll()
    {
        var (rig, host) = Make();
        using (rig)
        {
            host.Current = host.Current with { AutoMerge = true };
            await rig.Poll();
            rig.OpenPull(9, Head, author: "chargehand[bot]", branch: Branch);
            await rig.Poll();

            Assert.Contains("disable-auto-merge", host.Calls);
            Assert.False(Merged(host, Head));
        }
    }

    [Fact]
    public void The_merge_chain_names_every_gap()
    {
        var plain = new AutomationGitHub(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, ["alice"]);
        Assert.Equal(2, GitHubIntake.MergeChainGaps(plain, Driven, null).Count);
        Assert.Single(GitHubIntake.MergeChainGaps(null, Driven, null));
        Assert.Empty(GitHubIntake.MergeChainGaps(Settings(), Driven, null));
        Assert.Empty(GitHubIntake.MergeChainGaps(plain, new DrivenIntegrator(1, 2, "k", new Dictionary<string, string> { [IntakeRig.Repo] = "ask" }, null), null));
    }
}
