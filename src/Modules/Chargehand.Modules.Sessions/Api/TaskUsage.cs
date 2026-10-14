using Chargehand.Contracts;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>What a running task has used so far, cumulative, and where its session is: turns and stage are known only while it runs, from the session's tally. <c>Usd</c> is
/// priced dollars (an API key only); <c>UsdEstimate</c> is the session's dollar equivalent from the price table, also on a subscription, and binds nothing.</summary>
public sealed record TaskUsage(long Tokens, decimal Usd, int? Turns = null, SessionStage? Stage = null, long? CacheRead = null, long? CacheWrite = null, decimal? UsdEstimate = null, bool UsdEstimatePartial = false);
