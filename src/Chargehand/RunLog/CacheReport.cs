using System.Text;
using Chargehand.Contracts;

namespace Chargehand.RunLog;

/// <summary>
/// Cache report (decision: cache doctor → cache report): reads, writes and hit rate per call, and per node the
/// first orchestrator block that changed where a prefix should have been shared — against the previous call in the
/// session, or against the node it forked (or could have forked). Breakers outside the prompt chain are listed as a
/// checklist, since the orchestrator cannot see OpenCode's own text.
/// </summary>
public static class CacheReport
{
    /// <summary>A later call reading less than this share of the previous call's prompt lost the shared prefix.</summary>
    public const double DropRatio = 0.5;

    public static readonly string[] Checklist =
    [
        "prompt below the model's cache minimum (1,024 tokens on most providers)",
        "TTL gap between calls longer than the provider's cache lifetime",
        "cache-key partition or loss of gateway or provider stickiness",
        "session ID in OpenCode's <env> block (no reuse across fresh sessions)",
        "instruction entry updated mid-session (appended as <system-update>)",
        "date rollover (OpenCode tracks core/date as an instruction)",
        "directory change (session move) or tool-set change (agent, ruleset, OpenCode version)",
    ];

    public static string Build(IReadOnlyList<CallRecord> calls)
    {
        var worker = calls.Where(c => c.Tokens is not null && c.SessionId is not null).OrderBy(c => c.Started).ToList();
        var nodes = worker.GroupBy(c => c.NodeId).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("node       call   prompt   cached  written  cache%");
        foreach (var node in nodes)
            foreach (var (c, i) in node.Select((c, i) => (c, i + 1)))
                sb.AppendLine($"{c.NodeId,-10} {i,4} {c.PromptTokens,8} {c.Tokens!.CacheRead,8} {c.Tokens.CacheWrite,8} {c.CacheRate,7:P0}");
        long prompt = worker.Sum(c => c.PromptTokens ?? 0), read = worker.Sum(c => c.Tokens!.CacheRead), write = worker.Sum(c => c.Tokens!.CacheWrite);
        sb.AppendLine($"total      {worker.Count,4} {prompt,8} {read,8} {write,8} {(prompt == 0 ? 0 : (double)read / prompt),7:P0}").AppendLine();

        var first = nodes.FirstOrDefault()?.First();
        foreach (var node in nodes)
        {
            var head = node.First();
            string verdict;
            if (head == first)
                verdict = $"first node: writes the session prefix ({head.Tokens!.CacheWrite} written)";
            else if (head.ForkedFrom is { } parent)
                verdict = $"forked {Owner(worker, parent)}: first call read {head.Tokens!.CacheRead} of {head.PromptTokens}";
            else
                verdict = $"fresh session, first call read {head.Tokens!.CacheRead} of {head.PromptTokens}: " +
                          (FirstChange(first!, head) is { } change
                              ? $"{change} differs from {first!.NodeId}, so it could not fork {first.NodeId}'s cached prefix"
                              : $"same blocks as {first!.NodeId}; its session had no completed call when this node started");
            sb.AppendLine($"{node.Key}: {verdict}");

            foreach (var (prev, next) in node.Zip(node.Skip(1)))
                if (next.Tokens!.CacheRead < prev.PromptTokens * DropRatio)
                    sb.AppendLine($"  call {node.ToList().IndexOf(next) + 1}: read {next.Tokens.CacheRead} after a {prev.PromptTokens}-token prompt: " +
                                  (FirstChange(prev, next) is { } change ? $"{change} changed" : "no orchestrator block changed; see the checklist"));
        }

        sb.AppendLine().AppendLine("Not visible to the orchestrator; check when a drop has no block to blame:");
        foreach (var item in Checklist)
            sb.AppendLine($"- {item}");
        return sb.ToString();
    }

    /// <summary>The first instruction entry, prompt block or as-sent field that differs between two calls.</summary>
    public static string? FirstChange(CallRecord a, CallRecord b)
    {
        var ia = a.Instructions ?? [];
        var ib = b.Instructions ?? [];
        for (var i = 0; i < Math.Max(ia.Count, ib.Count); i++)
            if (ia.ElementAtOrDefault(i) != ib.ElementAtOrDefault(i))
                return $"instruction entry `{(ib.ElementAtOrDefault(i) ?? ia[i]).Key}` ({Short(ia.ElementAtOrDefault(i)?.Sha256)} → {Short(ib.ElementAtOrDefault(i)?.Sha256)})";
        var ba = a.PromptChain.Blocks;
        var bb = b.PromptChain.Blocks;
        for (var i = 0; i < Math.Max(ba.Count, bb.Count); i++)
            if (ba.ElementAtOrDefault(i)?.Sha256 != bb.ElementAtOrDefault(i)?.Sha256)
                return $"block `{(bb.ElementAtOrDefault(i) ?? ba[i]).Name}` ({Short(ba.ElementAtOrDefault(i)?.Sha256)} → {Short(bb.ElementAtOrDefault(i)?.Sha256)})";
        var (sa, sb) = (a.PromptChain.AsSent, b.PromptChain.AsSent);
        return sa.OpencodeVersion != sb.OpencodeVersion ? "as_sent.opencode_version"
            : sa.Model != sb.Model ? "as_sent.model"
            : sa.Agent != sb.Agent ? "as_sent.agent"
            : sa.ToolsSha256 != sb.ToolsSha256 ? "as_sent.tools_sha256"
            : sa.Date != sb.Date ? "as_sent.date"
            : null;
    }

    private static string Owner(IReadOnlyList<CallRecord> calls, string session) =>
        calls.FirstOrDefault(c => c.SessionId == session)?.NodeId is { } node ? $"from {node}" : $"from session {session}";

    private static string Short(string? sha) => sha is null ? "none" : sha[..12];
}
