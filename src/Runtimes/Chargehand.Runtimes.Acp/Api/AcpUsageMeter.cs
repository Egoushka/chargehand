using AgentClientProtocol;
using Chargehand.Kernel;

namespace Chargehand.Runtimes.Acp.Api;

/// <summary>
/// What a turn cost, from what the agent reports (ADR 0086, decision 10): the turn result's token counts when it has them, else the last
/// <c>usage_update</c>'s context gauge as input tokens, so a watcher polling mid-turn sees a lower bound and a budget can still stop the turn.
/// Null: the agent reported nothing. Counts are as the agent reports them. Where an agent's output already includes its reasoning tokens (Codex's does), the priced total counts them twice: a cap errs on the high side.
/// </summary>
public sealed class AcpUsageMeter
{
    private long? _gauge;
    private TokenCounts? _reported;

    public TokenCounts? Tokens => _reported ?? (_gauge is { } used ? new TokenCounts(used, 0, 0, 0, 0) : null);

    public void Observe(UsageUpdate update) => _gauge = update.Used;

    public void Finish(TokenUsage? usage)
    {
        if (usage is null)
            return;
        var (input, output) = (usage.InputTokens ?? 0, usage.OutputTokens ?? 0);
        // A total with no split still has to count against a cap.
        if (input == 0 && output == 0 && usage.TotalTokens is { } total)
            input = total;
        if (input != 0 || output != 0 || usage.ThoughtTokens is > 0 || usage.CachedReadTokens is > 0 || usage.CachedWriteTokens is > 0)
            _reported = new TokenCounts(input, output, usage.ThoughtTokens ?? 0, usage.CachedReadTokens ?? 0, usage.CachedWriteTokens ?? 0);
    }

    public void Reset() => (_gauge, _reported) = (null, null);
}
