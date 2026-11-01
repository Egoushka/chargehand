using System.Globalization;
using Chargehand.Contracts;

namespace Chargehand.Modules.Ledger.Api;

/// <summary>Closes the runs a server restart left open (CHARGEHAND-260): a start record whose owner is gone and that has no run record gets a failed one, so nothing that reads the log sees it as running.</summary>
public static class InterruptedRuns
{
    public const string Reason = "the server restarted before the run finished";

    /// <summary>Appends a failed run record for every run whose latest start has no live owner (<see cref="StartRecord.OwnerAlive"/>: an earlier boot, or no boot id) and has no run record yet,
    /// except those in <paramref name="keep"/> (batches this boot resumed). A second call finds them finished and writes nothing. Returns how many it closed.</summary>
    public static async Task<int> CloseAsync(IRunLog log, IReadOnlySet<string> keep, DateTimeOffset now, CancellationToken ct)
    {
        var data = await log.ReadAllAsync(ct);
        var finished = data.Runs.Select(r => r.RunId).ToHashSet();
        var closed = 0;
        foreach (var start in data.Starts.GroupBy(s => s.RunId).Select(g => g.Last()))
        {
            if (finished.Contains(start.RunId) || keep.Contains(start.RunId) || start.OwnerAlive())
                continue;
            await log.AppendAsync(new RunRecord(start.RunId, start.Started, now, start.Request.Context.Preset, null, null, Interrupted(start, now), Actor: start.Actor), ct);
            closed++;
        }
        return closed;
    }

    private static ResultContract Interrupted(StartRecord start, DateTimeOffset now)
    {
        var error = new ResultError(ErrorCode.Internal, Reason, true, "Start the run again if it is still wanted.");
        return new("result/v1", start.RunId, "run", start.TraceId, new PromptChain([], new AsSent("", "", "", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            ResultStatus.Failed, Reason, [], [], [], [Reason], 0, new Usage(0, 0, 0, 0, 0), error, Actor: start.Actor)
        { Name = RunNames.Of(start.RunId) };
    }
}
