using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Automation;

/// <summary>Starts a batch for a trigger so that a crash between the start and the state file's save cannot start it twice. The trigger is saved as starting before the batch starts and its key rides in
/// the request's text, so the next attempt finds the batch in the run log and adopts it; it starts again only when the log holds none.</summary>
internal static class TriggeredStart
{
    /// <summary>The run log's clock and the state's are one process's, but a start is looked for from a little before the state says.</summary>
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(1);

    private static string Mark(string key) => $"\n\n[trigger {key}]";

    /// <param name="starting">The state's triggers that began to start and are not settled; <paramref name="save"/> writes the state.</param>
    public static async Task<BatchStart> StartAsync(IBatchStarter starter, IRunLog log, Dictionary<string, DateTimeOffset> starting, Action<DateTimeOffset> save, string key, RunRequest request,
        DateTimeOffset now, CancellationToken ct)
    {
        if (starting.TryGetValue(key, out var since))
        {
            if (await FindAsync(log, key, since, ct) is { } existing)
                return new BatchStart(existing);
        }
        else
        {
            starting[key] = now;
            save(now);
        }
        var started = await starter.StartAsync(request with { Text = request.Text + Mark(key) }, ct);
        if (started.RunId is null)
            starting.Remove(key);
        return started;
    }

    private static async Task<string?> FindAsync(IRunLog log, string key, DateTimeOffset since, CancellationToken ct)
    {
        var mark = Mark(key);
        foreach (var run in await log.ListAsync(new RunListQuery(Since: since - Skew, Limit: 500), ct))
            if ((await log.ReadAsync(run.RunId, ct)).Start?.Request.Text.EndsWith(mark, StringComparison.Ordinal) == true)
                return run.RunId;
        return null;
    }
}
