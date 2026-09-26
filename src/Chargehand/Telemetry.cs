using System.Diagnostics;

namespace Chargehand;

/// <summary>Span source for run, node and call spans (ADR 0012). Usage lives on the gateway side only.</summary>
public static class Telemetry
{
    /// <summary>Pinned OpenTelemetry GenAI semantic-conventions version.</summary>
    public const string GenAiSemConvVersion = "1.44.0";

    public static readonly ActivitySource Source = new("Chargehand");
}
