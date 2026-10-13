using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>A driven task's pull request has a short title, its own or the goal's first sentence; the body keeps the whole goal.</summary>
public class PullRequestTitleTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string Long = "Pair the app with a server by QR code (TRK-71). On the first-run server step, add a scan button that reads the pairing code and fills in the address, with a manual fallback.";

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest Request(params DrivenTask[] tasks) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([.. tasks], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    [Theory]
    [InlineData("Add a retry", "Add a retry")]
    [InlineData("Add a retry.", "Add a retry")]
    [InlineData("  Add a retry!  ", "Add a retry")]
    [InlineData("Add a retry\n\nIt should back off.", "Add a retry")]
    [InlineData("Add a retry\r\nIt should back off.", "Add a retry")]
    [InlineData("Does it retry? Make it so.", "Does it retry")]
    [InlineData("Bump to v1.2 and tag it. Then publish.", "Bump to v1.2 and tag it")]
    [InlineData("Fix the parser...", "Fix the parser")]
    [InlineData("Handle e.g.foo.bar in the router", "Handle e.g.foo.bar in the router")]
    [InlineData(Long, "Pair the app with a server by QR code (TRK-71)")]
    public void The_title_is_the_goals_first_sentence_without_its_closing_punctuation(string goal, string expected) =>
        Assert.Equal(expected, PullRequestTitle.FromGoal(goal));

    [Fact]
    public void A_sentence_longer_than_72_characters_is_cut_at_a_word_and_ends_in_no_punctuation()
    {
        var goal = "Replace the hand written pagination in the invoices list with the shared pager component, and move its tests";
        var title = PullRequestTitle.FromGoal(goal);

        Assert.Equal("Replace the hand written pagination in the invoices list with the shared", title);
        Assert.True(title.Length <= PullRequestTitle.MaxLength);
        Assert.StartsWith(title + " ", goal);
        // A comma or a dash the cut leaves at the end goes too, and a word the cut splits is dropped whole.
        const string Head = "Replace the hand written pagination in the invoices list with the app";
        Assert.Equal(Head, PullRequestTitle.FromGoal(Head + ", and more things"));
        Assert.Equal(Head, PullRequestTitle.FromGoal(Head + " - and more things"));
        Assert.Equal(Head, PullRequestTitle.FromGoal(Head + " (the shared pager)"));
    }

    [Fact]
    public void A_sentence_of_exactly_72_characters_is_kept_and_one_character_more_loses_its_last_word()
    {
        var exact = new string('a', 36) + " " + new string('b', 35);
        Assert.Equal(72, exact.Length);
        Assert.Equal(exact, PullRequestTitle.FromGoal(exact));
        Assert.Equal(new string('a', 36), PullRequestTitle.FromGoal(exact + "b"));
        Assert.Equal(exact, PullRequestTitle.FromGoal(exact + " c"));
    }

    [Fact]
    public void One_word_longer_than_72_characters_is_cut_where_it_is_and_a_blank_goal_still_has_a_title()
    {
        Assert.Equal(new string('x', 72), PullRequestTitle.FromGoal(new string('x', 100)));
        Assert.Equal("Chargehand task", PullRequestTitle.FromGoal("  \n "));
        Assert.Equal("Chargehand task", PullRequestTitle.FromGoal("... and so on"));
    }

    [Fact]
    public void A_title_the_request_gives_is_used_as_it_is_and_a_blank_one_falls_back_to_the_goal()
    {
        Assert.Equal("Pair the app by QR", PullRequestTitle.Of("  Pair the app by QR ", Long));
        Assert.Equal("Pair the app with a server by QR code (TRK-71)", PullRequestTitle.Of(null, Long));
        Assert.Equal("Pair the app with a server by QR code (TRK-71)", PullRequestTitle.Of(" ", Long));
    }

    [Fact]
    public void A_title_is_one_line_of_1_to_200_characters()
    {
        RequestDriven Driven(string title) => new([new DrivenTask("t1", Goal: "x", Title: title)]);

        Assert.Empty(DrivenRules.Problems(Driven("Pair the app by QR")));
        Assert.Empty(DrivenRules.Problems(Driven(new string('a', 200))));
        foreach (var bad in new[] { "", "  ", "two\nlines", "two\rlines", new string('a', 201) })
            Assert.Equal(["task 't1': title must be one line of 1 to 200 characters"], DrivenRules.Problems(Driven(bad)));
    }

    [Fact]
    public void A_request_with_a_title_reads_it_and_one_without_leaves_it_null()
    {
        var parsed = System.Text.Json.JsonSerializer.Deserialize<RunRequest>("""
            {"contract_version":"request/v1","text":"t","context":{"interactive":false,"preset":"driven"},
             "driven":{"tasks":[{"id":"a","goal":"x","title":"Short one"},{"id":"b","goal":"y"}]}}
            """, ContractJson.Options)!;

        Assert.Equal(["Short one", null], parsed.Driven!.Tasks.Select(t => t.Title));
    }

    [Fact]
    public async Task A_tasks_own_title_is_the_pull_request_title_and_the_body_keeps_the_whole_goal()
    {
        using var rig = new DrivenRunTests.Rig();
        await rig.Run.RunAsync(Request(new DrivenTask("t1", Goal: Long, Title: "Pair the app by QR")), "run-batch-title", Token, default);

        var input = Assert.Single(rig.Handover.Inputs);
        Assert.Equal("Pair the app by QR", input.Title);
        Assert.Contains($"Task: t1\n\nGoal:\n\n{Long}", input.Body);
    }

    [Fact]
    public async Task Without_a_title_the_pull_request_gets_the_goals_first_sentence_and_the_body_the_whole_goal()
    {
        using var rig = new DrivenRunTests.Rig();
        await rig.Run.RunAsync(Request(new DrivenTask("t1", Goal: Long), new DrivenTask("t2", Goal: "Add a retry\n\nWith a backoff.")), "run-batch-derived", Token, default);

        var (first, second) = (rig.Handover.Inputs.Single(i => i.Body.Contains("Task: t1", StringComparison.Ordinal)), rig.Handover.Inputs.Single(i => i.Body.Contains("Task: t2", StringComparison.Ordinal)));
        Assert.Equal(("Pair the app with a server by QR code (TRK-71)", "Add a retry"), (first.Title, second.Title));
        Assert.Contains($"Goal:\n\n{Long}", first.Body);
        Assert.Contains("Goal:\n\nAdd a retry\n\nWith a backoff.", second.Body);
    }

    [Fact]
    public async Task A_title_given_in_the_request_survives_a_restart_of_the_server()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        rig.Engine.HoldNth = 1;
        var request = Request(new DrivenTask("t1", Goal: Long, Title: "Pair the app by QR"));
        using var stop = new CancellationTokenSource();
        var first = rig.Run.RunAsync(request, "run-batch-title-r", Token, stop.Token, null, () => false);
        for (var i = 0; i < 400 && rig.Engine.Started.Count == 0; i++)
            await Task.Delay(25);
        await stop.CancelAsync();
        await first;
        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));
        Assert.Equal("Pair the app by QR", Assert.Single(state.Tasks).Title);

        rig.Engine.Release();
        await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal("Pair the app by QR", Assert.Single(rig.Handover.Inputs).Title);
    }
}
