using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Server;

/// <summary>What a driven session's token allows (ADR 0039): calls on one repository at one commit, capped in dollars and tokens, until it expires.</summary>
/// <param name="RunId">The batch task's run; runs the session starts record it as their parent, and their spend is charged to it.</param>
/// <param name="MaxUsd">Null: no dollar cap (a subscription has none).</param>
/// <param name="Actor">The account that started the batch (ADR 0050); the runs the session starts belong to it. Null on a token from before accounts: the owner.</param>
public sealed record RunTokenClaims(string RunId, string RepositoryPath, string Commit, decimal? MaxUsd, long? MaxTokens, DateTimeOffset Expires, string? Actor = null);

/// <summary>Issues and checks run-scoped tokens: <c>chr1.&lt;payload&gt;.&lt;signature&gt;</c>, an HMAC-SHA256 over the payload with a key only this server
/// holds. A token is not stored; it dies when it expires, or when the server restarts without a key file (<see cref="LoadOrCreate"/> keeps the key, so a batch's sessions go on across a restart, ADR 0043).</summary>
public sealed class RunTokenService(byte[] key)
{
    public const string Prefix = "chr1";

    private const int MaxLength = 2048;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>A service with a fresh random key.</summary>
    public RunTokenService() : this(RandomNumberGenerator.GetBytes(32))
    {
    }

    /// <summary>A service whose key is kept in <paramref name="file"/> (created readable by its owner only), so tokens issued before a restart still validate after it.</summary>
    public static RunTokenService LoadOrCreate(string file)
    {
        try
        {
            if (File.Exists(file) && Convert.FromHexString(File.ReadAllText(file).Trim()) is { Length: 32 } stored)
                return new RunTokenService(stored);
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            // A damaged or unreadable key file must not keep the server from starting: a new key only costs the tokens already issued.
        }
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temp = $"{file}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(Encoding.ASCII.GetBytes(Convert.ToHexString(key)));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, file, overwrite: true);
        return new RunTokenService(key);
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

/// <summary>How a call's commit relates to its task's commit in the server's checkout of the repository.</summary>
public enum CommitRelation
{
    /// <summary>The task's commit, or a descendant of it.</summary>
    Descendant,

    /// <summary>A commit the server's checkout does not have: the session's own commit, which never left its container.</summary>
    Unknown,

    /// <summary>A commit the checkout has that does not descend from the task's commit.</summary>
    Unrelated,
}

/// <summary>The rules a token's calls must meet (ADR 0039). The presets are read-only ones: a session may research and review through chargehand,
/// never write, never start a batch, never reach another repository or a commit outside its task's line, and never spend past its task's cap.</summary>
public static class RunScope
{
    /// <summary>Where the middleware leaves a request's token claims.</summary>
    public const string ItemKey = "chargehand.run-token";

    private static readonly string[] AllowedPresets = ["default", "review"];

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);

    /// <summary><see cref="Apply"/> with the call's commit related to the task's through git in the task's repository.</summary>
    public static async Task<(RunRequest? Request, string? Refusal)> ApplyAsync(RunTokenClaims claims, RunRequest request, RunTokenLedger ledger, CancellationToken ct) =>
        Apply(claims, request, ledger, request.Context.Repository is { } repository ? await RelateAsync(claims, repository.Commit, ct) : CommitRelation.Unrelated);

    /// <param name="relation">How the call's commit relates to the task's; unused when they are the same commit.</param>
    public static (RunRequest? Request, string? Refusal) Apply(RunTokenClaims claims, RunRequest request, RunTokenLedger ledger, CommitRelation relation = CommitRelation.Unrelated)
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
        {
            if (relation == CommitRelation.Unrelated)
                return (null, "a session's calls are limited to the commit of its task and its descendants");
            // The session's own commits stay in its container: research and review run on the task's commit, and a review takes the diff as input.
            if (relation == CommitRelation.Unknown)
                request = request with { Context = request.Context with { Repository = repository with { Commit = claims.Commit } } };
        }
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

    /// <summary>How <paramref name="commit"/> relates to the token's commit in the token's repository; a failed git call counts as unrelated.</summary>
    public static async Task<CommitRelation> RelateAsync(RunTokenClaims claims, string commit, CancellationToken ct)
    {
        if (string.Equals(commit, claims.Commit, StringComparison.OrdinalIgnoreCase))
            return CommitRelation.Descendant;
        if (!Directory.Exists(claims.RepositoryPath) || commit.Length is < 4 or > 64 || !commit.All(char.IsAsciiHexDigit))
            return CommitRelation.Unrelated;
        try
        {
            var env = GitProcess.Environment(Path.GetTempPath());
            var known = await GitProcess.RunAsync(claims.RepositoryPath, env, ["cat-file", "-e", $"{commit}^{{commit}}"], GitTimeout, ct);
            if (known.ExitCode != 0)
                return CommitRelation.Unknown;
            var ancestor = await GitProcess.RunAsync(claims.RepositoryPath, env, ["merge-base", "--is-ancestor", claims.Commit, commit], GitTimeout, ct);
            return ancestor.ExitCode == 0 ? CommitRelation.Descendant : CommitRelation.Unrelated;
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CommitRelation.Unrelated;
        }
    }
}
