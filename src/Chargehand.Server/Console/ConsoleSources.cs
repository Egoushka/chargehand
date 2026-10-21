using System.Text.Json;
using Chargehand.Automation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <param name="Origin"><c>listed</c> (the profile's <c>repositories</c>) or <c>discovered</c> (the App can see it; its checkout is a managed clone).</param>
public sealed record IntakeSourceBody(string Repository, bool Enabled, string Origin, string Path);

/// <summary><c>intake-sources/v1</c>. <paramref name="Configured"/> is false when the profile has no <c>automation.github</c>; the list is then empty.</summary>
public sealed record IntakeSourcesBody(string ContractVersion, bool Configured, bool Discover, IReadOnlyList<IntakeSourceBody> Sources);

/// <summary>The on/off switch of each repository GitHub intake watches (CHARGEHAND-241), for the console: <c>GET /v1/intake/sources</c> lists them, <c>PUT</c> with
/// <c>{"sources": [{"repository": "owner/repo", "enabled": false}]}</c> sets some and answers the list. The owner's alone. A repository intake does not watch is refused, so a typo cannot pass for a switch.</summary>
internal static class ConsoleSources
{
    public const string Contract = "intake-sources/v1";

    private const int MaxChanges = 200;

    public static void Map(WebApplication app, GitHubIntake? intake)
    {
        app.MapGet("/v1/intake/sources", (HttpContext ctx) => ChargehandServer.OwnerOnly(ctx) ?? ChargehandServer.Json(Build(intake), StatusCodes.Status200OK));

        app.MapPut("/v1/intake/sources", async (HttpContext ctx) =>
        {
            if (ChargehandServer.OwnerOnly(ctx) is { } refused)
                return refused;
            if (intake is null)
                return ChargehandServer.Json(ChargehandServer.Problem("GitHub intake is not configured (the profile has no automation.github)"), StatusCodes.Status409Conflict);
            if (await ChangesAsync(ctx) is not { } changes)
                return ChargehandServer.Json(ChargehandServer.Problem($"the body must be {{\"sources\": [{{\"repository\": \"owner/repo\", \"enabled\": true|false}}]}} with 1 to {MaxChanges} entries"), StatusCodes.Status400BadRequest);
            var unknown = changes.Where(c => !intake.Watched.ContainsKey(c.Repository)).Select(c => c.Repository).ToList();
            if (unknown.Count > 0)
                return ChargehandServer.Json(ChargehandServer.Problem($"intake does not watch {string.Join(", ", unknown)}"), StatusCodes.Status404NotFound);
            try
            {
                foreach (var (repository, enabled) in changes)
                    intake.Sources.Set(repository, enabled);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ChargehandServer.Json(ChargehandServer.Problem($"the switches could not be written: {e.Message}"), StatusCodes.Status503ServiceUnavailable);
            }
            return ChargehandServer.Json(Build(intake), StatusCodes.Status200OK);
        });
    }

    public static IntakeSourcesBody Build(GitHubIntake? intake) => intake is null
        ? new(Contract, false, false, [])
        : new(Contract, true, intake.Discovers, [.. intake.Watched.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase)
            .Select(w => new IntakeSourceBody(w.Key, intake.Sources.IsOn(w.Key), w.Value.Discovered ? "discovered" : "listed", w.Value.Path))]);

    private static async Task<IReadOnlyList<(string Repository, bool Enabled)>?> ChangesAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
                return null;
            List<(string, bool)> changes = [];
            foreach (var entry in sources.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.String
                    || repository.GetString() is not { Length: > 0 } name || !entry.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return null;
                changes.Add((name, enabled.GetBoolean()));
            }
            return changes.Count is > 0 and <= MaxChanges ? changes : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
