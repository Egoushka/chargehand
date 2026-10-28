using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Conversations.Core;

/// <summary>What a caller may start and say (ADR 0050, ADR 0086): the account's rules for a run, applied to a conversation at its start and again at each message, and the
/// conversation's own limit on messages. A refusal is an <see cref="AccountRefusal"/>, so the host answers it as it answers a refused run. Pure: the caller reads the spend.</summary>
internal static class ConversationAdmission
{
    /// <summary>An account's open conversations count against <c>max_concurrent_runs</c> as unfinished runs do; its repositories, credential and spend windows apply as they do to a run.</summary>
    public static AccountRefusal? AtStart(Principal? who, string repository, string commit, string? preset, int open, ActorSpend day, ActorSpend month, DateTimeOffset now) =>
        who?.Account is { } account ? AccountPolicy.Refusal(account, Request(repository, commit, preset), open, day, month, now) : null;

    /// <summary>The conversation's limit first, then the account's windows. The concurrency limit is not asked again: the conversation is the one the account already holds.</summary>
    public static AccountRefusal? AtMessage(Principal? who, ConversationSnapshot conversation, int queued, ConversationCaps caps, ActorSpend day, ActorSpend month, DateTimeOffset now)
    {
        if (conversation.Messages + queued >= caps.MaxMessages)
            return new("conversation_cap_exceeded", $"this conversation has taken {caps.MaxMessages} messages, its limit; start another", AccountRefusal.TooManyRequests);
        return who?.Account is { } account
            ? AccountPolicy.Refusal(account, Request(conversation.Repository.Path, conversation.Repository.Commit, conversation.Preset), 0, day, month, now)
            : null;
    }

    private static RunRequest Request(string repository, string commit, string? preset) =>
        new("request/v1", "conversation", new RequestContext(false, preset ?? "conversation", Repository: new RepositoryRef(repository, commit)));
}
