using System.Text.Json;
using Chargehand.Modules.Events.Api;

namespace Chargehand.Modules.Events.Tests;

public sealed class ThreadCardTests
{
    private static async Task<JsonElement> CardAsync(Log log, string thread = "T") =>
        Log.Parse(await log.Service.CardAsync(EventGrant.Owner, thread, CancellationToken.None));

    private static async Task ThreeStepsAsync(Log log)
    {
        await log.PostAsync(Log.Step("s1", "T", "first", "second", goal: "goal one", refs: ["a", "b"], time: "2026-11-08T10:00:00Z", actor: "cc/one"));
        await log.PostAsync(Log.Step("s2", "T", "second", "third", goal: "goal two", refs: ["c"], time: "2026-11-08T11:00:00Z", actor: "cc/two"));
        await log.PostAsync(Log.Step("s3", "T", "third", "fourth", refs: ["a", "d"], time: "2026-11-08T12:00:00Z", actor: "cc/one"));
    }

    [Fact]
    public async Task After_three_steps_by_two_actors_the_card_shows_the_third_and_the_latest_goal()
    {
        using var log = new Log();
        await ThreeStepsAsync(log);

        var card = await CardAsync(log);

        Assert.Equal("T", card.GetProperty("thread").GetString());
        Assert.Equal(3, card.GetProperty("steps").GetInt32());
        Assert.Equal("open", card.GetProperty("state").GetString());
        Assert.Equal("goal two", card.GetProperty("goal").GetString());
        Assert.Equal("fourth", card.GetProperty("next").GetString());
        Assert.Equal("third", card.GetProperty("last_step").GetProperty("done").GetString());
        Assert.Equal(3, card.GetProperty("last_step").GetProperty("seq").GetInt64());
        Assert.Equal("cc/one", card.GetProperty("owner").GetProperty("id").GetString());
        Assert.Equal("cc/one", card.GetProperty("last_step").GetProperty("actor").GetProperty("id").GetString());
        Assert.Equal(["a", "d", "c", "b"], card.GetProperty("refs").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("2026-11-08T12:00:00.000Z", card.GetProperty("last_activity").GetString());
    }

    [Fact]
    public async Task A_later_tool_call_correlated_with_the_thread_moves_last_activity_and_nothing_else()
    {
        using var log = new Log();
        await ThreeStepsAsync(log);
        var before = await CardAsync(log);

        await log.PostAsync(Log.Envelope("tool1", "chargehand.tool.called", correlation: "T", time: "2026-11-08T13:30:00Z"));
        var after = await CardAsync(log);

        Assert.Equal("2026-11-08T13:30:00.000Z", after.GetProperty("last_activity").GetString());
        foreach (var field in new[] { "goal", "next", "steps", "owner", "last_step", "refs", "state", "scope" })
            Assert.Equal(before.GetProperty(field).GetRawText(), after.GetProperty(field).GetRawText());
    }

    [Fact]
    public async Task An_earlier_correlated_event_does_not_move_last_activity_back()
    {
        using var log = new Log();
        await ThreeStepsAsync(log);

        await log.PostAsync(Log.Envelope("tool0", "chargehand.tool.called", subject: "T", time: "2026-11-08T09:00:00Z"));

        Assert.Equal("2026-11-08T12:00:00.000Z", (await CardAsync(log)).GetProperty("last_activity").GetString());
    }

    [Fact]
    public async Task The_text_is_at_most_2000_characters_with_the_id_the_goal_and_the_next_step()
    {
        using var log = new Log();
        await ThreeStepsAsync(log);

        var text = (await CardAsync(log)).GetProperty("text").GetString()!;

        Assert.True(text.Length <= ThreadStep.MaxText, $"{text.Length} characters");
        Assert.Contains("T", text, StringComparison.Ordinal);
        Assert.Contains("goal two", text, StringComparison.Ordinal);
        Assert.Contains("fourth", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_text_stays_under_2000_characters_with_the_longest_fields()
    {
        using var log = new Log();
        var refs = Enumerable.Range(1, 20).Select(i => $"{i}" + new string('r', 290)).ToArray();
        await log.PostAsync(Log.Step("s1", "T", new string('d', 2000), new string('n', 2000), goal: new string('g', 2000), refs: refs));

        var text = (await CardAsync(log)).GetProperty("text").GetString()!;

        Assert.True(text.Length <= ThreadStep.MaxText, $"{text.Length} characters");
        Assert.StartsWith("Thread T", text, StringComparison.Ordinal);
        Assert.Contains("nnnn", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_thread_gets_404()
    {
        using var log = new Log();

        Assert.Equal(404, (await log.Service.CardAsync(EventGrant.Owner, "nope", CancellationToken.None)).Status);
    }
}
