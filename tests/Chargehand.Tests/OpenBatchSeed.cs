using Chargehand.Driven;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

/// <summary>An unfinished batch put on disk as a server that ran it and stopped would leave it (ADR 0061, amendments of 2026-10-14): its start record, its steps with its tasks' entries, and its token
/// key file. The tasks may stand anywhere: each one's entry goes into the task flow as it is.</summary>
internal static class OpenBatchSeed
{
    /// <param name="run">A server already running on <paramref name="directory"/>: it reads the batch back, as at its start, so its live view shows it.</param>
    /// <returns>The batch's token key.</returns>
    public static async Task<string> SaveAsync(string directory, IRunLog log, OpenBatch batch, DrivenRun? run = null)
    {
        if ((await log.ReadAsync(batch.RunId, default)).Start is null)
            await log.AppendAsync(new StartRecord(batch.RunId, batch.Started, batch.TraceId, batch.Request, Environment.ProcessId, BootId: ProcessBoot.Id, Actor: batch.Actor), default);
        using (var batches = new OpenBatches(directory, log))
            await batches.AdoptAsync(batch, default);
        var key = TokenKeyFile.ReadOrCreate(directory, batch.RunId);
        if (run is not null)
            await run.UnfinishedAsync(default);
        return key;
    }
}
