using Chargehand.Modules.Accounts.Api;

namespace Chargehand.Modules.Conversations.Api;

/// <summary>Why a conversation call did not do what was asked. A host maps the kind to a status: 404, 400, 409, the refusal's own (403 or 429), 502.</summary>
public enum ProblemKind { NotFound, Invalid, Conflict, Refused, Unavailable }

/// <param name="Refusal">The account's refusal, with the body and status a refused run has (ADR 0050); only for <see cref="ProblemKind.Refused"/>.</param>
/// <param name="ConversationId">Set when the call made a conversation that then failed.</param>
public sealed record ConversationProblem(ProblemKind Kind, IReadOnlyList<string> Errors, AccountRefusal? Refusal = null, string? ConversationId = null);

/// <summary>A value, or the problem. An account that may not see a conversation gets <see cref="ProblemKind.NotFound"/>, as for a run.</summary>
public sealed record ConversationOutcome<T>(T? Value, ConversationProblem? Problem)
{
    public bool Ok => Problem is null;
}

/// <summary>Makes a <see cref="ConversationOutcome{T}"/>: a value, a problem, or the account's refusal.</summary>
public static class ConversationOutcome
{
    public static ConversationOutcome<T> Of<T>(T value) => new(value, null);

    public static ConversationOutcome<T> Fail<T>(ProblemKind kind, params string[] errors) => new(default, new(kind, errors));

    public static ConversationOutcome<T> Fail<T>(ConversationProblem problem) => new(default, problem);

    public static ConversationOutcome<T> Refuse<T>(AccountRefusal refusal) => new(default, new(ProblemKind.Refused, [refusal.Message], refusal));
}

/// <summary>What <c>POST /v1/conversations</c> takes.</summary>
public sealed record StartConversation(string? Repository, string? Commit, string? Runtime, string? Model = null, string? Preset = null);

/// <param name="Queued">The message waits for the running turn to end.</param>
public sealed record MessageAccepted(string ConversationId, bool Queued, int Messages);
