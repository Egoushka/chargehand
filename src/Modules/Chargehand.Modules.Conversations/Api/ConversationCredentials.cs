using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Conversations.Core;

namespace Chargehand.Modules.Conversations.Api;

/// <summary>The pause on a conversation runtime's credential (ADR 0053's rule, for conversations): a conversation that ends because the provider rejected the agent's key
/// pauses the key, and a new conversation on it is refused <c>credential_paused</c> (503). A rejected key is not probed, as the server's model credential is: after
/// <see cref="Retry"/> one conversation is let through, and a turn of it that works resumes the key.</summary>
/// <param name="report">Pauses or resumes a key and tells the owner once per change (<c>RunService.ReportCredentialAsync</c>); a null error means the credential worked.</param>
public sealed class ConversationCredentials(CredentialHealth health, Func<string, string?, CancellationToken, Task> report, TimeProvider? clock = null)
{
    /// <summary>How long a paused key refuses conversations before one is let through to find out whether it works again.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, DateTimeOffset> _trials = [];

    /// <summary>The refusal to start a conversation on <paramref name="key"/>, or null when it is not paused or its retry is due.</summary>
    public AccountRefusal? Refusal(string key)
    {
        if (health.Paused(key) is not { } pause)
            return null;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        lock (_trials)
        {
            if (now - _trials.GetValueOrDefault(key, pause.Since) >= Retry)
            {
                _trials[key] = now;
                return null;
            }
        }

        return new("credential_paused", $"the credential the {key} runtime uses was rejected at {pause.Since:u} ({pause.Reason}); conversations on it are admitted again once one works",
            ServiceUnavailable);
    }

    /// <summary>What a conversation on <paramref name="key"/> shows of the credential.</summary>
    internal ICredentialWatch Watch(string key) => new KeyWatch(this, key);

    private const int ServiceUnavailable = 503;

    private void Rejected(string key, string error)
    {
        if (CredentialHealth.Rejected(error))
            _ = report(key, error, CancellationToken.None);
    }

    private void Worked(string key)
    {
        if (health.Paused(key) is null)
            return;
        lock (_trials)
            _trials.Remove(key);
        _ = report(key, null, CancellationToken.None);
    }

    private sealed class KeyWatch(ConversationCredentials owner, string key) : ICredentialWatch
    {
        public void Rejected(string error) => owner.Rejected(key, error);

        public void Worked() => owner.Worked(key);
    }
}
