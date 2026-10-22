using System.Globalization;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>What the readiness score of one repository is computed from.</summary>
/// <param name="Repository">The forge name (<c>owner/name</c>) or the checkout path; it names the repository in the proposed task.</param>
/// <param name="Entries">The repository's learning records.</param>
/// <param name="Verify">The command a driven task of the repository is verified with when the request names none (<c>driven.services.verify</c>); null: none set.</param>
/// <param name="Proposed">The command <c>chargehand init</c> proposes from the checkout's marker files; null: none.</param>
/// <param name="FirstCiPassed">Per task whose pull request's checks were read: whether the checks passed with no CI fix.</param>
/// <param name="HasCi">The checkout has a CI configuration; null: the checkout was not read.</param>
public sealed record ReadinessInput(string Repository, IReadOnlyList<LearningEntry> Entries, IReadOnlyList<string>? Verify, IReadOnlyList<string>? Proposed, IReadOnlyList<bool> FirstCiPassed,
    bool? HasCi);

/// <param name="Name"><c>completion</c>, <c>verify</c>, <c>first_ci</c>, <c>human_fix</c> or <c>ci</c>.</param>
/// <param name="Value">0 to 1, higher is readier; null: no data, so the signal does not count.</param>
/// <param name="Weight">Its share of the score when every signal has data.</param>
public sealed record ReadinessSignal(string Name, double? Value, int Weight, string Detail);

/// <param name="Score">0 to 100: the weighted mean of the signals that have data; null when none has.</param>
/// <param name="Below">The score is under <paramref name="Threshold"/>: feature work there is warned about, not refused.</param>
/// <param name="Gaps">What to fix, one line per weak signal; the proposed task's goal lists them.</param>
public sealed record Readiness(int? Score, int Threshold, bool Below, IReadOnlyList<ReadinessSignal> Signals, IReadOnlyList<string> Gaps)
{
    /// <summary>The goal of the "make this repository agent-ready" task, or null when there is no gap.</summary>
    public string? ProposalGoal(string repository) => Gaps.Count == 0 ? null
        : $"Make {repository} ready for coding agents. Fix these gaps, one commit each, and change no product behaviour:\n"
          + string.Concat(Gaps.Select(g => $"- {g}\n"))
          + "Write the commands you settle on in AGENTS.md (or the repository's CLAUDE.md) so the next session finds them.";
}

/// <summary>How ready a repository is for driven work: completion of its past tasks, a verification command that runs the tests, the share of pull requests whose checks passed first time,
/// the share a person changed before merging, and a CI configuration. Run data showed completion differs by repository far more than by model, so a weak repository is fixed first.
/// Pure: the same input gives the same score.</summary>
public static class RepositoryReadiness
{
    public const int Threshold = 60;

    /// <summary>Fewer finished tasks than this give no completion rate.</summary>
    public const int MinTasks = 3;

    private const double CompletionFloor = 0.6, FirstCiFloor = 0.7, HumanFixCeiling = 0.3;

    private static readonly string[] TestWords = ["test", "check", "spec", "jest", "vitest"];

    private static readonly string[] CiFiles = [".gitlab-ci.yml", ".circleci/config.yml", "azure-pipelines.yml", "Jenkinsfile", ".woodpecker.yml", "bitbucket-pipelines.yml"];

    private static readonly string[] CiDirectories = [".github/workflows", ".forgejo/workflows", ".gitea/workflows", ".woodpecker"];

    public static Readiness Score(ReadinessInput input)
    {
        List<string> gaps = [];
        ReadinessSignal[] signals = [Completion(input, gaps), Verification(input, gaps), FirstCi(input, gaps), HumanFix(input, gaps), Ci(input, gaps)];
        var known = signals.Where(s => s.Value is not null).ToList();
        int? score = known.Count == 0 ? null : (int)Math.Round(100 * known.Sum(s => s.Value!.Value * s.Weight) / known.Sum(s => s.Weight), MidpointRounding.AwayFromZero);
        return new Readiness(score, Threshold, score < Threshold, signals, gaps);
    }

