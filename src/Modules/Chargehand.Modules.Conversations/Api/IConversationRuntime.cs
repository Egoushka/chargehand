using Chargehand.Contracts;

namespace Chargehand.Modules.Conversations.Api;

/// <summary>The port to an agent a person can steer (ADR 0086). One implementation per way of reaching agents: the fake here, the ACP runtime later. It is keyed by the conversation's id,
/// which is the id of the agent's own session (ACP's <c>session</c>, which is not a driven session, ADR 0039). <see cref="IWorkerRuntime"/> stays for research workers.</summary>
public interface IConversationRuntime
{
    /// <summary>Whether <paramref name="runtime"/>, the <c>runtime</c> of a start request, names an agent this implementation reaches.</summary>
    bool Supports(string runtime);

    /// <summary>The credential a conversation on <paramref name="runtime"/> pays with, named for the pause check (<see cref="ConversationCredentials"/>); null: it needs none.</summary>
    string? CredentialKey(string runtime) => null;

    /// <summary>Opens the agent's side of the conversation. The checkout is the repository at the commit; it is read-only unless the person approves an edit.</summary>
    /// <exception cref="Exception">The agent could not start; the conversation then fails.</exception>
    Task<RuntimeStarted> StartAsync(RuntimeStart start, CancellationToken ct);

    /// <summary>What the agent does, from the first update after <see cref="StartAsync"/> until the conversation ends. Called once.</summary>
    IAsyncEnumerable<ConversationUpdate> UpdatesAsync(string conversationId, CancellationToken ct);

    /// <summary>Starts a turn with the person's message and returns once it is accepted; the turn's progress arrives as updates and ends with <see cref="TurnEnded"/>. Never called while a turn runs.</summary>
    Task PromptAsync(string conversationId, string text, CancellationToken ct);

    /// <summary>Stops the running turn, which then ends with <c>cancelled</c>. Nothing to do when none runs.</summary>
    Task CancelAsync(string conversationId, CancellationToken ct);

    /// <summary>Answers an <see cref="ApprovalRequested"/>: this once, or not at all (ADR 0006).</summary>
    Task AnswerPermissionAsync(string conversationId, string callId, PermissionAnswer answer, CancellationToken ct);

    /// <summary>Closes the agent's session. The updates then end.</summary>
    Task EndAsync(string conversationId, CancellationToken ct);
}

/// <param name="Model">The model the person asked for; null: the agent's own default.</param>
/// <param name="Preset">The preset the person named, if any; the runtime decides what it means for the agent.</param>
public sealed record RuntimeStart(string ConversationId, string Runtime, RepositoryRef Repository, string? Model, string? Preset);

/// <param name="Model">The model the agent settled on, when it says; null: not known.</param>
public sealed record RuntimeStarted(string? Model = null);

public enum PermissionAnswer { AllowOnce, Reject }

/// <summary>What a runtime tells the service. Most updates are an event the person's client shows; two are about the turn and the session.</summary>
public abstract record ConversationUpdate;

/// <summary>An event for the stream: a message chunk, a tool call or its update, an approval request, usage, or an error. A fatal <see cref="ErrorData"/> ends the conversation.</summary>
public sealed record Emitted(ConversationEventData Data) : ConversationUpdate;

/// <summary>The turn is over. The next message may start one.</summary>
/// <param name="StopReason">As ACP names it: <c>end_turn</c>, <c>cancelled</c>, <c>max_tokens</c>, <c>refusal</c>.</param>
public sealed record TurnEnded(string StopReason) : ConversationUpdate;

/// <summary>The agent's session is over, whatever the person did.</summary>
public sealed record RuntimeEnded : ConversationUpdate;
