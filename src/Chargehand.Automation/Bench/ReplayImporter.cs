using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Chargehand.Bench;

/// <summary>Turns merged pull requests into bench tasks (ADR 0087, cycle B decision 2): the session starts from the merge commit's first parent, is given the pull request's text, and is
/// graded by the test files of the merged diff, which it never sees and which are put on its branch afterwards. Nothing here calls a model.</summary>
public static partial class ReplayImporter
{
    /// <summary>The largest oracle (bytes of test files) a task may carry; the acceptance script embeds it.</summary>
    public const int MaxOracleBytes = 400 * 1024;

    public static async Task<ReplayImport> ImportAsync(ReplayRequest request, CancellationToken ct)
    {
        List<BenchRepository> repositories = [];
        List<BenchTask> tasks = [];
        List<ReplayEntry> entries = [];
        List<ReplayRejection> rejected = [];
        Dictionary<string, string> scripts = [];
        Dictionary<string, IReadOnlyDictionary<string, byte[]>> files = [];
        var repo = request.RepositoryPath;
        foreach (var pr in request.PullRequests.OrderByDescending(p => p.Number))
        {
            var plan = await PlanAsync(repo, pr, ct);
            if (plan.Rejection is { } rejection)
            {
                rejected.Add(rejection);
                continue;
            }
            var p = plan.Value!;
            var goal = ReplayGoal.Build(pr, p.Oracle.Select(f => f.Path).ToList(), out var reason, out var detail);
            if (goal is null)
            {
                rejected.Add(new ReplayRejection(pr.Number, reason, detail));
                continue;
            }
            var id = $"pr-{pr.Number}";
            var script = AcceptanceScript(p.Oracle, p.Contents, p.Projects);
            repositories.Add(new BenchRepository($"repo-{id}", "dotnet", request.Url, p.Parent, request.License, ["dotnet", "test"]));
            tasks.Add(new BenchTask(id, $"repo-{id}", BenchTask.Change, p.Size, goal, new BenchAcceptance(Script: $"accept/{id}.sh", TimeoutMinutes: 45)));
            entries.Add(new ReplayEntry(id, pr.Number, p.Parent, pr.MergeCommit, p.Projects, p.Oracle, $"accept/{id}.sh"));
            scripts[id] = script;
            files[id] = p.Contents;
        }
        var set = new TaskSet(TaskSet.SchemaId, request.SetName, "Replay of merged pull requests: each task starts from a pull request's parent and is graded by the tests the pull request added or changed.", repositories, tasks);
        return new ReplayImport(set, new ReplayManifest(ReplayManifest.SchemaId, request.SetName, request.Url, entries), scripts, files, rejected);
    }

    /// <summary>Whether a path is a test file of the merged diff: anything under a top-level <c>tests</c> or <c>test</c> folder, or a file named like a test class.</summary>
    public static bool IsTestPath(string path) =>
        path.StartsWith("tests/", StringComparison.Ordinal) || path.StartsWith("test/", StringComparison.Ordinal) || path.EndsWith("Tests.cs", StringComparison.Ordinal) || path.EndsWith("Test.cs", StringComparison.Ordinal);

    private sealed record Plan(string Parent, string Size, IReadOnlyList<ReplayFile> Oracle, IReadOnlyDictionary<string, byte[]> Contents, IReadOnlyList<string> Projects);

    private readonly record struct Planned(Plan? Value, ReplayRejection? Rejection);

