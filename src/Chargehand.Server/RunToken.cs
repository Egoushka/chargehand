using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Server;

/// <summary>What a driven session's token allows (ADR 0039): calls on one repository at one commit, capped in dollars and tokens, until it expires.</summary>
/// <param name="RunId">The batch task's run; runs the session starts record it as their parent, and their spend is charged to it.</param>
/// <param name="MaxUsd">Null: no dollar cap (a subscription has none).</param>
public sealed record RunTokenClaims(string RunId, string RepositoryPath, string Commit, decimal? MaxUsd, long? MaxTokens, DateTimeOffset Expires);

/// <summary>Issues and checks run-scoped tokens: <c>chr1.&lt;payload&gt;.&lt;signature&gt;</c>, an HMAC-SHA256 over the payload with a key only this server
/// holds. A token is not stored; it dies when it expires or the server restarts (the batch it belongs to dies with the server then too).</summary>
public sealed class RunTokenService(byte[] key)
{
    public const string Prefix = "chr1";

    private const int MaxLength = 2048;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>A service with a fresh random key.</summary>
    public RunTokenService() : this(RandomNumberGenerator.GetBytes(32))
    {
    }

    public string Issue(RunTokenClaims claims, DateTimeOffset now)
    {
        if (claims.Expires <= now)
            throw new ArgumentException("a token must expire in the future", nameof(claims));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims, Json));
        return $"{Prefix}.{payload}.{Base64Url(Sign(payload))}";
    }

    /// <returns>The claims, or null for anything that is not a live token of this server.</returns>
    public RunTokenClaims? Validate(string token, DateTimeOffset now)
    {
        if (token.Length is 0 or > MaxLength)
            return null;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Prefix)
            return null;
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(Sign(parts[1]), FromBase64Url(parts[2])))
                return null;
            var claims = JsonSerializer.Deserialize<RunTokenClaims>(FromBase64Url(parts[1]), Json);
            return claims is not null && claims.Expires > now ? claims : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private byte[] Sign(string payload) => HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{Prefix}.{payload}"));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}

/// <summary>What the runs a token started have cost so far, per parent run.</summary>
public sealed class RunTokenLedger
{
    private readonly ConcurrentDictionary<string, (decimal Usd, long Tokens)> _spent = new();

    public void Charge(string runId, decimal usd, long tokens) =>
        _spent.AddOrUpdate(runId, (usd, tokens), (_, s) => (s.Usd + usd, s.Tokens + tokens));

    public (decimal Usd, long Tokens) Spent(string runId) => _spent.GetValueOrDefault(runId);
}

/// <summary>The rules a token's calls must meet (ADR 0039). The presets are read-only ones: a session may research and review through chargehand,
/// never write, never start a batch, never reach another repository or commit, and never spend past its task's cap.</summary>
public static class RunScope
{
    /// <summary>Where the middleware leaves a request's token claims.</summary>
    public const string ItemKey = "chargehand.run-token";

    private static readonly string[] AllowedPresets = ["default", "review"];

    public static (RunRequest? Request, string? Refusal) Apply(RunTokenClaims claims, RunRequest request, RunTokenLedger ledger)
    {
        if (request.Driven is not null)
            return (null, "a session cannot start a batch");
        if (!AllowedPresets.Contains(request.Context.Preset))
            return (null, $"preset '{request.Context.Preset}' is not open to a session's own calls (default and review only)");
        if (request.Context.Repository is not { } repository)
            return (null, "a session's call must name a repository");
        if (Path.TrimEndingDirectorySeparator(repository.Path) != Path.TrimEndingDirectorySeparator(claims.RepositoryPath))
            return (null, "a session's calls are limited to the repository of its task");
        if (!string.Equals(repository.Commit, claims.Commit, StringComparison.OrdinalIgnoreCase))
            return (null, "a session's calls are limited to the commit of its task");
        var (usd, tokens) = ledger.Spent(claims.RunId);
        if (claims.MaxUsd is { } maxUsd && usd >= maxUsd)
            return (null, "the task's spend cap is used up");
        if (claims.MaxTokens is { } maxTokens && tokens >= maxTokens)
            return (null, "the task's token cap is used up");
        if (claims.MaxUsd is { } cap)
        {
            var remaining = cap - usd;
            request = request with { Context = request.Context with { BudgetUsd = Math.Min(request.Context.BudgetUsd ?? remaining, remaining) } };
        }
        return (request, null);
    }
}
