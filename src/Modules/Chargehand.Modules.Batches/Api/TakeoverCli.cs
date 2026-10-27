using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>
/// <c>chargehand takeover &lt;batch-run&gt;/&lt;task&gt; [--repo &lt;dir&gt;] [--remote &lt;name&gt;] [--worktree &lt;dir&gt;]</c> (issue 443): the bridge from a driven task that stalled to a person's own Claude Code session.
/// It fetches the task's pushed branch into a worktree of the local clone and writes <c>handoff.json</c> and <c>progress.md</c> (built by <see cref="HandoffBuilder"/> from git, as the driver does on a stop) into the
/// worktree's <c>.chargehand/</c>, then prints the command that opens a session there. Exit 0 when the worktree is ready, 1 when the task cannot be taken over, 2 for a usage error.
/// </summary>
public static class TakeoverCli
{
    public const string Usage = "usage: chargehand takeover <batch-run>/<task> [--repo <dir>] [--remote <name>] [--worktree <dir>]";

    private const string HandoffDirectory = ".chargehand";

    /// <summary>The command the console shows on a task page; the same text the CLI accepts.</summary>
    public static string Command(string batchRunId, string taskId) => $"chargehand takeover {batchRunId}/{taskId}";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, IRunLog log, string workingDirectory, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (Parse(args) is not { } request)
        {
            error.WriteLine(Usage);
            return 2;
        }
        var repo = Path.GetFullPath(request.Repo ?? workingDirectory);
        try
        {
            var task = await FindAsync(log, request.Batch, request.Task, ct);
            var worktree = Path.GetFullPath(request.Worktree ?? Path.Combine(repo, ".claude", "worktrees", "takeover-" + Safe(request.Task)));
            await EnsureWorktreeAsync(repo, request.Remote, task.Branch, worktree, ct);
            await WriteHandoffAsync(worktree, task, ct);
            output.WriteLine($"Task {request.Task} of batch {request.Batch} ({task.Status.Replace('_', ' ')}) is checked out on {task.Branch} in {worktree}.");
            output.WriteLine("Open a Claude Code session on it:");
            output.WriteLine();
            output.WriteLine($"  cd {worktree} && claude \"Continue this task. Read {HandoffDirectory}/progress.md first: it is the handoff from the stopped chargehand session.\"");
            return 0;
        }
        catch (ChargehandException e)
        {
            error.WriteLine($"takeover {request.Batch}/{request.Task}: {e.Message}");
            return 1;
        }
    }

    private sealed record Request(string Batch, string Task, string? Repo, string Remote, string? Worktree);

    private static Request? Parse(IReadOnlyList<string> args)
    {
        string? target = null, repo = null, remote = null, worktree = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "--repo" or "--remote" or "--worktree" && i + 1 < args.Count)
            {
                var value = args[i + 1];
                switch (args[i])
                {
                    case "--repo": repo = value; break;
                    case "--remote": remote = value; break;
                    default: worktree = value; break;
                }
                i++;
            }
            else if (target is null && !args[i].StartsWith('-'))
                target = args[i];
            else
                return null;
        }
        var split = target?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        return target is null || split < 1 || split == target.Length - 1 ? null : new Request(target[..split], target[(split + 1)..], repo, remote ?? "origin", worktree);
    }

    private sealed record Found(string Status, string Branch, string Base, string Goal, IReadOnlyList<string> Questions);

    private static ChargehandException Refuse(string message) => new(ErrorCode.InvalidRequest, message);

    private static async Task<JsonDocument> BatchRowsAsync(IRunLog log, string batch, CancellationToken ct) =>
        JsonDocument.Parse((await log.ReadAsync(batch, ct)).Run?.Result.Artifacts.FirstOrDefault(a => a.Kind == "driven-batch")?.Content
            ?? throw Refuse($"{batch} is not a finished driven batch in this run log"));

    private static async Task<Found> FindAsync(IRunLog log, string batch, string task, CancellationToken ct)
    {
        using var doc = await BatchRowsAsync(log, batch, ct);
        var row = doc.RootElement.GetProperty("tasks").EnumerateArray().FirstOrDefault(t => t.GetProperty("id").GetString() == task);
        if (row.ValueKind != JsonValueKind.Object)
            throw Refuse($"batch {batch} has no task '{task}'");
        var status = Text(row, "status") ?? "";
        if (status == "completed")
            throw Refuse($"task '{task}' completed: there is nothing to take over, review its pull request");
        var runId = Text(row, "run_id");
        var entry = runId is null ? null : await log.ReadAsync(runId, ct);
        var result = entry?.Run?.Result;
        var work = WorkOf(result) ?? throw Refuse($"task '{task}' ended {status.Replace('_', ' ')} with no pushed branch: its work, if any, is only in the output volume of run {runId}");
        var (branch, start) = work;
        return new Found(status, branch, start, entry?.Start?.Request.Text ?? "", result?.OpenQuestions ?? []);
    }

    private static string? Text(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The branch a task pushed and the commit it started at: from its <c>branch</c> artifact, else from the <c>kept-work</c> a stopped session left.</summary>
    private static (string Branch, string Base)? WorkOf(ResultContract? result) =>
        ContinuationSource.PushedWorkOf(result) is { } pushed ? (pushed.Branch, pushed.Base)
            : ContinuationSource.KeptWorkOf(result) is { } kept ? (kept.Branch, kept.Base) : null;

    private static string Safe(string name) => new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')]);

    private static async Task EnsureWorktreeAsync(string repo, string remote, string branch, string worktree, CancellationToken ct)
    {
        await GitAsync(repo, ct, "fetch", "--quiet", remote, $"+refs/heads/{branch}:refs/remotes/{remote}/{branch}");
        if (Directory.Exists(worktree))
        {
            if (await GitAsync(worktree, ct, "rev-parse", "--abbrev-ref", "HEAD") != branch)
                throw Refuse($"{worktree} exists and is not on {branch}");
            return;
        }
        await GitAsync(repo, ct, "worktree", "add", "--quiet", "-B", branch, worktree, $"{remote}/{branch}");
    }

    private static async Task WriteHandoffAsync(string worktree, Found task, CancellationToken ct)
    {
        var head = await GitAsync(worktree, ct, "rev-parse", "HEAD");
        var commits = Lines(await GitAsync(worktree, ct, "log", "--reverse", "--format=%s", $"{task.Base}..HEAD"));
        var changes = Lines(await GitAsync(worktree, ct, "diff", "--numstat", task.Base, "HEAD")).Select(Change).OfType<ChangedPath>().ToList();
        var facts = new HandoffFacts(task.Status, 0, task.Base, head, commits, changes, null, [], [], []);
        var files = HandoffBuilder.Build(facts, []);

        var markdown = new StringBuilder("# Takeover of a stopped chargehand task\n\n").Append(string.Create(CultureInfo.InvariantCulture, $"The task's goal:\n\n{task.Goal.Trim()}\n\n"));
        if (task.Questions.Count > 0)
            markdown.Append("The session stopped to ask:\n\n").AppendJoin('\n', task.Questions.Select(q => $"- {q}")).Append("\n\n");
        markdown.Append(files.Markdown);

        var directory = Path.Combine(worktree, HandoffDirectory);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "handoff.json"), files.Json, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "progress.md"), markdown.ToString(), ct);
        var exclude = (await GitAsync(worktree, ct, "rev-parse", "--git-path", "info/exclude")).Trim();
        exclude = Path.GetFullPath(exclude, worktree);
        Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
        if (!(File.Exists(exclude) && (await File.ReadAllLinesAsync(exclude, ct)).Contains(HandoffDirectory + "/")))
            await File.AppendAllTextAsync(exclude, HandoffDirectory + "/\n", ct);
    }

    private static IReadOnlyList<string> Lines(string text) => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>A <c>git diff --numstat</c> line: added, removed and path, with <c>-</c> for the counts of a binary file.</summary>
    private static ChangedPath? Change(string line) =>
        line.Split('\t', 3) is [var added, var removed, var path]
            ? new ChangedPath(path, long.TryParse(added, CultureInfo.InvariantCulture, out var a) ? a : 0, long.TryParse(removed, CultureInfo.InvariantCulture, out var d) ? d : 0)
            : null;

    private static async Task<string> GitAsync(string directory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in (string[])["-c", "core.quotepath=off", .. args])
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var (stdout, stderr) = (p.StandardOutput.ReadToEndAsync(ct), p.StandardError.ReadToEndAsync(ct));
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? (await stdout).Trim() : throw Refuse($"git {args[0]} failed: {(await stderr).Trim()}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw Refuse("git is not installed");
        }
    }
}
