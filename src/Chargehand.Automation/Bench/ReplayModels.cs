using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Bench;

/// <summary>A merged pull request as the importer reads it (<c>replay-prs/v1</c> is a JSON array of these). <see cref="MergeCommit"/> is the squash or merge commit on the default branch;
/// its first parent is the state the pull request started from.</summary>
public sealed record ReplayPr(int Number, string Title, string? Body, string MergeCommit, IReadOnlyList<ReplayIssue>? Issues = null);

/// <summary>An issue the pull request closes; its text becomes part of the goal, scrubbed like the body.</summary>
public sealed record ReplayIssue(string? Title, string? Body);

/// <param name="Change"><c>write</c>: the file is put on the branch with the merged content. <c>delete</c>: the file is removed.</param>
public sealed record ReplayFile(string Path, string Change, string? Sha256)
{
    public const string Write = "write", Delete = "delete";
}

/// <param name="Projects">The test projects the oracle runs, found at the merged commit; the oracle is these projects' tests.</param>
/// <param name="Accept">The acceptance script, relative to the set's directory.</param>
public sealed record ReplayEntry(string Id, int PullRequest, string Parent, string Merge, IReadOnlyList<string> Projects, IReadOnlyList<ReplayFile> Oracle, string Accept);

/// <summary>What the importer knows about each task beyond the task set: the pull request, the merged commit and the oracle files. It sits next to <c>task-set.json</c> and, like the whole
/// directory, stays in the gitignored <c>runs/</c>: pull request numbers are real work.</summary>
public sealed record ReplayManifest(string Schema, string Name, string Url, IReadOnlyList<ReplayEntry> Tasks)
{
    public const string SchemaId = "replay-manifest/v1";
    public const string FileName = "replay.json";

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}

/// <summary>A pull request the importer left out, and why. The reasons are fixed words so a count by reason is meaningful.</summary>
public sealed record ReplayRejection(int PullRequest, string Reason, string Detail)
{
    public const string ReleasePr = "release_pr";
    public const string NoMergeParent = "merge_parent_unreachable";
    public const string NoTestFiles = "no_test_files";
    public const string NoSourceChange = "no_source_change";
    public const string NoTestProject = "no_test_project";
    public const string OracleTooLarge = "oracle_too_large";
    public const string UnsafePath = "unsafe_path";
    public const string GoalLeaksTests = "goal_names_a_test_file";
    public const string EmptyGoal = "empty_goal";
}

/// <param name="Scripts">Task id to the acceptance script text.</param>
/// <param name="OracleFiles">Task id, then repository path, to the merged content, kept so the oracle can be read without the repository.</param>
public sealed record ReplayImport(
    TaskSet Set,
    ReplayManifest Manifest,
    IReadOnlyDictionary<string, string> Scripts,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, byte[]>> OracleFiles,
    IReadOnlyList<ReplayRejection> Rejected);

/// <param name="RepositoryPath">A local clone that has every merge commit and its parent.</param>
/// <param name="Url">The public https upstream of the repository, written into the task set.</param>
public sealed record ReplayRequest(string RepositoryPath, string Url, string License, string SetName, IReadOnlyList<ReplayPr> PullRequests);
