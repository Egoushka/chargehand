namespace Chargehand.Automation;

/// <summary>A task that waits for a person (ADR 0077), as the inbox lists it: a question (the task ended <c>needs_input</c>) or a partial stop (it stopped before it finished and kept its work).</summary>
/// <param name="Id">The inbox item's id; a notice is sent once per id.</param>
/// <param name="Kind"><see cref="QuestionKinds.Question"/> or <see cref="QuestionKinds.Partial"/>.</param>
/// <param name="Questions">What the session asked; empty for a partial stop.</param>
/// <param name="PullRequest">The draft pull request of a partial stop, when it opened one.</param>
/// <param name="Round">How many times it was sent again because a decision carried no answer; a sink that cannot reopen a decided notice sends it under a new id.</param>
public sealed record QuestionNotice(string Id, string Kind, string BatchRunId, string TaskId, string? Repository, string Title, IReadOnlyList<string> Questions, DateTimeOffset At,
    string? PullRequest = null, int Round = 0);

public static class QuestionKinds
{
    public const string Question = "question";
    public const string Partial = "partial";
}

/// <summary>The answers a person can give (ADR 0077). Each resumes the task through <c>continue_from</c> except <see cref="Drop"/>, which dismisses the inbox item.</summary>
public static class AnswerActions
{
    /// <summary>A question: continue with the text the person typed as the answer.</summary>
    public const string Answer = "answer";

    /// <summary>A question: continue and let the session decide every open question itself.</summary>
    public const string Decide = "decide";

    /// <summary>A partial stop: continue from the work it kept, with the typed text as a note when there is one.</summary>
    public const string Continue = "continue";

    /// <summary>Either: nothing starts; the inbox item is dismissed.</summary>
    public const string Drop = "drop";

    /// <summary>The answer given for <see cref="Decide"/>: the session's goal carries it as the person's answer.</summary>
    public const string DecideText = "Decide each open question yourself, choosing the option that changes the least, and say in the pull request what you chose and why.";

    public static IReadOnlyList<string> For(string kind) => kind == QuestionKinds.Question ? [Answer, Decide, Drop] : [Continue, Drop];
}

/// <summary>The open notices: the server reads them from its inbox.</summary>
public interface IQuestionSource
{
    Task<IReadOnlyList<QuestionNotice>> OpenAsync(CancellationToken ct);
}

/// <summary>Where a notice goes: an approvals server, an ntfy topic.</summary>
public interface IQuestionSink
{
    /// <summary>The sink's name in warnings and in the notifier's state.</summary>
    string Name { get; }

    /// <exception cref="IntakeException">It was not delivered; it is sent again at a later wake. The text never names a URL or a secret.</exception>
    Task SendAsync(QuestionNotice notice, CancellationToken ct);

    /// <summary>The notice left the inbox (answered on the console, superseded, dismissed): take it back where the sink can.</summary>
    /// <exception cref="IntakeException">It was not taken back; it is tried again at a later wake.</exception>
    Task WithdrawAsync(QuestionNotice notice, CancellationToken ct);
}
