using System.Diagnostics;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.RunLog;

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
    /// <summary>Under serve, a run starts inside a request's activity that the SDK does not record; a parent-based sampler
    /// would then drop every run span. The run is its own trace instead.</summary>
    [Fact]
    public async Task A_run_under_an_unrecorded_ambient_activity_is_its_own_recorded_trace()
    {
        using var root = new TempDir();
        var spans = new List<Activity>();
        using var ambientSource = new ActivitySource("Test.Ambient");
        using var ambientListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Test.Ambient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
        };
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Chargehand",
            // The OpenTelemetry SDK's default: follow a parent's sampled flag, record a root.
            Sample = (ref ActivityCreationOptions<ActivityContext> o) =>
                o.Parent == default || o.Parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)
                    ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.PropagationData,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(ambientListener);
        ActivitySource.AddActivityListener(listener);
        using var ambient = ambientSource.StartActivity("request");
        Assert.False(ambient!.Recorded);

        var result = await new Orchestrator(Runs.Profile(root.Path), new ScriptedRuntime(Runs.DraftReply), "2.0.16", Repo.Root,
            new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>()).RunAsync(Runs.DraftRequest(), CancellationToken.None);

        Assert.Same(ambient, Activity.Current);
        Activity run;
        lock (spans)
            run = spans.Single(a => a.OperationName == "chargehand.run" && a.TraceId.ToHexString() == result.TraceId);
        Assert.True(run.Recorded);
        Assert.Equal(default, run.ParentSpanId);
    }
}
