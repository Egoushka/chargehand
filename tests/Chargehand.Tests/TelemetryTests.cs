using System.Diagnostics;
using System.Text.Json;
using Chargehand.Budget;
using Chargehand.Config;
using Chargehand.Driven;
using Chargehand.RunLog;
using Chargehand.Runtime;

namespace Chargehand.Tests;

public class TelemetryTests
{
    private static async Task<List<Activity>> CallSpans(TelemetrySettings? telemetry)
    {
        using var root = new TempDir();
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            // By name: reading Telemetry.Source here would create the source while this listener registers, and miss it.
            ShouldListenTo = s => s.Name == "Chargehand",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        var profile = Runs.Profile(root.Path) with { Telemetry = telemetry };
        var result = await new Orchestrator(profile, new ScriptedRuntime(Runs.DraftReply), "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>())
            .RunAsync(Runs.DraftRequest(), CancellationToken.None);
        lock (spans)
            // Other test classes run in parallel on the same source; keep this run's trace.
            return spans.Where(a => a.OperationName == "chargehand.call" && a.TraceId.ToHexString() == result.TraceId).ToList();
    }

    [Fact]
    public async Task Call_spans_carry_usage_and_cost_only_when_the_profile_asks()
    {
        var plain = await CallSpans(new TelemetrySettings("http://127.0.0.1:1/api/public/otel", "pk", "sk"));
        Assert.NotEmpty(plain);
        Assert.All(plain, span => Assert.Null(span.GetTagItem("langfuse.observation.usage_details")));

        // The draft run makes two calls (the answer and its evidence repair); scripted calls use the same tokens.
        var spans = await CallSpans(new TelemetrySettings("http://127.0.0.1:1/api/public/otel", "pk", "sk", UsageOnSpans: true));
        Assert.Equal(2, spans.Count);
        Assert.All(spans, span =>
        {
            var usage = JsonDocument.Parse((string)span.GetTagItem("langfuse.observation.usage_details")!).RootElement;
            Assert.Equal((1000, 100, 4000, 200), (usage.GetProperty("input").GetInt64(), usage.GetProperty("output").GetInt64(),
                usage.GetProperty("cache_read_input_tokens").GetInt64(), usage.GetProperty("cache_creation_input_tokens").GetInt64()));
            // Priced at the profile's small-model rates: 1000*0.1 + 100*0.5 + 4000*0.01 + 200*0.125 per million.
            var cost = JsonDocument.Parse((string)span.GetTagItem("langfuse.observation.cost_details")!).RootElement;
            Assert.Equal(0.000215m, cost.GetProperty("total").GetDecimal());
        });
    }

    [Fact]
    public void A_driven_session_is_priced_under_whatever_provider_the_table_names_its_model()
    {
        var prices = new Dictionary<string, ModelPrice>
        {
            ["provider/claude-sonnet-5-5"] = new(3m, 15m, 0.3m, 3.75m),
            ["provider/claude-haiku-4-5"] = new(1m, 5m, 0.1m, 1.25m),
            ["a/claude-opus-5-5"] = new(5m, 25m, 0.5m, 6.25m),
            ["b/claude-opus-5-5"] = new(6m, 25m, 0.5m, 6.25m),
        };
        Assert.Equal("provider/claude-sonnet-5-5", DrivenTelemetry.Find(prices, "claude-sonnet-5-5"));
        Assert.Equal("provider/claude-haiku-4-5", DrivenTelemetry.Find(prices, "claude-haiku-4-5-20251001"));
        Assert.Null(DrivenTelemetry.Find(prices, "claude-opus-5-5"));          // two providers, two prices: unknown, not a guess
        Assert.Null(DrivenTelemetry.Find(prices, "claude-fable-5-1"));

        // Model by model; one model without a price makes the whole cost unknown (ADR 0026).
        var session = new SessionOutcome(SessionStatus.Completed, "", 3, 0, 0, 0, null, 0, [], true, Model: "claude-sonnet-5-5",
            ModelUsage: new Dictionary<string, TokenCounts> { ["claude-sonnet-5-5"] = new(1_000_000, 0, 0, 0, 0), ["claude-haiku-4-5-20251001"] = new(0, 1_000_000, 0, 0, 0) });
        Assert.Equal(8m, DrivenTelemetry.PriceUsd(prices, session));
        Assert.Null(DrivenTelemetry.PriceUsd(prices, session with { ModelUsage = new Dictionary<string, TokenCounts> { ["claude-fable-5-1"] = new(1, 1, 0, 0, 0) } }));
    }
}
