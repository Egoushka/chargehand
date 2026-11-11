namespace Chargehand.Modules.Events.Tests;

public sealed class EventEnvelopeTests
{
    [Theory]
    [InlineData("id")]
    [InlineData("source")]
    [InlineData("type")]
    [InlineData("time")]
    public async Task An_envelope_missing_a_required_field_gets_400_and_nothing_is_stored(string field)
    {
        using var log = new Log();
        var json = System.Text.RegularExpressions.Regex.Replace(Log.Envelope("e1"), $"\"{field}\":\"[^\"]*\",?", "").Replace(",}", "}", StringComparison.Ordinal);

        var answer = await log.PostAsync(json);

        Assert.Equal(400, answer.Status);
        Assert.Contains(field, answer.Json, StringComparison.Ordinal);
        Assert.Equal(0, await log.CountAsync());
    }

    [Fact]
    public async Task Data_over_16_KB_without_dataref_gets_413_and_with_dataref_is_stored()
    {
        using var log = new Log();
        var big = $"{{\"summary\":\"{new string('x', 17 * 1024)}\"}}";

        Assert.Equal(413, (await log.PostAsync(Log.Envelope("e1", data: big))).Status);
        var withRef = Log.Envelope("e2", data: big).Replace("\"specversion\"", "\"dataref\":\"https://example.org/blob\",\"specversion\"", StringComparison.Ordinal);
        Assert.Equal(200, (await log.PostAsync(withRef)).Status);
        Assert.Equal(1, await log.CountAsync());
    }

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ghp_0123456789abcdefghijklmnopqrstuvwxyzAB")]
    public async Task A_data_value_matching_a_secret_pattern_gets_422_and_nothing_is_stored(string secret)
    {
        using var log = new Log();

        var answer = await log.PostAsync(Log.Envelope("e1", data: $"{{\"summary\":\"export TOKEN={secret}\"}}"));

        Assert.Equal(422, answer.Status);
        Assert.DoesNotContain(secret, answer.Json, StringComparison.Ordinal);
        Assert.Equal(0, await log.CountAsync());
        Assert.Equal(1, log.Service.Refused);
    }

    [Theory]
    [InlineData("work/acme/src/Foo.cs", null)]
    [InlineData("Edit", "{\"path\":\"/home/u/Projects/work/repo/x.cs\"}")]
    [InlineData("Edit", "{\"paths\":[\"a.cs\",\"work/b.cs\"]}")]
    public async Task An_event_with_a_work_path_in_subject_or_data_gets_422(string subject, string? data)
    {
        using var log = new Log();

        var answer = await log.PostAsync(Log.Envelope("e1", subject: subject, data: data));

        Assert.Equal(422, answer.Status);
        Assert.Equal(0, await log.CountAsync());
    }

    [Fact]
    public async Task A_path_that_only_ends_in_work_is_not_refused()
    {
        using var log = new Log();

        Assert.Equal(200, (await log.PostAsync(Log.Envelope("e1", data: "{\"path\":\"src/network/Client.cs\"}"))).Status);
    }

    [Fact]
    public async Task A_two_part_type_and_a_person_scope_are_refused()
    {
        using var log = new Log();

        Assert.Equal(400, (await log.PostAsync(Log.Envelope("e1", type: "thread.step"))).Status);
        Assert.Equal(422, (await log.PostAsync(Log.Envelope("e2", scope: "person:owner"))).Status);
    }

    [Fact]
    public async Task A_seq_sent_by_the_producer_is_replaced_by_the_servers()
    {
        using var log = new Log();
        var json = Log.Envelope("e1").Replace("{", "{\"seq\":999,", StringComparison.Ordinal);

        var seq = Log.Parse(await log.PostAsync(json)).GetProperty("seq")[0].GetInt64();
        var stored = await log.Store.ReadAsync(new Api.EventFilter(), CancellationToken.None);

        Assert.Equal(1, seq);
        Assert.StartsWith("{\"seq\":1,", stored[0].Json, StringComparison.Ordinal);
        Assert.DoesNotContain("999", stored[0].Json, StringComparison.Ordinal);
    }
}
