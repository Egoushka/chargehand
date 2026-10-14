using System.Net.ServerSentEvents;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Journal.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>ADR 0061: a batch's events replay from the journal, so a client that reconnects with its last event id gets every event it missed, once and in order, also from the process
/// that took the batch up after a restart.</summary>
public sealed class BatchEventStreamTests : IDisposable
{
    private const string Batch = "batch-1";

    private readonly TempDir _dir = new();

    private string State => Path.Combine(_dir.Path, "batches");

    public void Dispose() => _dir.Dispose();

    private static RunStatus Event(RunEventKind kind, string? detail = null) => RunStatus.Of(Batch, RunState.Running, kind) with { Detail = detail };

    /// <summary>As <see cref="DrivenRun"/> publishes: the records (and so the journal) first, then the run's own stream.</summary>
    private static void Publish(BatchRecords records, RunHandle run, RunStatus e)
    {
        records.AppendEvent(run.Id, e);
        run.Publish(e);
    }

    /// <summary>As a finished batch ends: its flow forgotten, then <c>run_finished</c>. Waits for the reader to have read the last journaled event first.</summary>
    private static async Task FinishAsync(BatchRecords records, RunHandle run, DrivenJournal journal, Task caughtUp)
    {
        await caughtUp.WaitAsync(TimeSpan.FromSeconds(20));
        await journal.ForgetAsync(run.Id);
        Publish(records, run, RunStatus.Of(Batch, RunState.Completed, RunEventKind.RunFinished));
    }

    private static (BatchRecords Records, RunHandle Run) Process(string records, DrivenJournal journal)
    {
        var batchRecords = new BatchRecords(records);
        batchRecords.Journal(Batch, journal);
        var run = new RunHandle(Batch, CancellationToken.None) { IsBatch = true };
        run.Publish(RunStatus.Of(Batch, RunState.Queued, RunEventKind.Accepted));
        return (batchRecords, run);
    }

    /// <param name="seen">Told each item as it comes, so a test can let the reader catch up before the batch's flow is forgotten.</param>
    private static async Task<List<SseItem<string>>> ReadAsync(RunHandle run, DrivenJournal journal, long after, int? take = null, Action<SseItem<string>>? seen = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        List<SseItem<string>> items = [];
        await foreach (var item in BatchEventStream.Of(run, async (a, ct) => BatchRecords.Parse(await journal.ReadEventsAsync(Batch, a, ct: ct)), after, timeout.Token))
        {
            items.Add(item);
            seen?.Invoke(item);
            if (items.Count == take)
                break;
        }
        return items;
    }

    private static long Last(List<SseItem<string>> items) => long.Parse(items.Last(i => i.EventId is not null).EventId!, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_client_that_reconnects_with_its_last_id_gets_every_event_after_it_once_and_in_order()
    {
        await using var journal = await DrivenJournal.OpenAsync(State);
        var (records, run) = Process(Path.Combine(_dir.Path, "records"), journal);
        Publish(records, run, Event(RunEventKind.Started));
        Publish(records, run, Event(RunEventKind.Plan, "plan"));
        Publish(records, run, Event(RunEventKind.Governor, "one"));

        var first = await ReadAsync(run, journal, 0, take: 2);
        Publish(records, run, Event(RunEventKind.Governor, "two"));
        var tail = new TaskCompletionSource();
        var resumed = ReadAsync(run, journal, Last(first), seen: i => _ = i.EventType == "task_finished" && tail.TrySetResult());
        Publish(records, run, Event(RunEventKind.TaskFinished, "done"));
        await FinishAsync(records, run, journal, tail.Task);
        var second = await resumed;

        Assert.Equal(["1", "2"], first.Select(i => i.EventId));
        Assert.Equal(["started", "plan"], first.Select(i => i.EventType));
        Assert.Equal(["3", "4", "5", null], second.Select(i => i.EventId));
        Assert.Equal(["governor", "governor", "task_finished", "run_finished"], second.Select(i => i.EventType));
    }

    [Fact]
    public async Task After_a_restart_a_client_resumes_from_its_last_id_in_the_journal_the_next_process_opened()
    {
        var recordsDir = Path.Combine(_dir.Path, "records");
        List<SseItem<string>> first;
        await using (var before = await DrivenJournal.OpenAsync(State))
        {
            var (records, run) = Process(recordsDir, before);
            Publish(records, run, Event(RunEventKind.Started));
            Publish(records, run, Event(RunEventKind.Plan, "plan"));
            Publish(records, run, Event(RunEventKind.Governor, "one"));
            first = await ReadAsync(run, before, 0, take: 1);
        }

        await using var after = await DrivenJournal.OpenAsync(State);
        var (again, resumedRun) = Process(recordsDir, after);
        Publish(again, resumedRun, Event(RunEventKind.Started, "resumed"));
        var tail = new TaskCompletionSource();
        var resumed = ReadAsync(resumedRun, after, Last(first), seen: i => _ = i.EventType == "task_finished" && tail.TrySetResult());
        Publish(again, resumedRun, Event(RunEventKind.TaskFinished, "done"));
        await FinishAsync(again, resumedRun, after, tail.Task);
        var second = await resumed;

        Assert.Equal(["1"], first.Select(i => i.EventId));
        Assert.Equal(["2", "3", "4", "5", null], second.Select(i => i.EventId));
        Assert.Equal(["plan", "governor", "started", "task_finished", "run_finished"], second.Select(i => i.EventType));
    }

    [Fact]
    public void The_last_event_id_header_is_where_a_stream_resumes()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        Assert.Equal(0, BatchEventStream.After(context.Request));
        context.Request.Headers["Last-Event-ID"] = "42";
        Assert.Equal(42, BatchEventStream.After(context.Request));
        context.Request.Headers["Last-Event-ID"] = "-1";
        Assert.Equal(0, BatchEventStream.After(context.Request));
    }
}
