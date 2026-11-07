using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Chargehand.Bench;

/// <summary><c>chargehand bench replay import|baseline</c> (ADR 0087, cycle B spec task 2). Neither verb calls a model or needs a profile.</summary>
public static class ReplayCli
{
    public const string Usage = """
        usage: chargehand bench replay import --repo <clone> --url <https upstream> --license <spdx> --out <dir> (--prs <replay-prs.json> | --github <owner/name> [--count <n>]) [--name <set name>]
               chargehand bench replay baseline <dir>/task-set.json --repo <clone> [--only <id,id>] [--max-parallel <n>] [--work <dir>]
          import     reads merged pull requests, rebuilds each one's parent and the test files it merged, and writes task-set.json, replay.json, accept/<id>.sh and oracle/<id>/ to --out
                     (keep --out under the gitignored runs/: the tasks are real work). --github lists the latest merged pull requests with `gh`; --prs reads the same from a file.
                     A pull request that cannot be a task is listed with a named reason and left out
          baseline   runs every task's acceptance script on its parent (must fail) and on the merged commit (must pass), on this machine, with no model; exit 0 when every task is valid
        """;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        try
        {
            return args.ToArray() switch
            {
                ["import", .. var rest] => await ImportAsync(rest, output, error, ct),
                ["baseline", .. var rest] => await BaselineAsync(rest, output, error, ct),
                _ => Fail(error, Usage),
            };
        }
        catch (Exception e) when (e is BenchException or IOException or JsonException or UnauthorizedAccessException)
        {
            return Fail(error, e.Message);
        }
    }

    private static async Task<int> ImportAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var o = Options(args);
        if (o.Missing("repo", "url", "license", "out") is { } missing)
            return Fail(error, $"import needs --{missing}.\n{Usage}");
        if (!TaskSetLoader.PublicUrl(o["url"], out var urlProblem))
            return Fail(error, $"--url {urlProblem}");
        var prs = o.Has("prs") ? Read(o["prs"]!) : o.Has("github") ? await GhPullRequests.ListAsync(o["github"]!, int.Parse(o.Get("count") ?? "10", CultureInfo.InvariantCulture), ct) : null;
        if (prs is null)
            return Fail(error, $"import needs --prs or --github.\n{Usage}");
        var import = await ReplayImporter.ImportAsync(new ReplayRequest(Path.GetFullPath(o["repo"]!), o["url"]!, o["license"]!, o.Get("name") ?? "replay", prs), ct);
        var set = ReplayStore.Write(Path.GetFullPath(o["out"]!), import, out var problems);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{prs.Count} pull requests read: {import.Set.Tasks.Count} became tasks, {import.Rejected.Count} were left out."));
        foreach (var r in import.Rejected)
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  left out #{r.PullRequest}: {r.Reason} ({r.Detail})"));
        foreach (var t in set.Tasks)
            output.WriteLine($"  task {t.Id}: size {t.Size}, goal {t.Goal.Length} characters");
        foreach (var problem in problems)
            error.WriteLine($"task set problem: {problem}");
        return problems.Count == 0 ? 0 : 1;
    }

    private static async Task<int> BaselineAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var o = Options(args);
        if (o.Positional is not { } path || o.Missing("repo") is not null)
            return Fail(error, $"baseline needs a task-set.json and --repo.\n{Usage}");
        var set = TaskSetLoader.Load(path, out var problems);
        if (set is null || problems.Count > 0)
            return Fail(error, string.Join('\n', problems));
        var manifest = JsonSerializer.Deserialize<ReplayManifest>(File.ReadAllText(Path.Combine(set.Directory!, ReplayManifest.FileName)), ReplayManifest.Options)
            ?? throw new BenchException("replay.json is empty");
        var only = o.Get("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var work = o.Get("work") ?? Path.Combine(set.Directory!, "work");
        Directory.CreateDirectory(work);
        var verdicts = await ReplayBaseline.RunAsync(set, manifest, Path.GetFullPath(o["repo"]!), new HostReplayExecutor(work), only, int.Parse(o.Get("max-parallel") ?? "2", CultureInfo.InvariantCulture), ct);
        foreach (var v in verdicts)
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{v.Id}: {(v.Valid ? "valid" : "REJECTED " + v.Reason)} (parent exit {v.ParentExit?.ToString(CultureInfo.InvariantCulture) ?? "-"}, merge exit {v.MergeExit?.ToString(CultureInfo.InvariantCulture) ?? "-"}, {v.Elapsed.TotalMinutes:0.0} min)"));
        var valid = verdicts.Count(v => v.Valid);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{valid} of {verdicts.Count} tasks are valid."));
        foreach (var v in verdicts.Where(v => !v.Valid))
            error.WriteLine($"--- {v.Id}: {v.Reason}\n{v.Tail}");
        File.WriteAllText(Path.Combine(set.Directory!, "baseline.json"), JsonSerializer.Serialize(verdicts.Select(v => new { v.Id, v.Valid, v.Reason, v.ParentExit, v.MergeExit, Minutes = Math.Round(v.Elapsed.TotalMinutes, 1) }), ReplayManifest.Options));
        return valid == verdicts.Count ? 0 : 1;
    }

    private static List<ReplayPr> Read(string path) =>
        JsonSerializer.Deserialize<List<ReplayPr>>(File.ReadAllText(path), ReplayManifest.Options) ?? throw new BenchException($"{path} holds no pull requests");

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        return 2;
    }

    private static ArgOptions Options(IReadOnlyList<string> args)
    {
        Dictionary<string, string> values = [];
        string? positional = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Count)
                values[args[i][2..]] = args[++i];
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
                positional ??= args[i];
        }
        return new ArgOptions(values, positional);
    }

    private sealed record ArgOptions(Dictionary<string, string> Values, string? Positional)
    {
        public string? this[string name] => Values.GetValueOrDefault(name);
        public string? Get(string name) => this[name];
        public bool Has(string name) => Values.ContainsKey(name);
        public string? Missing(params string[] names) => names.FirstOrDefault(n => !Values.ContainsKey(n));
    }
}

