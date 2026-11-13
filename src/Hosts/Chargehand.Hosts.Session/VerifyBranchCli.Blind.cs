using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Research.Api;

namespace Chargehand.Hosts.Session;

/// <summary>A tests-only branch of a blind task (issue 789), laid over a checkout as files: <c>--tests-bundle &lt;file&gt;@chargehand/&lt;run&gt;</c>.</summary>
internal sealed record OverlayBundle(string File, string Branch)
{
    public static OverlayBundle? Parse(string value)
    {
        var at = value.LastIndexOf('@');
        if (at < 1 || at == value.Length - 1)
            return null;
        var branch = value[(at + 1)..];
        return branch.StartsWith("chargehand/", StringComparison.Ordinal) && !branch.Contains("..", StringComparison.Ordinal) ? new OverlayBundle(value[..at], branch) : null;
    }
}

internal sealed record VerifyOptions(string Bundle, string Branch, string Work, int Seconds, IReadOnlyList<OverlayBundle> Tests);

public static partial class VerifyBranchCli
{
    private static readonly string[] Safe = ["-c", "safe.directory=*", "-c", "core.hooksPath=/dev/null"];

    /// <summary>Runs the command on the base commit with each tests bundle laid over it. A bundle whose tests do not build there is dropped (<c>blind_uncompilable</c>): that is the blind task's fault, not the implementer's.
    /// Tests that build and fail there are the expected red and stay.</summary>
    private static async Task<(List<(OverlayBundle Bundle, string Ref)> Usable, List<string> Dropped)> ProbeBlindAsync(
        IReadOnlyList<OverlayBundle> tests, string baseCommit, string work, IReadOnlyDictionary<string, string> env, IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
    {
        List<(OverlayBundle, string)> usable = [];
        List<string> dropped = [];
        for (var i = 0; i < tests.Count; i++)
        {
            var reference = $"blind-{i}";
            var fetched = await Git(work, env, [.. Safe, "-c", "fetch.fsckObjects=true", "fetch", "--quiet", tests[i].File, $"refs/heads/{tests[i].Branch}:refs/heads/{reference}"], ct);
            if (fetched.ExitCode != 0 || !await CheckOutAsync(work, env, baseCommit, ct) || !await OverlayAsync(work, env, baseCommit, [reference], ct))
            {
                dropped.Add(tests[i].Branch);
                continue;
            }
            var result = await RunCommandAsync(argv, env, work, timeout, ct);
            if (result is null || (result.ExitCode != 0 && BlindTestOutput.DidNotCompile(result.OutputTail)))
                dropped.Add(tests[i].Branch);
            else
                usable.Add((tests[i], reference));
        }
        return (usable, dropped);
    }

    /// <summary>The command on the session's branch with the usable tests laid over it.</summary>
    private static async Task<BlindTestResult> RunBlindAsync(
        IReadOnlyList<(OverlayBundle Bundle, string Ref)> usable, string baseCommit, string work, IReadOnlyDictionary<string, string> env, IReadOnlyList<string> argv, TimeSpan timeout, IReadOnlyList<string> dropped, CancellationToken ct)
    {
        if (!await CheckOutAsync(work, env, "verify-target", ct) || !await OverlayAsync(work, env, baseCommit, [.. usable.Select(u => u.Ref)], ct))
            return new BlindTestResult(0, 1, ["the blind tests could not be laid over the branch"], dropped);
        var result = await RunCommandAsync(argv, env, work, timeout, ct);
        return result is null
            ? new BlindTestResult(0, 1, [], dropped)
            : BlindTestOutput.Of(result.OutputTail, result.TimedOut ? -1 : result.ExitCode, dropped);
    }

    private static async Task<bool> CheckOutAsync(string work, IReadOnlyDictionary<string, string> env, string commitish, CancellationToken ct)
    {
        if ((await Git(work, env, [.. Safe, "checkout", "--quiet", "--force", "--detach", commitish], ct)).ExitCode != 0)
            return false;
        return (await Git(work, env, [.. Safe, "clean", "--quiet", "-fd"], ct)).ExitCode == 0;
    }

    /// <summary>Takes the test files a tests branch added or changed since <paramref name="onto"/> from that branch. Other paths, symlinks and submodules are left out,
    /// and so is a file the checked-out branch itself changed: the implementer's version stays.</summary>
    private static async Task<bool> OverlayAsync(string work, IReadOnlyDictionary<string, string> env, string onto, IReadOnlyList<string> refs, CancellationToken ct)
    {
        var own = await Git(work, env, [.. Safe, "diff", "--name-only", "-z", onto, "HEAD"], ct);
        if (own.ExitCode != 0)
            return false;
        var kept = own.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        foreach (var reference in refs)
        {
            var changed = await Git(work, env, [.. Safe, "diff", "--raw", "-z", "--no-renames", "--diff-filter=AM", onto, reference], ct);
            if (changed.ExitCode != 0)
                return false;
            var paths = TestFiles(changed.Stdout, kept);
            if (paths.Count > 0 && (await Git(work, env, [.. Safe, "checkout", reference, "--", .. paths], ct)).ExitCode != 0)
                return false;
        }
        return true;
    }

    private static List<string> TestFiles(string raw, HashSet<string> excluded)
    {
        var fields = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<string> paths = [];
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var modes = fields[i].Split(' ');
            var regular = modes.Length > 1 && modes[1] is "100644" or "100755";
            if (regular && TestIntegrity.Classify(fields[i + 1]) == ChangeKind.Test && !excluded.Contains(fields[i + 1]))
                paths.Add(fields[i + 1]);
        }
        return paths;
    }
}
