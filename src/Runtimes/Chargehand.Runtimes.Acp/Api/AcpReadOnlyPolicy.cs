using AgentClientProtocol;

namespace Chargehand.Runtimes.Acp.Api;

/// <param name="Refused">True when the request was not granted.</param>
/// <param name="Outcome">What to send the agent.</param>
/// <param name="Reason">One sentence for the run's record.</param>
public sealed record AcpPermissionVerdict(bool Refused, RequestPermissionOutcome Outcome, string Reason);

/// <summary>
/// The first of the three read-only guards of a research worker (ADR 0086, decision 8): every <c>session/request_permission</c> that is not a read,
/// a search or a thought is rejected, and an unknown or missing kind counts as not a read. A grant is only ever "once": a saved allow or a
/// saved reject is never selected (ADR 0006).
/// </summary>
public static class AcpReadOnlyPolicy
{
    private static readonly HashSet<string> Reads = [AcpToolKind.Read, AcpToolKind.Search, AcpToolKind.Think];

    public static AcpPermissionVerdict Decide(RequestPermissionRequest request)
    {
        var kind = request.ToolCall.Kind;
        var what = $"{kind ?? "unknown"} request '{request.ToolCall.Title}'";
        if (kind is not null && Reads.Contains(kind) && Pick(request, AcpPermissionKind.AllowOnce) is { } allow)
            return new AcpPermissionVerdict(false, RequestPermissionOutcome.Selected(allow), $"allowed once: {what}");
        var reject = Pick(request, AcpPermissionKind.RejectOnce);
        return new AcpPermissionVerdict(true, reject is null ? RequestPermissionOutcome.Cancelled : RequestPermissionOutcome.Selected(reject),
            $"rejected, read-only worker: {what}");
    }

    private static string? Pick(RequestPermissionRequest request, string kind) => request.Options.FirstOrDefault(o => o.Kind == kind)?.OptionId;
}
