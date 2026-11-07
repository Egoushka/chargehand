using System.Diagnostics;
using System.Text;

namespace Chargehand.Bench;

/// <param name="ParentExit">The oracle's exit code on the parent; a valid task fails it.</param>
/// <param name="MergeExit">The oracle's exit code on the merged commit; a valid task passes it.</param>
/// <param name="Reason"><c>valid</c>, or the named reason the task is not.</param>
public sealed record ReplayVerdict(string Id, bool Valid, string Reason, int? ParentExit, int? MergeExit, TimeSpan Elapsed, string Tail)
{
    public const string ValidReason = "valid";
    public const string PassesAtParent = "oracle_passes_at_parent";
    public const string FailsAtMerge = "oracle_fails_at_merge";
    public const string Unreachable = "commit_unreachable";
}

/// <summary>Runs an acceptance script on a commit's tree. The host implementation is <see cref="HostReplayExecutor"/>; tests use a fake.</summary>
public interface IReplayExecutor
{
    Task<(int ExitCode, string Tail)> RunAsync(string repositoryPath, string commit, IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct);
}

/// <summary>The check that makes a replay task valid (cycle B spec, decision 2): its oracle fails at the parent and passes at the merged commit. It uses the importer's own acceptance script
/// on both, so a task that passes here is one the benchmark can grade. No model is called.</summary>
public static class ReplayBaseline
{
    public static async Task<IReadOnlyList<ReplayVerdict>> RunAsync(TaskSet set, ReplayManifest manifest, string repositoryPath, IReplayExecutor executor, IReadOnlyCollection<string>? only, int parallel, CancellationToken ct)
    {
        var entries = manifest.Tasks.Where(e => only is null || only.Count == 0 || only.Contains(e.Id)).ToList();
        var results = new ReplayVerdict[entries.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = ct },
            async (i, token) => results[i] = await CheckAsync(set, entries[i], repositoryPath, executor, token));
        return results;
    }

    private static async Task<ReplayVerdict> CheckAsync(TaskSet set, ReplayEntry entry, string repo, IReplayExecutor executor, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var task = set.Tasks.First(t => t.Id == entry.Id);
        var timeout = TimeSpan.FromMinutes(task.Acceptance.TimeoutMinutes ?? 30);
        var argv = TaskSetLoader.AcceptanceArgv(set, task, set.Repositories.First(r => r.Name == task.Repository));
        ReplayVerdict Done(bool valid, string reason, int? parent, int? merge, string tail) => new(entry.Id, valid, reason, parent, merge, Stopwatch.GetElapsedTime(started), tail);

        var atParent = await executor.RunAsync(repo, entry.Parent, argv, timeout, ct);
        if (atParent.ExitCode == -2)
            return Done(false, ReplayVerdict.Unreachable, null, null, atParent.Tail);
        if (atParent.ExitCode == 0)
            return Done(false, ReplayVerdict.PassesAtParent, 0, null, atParent.Tail);
        var atMerge = await executor.RunAsync(repo, entry.Merge, argv, timeout, ct);
        if (atMerge.ExitCode == -2)
            return Done(false, ReplayVerdict.Unreachable, atParent.ExitCode, null, atMerge.Tail);
        return atMerge.ExitCode == 0
            ? Done(true, ReplayVerdict.ValidReason, atParent.ExitCode, 0, atParent.Tail)
            : Done(false, ReplayVerdict.FailsAtMerge, atParent.ExitCode, atMerge.ExitCode, atMerge.Tail);
    }
}

/// <summary>Runs the script on this machine, in a directory made from <c>git archive</c> of the commit, with a small environment (no credentials are passed on). Exit -1 is a timeout; -2 a commit
/// that cannot be archived. A container engine is not needed, which is the difference from <c>bench run --baseline</c>.</summary>
public sealed class HostReplayExecutor(string workDirectory) : IReplayExecutor
{
    private static readonly string[] Inherit = ["PATH", "HOME", "TMPDIR", "LANG", "DOTNET_ROOT", "NUGET_PACKAGES", "SSL_CERT_FILE", "SSL_CERT_DIR", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "ALL_PROXY", "https_proxy", "http_proxy", "no_proxy",
        "NUGET_HTTP_CACHE_PATH", "DOTNET_CLI_HOME", "MSBUILDDISABLENODEREUSE", "DOTNET_CLI_USE_MSBUILD_SERVER"];

    public async Task<(int ExitCode, string Tail)> RunAsync(string repositoryPath, string commit, IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetFullPath(workDirectory), $"{commit[..12]}-{Guid.NewGuid():N}"[..30]);
        Directory.CreateDirectory(dir);
        try
        {
            var extract = await Shell(repositoryPath, ["sh", "-c", $"git archive {commit} | tar -x -C '{dir}'"], TimeSpan.FromMinutes(5), ct);
            if (extract.ExitCode != 0)
                return (-2, extract.Tail);
            return await Shell(dir, argv, timeout, ct);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                // A scratch directory under the work directory; the next run's cleanup or the system removes it.
            }
        }
    }

    private static async Task<(int ExitCode, string Tail)> Shell(string directory, IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(argv[0]) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in argv.Skip(1))
            psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var name in Inherit)
            if (Environment.GetEnvironmentVariable(name) is { } value)
                psi.Environment[name] = value;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var output = new StringBuilder();
        var pump = Task.WhenAll(Drain(process.StandardOutput, output), Drain(process.StandardError, output));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            await pump;
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            ct.ThrowIfCancellationRequested();
            return (-1, "timed out");
        }
        var lines = output.ToString().Split('\n');
        return (process.ExitCode, string.Join('\n', lines.TakeLast(15)));
    }

    private static async Task Drain(StreamReader reader, StringBuilder sink)
    {
        while (await reader.ReadLineAsync() is { } line)
            lock (sink)
                sink.AppendLine(line);
    }
}
