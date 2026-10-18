using System.Diagnostics;
using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0051: with <c>review_pulls</c> on, every open non-draft pull request gets one read-only review per head commit and one comment with its findings. GitHub, the run starter, the run log and the
/// clock are fakes; the git fetch runs against local repositories.</summary>
public class PullReviewTests
{
    private const string Sha = "1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Sha2 = "2222222bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static AutomationGitHub On(int perDay = 20, int concurrent = 2) =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: concurrent, ReviewPulls: true, MaxReviewsPerDay: perDay);

    private static bool Valid(RunRequest request) => ContractSchemas.Validate(ContractSchemas.Request, JsonSerializer.SerializeToElement(request, ContractJson.Options)).Count == 0;

    private static Profile Load(string github) => Profile.Load(WriteProfile(github));

    private static string WriteProfile(string github)
    {
        var dir = new TempDir();
        return dir.Write("local.json", "{\"schema\":\"profile/v1\",\"driven\":{\"enabled\":true,\"integrator\":{\"app_id\":1,\"private_key_secret\":\"k\"}},\"automation\":{\"github\":" + github + "}}");
    }

    [Fact]
    public void The_review_keys_default_to_off_and_twenty_a_day()
    {
        var github = Load("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":[]}""").Automation!.Github!;
        Assert.Equal((false, 20), (github.ReviewPulls, github.MaxReviewsPerDay));
        var set = Load("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":[],"review_pulls":true,"max_reviews_per_day":5}""").Automation!.Github!;
        Assert.Equal((true, 5), (set.ReviewPulls, set.MaxReviewsPerDay));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void A_daily_review_cap_outside_1_to_1000_is_refused(int cap)
    {
        var e = Assert.Throws<ChargehandException>(() => Load("{\"repositories\":{\"octo/repo\":{\"path\":\"/p\"}},\"allowed_authors\":[],\"max_reviews_per_day\":" + cap + "}"));
        Assert.Contains("max_reviews_per_day must be 1 to 1000", e.Message);
    }

    [Fact]
    public async Task Nothing_is_reviewed_unless_review_pulls_is_on()
    {
        using var rig = new IntakeRig();
        rig.OpenPull(5, Sha);
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        Assert.Empty(rig.Heads.Fetched);
    }

    [Fact]
    public async Task An_open_pull_request_starts_a_read_only_review_of_its_head_with_the_diff_and_the_goal()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha);
        rig.GitHub.Diffs[5] = "diff --git a/Retry.cs b/Retry.cs\n+retry();";
        await rig.Poll();

        var request = Assert.Single(rig.Starter.Started);
        Assert.True(Valid(request));
        Assert.Equal(("review", false, new RepositoryRef("/srv/repos/repo", Sha)), (request.Context.Preset, request.Context.Interactive, request.Context.Repository));
        Assert.Null(request.Driven);
        Assert.Equal([5], rig.Heads.Fetched);
        var inputs = request.Inputs!.ToDictionary(i => i.Id);
        Assert.Equal("diff --git a/Retry.cs b/Retry.cs\n+retry();", inputs["diff"].Text);
        Assert.Contains("Retry the call", inputs["goal"].Text);
        Assert.Contains("Gives up less often.", inputs["goal"].Text);
        Assert.Equal("not run by chargehand; see the pull request's checks", inputs["tests"].Text);
        Assert.Empty(rig.GitHub.Posted);
    }

    [Fact]
    public async Task A_diff_over_60000_characters_is_cut_with_a_marker_line()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha);
        rig.GitHub.Diffs[5] = new string('x', 70_000);
        await rig.Poll();

        var diff = rig.Starter.Started.Single().Inputs!.Single(i => i.Id == "diff").Text;
        Assert.Equal(new string('x', 60_000) + "\n[diff truncated at 60000 characters]", diff);
    }

    [Fact]
    public async Task Drafts_and_bot_authored_pull_requests_are_skipped()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha, draft: true);
        rig.OpenPull(6, Sha, author: "dependabot[bot]");
        rig.OpenPull(7, Sha, author: "carol");
        await rig.Poll();

        Assert.Equal(["Review pull request octo/repo#7."], rig.Starter.Started.Select(r => r.Text));
        Assert.Equal([7], rig.Heads.Fetched);
    }

    [Fact]
    public async Task A_head_commit_is_reviewed_once_a_new_push_is_reviewed_again_and_a_restart_changes_nothing()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha);
        await rig.Poll();
        rig.Log.Finish("run-1", ResultStatus.Completed, "Looks fine.");
        await rig.Poll();
        await rig.Poll();
        rig.Restart();
        await rig.Poll();
        Assert.Single(rig.Starter.Started);

        rig.OpenPull(5, Sha2);
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Equal(Sha2, rig.Starter.Started[1].Context.Repository!.Commit);
    }

    [Fact]
    public async Task A_head_that_moved_between_the_listing_and_the_fetch_waits_for_the_next_poll()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha);
        rig.Heads.PullHeads[5] = Sha2;
        await rig.Poll();
        Assert.Empty(rig.Starter.Started);

        rig.Heads.PullHeads.Clear();
        await rig.Poll();
        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public async Task At_most_max_reviews_per_day_start_in_any_24_hours_and_the_rest_wait_without_being_dropped()
    {
        using var rig = new IntakeRig(On(perDay: 2, concurrent: 10));
        rig.OpenPull(1, Sha);
        rig.OpenPull(2, Sha);
        rig.OpenPull(3, Sha);
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);

        rig.Time.Advance(TimeSpan.FromHours(25));
        await rig.Poll();
        Assert.Equal(3, rig.Starter.Started.Count);
        Assert.Equal("Review pull request octo/repo#3.", rig.Starter.Started[2].Text);
    }

    [Fact]
    public async Task Reviews_wait_for_a_free_slot_while_max_concurrent_batches_and_reviews_run()
    {
        using var rig = new IntakeRig(On(concurrent: 1));
        rig.OpenPull(1, Sha);
        rig.OpenPull(2, Sha);
        await rig.Poll();
        Assert.Single(rig.Starter.Started);

        rig.Log.Finish("run-1", ResultStatus.Completed, "Fine.");
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Equal(["Review pull request octo/repo#1.", "Review pull request octo/repo#2."], rig.Starter.Started.Select(r => r.Text));
    }

    [Fact]
    public async Task A_refused_start_is_tried_again_at_the_next_poll()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha);
        rig.Starter.Refuse = "halted";
        await rig.Poll();
        rig.Starter.Refuse = null;
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Contains(rig.Warnings, w => w.Contains("halted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_diff_GitHub_will_not_render_is_skipped_for_good()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha);
        rig.GitHub.DiffFailsWith = 406;
        await rig.Poll();
        rig.GitHub.DiffFailsWith = null;
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
    }

    [Fact]
    public async Task A_finished_review_is_one_comment_with_the_marker_the_summary_and_each_finding_with_its_evidence()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha);
        await rig.Poll();
        rig.Log.Finish("run-1", ResultStatus.Completed, "One bug, @carol.", [new Claim("The retry never stops <loop>.\nIt spins.", ["e1", "e2"], 0.9), new Claim("No test covers it.", ["e2"], 0.6)],
            [new Evidence("e1", EvidenceKind.File, "src/Retry.cs:12-20"), new Evidence("e2", EvidenceKind.Diff, "src/Retry.cs:30")]);
        await rig.Poll();

        var (number, body) = Assert.Single(rig.GitHub.Posted);
        Assert.Equal(5, number);
        Assert.StartsWith($"<!-- chargehand-review {Sha} -->\n{GitHubIntake.Marker}\n", body);
        Assert.Contains("One bug, @​carol.", body);
        Assert.Contains("- The retry never stops &lt;loop>. It spins.\n  Confidence 0.9. Evidence: `src/Retry.cs:12-20`, `src/Retry.cs:30`", body);
        Assert.Contains("- No test covers it.\n  Confidence 0.6. Evidence: `src/Retry.cs:30`", body);
        Assert.DoesNotContain("No findings.", body);
    }

    [Fact]
    public async Task A_review_with_no_claims_says_no_findings()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha);
        await rig.Poll();
        rig.Log.Finish("run-1", ResultStatus.Completed, "Nothing wrong.");
        await rig.Poll();

        var body = Assert.Single(rig.GitHub.Posted).Body;
        Assert.Contains("Nothing wrong.\n\nNo findings.", body);
    }

    [Fact]
    public async Task A_failed_or_stopped_review_says_so_with_the_reason()
    {
        using var rig = new IntakeRig(On(concurrent: 5));
        rig.OpenPull(5, Sha);
        rig.OpenPull(6, Sha);
        await rig.Poll();
        rig.Log.Finish("run-1", ResultStatus.Failed, "s", error: new ResultError(ErrorCode.CostCapReached, "the cap was reached", false));
        rig.Log.Finish("run-2", ResultStatus.NeedsInput, "The diff does not say what it is for.");
        await rig.Poll();

        Assert.Contains("The review failed (`CostCapReached`): the cap was reached", rig.GitHub.Posted.Single(p => p.Issue == 5).Body);
        Assert.Contains("The review stopped with status `needsinput`: The diff does not say what it is for.", rig.GitHub.Posted.Single(p => p.Issue == 6).Body);
    }

    [Fact]
    public async Task A_restart_never_posts_a_second_comment_for_the_same_commit()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha);
        await rig.Poll();
        rig.Log.Finish("run-1", ResultStatus.Completed, "Fine.");
        // The comment is up but the state file never learned it: the marker on the pull request is what stops a second one.
        rig.Comment(5, "chargehand[bot]", $"<!-- chargehand-review {Sha} -->\nearlier");
        await rig.Poll();
        rig.Restart();
        await rig.Poll();

        Assert.Empty(rig.GitHub.Posted);
        Assert.Empty(rig.State.Reviews);
    }

    [Fact]
    public async Task A_commit_whose_comment_is_already_up_is_not_reviewed_after_the_state_is_lost()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha);
        rig.Comment(5, "chargehand[bot]", $"<!-- chargehand-review {Sha} -->\nearlier");
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        Assert.Empty(rig.Heads.Fetched);
    }

    [Fact]
    public async Task A_review_finishes_even_when_review_pulls_is_switched_off()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha);
        await rig.Poll();
        rig.Log.Finish("run-1", ResultStatus.Completed, "Fine.");

        await rig.Poll();
        Assert.Single(rig.GitHub.Posted);
    }

    [Fact]
    public async Task A_comment_that_fails_is_tried_again_at_the_next_poll()
    {
        using var rig = new IntakeRig(On());
        rig.OpenPull(5, Sha);
        await rig.Poll();
        rig.Log.Finish("run-1", ResultStatus.Completed, "Fine.");
        rig.GitHub.CommentsFail = true;
        await rig.Poll();
        rig.GitHub.CommentsFail = false;
        await rig.Poll();

        Assert.Single(rig.GitHub.Posted);
    }

    [Fact]
    public async Task The_review_comment_is_never_read_as_a_trigger()
    {
        using var rig = new IntakeRig(On());
        rig.Plain(5);
        rig.OpenPull(5, Sha);
        await rig.Poll();
        rig.Log.Finish("run-1", ResultStatus.Completed, "Ask @chargehand to fix it.");
        await rig.Poll();
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public async Task Fetching_a_pull_request_head_brings_in_its_commit_even_from_a_fork()
    {
        using var origin = new TempDir();
        using var clone = new TempDir();
        Git(origin.Path, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(origin.Path, "a.txt"), "one");
        Git(origin.Path, "add", ".");
        Git(origin.Path, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "one");
        Git(clone.Path, "clone", "-q", origin.Path, ".");
        File.WriteAllText(Path.Combine(origin.Path, "a.txt"), "two");
        Git(origin.Path, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-am", "two");
        var head = Git(origin.Path, "rev-parse", "HEAD");
        Git(origin.Path, "update-ref", "refs/pull/5/head", head);
        Git(origin.Path, "reset", "-q", "--hard", "HEAD~1");

        var fetched = await new GitRepositoryHeads().FetchPullHeadAsync(clone.Path, 5, default);

        Assert.Equal(head, fetched);
        Assert.Equal(head, Git(clone.Path, "rev-parse", "refs/chargehand/pull/5"));
        var missing = await Assert.ThrowsAsync<IntakeException>(() => new GitRepositoryHeads().FetchPullHeadAsync(clone.Path, 6, default));
        Assert.Contains("pull request 6", missing.Message);
    }

    private static string Git(string directory, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
        return output.Trim();
    }
}
