using System.Collections.Concurrent;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Runtime;

namespace Chargehand.ClaudeCode;

/// <summary>The worker runtime a run of an account uses (ADR 0053): the server's own, or one over the account's API key (and gateway), made on the account's first run and kept. Each
/// account's runtime has its own sessions and its own environment, so no run sees another account's key.</summary>
/// <param name="secret">Resolves a secret item through the profile's sources.</param>
public sealed class AccountRuntimes(IWorkerRuntime server, AccountCredentials credentials, Func<string, string> secret)
{
    private readonly ConcurrentDictionary<string, IWorkerRuntime> _own = new();

    private const string NeedsClaudeCode = "accounts with their own model credential (http.accounts[].model) need the claude_code runtime: OpenCode holds its provider credentials in its own server";

    /// <summary>Why the profile's accounts cannot run on <paramref name="runtime"/>, or null: OpenCode keeps its provider credentials in its own server, so an account's key has nowhere to go.</summary>
    public static string? Unsupported(IWorkerRuntime runtime, IReadOnlyList<AccountSettings>? accounts) =>
        runtime is not ClaudeCodeWorkerRuntime && accounts?.Any(a => a.Model is not null) == true ? NeedsClaudeCode : null;

    /// <summary>The runtime for <paramref name="actor"/>. Throws <see cref="ChargehandException"/> (<c>credential_unavailable</c>) for an account that may use no credential or whose secret does not resolve.</summary>
    public IWorkerRuntime For(string? actor)
    {
        if (credentials.For(actor) is not { } model)
            return server;
        if (server is not ClaudeCodeWorkerRuntime claude)
            throw new ChargehandException(ErrorCode.CredentialUnavailable, NeedsClaudeCode, "Run the server on the claude_code runtime.");
        return _own.GetOrAdd(actor!, _ =>
        {
            try
            {
                return claude.WithCredential(new ClaudeCodeCredential(secret(model.ApiKeySecret!), Subscription: false), model.BaseUrl is null ? null : new Uri(model.BaseUrl));
            }
            catch (InvalidOperationException e)
            {
                throw new ChargehandException(ErrorCode.CredentialUnavailable, e.Message, $"Check the secret sources for http.accounts[].model.api_key_secret of account '{actor}'.");
            }
        });
    }
}
