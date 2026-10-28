using System.Diagnostics;
using System.Text;
using Chargehand.Modules.Research.Api;

namespace Chargehand.Acp;

/// <summary>The checkout's state, for the diff a node leaves. A research worker leaves none; a diff here means a guard failed.</summary>
internal static class AcpGit
{
    public static async Task<string?> HeadAsync(string directory, CancellationToken ct)
    {
        var (exit, stdout) = await RunAsync(directory, ["rev-parse", "HEAD"], ct);
        return exit == 0 ? stdout.Trim() : null;
    }

    /// <summary>Working tree against <paramref name="baseCommit"/>, untracked files included.</summary>
    public static async Task<IReadOnlyList<FileDiff>> DiffAsync(string directory, string baseCommit, CancellationToken ct)
    {
        var patches = new StringBuilder((await RunAsync(directory, ["-c", "core.quotepath=off", "diff", "--no-color", "--no-ext-diff", baseCommit], ct)).Stdout);
        var untracked = (await RunAsync(directory, ["-c", "core.quotepath=off", "ls-files", "--others", "--exclude-standard"], ct)).Stdout;
        foreach (var file in untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            patches.Append((await RunAsync(directory, ["diff", "--no-color", "--no-index", "--", "/dev/null", file], ct)).Stdout);
        return ParseDiff(patches.ToString());
    }

    /// <summary>Splits unified diff output into files.</summary>
    internal static IReadOnlyList<FileDiff> ParseDiff(string diff)
    {
        var files = new List<FileDiff>();
        foreach (var chunk in ("\n" + diff).Split("\ndiff --git ", StringSplitOptions.RemoveEmptyEntries))
        {
            string? from = null, to = null;
            int add = 0, del = 0;
            foreach (var line in chunk.Split('\n'))
                if (to is null && line.StartsWith("--- ", StringComparison.Ordinal))
                    from = line[4..];
                else if (to is null && line.StartsWith("+++ ", StringComparison.Ordinal))
                    to = line[4..];
                else if (to is null)
                    continue;
                else if (line.StartsWith('+'))
                    add++;
                else if (line.StartsWith('-'))
                    del++;
            if (from is null || to is null)
                continue;
            var status = from == "/dev/null" ? "added" : to == "/dev/null" ? "deleted" : "modified";
            var path = status == "deleted" ? from : to;
            files.Add(new FileDiff(path[(path.IndexOf('/') + 1)..], "diff --git " + chunk.TrimEnd('\n') + "\n", add, del, status));
        }

        return files;
    }

    private static async Task<(int Exit, string Stdout)> RunAsync(string directory, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        _ = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout);
    }
}
