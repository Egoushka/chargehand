using System.Net.Http.Headers;
using System.Text.Json;

namespace Chargehand.Modules.Forge.Api;

/// <summary>Whether a branch's rules make the forge wait for CI before a merge. <c>Unknown</c>: the rules could not be read, which chargehand treats as <c>None</c>.</summary>
public enum ChecksGate { Required, None, Unknown }

/// <summary>Reads a base branch's rules (CHARGEHAND-244, CHARGEHAND-245). Auto-merge waits only for required checks, so on a branch without them a pull request lands before CI runs:
/// the integrator then asks instead of merging, and the handover does not let the forge's CI stand in for a test command it could not find.</summary>
public interface IBranchRules
{
    /// <summary>Never throws for a refusal or an unreachable forge: that is <see cref="ChecksGate.Unknown"/>.</summary>
    Task<ChecksGate> ChecksGateAsync(RemoteRepository repository, string branch, CancellationToken ct);
}

/// <summary>GitHub's two places for a required check: an active ruleset's <c>required_status_checks</c> rule (<c>GET rules/branches/{branch}</c>), and classic branch protection
/// (<c>protection.required_status_checks</c> of <c>GET branches/{branch}</c>). Both need only read access to the repository's metadata.</summary>
internal static class GitHubBranchRules
{
    public static async Task<ChecksGate> ReadAsync(HttpClient http, string token, RemoteRepository repository, string branch, CancellationToken ct)
    {
        var repo = $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}";
        var rules = await GetAsync(http, token, $"{repo}/rules/branches/{Uri.EscapeDataString(branch)}", ct);
        var protection = rules is null ? null : await GetAsync(http, token, $"{repo}/branches/{Uri.EscapeDataString(branch)}", ct);
        if (rules is null || protection is null)
            return ChecksGate.Unknown;
        try
        {
            return RulesetRequires(rules) || ProtectionRequires(protection) ? ChecksGate.Required : ChecksGate.None;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return ChecksGate.Unknown;
        }
    }

    /// <summary>A <c>required_status_checks</c> rule that names at least one check.</summary>
    public static bool RulesetRequires(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.EnumerateArray().Any(rule => rule.TryGetProperty("type", out var type) && type.GetString() == "required_status_checks"
            && (!rule.TryGetProperty("parameters", out var p) || !p.TryGetProperty("required_status_checks", out var checks) || checks.GetArrayLength() > 0));
    }

    /// <summary>Classic protection that enforces at least one check.</summary>
    public static bool ProtectionRequires(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("protection", out var protection) || !protection.TryGetProperty("required_status_checks", out var checks) || checks.ValueKind != JsonValueKind.Object)
            return false;
        if (checks.TryGetProperty("enforcement_level", out var level) && level.GetString() == "off")
            return false;
        return checks.TryGetProperty("contexts", out var contexts) && contexts.GetArrayLength() > 0 || checks.TryGetProperty("checks", out var named) && named.GetArrayLength() > 0;
    }

    private static async Task<string?> GetAsync(HttpClient http, string token, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("chargehand", "1"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
