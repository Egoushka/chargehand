using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Server;

public sealed partial class RunService
{
    /// <summary>What a run reserves against the dollar caps (ADR 0083, 0100): its budget when it bills a priced API key, nothing on a subscription.</summary>
    private decimal Reserved(RunRequest request, string? actor) =>
        BillingOf(actor, request.Driven is not null) is { Credential: CredentialKind.ApiKey } ? Reserve(request) : 0;

    /// <summary>The credential a run of <paramref name="actor"/> bills and the subscription whose windows its runtime reports; null when the account may use no credential. A batch runs
    /// Claude Code in its containers, so it reports the <c>claude</c> windows.</summary>
    private (string Credential, string? UsageProvider)? BillingOf(string? actor, bool batch)
    {
        try
        {
            if (batch && driven is not null)
                return (driven.Priced(actor) ? CredentialKind.ApiKey : CredentialKind.Subscription, "claude");
        }
        catch (Chargehand.Kernel.ChargehandException)
        {
            return null;
        }
        return orchestrator?.Billing(actor);
    }

    /// <summary>The account's use of its subscription today against its percent caps (ADR 0100); null when it sets none or has no run log.</summary>
    public async Task<SubscriptionStanding?> SubscriptionStandingAsync(AccountSettings account, bool batch, CancellationToken ct)
    {
        if (account.MaxPctPerDay7d is null && account.MaxPctPerDay5h is null)
            return null;
        var billing = BillingOf(account.Name, batch);
        return await SubscriptionCap.ReadAsync(account, billing?.Credential ?? CredentialKind.ApiKey, billing?.UsageProvider, log, Now, ct);
    }
}
