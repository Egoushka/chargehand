using System.Globalization;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Oversight.Api;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

public sealed partial class RunService
{
    /// <summary>The owner's gate for paid spend (ADR 0092): the per-run threshold and the requests held above it.</summary>
    public RunApprovalGate Approvals { get; } = approvals ?? new RunApprovalGate(null);

    /// <summary>Null when the run may start now: the threshold is off, nothing it spends is priced, its figure is not above the threshold, or the owner approved it (the approval is used up).
    /// Otherwise the refusal that holds it, with the question in the owner's inbox. <paramref name="asked"/> is the request as it came (what an approval is for); <paramref name="admitted"/>
    /// is what it will run as, with an account's budgets cut.</summary>
    public async Task<AccountRefusal?> ApprovalAsync(Principal? who, RunRequest asked, RunRequest admitted, CancellationToken ct)
    {
        if (Approvals.Limit.LimitUsd is not { } threshold || UsesPool(admitted))
            return null;
        var actor = who?.Name;
        if (!(admitted.Driven is not null ? driven?.Priced(actor) ?? false : orchestrator.Priced(actor)))
            return null;
        var (usd, basis) = await ExposureAsync(who, admitted, ct);
        if (usd <= threshold)
            return null;
        var (_, verdict) = Approvals.Check(RunApprovalGate.Fingerprint(actor, asked), actor ?? AccountSettings.Owner, admitted.Context.Repository?.Path, asked.Text, usd, basis, Now);
        return verdict switch
        {
            RunApprovalGate.Verdict.Start => null,
            RunApprovalGate.Verdict.Declined => new("approval_declined", string.Create(CultureInfo.InvariantCulture,
                $"the owner declined this run (up to ${usd:0.00}, over the ${threshold:0.00} approval threshold); send it again to ask again"), StatusCodes.Status403Forbidden),
            _ => new("approval_required", string.Create(CultureInfo.InvariantCulture,
                $"this run may spend up to ${usd:0.00}, over the ${threshold:0.00} approval threshold; it starts once the owner approves it in the inbox"), StatusCodes.Status409Conflict),
        };
    }

    /// <summary>The most a run may spend, and where the figure comes from. A run states its budget; a batch states <c>max_usd_total</c> or a budget on every task; otherwise a batch is forecast
    /// from finished tasks like it. A forecast with no history is the size class's flat budget, which the preset already caps, so it asks nothing.</summary>
    private async Task<(decimal Usd, string Basis)> ExposureAsync(Principal? who, RunRequest request, CancellationToken ct)
    {
        if (request.Driven is not { } batch)
            return (request.Context.BudgetUsd ?? 0, RunApprovalGate.BudgetBasis);
        if (batch.MaxUsdTotal is { } cap)
            return (cap, RunApprovalGate.BudgetBasis);
        if (batch.Tasks.Count > 0 && batch.Tasks.All(t => t.BudgetUsd is not null))
            return (batch.Tasks.Sum(t => t.BudgetUsd!.Value), RunApprovalGate.BudgetBasis);
        if (driven is null || request.Context.Repository?.Path is not { Length: > 0 } repository)
            return (0, RunApprovalGate.ForecastBasis);
        decimal forecast = 0;
        foreach (var task in batch.Tasks)
        {
            var predicted = await driven.ForecastAsync(new ForecastQuery(repository, task.Goal, task.Size, request.Context.Preset), actor => Principal.Sees(who, actor), ct);
            forecast += predicted.Basis == ForecastBasis.Flat ? task.BudgetUsd ?? 0 : predicted.UsdP90;
        }
        return (forecast, RunApprovalGate.ForecastBasis);
    }
}
