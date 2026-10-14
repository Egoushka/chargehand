using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Runners.Tests;

/// <summary>ADR 0060: the runner client writes and reads the runner's bodies without reflection, byte for byte as <see cref="RunnerJson.Options"/> did, which the runner server still uses.</summary>
public sealed class RunnerJsonContextTests
{
    // Characters the default encoder escapes, so a difference in encoders shows.
    private const string Text = "a <b> & \"c\" — ü";

    [Fact]
    public void Every_request_body_is_written_as_the_reflection_options_wrote_it()
    {
        Same(new RunnerStart("r1", Text, "w", "o", "n", new() { ["A"] = Text }, 1024, 1.5, 64, ["session", Text], ["cache-npm-0123456789ab"]), RunnerJsonContext.Default.RunnerStart);
        Same(new RunnerStart("r1", "i", "w", "o", "n", [], 1024, 1, 64, ["session"]), RunnerJsonContext.Default.RunnerStart);
        Same(new RunnerService("b1", "db", "i", "n", 5432, new() { ["P"] = Text }, ["/var/lib"], 512), RunnerJsonContext.Default.RunnerService);
        Same(new RunnerSignal("c1", "TERM"), RunnerJsonContext.Default.RunnerSignal);
        Same(new RunnerVolume("v1", "r1"), RunnerJsonContext.Default.RunnerVolume);
        Same(new RunnerNetwork("n1", "b1"), RunnerJsonContext.Default.RunnerNetwork);
        Same(new RunnerConnect("c1", "n1"), RunnerJsonContext.Default.RunnerConnect);
        Same(new RunnerEgress("b1", "n1", [Text], ["8080=host:80"], new RunnerModelGateway("cred", new string('a', 64), "api.example.test", 8443)), RunnerJsonContext.Default.RunnerEgress);
        Same(new RunnerEgress("b1", "n1", []), RunnerJsonContext.Default.RunnerEgress);
        Same(new RunnerWorkspace("r1", "i", "/src", "w", "main", new string('0', 40)), RunnerJsonContext.Default.RunnerWorkspace);
        Same(new RunnerBundleWorkspace("r1", "i", "w", "main", new string('0', 40)), RunnerJsonContext.Default.RunnerBundleWorkspace);
        Assert.Equal(JsonSerializer.Serialize(new { }, RunnerJson.Options), JsonSerializer.Serialize(new RunnerEmpty(), RunnerJsonContext.Default.RunnerEmpty));
    }

    [Fact]
    public void Every_reply_reads_back_as_the_reflection_options_read_it()
    {
        Read<RunnerInspect>("""{"status":"running","exit_code":null,"oom_killed":false,"memory_peak_mb":"12"}""", RunnerJsonContext.Default.RunnerInspect);
        Read<RunnerResources>("""{"containers":["a"],"networks":[],"volumes":["v"]}""", RunnerJsonContext.Default.RunnerResources);
        Read<HostSnapshot>("""{"available_mb":4000,"total_mb":16000,"swap_total_mb":0,"swap_free_mb":0,"load1":0.2,"cpus":8,"state":"draining"}""", RunnerJsonContext.Default.HostSnapshot);
        Read<IdReply>("""{"ID":"c1"}""", RunnerJsonContext.Default.IdReply);
        Read<LogsReply>("""{"logs":"x"}""", RunnerJsonContext.Default.LogsReply);
        Read<CountReply>("""{"count":3}""", RunnerJsonContext.Default.CountReply);
    }

    [Fact]
    public void A_reply_with_a_field_the_client_does_not_know_is_refused_as_before()
    {
        const string json = """{"id":"c1","extra":1}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IdReply>(json, RunnerJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, RunnerJsonContext.Default.IdReply));
    }

    private static void Same<T>(T value, JsonTypeInfo<T> info) =>
        Assert.Equal(JsonSerializer.Serialize(value, RunnerJson.Options), JsonSerializer.Serialize(value, info));

    private static void Read<T>(string json, JsonTypeInfo<T> info) =>
        Assert.Equal(JsonSerializer.Serialize(JsonSerializer.Deserialize<T>(json, RunnerJson.Options), RunnerJson.Options),
            JsonSerializer.Serialize(JsonSerializer.Deserialize(json, info), RunnerJson.Options));
}
