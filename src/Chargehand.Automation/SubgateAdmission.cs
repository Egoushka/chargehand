using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Chargehand.Automation;

/// <summary>Asks subgate (<c>POST /v1/admit</c>) before a start that runs on the subscription, so interactive sessions and the pool share one allowance. A hold is remembered until its retry time,
/// so a queue of waiting starts asks once. It fails open: a subgate that cannot be reached, or answers something unexpected, never stops a start. <c>api</c> and <c>cheap</c> are logged; routing on
/// them is not built yet.</summary>
public sealed class SubgateAdmission(HttpClient http, string consumer, TimeProvider? clock = null, Action<string>? warn = null)
{
    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private DateTimeOffset? _heldUntil;
    private bool _unreachable;

    /// <summary>When the start may be asked again: null when subgate admits it, is unreachable or says <c>api</c> or <c>cheap</c>.</summary>
    public async Task<DateTimeOffset?> HoldUntilAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_heldUntil is { } cached && cached > now)
            return cached;
        _heldUntil = null;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["consumer"] = consumer }), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(new Uri("v1/admit", UriKind.Relative), content, ct);
            if (!response.IsSuccessStatusCode)
                return FailOpen($"answered {(int)response.StatusCode}");
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var decision = body.RootElement.TryGetProperty("decision", out var d) ? d.GetString() : null;
            if (decision is "allow" or "hold" or "api" or "cheap")
                _unreachable = false;
            switch (decision)
            {
                case "allow":
                    return null;
                case "hold":
                    var seconds = body.RootElement.TryGetProperty("retry_after_s", out var r) && r.ValueKind == JsonValueKind.Number ? Math.Max(1, r.GetInt64()) : 300;
                    return _heldUntil = now.AddSeconds(seconds);
                case "api" or "cheap":
                    warn?.Invoke($"subgate: decision '{decision}' is not routed yet; the start goes on");
                    return null;
                default:
                    return FailOpen($"sent decision '{decision}'");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return FailOpen(e.GetType().Name);
        }
    }

    private DateTimeOffset? FailOpen(string why)
    {
        if (!_unreachable)
            warn?.Invoke($"subgate: {why}; starts go on without it");
        _unreachable = true;
        return null;
    }
}
