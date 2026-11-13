using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

public sealed class RunEventsTests
{
    [Fact]
    public async Task A_run_writes_started_and_finished_events_with_its_status()
    {
        using var dir = new TempDir();
        using var log = SqliteEventLog.Open(Path.Combine(dir.Path, "events.db"));
        string runId;
        await using (var runs = new RunService(Runs.Orchestrator(new ScriptedRuntime(Runs.DraftReply), dir.Path, new JsonlRunLog(Path.Combine(dir.Path, "log.jsonl"))),
            Repo.Path("presets"), CancellationToken.None, events: log))
        {
            var run = runs.Start(Runs.DraftRequest())!;
            runId = run.Id;
            await run.Done;
        }

        var events = await log.ReadAsync(new EventFilter(), default);
        var types = events.Where(e => e.Type.StartsWith("chargehand.run.", StringComparison.Ordinal)).Select(e => e.Type).ToList();
        Assert.Equal(RunEventEmitter.StartedType, types.First());
        Assert.Equal(RunEventEmitter.FinishedType, types.Last());
        Assert.Single(types, RunEventEmitter.FinishedType);
        var finished = events.Single(e => e.Type == RunEventEmitter.FinishedType);
        using var doc = JsonDocument.Parse(finished.Envelope);
        Assert.Equal(runId, doc.RootElement.GetProperty("data").GetProperty("run_id").GetString());
        Assert.Equal("completed", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal($"run/{runId}", doc.RootElement.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task A_status_that_comes_back_is_a_new_event_and_the_end_carries_the_pull_request()
    {
        using var dir = new TempDir();
        using var log = SqliteEventLog.Open(Path.Combine(dir.Path, "events.db"));
        await using (var emitter = new RunEventEmitter(log))
        {
            const string id = "r1";
            emitter.Offer(RunStatus.Of(id, RunState.Queued, RunEventKind.Accepted), "repo");
            emitter.Offer(RunStatus.Of(id, RunState.Running, RunEventKind.Started), "repo");
            emitter.Offer(RunStatus.Of(id, RunState.NeedsInput, RunEventKind.Question), "repo");
            emitter.Offer(RunStatus.Of(id, RunState.Running, RunEventKind.Operator), "repo");
            emitter.Offer(RunStatus.Of(id, RunState.NeedsInput, RunEventKind.Question), "repo");
            emitter.Offer(RunStatus.Of(id, RunState.Running, RunEventKind.PrOpened) with { PrUrl = "https://github.com/o/r/pull/7", Branch = "b" }, "repo");
            emitter.Offer(RunStatus.Of(id, RunState.Completed, RunEventKind.RunFinished), "repo");
        }

        var events = await log.ReadAsync(new EventFilter(), default);
        Assert.Equal(["started", "status_changed", "status_changed", "status_changed", "status_changed", "finished"], events.Select(e => e.Type[(e.Type.LastIndexOf('.') + 1)..]).ToList());
        using var doc = JsonDocument.Parse(events[^1].Envelope);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(7, data.GetProperty("pr_number").GetInt32());
        Assert.Equal("b", data.GetProperty("branch").GetString());
    }

    [Theory]
    [InlineData("https://github.com/o/r/pull/42", 42)]
    [InlineData("https://github.com/o/r/pull/42/", 42)]
    [InlineData("https://example.test/not-a-pr", null)]
    public void A_pull_request_number_is_the_end_of_its_url(string url, int? expected) => Assert.Equal(expected, RunEventEmitter.PrNumber(url));
}
