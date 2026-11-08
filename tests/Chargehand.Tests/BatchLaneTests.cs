using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Chargehand.Automation;
using Chargehand.Kernel;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

/// <summary>A fake Anthropic Message Batches endpoint: records each request, answers a submit with an id, reports processing until <see cref="Ended"/>, then serves JSONL results.</summary>
internal sealed class FakeBatchEndpoint : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string? Key, string? Version, string? Body)> Requests { get; } = [];

    public bool Ended { get; set; }

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Results { get; set; } =
        """{"custom_id":"schedule-digest","result":{"type":"succeeded","message":{"content":[{"type":"text","text":"Three findings."}],"usage":{"input_tokens":1000000,"output_tokens":200000}}}}""" + "\n";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.GetValues("x-api-key").Single(), request.Headers.GetValues("anthropic-version").Single(), body));
        if (Status != HttpStatusCode.OK)
            return new HttpResponseMessage(Status);
        var path = request.RequestUri.AbsolutePath;
        var json = request.Method == HttpMethod.Post ? """{"id":"msgbatch_01","processing_status":"in_progress"}"""
            : path.EndsWith("/results", StringComparison.Ordinal) ? Results
            : $$"""{"id":"msgbatch_01","processing_status":"{{(Ended ? "ended" : "in_progress")}}"}""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

public sealed class BatchLaneTests
{
    private const string Root = "https://gateway.example/anthropic";
    private const string Model = "batch/model";

    private static readonly PriceTable Prices = new(new Dictionary<string, ModelPrice> { [Model] = new(1m, 5m, 0.1m, 1.25m) });

    private static ScheduleBatchLane Lane() => new(Root, Model, "batch-key", 512);

    private static ScheduleEntry Digest() => new("digest", "daily 04:00", Goal: "Summarise the week.", Batch: true);

    [Fact]
    public async Task Submit_posts_the_prompt_to_the_profiles_root_with_its_model_and_key()
    {
        var fake = new FakeBatchEndpoint();
        var batches = new AnthropicMessageBatches(Root + "/", () => "sk-test", new HttpClient(fake));

        var id = await batches.SubmitAsync(Model, 512, [new BatchPrompt("schedule-digest", "Summarise the week.")], default);

        Assert.Equal("msgbatch_01", id);
        var sent = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://gateway.example/anthropic/v1/messages/batches", sent.Url);
        Assert.Equal(("sk-test", AnthropicMessageBatches.ApiVersion), (sent.Key, sent.Version));
        var request = JsonNode.Parse(sent.Body!)!["requests"]![0]!;
        Assert.Equal("schedule-digest", request["custom_id"]!.GetValue<string>());
        Assert.Equal(Model, request["params"]!["model"]!.GetValue<string>());
        Assert.Equal(512, request["params"]!["max_tokens"]!.GetValue<int>());
        Assert.Equal("Summarise the week.", request["params"]!["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Poll_reads_nothing_while_processing_and_the_results_once_ended()
    {
        var fake = new FakeBatchEndpoint();
        var batches = new AnthropicMessageBatches(Root, () => "sk-test", new HttpClient(fake));

        var processing = await batches.PollAsync("msgbatch_01", default);
        fake.Ended = true;
        var ended = await batches.PollAsync("msgbatch_01", default);

        Assert.False(processing.Ended);
        Assert.Empty(processing.Answers);
        Assert.True(ended.Ended);
        var answer = Assert.Single(ended.Answers);
        Assert.Equal(new BatchAnswer("schedule-digest", "Three findings.", new TokenCounts(1_000_000, 200_000, 0, 0, 0)), answer);
        Assert.Equal(
            ["https://gateway.example/anthropic/v1/messages/batches/msgbatch_01", "https://gateway.example/anthropic/v1/messages/batches/msgbatch_01", "https://gateway.example/anthropic/v1/messages/batches/msgbatch_01/results"],
            fake.Requests.Select(r => r.Url));
    }

    [Fact]
    public void An_errored_request_is_an_answer_with_the_providers_message()
    {
        var answer = AnthropicMessageBatches.Answer("""{"custom_id":"x","result":{"type":"errored","error":{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens too large"}}}}""");

        Assert.Null(answer.Text);
        Assert.Equal("errored: max_tokens too large", answer.Error);
    }

    [Fact]
    public async Task A_refusal_is_an_intake_exception_without_the_key()
    {
        var fake = new FakeBatchEndpoint { Status = HttpStatusCode.Unauthorized };
        var batches = new AnthropicMessageBatches(Root, () => "sk-secret", new HttpClient(fake));

        var e = await Assert.ThrowsAsync<IntakeException>(() => batches.SubmitAsync(Model, 1, [new BatchPrompt("a", "b")], default));

        Assert.Equal(401, e.Status);
        Assert.DoesNotContain("sk-secret", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cost_is_the_batch_rate_next_to_the_live_price()
    {
        var cost = BatchCost.Of(new TokenCounts(1_000_000, 200_000, 0, 0, 0), Model, Prices);

        Assert.Equal((1m, 2m), (cost.BatchUsd, cost.LiveUsd));
        Assert.Equal("Cost: $1.0000 at the batch rate; $2.0000 for the same tokens run live (1000000 in, 200000 out).", cost.Line());
        Assert.Null(BatchCost.Of(new TokenCounts(1, 1, 0, 0, 0), "unpriced/model", Prices).BatchUsd);
    }

    [Fact]
    public async Task A_batch_entry_is_submitted_at_its_occurrence_and_posted_with_its_cost_when_it_ended()
    {
        var fake = new FakeBatchEndpoint();
        var batches = new ScheduleBatches(Lane(), new AnthropicMessageBatches(Root, () => "sk-test", new HttpClient(fake)), Prices);
        using var rig = new ScheduleRig(new AutomationSchedules([Digest()], ScheduleRig.Url, Lane()), batches);

        await rig.Tick();
        await rig.Wake(TimeSpan.FromHours(4));
        await rig.Wake(TimeSpan.FromDays(1));
        fake.Ended = true;
        await rig.Wake(TimeSpan.FromMinutes(1));

        Assert.Empty(rig.Starter.Started);
        Assert.Single(fake.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains(rig.Warnings, w => w.Contains("still going", StringComparison.Ordinal));
        var posted = Assert.Single(rig.Notifier.Posted);
        Assert.Equal("chargehand: digest", posted.Title);
        Assert.StartsWith("Done (batch).\n\nThree findings.", posted.Body, StringComparison.Ordinal);
        Assert.EndsWith("Cost: $1.0000 at the batch rate; $2.0000 for the same tokens run live (1000000 in, 200000 out).", posted.Body, StringComparison.Ordinal);
        Assert.Empty(rig.State.Batches);
    }
}