    /// <summary>A command that runs tests rather than only building or formatting: one of its words names a test runner or a check.</summary>
    public static bool RunsTests(IReadOnlyList<string> command) =>
        command.Any(word => TestWords.Any(t => word.Contains(t, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The checkout has a CI configuration a forge runs.</summary>
    public static bool CiPresent(string directory) =>
        CiFiles.Any(f => File.Exists(Path.Combine(directory, f)))
        || CiDirectories.Any(d => Directory.Exists(Path.Combine(directory, d)) && Directory.EnumerateFiles(Path.Combine(directory, d)).Any(f => f.EndsWith(".yml", StringComparison.Ordinal) || f.EndsWith(".yaml", StringComparison.Ordinal)));

    private static ReadinessSignal Completion(ReadinessInput input, List<string> gaps)
    {
        var tasks = input.Entries.Count;
        var completed = input.Entries.Count(e => e.Record.Completed);
        if (tasks < MinTasks)
            return new("completion", null, 35, Invariant($"{tasks} driven {Plural(tasks, "task")}, too few to rate ({MinTasks} needed)"));
        var rate = (double)completed / tasks;
        if (rate < CompletionFloor)
            gaps.Add(Invariant($"{tasks - completed} of {tasks} driven tasks did not complete: document how to build, test and run the project, and anything a fresh checkout needs first."));
        return new("completion", rate, 35, Invariant($"{completed} of {tasks} driven tasks completed"));
    }

    private static ReadinessSignal Verification(ReadinessInput input, List<string> gaps)
    {
        if (input.Verify is { Count: > 0 } verify)
        {
            if (RunsTests(verify))
                return new("verify", 1, 20, $"driven tasks are verified with `{string.Join(' ', verify)}`");
            gaps.Add($"The verification command `{string.Join(' ', verify)}` builds but runs no tests: make the test suite runnable in one command inside a session (no local servers, no network).");
            return new("verify", 0.5, 20, $"`{string.Join(' ', verify)}` runs no tests");
        }
        if (input.Proposed is { Count: > 0 } proposed)
        {
            gaps.Add($"No verification command is set for driven tasks; `{string.Join(' ', proposed)}` is only a guess from marker files: make one command build and test the project inside a session.");
            return new("verify", 0.5, 20, $"none set; `{string.Join(' ', proposed)}` is proposed");
        }
        gaps.Add("No command builds and tests the project: add one (a script or a make target) that runs inside a session without network.");
        return new("verify", 0, 20, "no verification command");
    }

    private static ReadinessSignal FirstCi(ReadinessInput input, List<string> gaps)
    {
        var read = input.FirstCiPassed.Count;
        if (read == 0)
            return new("first_ci", null, 15, "no pull request's checks were read");
        var passed = input.FirstCiPassed.Count(p => p);
        var rate = (double)passed / read;
        if (rate < FirstCiFloor)
            gaps.Add(Invariant($"CI failed on {read - passed} of {read} pull requests before a fix: make the local check run what CI runs, so a session sees the failure before it pushes."));
        return new("first_ci", rate, 15, Invariant($"{passed} of {read} pull requests passed CI first time"));
    }

    private static ReadinessSignal HumanFix(ReadinessInput input, List<string> gaps)
    {
        var merged = input.Entries.Where(e => e.PrOutcome?.Outcome is PrOutcomes.MergedUnchanged or PrOutcomes.MergedEdited).ToList();
        if (merged.Count == 0)
            return new("human_fix", null, 15, "no merged pull request to compare");
        var edited = merged.Count(e => e.PrOutcome!.Outcome == PrOutcomes.MergedEdited);
        var share = (double)edited / merged.Count;
        if (share > HumanFixCeiling)
            gaps.Add(Invariant($"A person changed {edited} of {merged.Count} merged pull requests before merging: write the conventions those changes enforced into the agent instructions."));
        return new("human_fix", 1 - share, 15, Invariant($"{edited} of {merged.Count} merged pull requests needed a person's commit"));
    }

    private static ReadinessSignal Ci(ReadinessInput input, List<string> gaps)
    {
        if (input.HasCi is not { } present)
            return new("ci", null, 15, "the checkout was not read");
        if (!present)
            gaps.Add("No CI configuration: add a workflow that builds and tests every pull request.");
        return new("ci", present ? 1 : 0, 15, present ? "CI is configured" : "no CI configuration");
    }

    private static string Plural(int n, string word) => n == 1 ? word : word + "s";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
