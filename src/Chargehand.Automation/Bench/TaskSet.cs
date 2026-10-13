using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Chargehand.Bench;

/// <summary>A benchmark task set (<c>bench-taskset/v1</c>): public repositories pinned by commit, and the tasks to run on them (ADR 0050). The goals are the only text a session
/// sees. The acceptance check of each task is a command chargehand runs itself on the task's branch after the batch; no session reads it.</summary>
public sealed record TaskSet(
    string Schema,
    string Name,
    string? Description,
    IReadOnlyList<BenchRepository> Repositories,
    IReadOnlyList<BenchTask> Tasks)
{
    public const string SchemaId = "bench-taskset/v1";

    /// <summary>Where the file was read from; relative acceptance scripts resolve against its directory. Not part of the file.</summary>
    [JsonIgnore]
    public string? Directory { get; init; }

    /// <summary>sha256 of the file's bytes, so two results can say whether they ran the same set. Not part of the file.</summary>
    [JsonIgnore]
    public string Sha256 { get; init; } = "";
}

/// <param name="Name">The checkout's folder name under the work directory, and the <c>{name}</c> of a remote template.</param>
/// <param name="Ecosystem">dotnet, node, python, go, rust, gradle, maven or other: how the report groups the task.</param>
/// <param name="Url">The public upstream, https only, no credentials.</param>
/// <param name="Commit">The full 40-hex commit every task of the repository starts from.</param>
/// <param name="License">The upstream's SPDX id, so the set records that the repository may be used.</param>
/// <param name="Test">The repository's test command as an argument vector (<c>context.verify</c>): chargehand runs it before a pull request is opened.</param>
public sealed record BenchRepository(string Name, string Ecosystem, string Url, string Commit, string License, IReadOnlyList<string> Test);

/// <param name="Kind"><c>change</c>: the goal can be done and the acceptance check says whether it was. <c>impossible</c>: the goal contradicts itself or the code, and the right ending is
/// an honest stop; its acceptance is a guard that must still pass on any branch the session leaves. <c>question</c>: the goal leaves a decision open; the session should ask, chargehand answers
/// with <see cref="BenchTask.Answers"/> in a second batch, and the acceptance check runs on the result.</param>
/// <param name="Size">S, M or L: the budget class the governor holds the task to.</param>
public sealed record BenchTask(string Id, string Repository, string Kind, string Size, string Goal, BenchAcceptance Acceptance, IReadOnlyList<string>? Answers = null)
{
    public const string Change = "change", Impossible = "impossible", Question = "question";
}

/// <summary>A command that must exit 0 on the task's branch, in a fresh container, run from the checkout's root. <c>Script</c> is a shell script in a file next to the task set
/// (run as <c>sh -c &lt;its text&gt;</c>), for the checks that write a test file of their own; <c>Argv</c> is a plain command. The text <c>{base}</c> in either is the pinned commit.</summary>
public sealed record BenchAcceptance(string? Script = null, IReadOnlyList<string>? Argv = null, int? TimeoutMinutes = null);

public static partial class TaskSetLoader
{
    public static readonly IReadOnlySet<string> Ecosystems = new HashSet<string>(StringComparer.Ordinal) { "dotnet", "node", "python", "go", "rust", "gradle", "maven", "other" };

