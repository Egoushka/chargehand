using System.Diagnostics;

namespace Chargehand.Kernel;

/// <summary>Span source for run, node and call spans (ADR 0012). Usage lives on the gateway side, unless the profile
/// puts it on call spans for calls no gateway records (ADR 0021).</summary>
public static class Telemetry
{
    /// <summary>Pinned OpenTelemetry GenAI semantic-conventions version.</summary>
    public const string GenAiSemConvVersion = "1.44.0";

    public static readonly ActivitySource Source = new("Chargehand");
}

/// <summary>The profile's <c>telemetry</c> (ADR 0012): the OTLP endpoint and the secret-store items holding its keys.</summary>
/// <param name="UsageOnSpans">Call spans carry tokens and cost (ADR 0021). Only for calls no gateway records, e.g. Claude
/// Code on a subscription; with a LiteLLM gateway, usage stays on its generations (ADR 0012).</param>
public sealed record TelemetrySettings(string OtlpEndpoint, string PublicKeySecret, string SecretKeySecret, bool UsageOnSpans = false);
