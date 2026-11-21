using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Research.Api;
using Chargehand.Results;
using Chargehand.Verification;

namespace Chargehand.Evals;

public enum DisproofOutcome { Disproved, Stands, Open }

/// <param name="Reason">Why: the judge's reason for a disproved claim, the outcome `ClaimScorer` gave for one left open, or the
/// worker's own words for one that stands.</param>
public sealed record DisproofResult(DisproofOutcome Outcome, string Reason);

/// <summary>
/// Claim bounty (docs/specs/2026-11-11-claim-bounty.md, issue #787): judges a "disprove" worker's answer about one claim of the
/// queue. The worker's reply is a normal result/v1 result (spec decision 3); its own claim is scored exactly as
/// <see cref="ClaimScorer"/> scores a public-benchmark arm's claims (spec decision 4) — a resolved locator plus a
/// <see cref="ClaimOutcome.Supported"/> verdict is the only way a disproof counts.
/// </summary>
public static class ClaimBounty
{
    public static async Task<DisproofResult> JudgeAsync(IWorkerRuntime runtime, ModelRef? judge, ClaimRecord claim, string workerReply,
        EvidenceScope scope, string disproverPreset, CancellationToken ct)
    {
        var envelope = new ResultEnvelope(claim.Id, "disprove", new string('0', 32),
            new PromptChain([], new AsSent("", disproverPreset, "", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))),
            new Usage(0, 0, 0, 0, 0));
        var assembled = ResultAssembler.Assemble(workerReply, envelope);
        if (assembled.Contract is not { } contract)
            return new(DisproofOutcome.Open, string.Join("; ", assembled.Errors));
        if (contract.Claims.Count == 0)
            return new(DisproofOutcome.Stands, contract.OpenQuestions.Count > 0 ? contract.OpenQuestions[0] : "the worker found no counter-evidence");
        if (claim.Preset is { } authorPreset && authorPreset == disproverPreset)
            return new(DisproofOutcome.Open, "a disprove run earns nothing for a claim its own preset wrote (no self-farming)");

        var disprove = contract.Claims[0];
        var byId = contract.Evidence.ToDictionary(e => e.Id);
        var locators = disprove.Evidence.Select(id => byId.GetValueOrDefault(id)?.Locator).OfType<string>().ToList();
        var scored = await ClaimScorer.ScoreAsync(runtime, judge, [new BenchClaim("bounty", claim.Id, 0, disprove.Text, locators)], scope, ct);
        return scored[0].Outcome == ClaimOutcome.Supported
            ? new(DisproofOutcome.Disproved, scored[0].Reason)
            : new(DisproofOutcome.Open, $"{scored[0].Outcome}: {scored[0].Reason}");
    }

    /// <summary>Leases one claim, runs it through <paramref name="disprove"/>, judges the answer and records the outcome: the
    /// claim moves to disproved or stands, or returns to open when the disproof earned nothing. Null: no open claim was leased.</summary>
    public static async Task<DisproofOutcome?> RunOnceAsync(ClaimQueue queue, BountyLedger ledger, IWorkerRuntime runtime, ModelRef? judge,
        Func<ClaimRecord, CancellationToken, Task<string>> disprove, EvidenceScope scope, string disproverPreset, TimeSpan lease, CancellationToken ct)
    {
        var claim = await queue.LeaseAsync(lease, ct);
        if (claim is null)
            return null;
        var reply = await disprove(claim, ct);
        var result = await JudgeAsync(runtime, judge, claim, reply, scope, disproverPreset, ct);
        await queue.MarkAsync(claim.Id, result.Outcome switch
        {
            DisproofOutcome.Disproved => ClaimState.Disproved,
            DisproofOutcome.Stands => ClaimState.Stands,
            _ => ClaimState.Open,
        }, ct);
        if (result.Outcome == DisproofOutcome.Disproved)
            await ledger.AppendAsync(new BountyEntry(claim.Id, RunIds.New(), disproverPreset, 1, DateTimeOffset.UtcNow), ct);
        return result.Outcome;
    }
}
