using Chargehand.Kernel;

namespace Chargehand.Kernel;

/// <summary>Tokens one model call used, as the runtime reports them; the ledger prices them.</summary>
public sealed record TokenCounts(long Input, long Output, long Reasoning, long CacheRead, long CacheWrite);