    private static async Task<Planned> PlanAsync(string repo, ReplayPr pr, CancellationToken ct)
    {
        ReplayRejection Reject(string reason, string detail) => new(pr.Number, reason, detail);
        if (pr.Title.StartsWith("chore(main): release", StringComparison.Ordinal))
            return new(null, Reject(ReplayRejection.ReleasePr, "a release pull request changes versions, not behaviour"));
        var parent = (await Git(repo, ["rev-parse", "--verify", "--quiet", $"{pr.MergeCommit}^1"], ct)).Trim();
        if (parent.Length != 40)
            return new(null, Reject(ReplayRejection.NoMergeParent, "the merge commit or its first parent is not in the clone; fetch the default branch"));
        var changed = ParseNameStatus(await Git(repo, ["-c", "core.quotepath=off", "diff", "--name-status", "--no-renames", parent, pr.MergeCommit], ct));
        if (changed.FirstOrDefault(c => !SafePath().IsMatch(c.Path)) is { Path: { } bad })
            return new(null, Reject(ReplayRejection.UnsafePath, $"'{bad}' has characters the script cannot carry"));
        var test = changed.Where(c => IsTestPath(c.Path)).ToList();
        if (test.Count == 0)
            return new(null, Reject(ReplayRejection.NoTestFiles, "the diff touches no test file, so there is no oracle"));
        var source = changed.Where(c => !IsTestPath(c.Path)).ToList();
        if (source.Count == 0)
            return new(null, Reject(ReplayRejection.NoSourceChange, "the diff changes only tests, so there is nothing to implement"));

        Dictionary<string, byte[]> contents = [];
        List<ReplayFile> oracle = [];
        foreach (var (status, path) in test)
        {
            if (status == 'D')
            {
                oracle.Add(new ReplayFile(path, ReplayFile.Delete, null));
                continue;
            }
            var bytes = await GitBytes(repo, ["cat-file", "blob", $"{pr.MergeCommit}:{path}"], ct);
            contents[path] = bytes;
            oracle.Add(new ReplayFile(path, ReplayFile.Write, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        if (contents.Values.Sum(b => b.Length) > MaxOracleBytes)
            return new(null, Reject(ReplayRejection.OracleTooLarge, $"the test files are over {MaxOracleBytes / 1024} KB"));
        var projects = await ProjectsAsync(repo, pr.MergeCommit, test.Select(t => t.Path), ct);
        if (projects.Count == 0)
            return new(null, Reject(ReplayRejection.NoTestProject, "no .csproj above the test files at the merged commit"));
        return new(new Plan(parent, await SizeAsync(repo, parent, pr.MergeCommit, source.Select(s => s.Path), ct), oracle, contents, projects), null);
    }

    private static async Task<IReadOnlyList<string>> ProjectsAsync(string repo, string merge, IEnumerable<string> testPaths, CancellationToken ct)
    {
        var all = await GitLinesAsync(repo, ["ls-tree", "-r", "--name-only", merge], ct);
        var projects = all.Where(p => p.EndsWith(".csproj", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        SortedSet<string> found = new(StringComparer.Ordinal);
        foreach (var path in testPaths)
        {
            var dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
            while (true)
            {
                var project = projects.FirstOrDefault(p => (Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "") == dir);
                if (project is not null)
                {
                    found.Add(project);
                    break;
                }
                if (dir.Length == 0)
                    break;
                dir = dir.Contains('/', StringComparison.Ordinal) ? dir[..dir.LastIndexOf('/')] : "";
            }
        }
        return [.. found];
    }

    /// <summary>S, M or L by the non-test lines changed, the budget class the governor holds the task to.</summary>
    private static async Task<string> SizeAsync(string repo, string parent, string merge, IEnumerable<string> sourcePaths, CancellationToken ct)
    {
        var lines = 0;
        foreach (var row in await GitLinesAsync(repo, ["-c", "core.quotepath=off", "diff", "--numstat", "--no-renames", parent, merge, "--", .. sourcePaths], ct))
        {
            var cols = row.Split('\t');
            if (cols.Length >= 2 && int.TryParse(cols[0], CultureInfo.InvariantCulture, out var added) && int.TryParse(cols[1], CultureInfo.InvariantCulture, out var removed))
                lines += added + removed;
        }
        return lines < 80 ? "S" : lines < 400 ? "M" : "L";
    }

    /// <summary>The check: put the merged test files on the branch (over whatever the session wrote at those paths), remove the deleted ones, then run each test project, filtered to the
    /// changed test classes when every changed file is a class file. It passes only when every project exits 0 and ran tests.</summary>
    internal static string AcceptanceScript(IReadOnlyList<ReplayFile> oracle, IReadOnlyDictionary<string, byte[]> contents, IReadOnlyList<string> projects)
    {
        var o = new StringBuilder();
        o.AppendLine("set -u");
        o.AppendLine("put() { mkdir -p \"$(dirname \"$1\")\" && base64 -d > \"$1\"; }");
        foreach (var file in oracle)
        {
            if (file.Change == ReplayFile.Delete)
            {
                o.AppendLine(CultureInfo.InvariantCulture, $"rm -f '{file.Path}'");
                continue;
            }
            o.AppendLine(CultureInfo.InvariantCulture, $"put '{file.Path}' <<'ORACLE_EOF'");
            o.AppendLine(Convert.ToBase64String(contents[file.Path], Base64FormattingOptions.InsertLineBreaks));
            o.AppendLine("ORACLE_EOF");
        }
        var classes = oracle.Where(f => f.Change == ReplayFile.Write && f.Path.EndsWith(".cs", StringComparison.Ordinal)).Select(f => Path.GetFileNameWithoutExtension(f.Path)).Distinct().ToList();
        var filter = classes.Count > 0 && oracle.Where(f => f.Change == ReplayFile.Write).All(f => f.Path.EndsWith(".cs", StringComparison.Ordinal))
            ? $" --filter '{string.Join('|', classes.Select(c => $"FullyQualifiedName~{c}"))}'"
            : "";
        o.AppendLine("status=0");
        o.AppendLine("log=\"./.replay-test.$$.log\"");
        foreach (var project in projects)
        {
            o.AppendLine(CultureInfo.InvariantCulture, $"dotnet test '{project}' --nologo{filter} >\"$log\" 2>&1 || status=1");
            o.AppendLine("tail -n 25 \"$log\"");
            o.AppendLine("grep -q 'Passed!' \"$log\" || status=1");
        }
        o.AppendLine("rm -f \"$log\"");
        o.AppendLine("exit $status");
        return o.ToString();
    }

    private static List<(char Status, string Path)> ParseNameStatus(string output) =>
        [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t', 2)).Where(c => c.Length == 2).Select(c => (c[0][0], c[1]))];

    private static async Task<string> Git(string repo, IReadOnlyList<string> args, CancellationToken ct) => (await GitBenchWorkspace.Git(repo, args, ct)).Output;

    private static async Task<IReadOnlyList<string>> GitLinesAsync(string repo, IReadOnlyList<string> args, CancellationToken ct) =>
        (await Git(repo, args, ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static async Task<byte[]> GitBytes(string repo, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in NoHooks.Concat(args))
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(psi)!;
        using var buffer = new MemoryStream();
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 ? buffer.ToArray() : throw new BenchException($"git {args[0]} failed in {repo}: {(await stderr).Trim()}");
    }

    private static readonly string[] NoHooks = ["-c", "core.hooksPath=/dev/null"];

    [GeneratedRegex(@"^[A-Za-z0-9_./ +@=,-]+$")]
    private static partial Regex SafePath();
}
