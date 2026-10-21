using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Chargehand.Automation;

/// <summary>The commit a triggered batch starts from: the default branch of the checkout's <c>origin</c>, after a fetch with the server's own git credentials. A fetch that fails is not fatal, since a
/// batch on a slightly old base still ends in a pull request the integrator can bring up to date; the caller is told through <paramref name="warn"/>. A checkout of <paramref name="clones"/> is fetched with
/// the App's token instead (ADR 0051, <c>discover</c>). With <paramref name="clones"/>, a checkout git cannot write (a read-only mount) is not taken from: <see cref="ReadOnlyCheckoutException"/> names its
/// remote, so the caller can fetch a managed clone of it instead.</summary>
public sealed partial class GitRepositoryHeads(Action<string>? warn = null, ManagedClones? clones = null) : IRepositoryHeads
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<string> CurrentCommitAsync(string path, CancellationToken ct)
    {
        var fetch = await GitAsync(path, ["fetch", "--quiet", "origin"], ct, await AuthAsync(path, ct));
        if (fetch.ExitCode != 0)
        {
            if (clones is not null && !clones.Owns(path) && CannotWrite().IsMatch(fetch.Output))
            {
                var origin = await GitAsync(path, ["remote", "get-url", "origin"], ct);
                throw new ReadOnlyCheckoutException(fetch.Output, origin.ExitCode == 0 ? origin.Output : "", await HeadAsync(path, ct));
            }
            warn?.Invoke($"intake: could not fetch {Path.GetFileName(Path.GetFullPath(path))}: {fetch.Output}");
        }
        return await HeadAsync(path, ct) ?? throw new IntakeException($"{Path.GetFileName(Path.GetFullPath(path))} has no origin/HEAD to start from");
    }

    private static async Task<string?> HeadAsync(string path, CancellationToken ct)
    {
        var head = await GitAsync(path, ["rev-parse", "--verify", "--end-of-options", "origin/HEAD^{commit}"], ct);
        return head.ExitCode == 0 && head.Output.Length >= 7 ? head.Output : null;
    }

    /// <summary>What git says when the checkout's <c>.git</c> cannot be written; an ssh "Permission denied (publickey)" is not this.</summary>
    [GeneratedRegex(@"Read-only file system|cannot open '[^']*': Permission denied|insufficient permission for adding an object")]
    private static partial Regex CannotWrite();

    public async Task<string> FetchPullHeadAsync(string path, int number, CancellationToken ct)
    {
        var id = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var local = $"refs/chargehand/pull/{id}";
        var fetch = await GitAsync(path, ["fetch", "--quiet", "origin", $"+refs/pull/{id}/head:{local}"], ct, await AuthAsync(path, ct));
        if (fetch.ExitCode != 0)
        {
            // A read-only checkout cannot fetch; a host job that keeps it current may have fetched refs/pull/* already. The caller
            // compares the commit with the pull request's head, so a stale ref only waits for the next poll.
            var kept = await GitAsync(path, ["rev-parse", "--verify", "--end-of-options", $"refs/pull/{id}/head^{{commit}}"], ct);
            return kept.ExitCode == 0 && kept.Output.Length >= 7 ? kept.Output
                : throw new IntakeException($"could not fetch the head of pull request {id} into {Path.GetFileName(Path.GetFullPath(path))}: {fetch.Output}");
        }
        var head = await GitAsync(path, ["rev-parse", "--verify", "--end-of-options", $"{local}^{{commit}}"], ct);
        return head.ExitCode == 0 && head.Output.Length >= 7 ? head.Output : throw new IntakeException($"pull request {id} has no head commit in {Path.GetFileName(Path.GetFullPath(path))}");
    }

    private async Task<IReadOnlyDictionary<string, string>?> AuthAsync(string path, CancellationToken ct) => clones is not null && clones.Owns(path) ? await clones.AuthAsync(path, ct) : null;

    internal static async Task<(int ExitCode, string Output)> GitAsync(string directory, string[] args, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // git's messages are read (CannotWrite), so they are the untranslated ones.
        psi.Environment["LC_ALL"] = "C";
        foreach (var (key, value) in env ?? new Dictionary<string, string>())
            psi.Environment[key] = value;
        using var process = Process.Start(psi) ?? throw new IntakeException("git could not be started");
        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return (process.ExitCode, process.ExitCode == 0 ? (await output).Trim() : (await error).Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (-1, "git timed out");
        }
    }
}

/// <summary>The checkout cannot be written, so it was not fetched.</summary>
/// <param name="OriginUrl">The checkout's <c>origin</c>; empty when it has none.</param>
/// <param name="Stale">The commit its <c>origin/HEAD</c> points at as it is; null when it has none.</param>
public sealed class ReadOnlyCheckoutException(string message, string originUrl, string? stale) : Exception(message)
{
    public string OriginUrl => originUrl;

    public string? Stale => stale;
}