/// <summary>Writes an import to a directory: the task set, the manifest, one acceptance script and the oracle files per task. Returns the set as the loader reads it back, with its problems.</summary>
public static class ReplayStore
{
    public static TaskSet Write(string directory, ReplayImport import, out IReadOnlyList<string> problems)
    {
        Directory.CreateDirectory(Path.Combine(directory, "accept"));
        foreach (var (id, script) in import.Scripts)
            File.WriteAllText(Path.Combine(directory, "accept", $"{id}.sh"), script);
        foreach (var (id, files) in import.OracleFiles)
            foreach (var (path, bytes) in files)
            {
                var target = Path.Combine(directory, "oracle", id, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }
        File.WriteAllText(Path.Combine(directory, ReplayManifest.FileName), JsonSerializer.Serialize(import.Manifest, ReplayManifest.Options));
        var setPath = Path.Combine(directory, "task-set.json");
        File.WriteAllText(setPath, TaskSetLoader.ToJson(import.Set));
        return TaskSetLoader.Load(setPath, out problems) ?? import.Set;
    }
}

/// <summary>Merged pull requests of a GitHub repository through the <c>gh</c> command line, with the text of the issues each one closes.</summary>
public static class GhPullRequests
{
    public static async Task<IReadOnlyList<ReplayPr>> ListAsync(string slug, int count, CancellationToken ct)
    {
        using var list = JsonDocument.Parse(await GhAsync(["pr", "list", "-R", slug, "--state", "merged", "--limit", count.ToString(CultureInfo.InvariantCulture), "--json", "number,title,body,mergeCommit,closingIssuesReferences"], ct));
        List<ReplayPr> prs = [];
        foreach (var pr in list.RootElement.EnumerateArray())
        {
            List<ReplayIssue> issues = [];
            foreach (var reference in pr.GetProperty("closingIssuesReferences").EnumerateArray())
            {
                using var issue = JsonDocument.Parse(await GhAsync(["issue", "view", reference.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture), "-R", slug, "--json", "title,body"], ct));
                issues.Add(new ReplayIssue(issue.RootElement.GetProperty("title").GetString(), issue.RootElement.GetProperty("body").GetString()));
            }
            prs.Add(new ReplayPr(pr.GetProperty("number").GetInt32(), pr.GetProperty("title").GetString() ?? "", pr.GetProperty("body").GetString(), pr.GetProperty("mergeCommit").GetProperty("oid").GetString() ?? "", issues));
        }
        return prs;
    }

    private static async Task<string> GhAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 ? await stdout : throw new BenchException($"gh {args[0]} {args[1]} failed: {(await stderr).Trim()}");
    }
}
