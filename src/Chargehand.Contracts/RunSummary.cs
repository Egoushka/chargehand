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
}
