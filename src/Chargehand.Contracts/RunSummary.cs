namespace Chargehand.Contracts;

/// <summary>run-summary/v1: one row of a run list (ADR 0039), enough for a client to show state, cost and the pull request.</summary>
public sealed record RunSummary(
    string ContractVersion,
    string RunId,
    RunState Status,
    string Preset,
    DateTimeOffset StartedAt,
    string? ParentRunId = null,
    string? TaskRef = null,
    DateTimeOffset? FinishedAt = null,
    decimal? Usd = null,
    string? Branch = null,
    string? PrUrl = null,
    ErrorCode? ErrorCode = null,
    string? Actor = null,
    string? Kind = null,
    string? Repository = null,
    long? Tokens = null,
    string? Commit = null)
{
    /// <summary>The run's friendly name, like "Brave Capybara"; the id stays the key.</summary>
    public string? Name { get; init; }

    /// <summary>Who paid for the run, <c>api_key</c> or <c>subscription</c> (ADR 0082); null on a run from before the record held it, which counted as an API key. <see cref="Usd"/> is the price table's figure either way: on a subscription it is not money spent.</summary>
    public string? Credential { get; init; }

    /// <summary>How much of the subscription's usage windows the run had used, as the provider last reported it while the run ran (ADR 0098); null when the run did not use a subscription or none was reported.</summary>
    public SubscriptionUsage? SubscriptionUsage { get; init; }
}
