using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Handover.Api;

/// <param name="Goal">The goal the fix session is given: the failing jobs and the ends of their logs.</param>
public sealed record CiFixRequest(IntegrationTarget Target, int Attempt, int MaxAttempts, IReadOnlyList<FailedJob> Failures, string Goal);

/// <param name="Pushed">The session's fix passed chargehand's own verification and its commits are on the pull request's branch.</param>
public sealed record CiFixResult(bool Pushed, string Detail);

/// <summary>Runs a short session on the pull request's branch and pushes what it fixed; supplied by <see cref="TaskRunner"/>, which owns the containers.</summary>
public delegate Task<CiFixResult> CiFixSession(CiFixRequest request, CancellationToken ct);

/// <summary>One look at a pull request during <see cref="Integrator.FollowAsync"/>: the checks as the forge listed them, and what the CI fixer's policy made of them.</summary>
/// <param name="Decision"><c>wait</c>, <c>done</c>, <c>fix</c> or <c>give_up</c> (<see cref="CiFixAction"/>), or <c>merged</c> or <c>closed</c> for the look that ended the watch.</param>
public sealed record CheckObservation(DateTimeOffset At, string HeadSha, string MergeStatus, bool IsDraft, bool AutoMerge, IReadOnlyList<CheckStatus> Checks, string Decision);

/// <summary>A CI fix attempt as the integrator ran it: the failing required checks it was for, and whether its commits reached the branch.</summary>
public sealed record CiAttemptObservation(DateTimeOffset At, int Attempt, int MaxAttempts, string JobsKey, IReadOnlyList<string> Failing, bool Pushed, string Detail);

/// <summary>Told what <see cref="Integrator.FollowAsync"/> sees, so it can be kept (CHARGEHAND-214). Nothing it does depends on the callbacks.</summary>
public sealed record IntegrationWatch(Action<CheckObservation>? Checks = null, Action<CiAttemptObservation>? Attempt = null);
