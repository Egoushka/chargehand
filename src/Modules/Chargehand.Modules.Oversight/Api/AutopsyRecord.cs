namespace Chargehand.Modules.Oversight.Api;

/// <summary>The words a failure autopsy uses for why a driven task ended failed or partial (ADR 0045's sibling to the retro). A model's answer outside this list is kept as <see cref="Other"/>.</summary>
public static class AutopsyClass
{
    public const string Auth = "auth";
    public const string OomKill = "oom_kill";
    public const string Verification = "verification";
    public const string TurnLimitNoCommits = "turn_limit_no_commits";
    public const string SecretGuard = "secret_guard";
    public const string RepoPath = "repo_path";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Auth, OomKill, Verification, TurnLimitNoCommits, SecretGuard, RepoPath, Other];
}

/// <summary>One task attempt's failure autopsy: the short root-cause class a cheap model call put on why it ended failed or partial, next to its <see cref="LearningRecord"/> in the learning log.
/// Holds no goal or session text, like the record it is of.</summary>
/// <param name="RunId">The task attempt the autopsy is of; the latest line for a run id wins, like a <see cref="PrOutcomeRecord"/>.</param>
/// <param name="Class">An <see cref="AutopsyClass"/>.</param>
/// <param name="Detail">One short line from the model on why; null when it gave none.</param>
public sealed record AutopsyRecord(string RunId, DateTimeOffset At, string Class, string? Detail = null);
