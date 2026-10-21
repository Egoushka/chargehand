using System.Diagnostics;
using System.Globalization;
using System.Text;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>
/// <c>chargehand why &lt;path&gt;:&lt;line&gt; [--repo &lt;dir&gt;] [--stream &lt;stream.jsonl&gt;]</c> (ADR 0070): <c>git blame</c> for the line, the <c>Chargehand-*</c> trailers of the commit it points at, then what the run log and the
/// batch records know of that run, and the session messages that edited the file when the session's stream is given. A line a person wrote, or one from a commit without trailers, is said to be so.
/// Exit 0 when the line was traced (to a run or to a commit without one), 1 when it could not be, 2 for a usage error.
/// </summary>
public static class WhyCli
{
    private const int MaxGoal = 400;
    private const int MaxSaid = 300;
    private const int MaxTouches = 5;

    public const string Usage = "usage: chargehand why <path>:<line> [--repo <dir>] [--stream <stream.jsonl>]";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, IRunLog log, string recordsDirectory, string workingDirectory, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (ParseArgs(args) is not { } request)
        {
            error.WriteLine(Usage);
            return 2;
        }
        var (path, line, stream) = (request.Path, request.Line, request.Stream);
        var dir = Path.GetFullPath(request.Repo ?? workingDirectory);
        var blamed = await Git(dir, ct, "blame", "--porcelain", "-L", $"{line},{line}", "--", path);
        if (blamed.Exit != 0)
        {
            error.WriteLine($"{path}:{line}: git blame failed: {blamed.Err.Trim()}");
            return 1;
        }
        var sha = blamed.Out.Split(' ', 2)[0];
        output.WriteLine($"{path}:{line}");
        if (sha.All(c => c == '0'))
        {
            output.WriteLine("  not committed yet: the line is a change in the working tree.");
            return 0;
        }
        var info = await Git(dir, ct, "log", "-1", "--format=%h%x00%s%x00%an%x00%as%x00%B", sha);
        if (info.Exit != 0 || info.Out.Split('\0', 5) is not [var shortSha, var subject, var author, var date, var message])
        {
            error.WriteLine($"{path}:{line}: could not read commit {sha}");
            return 1;
        }
        output.WriteLine($"  commit   {shortSha} \"{subject}\" by {author}, {date}");
        var trailers = Provenance.Read(message);
        if (!trailers.TryGetValue(Provenance.RunKey, out var run))
        {
            output.WriteLine("  This commit has no Chargehand-Run trailer: a person wrote it, or a tool that does not stamp commits.");
            return 0;
        }
        output.WriteLine($"  run      {run}");
        PrintTrailers(trailers, output);
        await PrintRunAsync(run, trailers.GetValueOrDefault(Provenance.TaskKey), log, recordsDirectory, output, ct);
        await PrintTouchesAsync(dir, path, stream, output, ct);
        return 0;
    }

    private sealed record Request(string Path, int Line, string? Repo, string? Stream);

    private static Request? ParseArgs(IReadOnlyList<string> args)
    {
        string? repo = null, stream = null, target = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "--repo" or "--stream" && i + 1 < args.Count)
            {
                if (args[i] == "--repo")
                    repo = args[++i];
                else
                    stream = args[++i];
            }
            else if (target is null && !args[i].StartsWith('-'))
                target = args[i];
            else
                return null;
        }
        var split = target?.LastIndexOf(':') ?? -1;
        return target is null || split < 1 || !int.TryParse(target[(split + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line < 1 ? null : new Request(target[..split], line, repo, stream);
    }

    private static void PrintTrailers(IReadOnlyDictionary<string, string> trailers, TextWriter output)
    {
        foreach (var (key, label) in new[] { (Provenance.TaskKey, "task"), (Provenance.RuntimeKey, "runtime"), (Provenance.SpendKey, "spend"), (Provenance.ChecksKey, "checks"), (Provenance.ResultKey, "result") })
            if (trailers.TryGetValue(key, out var value))
                output.WriteLine($"  {label,-8} {value}");
    }

    /// <summary>The goal and the session's own record: from the run log when the run has a start record there (a <c>code</c> run), else through the batch records, which map a session's run id to its task and batch.</summary>
    private static async Task PrintRunAsync(string run, string? taskTrailer, IRunLog log, string recordsDirectory, TextWriter output, CancellationToken ct)
    {
        string? goal = null;
        var entry = await log.ReadAsync(run, ct);
        if (entry.Start is { } start)
            goal = start.Request.Text;
        if (FindSession(run, recordsDirectory) is { } found)
        {
            var (batch, session) = found;
            var taskId = session.TaskId;
            goal ??= (await log.ReadAsync(batch, ct)).Start?.Request.Driven?.Tasks.FirstOrDefault(t => t.Id == taskId)?.Goal;
            var cost = session.Usd ?? session.UsdEstimate;
            var spend = cost is { } usd ? string.Create(CultureInfo.InvariantCulture, $", ${usd:0.00}") : "";
            output.WriteLine($"  session  {session.Role}, {session.Status}, {session.Turns.ToString(CultureInfo.InvariantCulture)} turns{spend} (batch {batch}, task {taskId})");
        }
        if (goal is not null)
            output.WriteLine($"  goal     {Cut(goal.ReplaceLineEndings(" ").Trim(), MaxGoal)}");
        else if (entry.Start is null)
            output.WriteLine($"  The run log on this machine has no record of run {run}{(taskTrailer is null ? "" : $" (task {taskTrailer})")}: it ran elsewhere, or the record was pruned. The trailers above are what the commit itself says.");
    }

    private static (string Batch, SessionRecord Session)? FindSession(string run, string recordsDirectory)
    {
        if (!Directory.Exists(recordsDirectory))
            return null;
        var store = new TaskRecordStore(recordsDirectory);
        foreach (var directory in Directory.EnumerateDirectories(recordsDirectory))
        {
            var batch = Path.GetFileName(directory);
            if (store.Read(batch).Sessions.FirstOrDefault(s => s.RunId == run) is { } session)
                return (batch, session);
        }
        return null;
    }

    private static async Task PrintTouchesAsync(string dir, string path, string? stream, TextWriter output, CancellationToken ct)
    {
        if (stream is null)
        {
            output.WriteLine("  Session messages are not kept after the session's output volume goes; pass --stream <stream.jsonl> to list the ones that edited this file.");
            return;
        }
        var root = await Git(dir, ct, "ls-files", "--full-name", "--", path);
        var relative = root.Exit == 0 && root.Out.Trim() is { Length: > 0 } named ? named : path;
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(stream, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"  Could not read {stream}: {e.Message}");
            return;
        }
        var touches = StreamTouches.Find(lines, relative);
        output.WriteLine(touches.Count == 0 ? $"  No message of that stream edited {relative}." : $"  Messages that edited {relative} ({touches.Count}):");
        foreach (var touch in touches.Take(MaxTouches))
            output.WriteLine($"    stream line {touch.Line}, {touch.Tool}: {(touch.Said.Length == 0 ? "(no text with the edit)" : Cut(touch.Said.ReplaceLineEndings(" "), MaxSaid))}");
        if (touches.Count > MaxTouches)
            output.WriteLine($"    and {touches.Count - MaxTouches} more");
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private static async Task<(int Exit, string Out, string Err)> Git(string directory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=off");
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var (stdout, stderr) = (p.StandardOutput.ReadToEndAsync(ct), p.StandardError.ReadToEndAsync(ct));
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, await stdout, await stderr);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "", "git is not installed");
        }
    }
}
