using System.Text.RegularExpressions;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>A model credential the provider rejected: since when, and the provider's words.</summary>
public sealed record CredentialPause(DateTimeOffset Since, string Reason);

/// <summary>
/// A circuit breaker per model credential: the server's (<see cref="Server"/>) and each account's own (ADR 0053). A run that ends on a rejected
/// credential (401, revoked or expired token, invalid key) pauses admission for every run on that credential, instead of letting each queued run
/// fail the same way; a probe that answers resumes it. The caller is told of each change once, by the return value.
/// </summary>
public sealed partial class CredentialHealth
{
    /// <summary>The key of the server's own credential: the owner's runs and those of accounts allowed to use it.</summary>
    public const string Server = "server";

    private readonly Dictionary<string, CredentialPause> _paused = [];

    /// <summary>The credential a run of <paramref name="actor"/> uses: the account's name when it has a model credential of its own, else <see cref="Server"/>.</summary>
    public static string KeyOf(string? actor, IReadOnlyList<AccountSettings>? accounts) =>
        accounts?.FirstOrDefault(a => a.Name == actor) is { Model.ApiKeySecret: not null } own ? own.Name : Server;

    /// <summary>The provider refused the credential itself, not the request: a retry with the same credential fails the same way.</summary>
    public static bool Rejected(string? error) => error is not null && RejectedText().IsMatch(error);

    /// <summary>Why runs on <paramref name="key"/> are not admitted, or null when they are.</summary>
    public CredentialPause? Paused(string key)
    {
        lock (_paused)
            return _paused.GetValueOrDefault(key);
    }

    /// <summary>Every paused credential.</summary>
    public IReadOnlyDictionary<string, CredentialPause> All
    {
        get
        {
            lock (_paused)
                return new Dictionary<string, CredentialPause>(_paused);
        }
    }

    /// <summary>Pauses <paramref name="key"/> when <paramref name="error"/> says the credential was rejected. True only when this paused it.</summary>
    public bool Fail(string key, string? error, DateTimeOffset now)
    {
        if (!Rejected(error))
            return false;
        lock (_paused)
            return _paused.TryAdd(key, new CredentialPause(now, error!.Length > MaxReason ? error[..MaxReason] + "…" : error));
    }

    /// <summary>A probe on <paramref name="key"/> answered. True only when this resumed it.</summary>
    public bool Succeed(string key)
    {
        lock (_paused)
            return _paused.Remove(key);
    }

    private const int MaxReason = 300;

    // Claude Code: "Failed to authenticate. API Error: 401 OAuth access token has been revoked."; the API: authentication_error, invalid x-api-key.
    [GeneratedRegex(@"(api error|api status|status|http)\W{0,3}401\b|\brevoked\b|authentication_error|invalid x-api-key|invalid api key|token (has )?expired|failed to authenticate", RegexOptions.IgnoreCase)]
    private static partial Regex RejectedText();
}
