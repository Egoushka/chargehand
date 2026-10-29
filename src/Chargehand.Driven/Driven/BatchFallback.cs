using Chargehand.Config;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Runtimes.ClaudeCode.Api;

namespace Chargehand.Driven;

/// <summary>What a subscription batch needs to fall back to the API key inside its egress gateway (ADR 0088): the key, the host it goes to, the price of the model it runs, and the ledger that
/// holds the day's cap. The batch's own allowance is reserved from the ledger just before its egress container starts (<see cref="Reserve"/>).</summary>
internal sealed record FallbackSetup(string Credential, string Host, FallbackPrice Price, ApiFallback Gate, string? BaseUrl = null)
{
    /// <returns>The fallback of a subscription batch whose profile names one and a ledger exists for; null for any other batch, and for a base URL <see cref="FallbackEndpoint"/> refuses
    /// (a line on stderr says why).</returns>
    public static FallbackSetup? Of(ClaudeCodeSettings claude, string model, ModelPriceLookup price, bool priced, ApiFallback? gate, Func<string, string> secret, string runId)
    {
        if (priced || gate is null || claude.ApiFallbackKeySecret is not { } item)
            return null;
        var found = price(model);
        claude.CheckApiFallbackModel(model, found is not null);
        if (FallbackEndpoint.Parse(claude.ApiFallbackBaseUrl, out var why) is not { } endpoint)
        {
            Console.Error.WriteLine($"chargehand: batch {runId} has no API fallback: claude_code.api_fallback_base_url {why}");
            return null;
        }
        return new FallbackSetup(secret(item), endpoint.Host, new FallbackPrice(found!.Input, found.Output, found.CacheRead, found.CacheWrite), gate, string.IsNullOrWhiteSpace(claude.ApiFallbackBaseUrl) ? null : endpoint.ToString());
    }

    /// <summary>Sets the batch's share aside; an allowance of 0 (the fallback is closed, spent or fully reserved) means the batch gets no fallback.</summary>
    public FallbackReservation Reserve(string batch) => new(Gate, batch, Gate.Reserve(batch), this);
}

internal delegate Chargehand.Modules.Ledger.Api.ModelPrice? ModelPriceLookup(string model);

/// <summary>A batch's hold on the day's fallback cap, from its start to its teardown. It settles once: with what the egress log says it spent, or the whole reservation when the log could not be
/// read; a batch that never got as far as an egress container spent nothing.</summary>
internal sealed class FallbackReservation(ApiFallback gate, string batch, decimal allowance, FallbackSetup setup)
{
    private bool _settled;
    private bool _unreadable;

    public decimal Allowance => allowance;

    /// <summary>What the batch spent, once settled; null before, and after an unreadable log.</summary>
    public FallbackSpend? Spent { get; private set; }

    public GatewayFallback Spec => new(setup.Credential, setup.Host, allowance, setup.Price, setup.BaseUrl);

    /// <param name="spend">What the egress containers' logs showed; null: a log could not be read.</param>
    public void Settle(FallbackSpend? spend)
    {
        if (allowance <= 0 || _settled)
            return;
        _settled = true;
        Spent = spend;
        _unreadable = spend is null;
        if (spend is null)
            gate.SettleUnreadable(batch);
        else
            gate.Settle(batch, spend.Usd, spend.Refused, spend.LimitedUntil);
    }

    /// <summary>The batch ended before any egress container could have been used: nothing was spent, so the whole reservation goes back.</summary>
    public void ReleaseUnused() => Settle(FallbackSpend.None);

    /// <summary>The batch result's <c>fallback</c> block; null when the batch had none. After an unreadable log the dollars are the whole reservation, as the ledger counted them.</summary>
    public BatchFallback? Report() => allowance <= 0 ? null : new BatchFallback(_unreadable ? allowance : Spent?.Usd ?? 0, Spent?.Calls ?? 0, Spent?.Refused ?? false,
        Spent?.ByRun.ToDictionary(r => r.Key, r => new TaskFallback(r.Value.Usd, r.Value.Calls)));
}