    /// <summary>The longest goal; a goal is a task, not a specification.</summary>
    public const int MaxGoalLength = 4000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>Reads and validates a task set file. <paramref name="problems"/> is empty when the set is usable; the set is null only when the file cannot be read as one.</summary>
    public static TaskSet? Load(string path, out IReadOnlyList<string> problems)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems = [$"{path}: {e.Message}"];
            return null;
        }
        TaskSet? set;
        try
        {
            set = JsonSerializer.Deserialize<TaskSet>(bytes, Options);
        }
        catch (JsonException e)
        {
            problems = [$"{path}: not a task set: {e.Message}"];
            return null;
        }
        if (set is null)
        {
            problems = [$"{path}: empty"];
            return null;
        }
        set = set with { Repositories = set.Repositories ?? [], Tasks = set.Tasks ?? [] } with { Directory = Path.GetDirectoryName(Path.GetFullPath(path)), Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        problems = Validate(set);
        return set;
    }

    /// <summary>Every rule at once, so a person fixes the file in one pass. Needs <see cref="TaskSet.Directory"/> to check that acceptance scripts exist; without it only the
    /// rest is checked.</summary>
    public static IReadOnlyList<string> Validate(TaskSet set)
    {
        List<string> problems = [];
        if (set.Schema != TaskSet.SchemaId)
            problems.Add($"schema must be '{TaskSet.SchemaId}'");
        if (string.IsNullOrEmpty(set.Name) || !Name().IsMatch(set.Name))
            problems.Add("name must be lower case letters, digits, '.', '_' or '-', at most 64 characters");
        if (set.Repositories.Count == 0)
            problems.Add("the set has no repositories");
        if (set.Tasks.Count == 0)
            problems.Add("the set has no tasks");

        foreach (var repo in set.Repositories)
        {
            var at = $"repository '{repo.Name}'";
            if (string.IsNullOrEmpty(repo.Name) || !Name().IsMatch(repo.Name))
                problems.Add($"{at}: name must be lower case letters, digits, '.', '_' or '-', at most 64 characters");
            if (!Ecosystems.Contains(repo.Ecosystem ?? ""))
                problems.Add($"{at}: ecosystem must be one of {string.Join(", ", Ecosystems.Order())}");
            if (!PublicUrl(repo.Url, out var urlProblem))
                problems.Add($"{at}: url {urlProblem}");
            if (string.IsNullOrEmpty(repo.Commit) || !FullCommit().IsMatch(repo.Commit))
                problems.Add($"{at}: commit must be the full 40-character lower case hex id; a branch or tag moves");
            if (string.IsNullOrWhiteSpace(repo.License))
                problems.Add($"{at}: license is required (the upstream's SPDX id)");
            if (repo.Test is not { Count: > 0 } || repo.Test.Any(string.IsNullOrWhiteSpace))
                problems.Add($"{at}: test must be a command as a list of non-empty arguments");
            if (set.Tasks.All(t => t.Repository != repo.Name))
                problems.Add($"{at}: no task uses it");
        }
        problems.AddRange(set.Repositories.GroupBy(r => r.Name).Where(g => g.Count() > 1).Select(g => $"repository name '{g.Key}' is used more than once"));

        foreach (var task in set.Tasks)
        {
            var at = $"task '{task.Id}'";
            if (string.IsNullOrEmpty(task.Id) || !TaskId().IsMatch(task.Id))
                problems.Add($"{at}: id must be lower case letters, digits, '.', '_' or '-', starting with a letter or digit, at most 64 characters");
            if (set.Repositories.All(r => r.Name != task.Repository))
                problems.Add($"{at}: repository '{task.Repository}' is not in the set");
            if (task.Kind is not (BenchTask.Change or BenchTask.Impossible or BenchTask.Question))
                problems.Add($"{at}: kind must be change, impossible or question");
            if (task.Size is not ("S" or "M" or "L"))
                problems.Add($"{at}: size must be S, M or L");
            if (string.IsNullOrWhiteSpace(task.Goal))
                problems.Add($"{at}: goal is empty");
            else if (task.Goal.Length > MaxGoalLength)
                problems.Add($"{at}: goal is longer than {MaxGoalLength} characters");
            if (task.Kind == BenchTask.Question)
            {
                if (task.Answers is not { Count: > 0 } || task.Answers.Any(string.IsNullOrWhiteSpace))
                    problems.Add($"{at}: a question task needs answers, each non-empty");
            }
            else if (task.Answers is { Count: > 0 })
                problems.Add($"{at}: answers belong to a question task");
            problems.AddRange(AcceptanceProblems(set, task, at));
        }
        problems.AddRange(set.Tasks.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => $"task id '{g.Key}' is used more than once"));
        return problems;
    }

    private static IEnumerable<string> AcceptanceProblems(TaskSet set, BenchTask task, string at)
    {
        if (task.Acceptance is null)
        {
            yield return $"{at}: acceptance is missing";
            yield break;
        }
        var a = task.Acceptance;
        if ((a.Script is null) == (a.Argv is null))
            yield return $"{at}: acceptance needs exactly one of script and argv";
        if (a.Argv is not null && (a.Argv.Count == 0 || a.Argv.Any(string.IsNullOrWhiteSpace)))
            yield return $"{at}: acceptance argv must be a command as a list of non-empty arguments";
        if (a.TimeoutMinutes is < 1 or > 120)
            yield return $"{at}: acceptance timeout_minutes must be 1 to 120";
        if (a.Script is null)
            yield break;
        if (Path.IsPathRooted(a.Script) || a.Script.Split('/', '\\').Contains(".."))
            yield return $"{at}: acceptance script must be a relative path inside the task set's directory";
        else if (set.Directory is { } directory)
        {
            var file = Path.Combine(directory, a.Script);
            if (!File.Exists(file))
                yield return $"{at}: acceptance script {a.Script} does not exist";
            else if (string.IsNullOrWhiteSpace(File.ReadAllText(file)))
                yield return $"{at}: acceptance script {a.Script} is empty";
        }
    }

    /// <summary>The argument vector that runs a task's acceptance check, with <c>{base}</c> replaced by the pinned commit.</summary>
    public static IReadOnlyList<string> AcceptanceArgv(TaskSet set, BenchTask task, BenchRepository repo)
    {
        var a = task.Acceptance;
        if (a.Script is not null)
            return ["sh", "-c", File.ReadAllText(Path.Combine(set.Directory ?? "", a.Script)).Replace("{base}", repo.Commit, StringComparison.Ordinal)];
        return [.. a.Argv!.Select(x => x.Replace("{base}", repo.Commit, StringComparison.Ordinal))];
    }

    /// <summary>A public https upstream: no credentials, no query, a host that is not an address or a local name.</summary>
    internal static bool PublicUrl(string? url, out string problem)
    {
        problem = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            problem = "must be an https URL";
        else if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            problem = "must carry no credentials, query or fragment";
        else if (uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains('.') || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            problem = "must name a public host";
        return problem.Length == 0;
    }

    public static string ToJson(TaskSet set) => JsonSerializer.Serialize(set, Options);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex Name();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.-]{0,63}$")]
    private static partial Regex TaskId();

    [GeneratedRegex(@"^[0-9a-f]{40}$")]
    private static partial Regex FullCommit();
}
