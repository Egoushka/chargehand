using System.Globalization;
using Chargehand.Kernel;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Automation;

/// <summary>A batch the scheduler submitted and has not seen end.</summary>
/// <param name="Attempts">Polls or posts that failed; the batch is dropped after <see cref="Scheduler.MaxPostAttempts"/>.</param>
public sealed record ScheduledBatch(string Entry, string BatchId, DateTimeOffset Submitted, int Attempts = 0);

/// <summary>What one task cost on the batch lane, beside what the same tokens cost run live. Null dollars: the model has no <c>prices</c> entry (ADR 0026), unknown rather than free.</summary>
public sealed record BatchCost(TokenCounts Tokens, decimal? BatchUsd, decimal? LiveUsd)
{
    /// <summary>The provider's batch discount on every token class (ADR 0087: 50% off input and output).</summary>
    public const decimal BatchRate = 0.5m;

    public static BatchCost Of(TokenCounts tokens, string model, IPriceTable prices) =>
        prices.PriceUsd(model, tokens) is { } live ? new(tokens, live * BatchRate, live) : new(tokens, null, null);

    public string Line() => BatchUsd is { } batch && LiveUsd is { } live
        ? $"Cost: ${Usd(batch)} at the batch rate; ${Usd(live)} for the same tokens run live ({Tokens.Input.ToString(CultureInfo.InvariantCulture)} in, {Tokens.Output.ToString(CultureInfo.InvariantCulture)} out)."
        : $"Cost: unknown, the model has no prices entry ({Tokens.Input.ToString(CultureInfo.InvariantCulture)} in, {Tokens.Output.ToString(CultureInfo.InvariantCulture)} out).";

    private static string Usd(decimal usd) => usd.ToString("0.0000", CultureInfo.InvariantCulture);
}

/// <summary>The batch lane of <c>automation.schedules</c> (ADR 0087): an entry with <c>batch</c> is one prompt submitted to the provider's batch endpoint instead of a worker run; each wake polls the
/// batches not yet ended, and an ended one is posted like a read-only result with its cost next to the live price.</summary>
public sealed class ScheduleBatches(ScheduleBatchLane lane, IMessageBatches endpoint, IPriceTable prices)
{
    public static string CustomId(string entry) => $"schedule-{entry}";

    /// <exception cref="IntakeException">The endpoint refused or was not reached: the occurrence is tried again later.</exception>
    public async Task<ScheduledBatch> SubmitAsync(ScheduleEntry entry, DateTimeOffset now, CancellationToken ct) =>
        new(entry.Id, await endpoint.SubmitAsync(lane.Model, lane.Tokens(), [new BatchPrompt(CustomId(entry.Id), entry.Text()!)], ct), now);

    /// <summary>The text to post once the batch ended; null while it is still processing.</summary>
    /// <exception cref="IntakeException">The endpoint refused or was not reached.</exception>
    public async Task<string?> PollAsync(ScheduledBatch batch, CancellationToken ct)
    {
        var poll = await endpoint.PollAsync(batch.BatchId, ct);
        if (!poll.Ended)
            return null;
        if (poll.Answers.FirstOrDefault(a => a.CustomId == CustomId(batch.Entry)) is not { } answer)
            return $"Ended: failed. The batch {batch.BatchId} ended without an answer for '{batch.Entry}'.";
        var cost = BatchCost.Of(answer.Tokens, lane.Model, prices).Line();
        return answer.Error is { } error
            ? $"{ScheduleResults.Cut($"Ended: failed. {error}")}\n\n{cost}"
            : $"{ScheduleResults.Cut($"Done (batch).\n\n{(answer.Text ?? "").Trim()}")}\n\n{cost}";
    }
}
