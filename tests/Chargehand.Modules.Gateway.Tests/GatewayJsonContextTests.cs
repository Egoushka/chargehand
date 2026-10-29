using System.Text.Json;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Modules.Gateway.Tests;

/// <summary>ADR 0060: the egress and model-endpoint log lines and the refusal body are written without reflection, byte for byte as the anonymous types wrote them.</summary>
public sealed class GatewayJsonContextTests
{
    // Characters the default encoder escapes, so a difference in encoders shows.
    private const string Text = "host <a> & \"quotes\" — ünïcode";

    private static readonly DateTimeOffset At = new(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData("run-1")]
    [InlineData(null)]
    public void An_egress_line_is_written_as_before(string? run)
    {
        var e = new EgressEvent(At, run, Text, 443, true, Text);
        var before = JsonSerializer.Serialize(new { at = e.At, run = e.RunId, host = e.Host, port = e.Port, allowed = e.Allowed, reason = e.Reason });

        Assert.Equal(before, JsonSerializer.Serialize(new EgressLogLine(e.At, e.RunId, e.Host, e.Port, e.Allowed, e.Reason), GatewayJsonContext.Default.EgressLogLine));
    }

    [Theory]
    [InlineData("run-1")]
    [InlineData(null)]
    public void A_model_endpoint_line_is_written_as_before(string? run)
    {
        var e = new GatewayEvent(At, run, 429, 12345678901, Text);
        var before = JsonSerializer.Serialize(new { at = e.At, model_run = e.RunId, status = e.Status, tokens = e.Tokens, reason = e.Reason });

        Assert.Equal(before, JsonSerializer.Serialize(new GatewayLogLine(e.At, e.RunId, e.Status, e.Tokens, e.Reason), GatewayJsonContext.Default.GatewayLogLine));
    }

    [Fact]
    public void A_fallback_call_adds_two_fields_and_no_other_line_does()
    {
        var e = new GatewayEvent(At, "run-1", 200, 50, "", Fallback: true, Usd: 0.0123m);
        Assert.Equal($"{{\"at\":{JsonSerializer.Serialize(At)},\"model_run\":\"run-1\",\"status\":200,\"tokens\":50,\"reason\":\"\",\"fallback\":true,\"usd\":0.0123}}",
            JsonSerializer.Serialize(new GatewayLogLine(e.At, e.RunId, e.Status, e.Tokens, e.Reason, e.Fallback, e.Usd), GatewayJsonContext.Default.GatewayLogLine));
    }

    [Fact]
    public void A_refusal_body_is_written_as_before()
    {
        const string type = "rate_limit_error";
        const string message = Text;
        var before = JsonSerializer.Serialize(new { type = "error", error = new { type, message } });

        Assert.Equal(before, JsonSerializer.Serialize(new GatewayError("error", new GatewayErrorDetail(type, message)), GatewayJsonContext.Default.GatewayError));
    }
}
