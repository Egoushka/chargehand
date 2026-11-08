using Chargehand.Modules.Events.Api;

namespace Chargehand.Modules.Events.Tests;

public sealed class ThreadStepTests
{
    [Fact]
    public async Task A_step_without_subject_gets_400_and_nothing_is_stored()
    {
        using var log = new Log();
        var json = Log.Envelope("s1", ThreadStep.Type, data: "{\"done\":\"x\",\"next\":\"y\",\"goal\":\"g\"}");

        Assert.Equal(400, (await log.PostAsync(json)).Status);
        Assert.Equal(0, await log.CountAsync());
    }

    [Fact]
    public async Task A_step_without_done_gets_400()
    {
        using var log = new Log();
        var json = Log.Envelope("s1", ThreadStep.Type, subject: "t", data: "{\"next\":\"y\",\"goal\":\"g\"}");

        Assert.Equal(400, (await log.PostAsync(json)).Status);
        Assert.Equal(0, await log.CountAsync());
    }

    [Fact]
    public async Task An_open_step_without_next_gets_400()
    {
        using var log = new Log();

        Assert.Equal(400, (await log.PostAsync(Log.Step("s1", "t", "did", goal: "g"))).Status);
        Assert.Equal(0, await log.CountAsync());
    }

    [Fact]
    public async Task A_first_step_without_goal_gets_400()
    {
        using var log = new Log();

        var answer = await log.PostAsync(Log.Step("s1", "t", "did", "next"));

        Assert.Equal(400, answer.Status);
        Assert.Contains("goal", answer.Json, StringComparison.Ordinal);
        Assert.Equal(0, await log.CountAsync());
    }

    [Fact]
    public async Task A_first_step_with_goal_earlier_in_the_same_batch_counts()
    {
        using var log = new Log();

        var answer = await log.PostAsync($"[{Log.Step("s1", "t", "one", "two", goal: "g")},{Log.Step("s2", "t", "two", "three")}]");

        Assert.Equal(200, answer.Status);
        Assert.Equal(2, await log.CountAsync());
    }

    [Fact]
    public async Task A_later_step_without_goal_is_stored_and_the_card_keeps_the_earlier_goal()
    {
        using var log = new Log();
        await log.PostAsync(Log.Step("s1", "t", "one", "two", goal: "the goal"));

        Assert.Equal(200, (await log.PostAsync(Log.Step("s2", "t", "two", "three"))).Status);
        var card = Log.Parse(await log.Service.CardAsync(EventGrant.Owner, "t", CancellationToken.None));

        Assert.Equal("the goal", card.GetProperty("goal").GetString());
        Assert.Equal(2, card.GetProperty("steps").GetInt32());
    }

    [Fact]
    public async Task A_closed_step_without_next_is_stored()
    {
        using var log = new Log();
        await log.PostAsync(Log.Step("s1", "t", "one", "two", goal: "g"));

        Assert.Equal(200, (await log.PostAsync(Log.Step("s2", "t", "finished", state: "closed"))).Status);
    }

    [Theory]
    [InlineData("{\"done\":\"x\",\"next\":\"y\",\"goal\":\"g\",\"state\":\"paused\"}")]
    [InlineData("{\"done\":\"\",\"next\":\"y\",\"goal\":\"g\"}")]
    [InlineData("{\"done\":\"x\",\"next\":\"y\",\"goal\":\"g\",\"refs\":\"one\"}")]
    [InlineData("\"not an object\"")]
    public async Task A_step_with_bad_data_gets_400(string data)
    {
        using var log = new Log();

        Assert.Equal(400, (await log.PostAsync(Log.Envelope("s1", ThreadStep.Type, subject: "t", data: data))).Status);
    }

    [Fact]
    public async Task A_step_with_too_many_refs_or_a_bad_thread_id_gets_400()
    {
        using var log = new Log();
        var refs = Enumerable.Range(1, 21).Select(i => $"r{i}").ToArray();

        Assert.Equal(400, (await log.PostAsync(Log.Step("s1", "t", "x", "y", goal: "g", refs: refs))).Status);
        Assert.Equal(400, (await log.PostAsync(Log.Step("s2", "-bad id", "x", "y", goal: "g"))).Status);
        Assert.Equal(400, (await log.PostAsync(Log.Step("s3", "t", new string('x', 2001), "y", goal: "g"))).Status);
    }
}
