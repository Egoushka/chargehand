using System.Text.Json.Serialization.Metadata;
using Switchknife.Journal;

namespace Chargehand.Modules.Journal.Api;

/// <summary>The side effects of driven batches as recorded steps (ADR 0061), in one SQLite file in the server's state directory beside the batch state files. A step's intent is written before it
/// runs and its result after: a key that completed never runs again, and one that was interrupted runs again with <see cref="StepAttempt.Resumed"/> set. A step runs at least once, so it must be
/// safe to repeat for its key. A batch's events append to the same flow and read back after a restart. One process owns the file; run one step of a key at a time.</summary>
public sealed class DrivenJournal : IAsyncDisposable, IDisposable
{
    /// <summary>The journal's file name inside the state directory.</summary>
    public const string FileName = "journal.db";

    private readonly StepJournal _journal;

    private DrivenJournal(StepJournal journal) => _journal = journal;

    /// <summary>Opens <see cref="FileName"/> in <paramref name="stateDirectory"/>, creating the directory and the file when missing.</summary>
    public static async Task<DrivenJournal> OpenAsync(string stateDirectory, TimeProvider? clock = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        return new DrivenJournal(await StepJournal.OpenAsync(Path.Combine(stateDirectory, FileName), clock, ct).ConfigureAwait(false));
    }

    /// <summary>Runs <paramref name="step"/> once in effect: a completed key returns its recorded result. A cancel leaves the step started, so the key's next run is resumed; another exception is
    /// recorded as a failure and rethrown. Keep secrets out of <typeparamref name="T"/>: it is stored as JSON.</summary>
    public Task<T> RunAsync<T>(StepKey key, Func<StepAttempt, CancellationToken, Task<T>> step, JsonTypeInfo<T> result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        return _journal.RunAsync(key.Batch, key.Name, (context, token) => step(Attempt(context), token), result, ct);
    }

    /// <summary><see cref="RunAsync{T}"/> for a step with no result worth keeping.</summary>
    public Task RunAsync(StepKey key, Func<StepAttempt, CancellationToken, Task> step, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        return _journal.RunAsync(key.Batch, key.Name, (context, token) => step(Attempt(context), token), ct);
    }

    /// <summary>Removes the record of a completed or failed step, so the key's next run is a first attempt (<see cref="StepAttempt.Number"/> 1, not resumed); false when the key had no record.
    /// Calling it states that the earlier effect is gone or may happen again: the journal cannot check that.</summary>
    /// <exception cref="InvalidOperationException">The step is started (in <see cref="UnfinishedAsync"/>): an interrupted attempt is run again, resumed, not forgotten.</exception>
    public Task<bool> ResetAsync(StepKey key, CancellationToken ct = default) => _journal.ResetAsync(key.Batch, key.Name, ct);

    /// <summary>The steps whose intent is recorded and whose outcome is not: what a crash or a kill left. Null <paramref name="batch"/>: every batch.</summary>
    public async Task<IReadOnlyList<StepKey>> UnfinishedAsync(string? batch = null, CancellationToken ct = default) =>
        [.. (await _journal.UnfinishedAsync(batch, ct).ConfigureAwait(false)).Select(s => new StepKey(s.Flow, s.Key))];

    /// <summary>Appends an event to the batch's flow; returns its <see cref="BatchEvent.Sequence"/>.</summary>
    public Task<long> AppendEventAsync(string batch, string kind, string payload, CancellationToken ct = default) => _journal.AppendEventAsync(batch, kind, payload, ct);

    /// <summary>The batch's events after <paramref name="after"/> (0: from the first), oldest first, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<BatchEvent>> ReadEventsAsync(string batch, long after = 0, int limit = 1000, CancellationToken ct = default) =>
        [.. (await _journal.ReadEventsAsync(batch, after, limit, ct).ConfigureAwait(false)).Select(e => new BatchEvent(e.Sequence, e.Kind, e.At, e.Payload))];

    /// <summary>Removes a batch's steps and events, once its run record is written and nothing will resume it.</summary>
    public Task ForgetAsync(string batch, CancellationToken ct = default) => _journal.ForgetAsync(batch, ct);

    public void Dispose() => _journal.Dispose();

    public ValueTask DisposeAsync() => _journal.DisposeAsync();

    private static StepAttempt Attempt(StepContext context) => new(context.Attempt, context.Resumed);
}
