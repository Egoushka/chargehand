using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>Where a task of a batch stands (ADR 0043). <c>Started</c>: the run id, branch and container are fixed; <c>Finished</c>: the session ended and its output was read, the handover may be partial;
/// <c>HandedOver</c>: a pull request opened; <c>Failed</c>: ended without one. The last two carry the task's outcome.</summary>
public enum TaskPhase { Pending, Started, Finished, HandedOver, Failed }

/// <param name="Continuation">How many continuation sessions have started (ADR 0044); <c>RunId</c> and <c>Container</c> are then the latest session's.</param>
/// <param name="Prior">The earlier sessions' usage added up; null before the first continuation.</param>
/// <param name="Runs">Run ids of all the task's sessions, the first one's first.</param>
/// <param name="SessionStarted">When the latest session started: its wall clock counts from here, while <c>Started</c> is the task's.</param>
/// <param name="Best">The latest session whose bundle holds commits: what a continuation is seeded from and a partial handover hands over.</param>
/// <param name="SessionGoal">The latest session's prompt when it is a continuation's (the goal plus the note); null: the task's goal.</param>
/// <param name="StartDetail">The <c>container_started</c> text of a continuation, kept so a continuation redone after a restart says the same.</param>
/// <param name="From">The earlier batch's task this one continues (ADR 0045); the goal above already carries its questions and the answers.</param>
/// <param name="DependsOn">The tasks this one waits for (ADR 0049), as the batch runs them: a planned chain's links included.</param>
/// <param name="Size">The size the governor holds the task to, after planning; null: the request's, else M.</param>
/// <param name="SplitFrom">The task the planning step split this one out of.</param>
/// <param name="SizeFrom"><c>plan</c> when <paramref name="Size"/> is the planning step's; null: the caller's.</param>
/// <param name="Runner">The runner the task was placed on (ADR 0058), written before anything starts there: a restarted server finds its sessions on that runner.</param>
/// <param name="Title">The request's title for the task's pull request; a planned split's parts have none.</param>
public sealed record TaskEntry(string Id, string? Ref, string Goal, TaskPhase Phase = TaskPhase.Pending, string? RunId = null, string? Branch = null, string? Container = null,
    DateTimeOffset? Started = null, TaskOutcome? Outcome = null, int Continuation = 0, SessionOutcome? Prior = null, IReadOnlyList<string>? Runs = null, DateTimeOffset? SessionStarted = null,
    BestBundle? Best = null, string? SessionGoal = null, string? StartDetail = null, ContinuedFrom? From = null, IReadOnlyList<string>? DependsOn = null, string? Size = null, string? SplitFrom = null, string? SizeFrom = null, string? Runner = null, string? Title = null);

/// <param name="Commits">Commits past the base the bundle holds; a continuation has to add to them to go on.</param>
public sealed record BestBundle(string RunId, int Commits);
