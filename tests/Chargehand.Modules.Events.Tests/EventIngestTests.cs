using Chargehand.Modules.Events.Api;

namespace Chargehand.Modules.Events.Tests;

public sealed class EventIngestTests
{
    [Fact]
    public async Task A_batch_of_500_returns_500_seqs_and_the_same_batch_again_adds_nothing()
    {
        using var log = new Log();
        var batch = "[" + string.Join(",", Enumerable.Range(1, 500).Select(i => Log.Envelope($"e{i}"))) + "]";

        var first = Log.Parse(await log.PostAsync(batch)).GetProperty("seq").EnumerateArray().Select(s => s.GetInt64()).ToList();
        var again = Log.Parse(await log.PostAsync(batch)).GetProperty("seq").EnumerateArray().Select(s => s.GetInt64()).ToList();

        Assert.Equal(500, first.Count);
        Assert.Equal(500, first.Distinct().Count());
        Assert.Equal(first, again);
        Assert.Equal(500, await log.CountAsync());
    }

    [Fact]
    public async Task A_batch_with_one_bad_envelope_stores_none_of_it()
    {
        using var log = new Log();
        var batch = $"[{Log.Envelope("e1")},{Log.Envelope("e2", type: "bad")}]";

        var answer = await log.PostAsync(batch);

        Assert.Equal(400, answer.Status);
        Assert.Equal(1, Log.Parse(answer).GetProperty("error").GetProperty("index").GetInt32());
        Assert.Equal(0, await log.CountAsync());
    }

    [Fact]
    public async Task More_than_500_in_one_post_gets_400()
    {
        using var log = new Log();
        var batch = "[" + string.Join(",", Enumerable.Range(1, 501).Select(i => Log.Envelope($"e{i}"))) + "]";

        Assert.Equal(400, (await log.PostAsync(batch)).Status);
    }

    [Fact]
    public async Task A_token_for_video_studio_writing_source_claude_gets_403()
    {
        using var log = new Log();

        Assert.Equal(403, (await log.PostAsync(Log.Envelope("e1", source: "claude"), Log.VideoStudio)).Status);
        Assert.Equal(200, (await log.PostAsync(Log.Envelope("e2", source: "video-studio/render"), Log.VideoStudio)).Status);
        Assert.Equal(403, (await log.PostAsync(Log.Envelope("e3", source: "video-studiox"), Log.VideoStudio)).Status);
    }

    [Fact]
    public async Task The_same_id_from_two_sources_is_two_events()
    {
        using var log = new Log();

        await log.PostAsync(Log.Envelope("same", source: "claude"));
        await log.PostAsync(Log.Envelope("same", source: "video-studio"));

        Assert.Equal(2, await log.CountAsync());
    }

    [Fact]
    public async Task The_log_survives_a_reopen_and_keeps_its_seq()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chargehand-events-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "events.db");
        try
        {
            using (var service = new EventService(SqliteEventLog.Open(path)))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(Log.Envelope("e1"));
                await service.AppendAsync(EventGrant.Owner, doc.RootElement, CancellationToken.None);
            }
            using var reopened = SqliteEventLog.Open(path);
            Assert.Equal(1, reopened.Signal.Latest);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
