namespace Chargehand.Modules.Pool.Api;

/// <summary>A queued item without its body, for the overview.</summary>
public sealed record PoolQueuedRow(string Key, string Repository, string Area, string Title, int Priority, int? PullRequest, DateTimeOffset EnqueuedAt, string? Reason);

/// <summary>A finished item without its body, for the overview.</summary>
public sealed record PoolDoneRow(string Key, string Repository, string Area, string Title, DateTimeOffset DoneAt);

/// <summary>A live worker and how many items it holds now.</summary>
public sealed record PoolLiveWorker(string Id, PoolScope Scope, long Tokens, DateTimeOffset StartedAt, int Items);
