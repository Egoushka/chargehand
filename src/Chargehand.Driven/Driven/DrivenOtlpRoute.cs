using System.Globalization;
using Chargehand.Containers;
using Chargehand.Contracts;

namespace Chargehand.Driven;

/// <summary>Where a session's Claude Code sends its own OpenTelemetry logs and metrics (ADR 0042): an OTLP/HTTP collector named by <c>driven.network.otlp_url</c>, reached through a
/// forward on the batch's egress container like the model gateway. Only <c>http</c>: whether Claude Code's exporter goes through the proxy for an <c>https</c> collector is unchecked.
/// No header is ever set, so no secret travels that the diff scan does not know.</summary>
/// <param name="Forward">The egress forward (<c>listen-port=host:port</c>).</param>
/// <param name="Endpoint">The session's <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>.</param>
public sealed record DrivenOtlpRoute(string Forward, string Endpoint)
{
    /// <summary>Null when <paramref name="otlpUrl"/> is not set. Throws <see cref="ChargehandException"/> for a URL that cannot work, before anything is created.</summary>
    /// <param name="takenPorts">The ports of the batch's other forwards.</param>
    public static DrivenOtlpRoute? Parse(string? otlpUrl, IEnumerable<int> takenPorts)
    {
        if (string.IsNullOrEmpty(otlpUrl))
            return null;
        if (!Uri.TryCreate(otlpUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host.Length == 0)
            throw Refuse("driven.network.otlp_url must be an absolute http URL (an OTLP/HTTP collector reached through a forward).");
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw Refuse("driven.network.otlp_url carries credentials, a query or a fragment; give the collector's base URL only.");
        if (takenPorts.Contains(uri.Port))
            throw Refuse($"driven.network.otlp_url uses port {uri.Port}, which another forward of the batch listens on; each forward needs its own port.");
        var port = uri.Port.ToString(CultureInfo.InvariantCulture);
        return new DrivenOtlpRoute($"{port}={uri.Host}:{port}", $"{BatchNetworkInfo.ServiceUrl(uri.Port)}{uri.AbsolutePath.TrimEnd('/')}");
    }

    /// <summary>The session container's environment for Claude Code's export. User prompts stay out (<c>OTEL_LOG_USER_PROMPTS</c> is never set), and no traces: the
    /// collector has no pipeline for them, and the batch's own spans already reach Langfuse.</summary>
    public IReadOnlyDictionary<string, string> Environment(string batchId, string taskId, string taskRunId, string? traceId)
    {
        var attributes = new List<string> { Attribute("chargehand.run_id", batchId), Attribute("chargehand.task_id", taskId), Attribute("chargehand.task_run_id", taskRunId) };
        if (traceId is not null)
            attributes.Add(Attribute("chargehand.trace_id", traceId));
        return new Dictionary<string, string>
        {
            ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1",
            ["OTEL_LOGS_EXPORTER"] = "otlp",
            ["OTEL_METRICS_EXPORTER"] = "otlp",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = Endpoint,
            ["OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE"] = "cumulative",
            ["OTEL_RESOURCE_ATTRIBUTES"] = string.Join(',', attributes),
        };
    }

    // A task id comes from the request: percent-encoded, it cannot add an attribute or break the list.
    private static string Attribute(string key, string value) => $"{key}={Uri.EscapeDataString(value)}";

    private static ChargehandException Refuse(string message) =>
        new(ErrorCode.InvalidRequest, message, "Fix driven.network.otlp_url, or remove it to run sessions without Claude Code's own telemetry.");
}
