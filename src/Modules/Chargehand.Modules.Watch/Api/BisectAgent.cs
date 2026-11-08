using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Chargehand.Modules.Watch.Api;

/// <summary>What to bisect: a repository, a commit where the check passed, one where it fails, and the check as a shell command (exit 0 passes, 125 skips the commit, anything else fails).</summary>
public sealed record BisectRequest(string Repository, string Good, string Bad, string Check, TimeSpan? Timeout = null);

/// <summary>The commit that broke the check, or why none was found. <see cref="Steps"/> counts the commits the check ran on during the bisection.</summary>
public sealed record BisectOutcome(bool Found, string? Commit, string? Subject, string? Author, string? Date, int Steps, string? Problem, string? Log)
{
    public static BisectOutcome Failed(string problem, string? log = null) => new(false, null, null, null, null, 0, problem, log);
}

/// <summary>The issue the agent opens: a title naming the commit and a markdown body.</summary>
public sealed record BisectIssue(string Title, string Body);

/// <summary>Where the agent opens its issue. The host supplies the forge; the agent itself makes no network call.</summary>
public interface IIssueTracker
{
    /// <summary>Opens the issue and returns its URL.</summary>
    Task<string> OpenAsync(BisectIssue issue, CancellationToken ct);
}

/// <summary>
/// On a red main or a regression report: runs <c>git bisect run</c> with the failing check between a good and a bad commit and names the first bad one.
/// It bisects in a detached worktree of its own, so the caller's checkout, index and bisect state stay as they were, and removes the worktree after.
/// The endpoints are checked first: a good commit that fails, or a bad one that passes, is reported instead of bisected.
/// </summary>
public static class BisectAgent
{
    private const int MaxLog = 4000;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Bisects, and opens an issue through <paramref name="tracker"/> when the commit was found. The URL is null when nothing was found or there is no tracker.</summary>
    public static async Task<(BisectOutcome Outcome, BisectIssue? Issue, string? Url)> RunAndReportAsync(BisectRequest request, IIssueTracker? tracker, CancellationToken ct)
    {
        var outcome = await RunAsync(request, ct);
        if (!outcome.Found)
            return (outcome, null, null);
        var issue = Issue(request, outcome);
        return (outcome, issue, tracker is null ? null : await tracker.OpenAsync(issue, ct));
    }

    public static async Task<BisectOutcome> RunAsync(BisectRequest request, CancellationToken ct)
    {
        if (request.Good.StartsWith('-') || request.Bad.StartsWith('-'))
            return BisectOutcome.Failed("a ref cannot start with '-'");
        if (string.IsNullOrWhiteSpace(request.Check))
            return BisectOutcome.Failed("the check is empty");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(request.Timeout ?? DefaultTimeout);
        var repository = Path.GetFullPath(request.Repository);
        var good = await ResolveAsync(repository, request.Good, deadline.Token);
        var bad = await ResolveAsync(repository, request.Bad, deadline.Token);
        if (good is null || bad is null)
            return BisectOutcome.Failed($"{(good is null ? request.Good : request.Bad)} is not a commit of {repository}");
        if ((await RunGitAsync(repository, deadline.Token, "merge-base", "--is-ancestor", good, bad)).Exit != 0)
            return BisectOutcome.Failed($"{request.Good} is not an ancestor of {request.Bad}");

        var worktree = Path.Combine(Path.GetTempPath(), "chargehand-bisect-" + Guid.NewGuid().ToString("N")[..12]);
        var added = await RunGitAsync(repository, deadline.Token, "worktree", "add", "--detach", "--quiet", worktree, bad);
        if (added.Exit != 0)
            return BisectOutcome.Failed($"could not add a worktree: {added.Err.Trim()}");
        try
        {
            return await BisectAsync(worktree, good, bad, request.Check, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return BisectOutcome.Failed($"the bisection did not finish within {(request.Timeout ?? DefaultTimeout).TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes");
        }
        finally
        {
            await RunGitAsync(repository, CancellationToken.None, "worktree", "remove", "--force", worktree);
            await RunGitAsync(repository, CancellationToken.None, "worktree", "prune");
        }
    }

    private static async Task<BisectOutcome> BisectAsync(string worktree, string good, string bad, string check, CancellationToken ct)
    {
        if (await CheckAtAsync(worktree, good, check, ct) is var atGood && atGood != 0)
            return BisectOutcome.Failed($"the check fails on the good commit {good[..12]} (exit {atGood}): it is not a regression between the two");
        if (await CheckAtAsync(worktree, bad, check, ct) == 0)
            return BisectOutcome.Failed($"the check passes on the bad commit {bad[..12]}: there is nothing to bisect");

        // The worktree is the agent's own, so bisect may check commits out in it; the check runs in each one.
        var started = await RunGitAsync(worktree, ct, "bisect", "start", bad, good);
        if (started.Exit != 0)
            return BisectOutcome.Failed($"git bisect start failed: {started.Err.Trim()}");
        var run = await RunGitAsync(worktree, ct, "bisect", "run", "sh", "-c", check);
        var log = Cut(run.Out + run.Err);
        var first = run.Exit == 0 ? await ResolveAsync(worktree, "refs/bisect/bad", ct) : null;
        var steps = (await RunGitAsync(worktree, ct, "bisect", "log")).Out.Split('\n').Count(l => l.StartsWith("git bisect good", StringComparison.Ordinal) || l.StartsWith("git bisect bad", StringComparison.Ordinal) || l.StartsWith("git bisect skip", StringComparison.Ordinal));
        await RunGitAsync(worktree, CancellationToken.None, "bisect", "reset", "--quiet");
        if (first is null)
            return BisectOutcome.Failed($"git bisect run did not name a commit (exit {run.Exit})", log);
        var info = await RunGitAsync(worktree, ct, "log", "-1", "--format=%s%x00%an%x00%as", first);
        var fields = info.Out.TrimEnd('\n').Split('\0');
        return fields.Length == 3
            ? new BisectOutcome(true, first, fields[0], fields[1], fields[2], Math.Max(0, steps - 2), null, log)
            : BisectOutcome.Failed($"could not read commit {first}", log);
    }

    /// <summary>The issue naming the commit: title, the check, the range and what <c>git bisect run</c> printed.</summary>
    public static BisectIssue Issue(BisectRequest request, BisectOutcome outcome)
    {
        if (!outcome.Found || outcome.Commit is not { } commit)
            throw new ArgumentException("the outcome names no commit", nameof(outcome));
        var shortSha = commit[..Math.Min(12, commit.Length)];
        var body = new StringBuilder()
            .Append("`git bisect run` names ").Append(commit).Append(" as the first commit on which the check fails.\n\n")
            .Append("- Commit: `").Append(shortSha).Append("` \"").Append(outcome.Subject).Append("\" by ").Append(outcome.Author).Append(", ").Append(outcome.Date).Append('\n')
            .Append("- Range: good `").Append(request.Good).Append("`, bad `").Append(request.Bad).Append("`\n")
            .Append("- Check: `").Append(request.Check.ReplaceLineEndings(" ").Replace('`', '\'')).Append("`\n")
            .Append("- Commits tested during the bisection: ").Append(outcome.Steps.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (!string.IsNullOrWhiteSpace(outcome.Log))
            body.Append("\n<details><summary>git bisect run</summary>\n\n```\n").Append(outcome.Log.Replace("```", "'''", StringComparison.Ordinal).Trim()).Append("\n```\n\n</details>\n");
        return new BisectIssue($"Regression: {shortSha} \"{outcome.Subject}\" breaks the check", body.ToString());
    }

    private static async Task<int> CheckAtAsync(string worktree, string commit, string check, CancellationToken ct)
    {
        var checkout = await RunGitAsync(worktree, ct, "checkout", "--quiet", "--force", "--detach", commit);
        if (checkout.Exit != 0)
            return -1;
        return (await RunAsync("sh", ["-c", check], worktree, ct)).Exit;
    }

    private static async Task<string?> ResolveAsync(string directory, string reference, CancellationToken ct)
    {
        var resolved = await RunGitAsync(directory, ct, "rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}");
        return resolved.Exit == 0 && resolved.Out.Trim() is { Length: > 0 } sha ? sha : null;
    }

    private static string Cut(string text) => text.Length <= MaxLog ? text : "…" + text[^MaxLog..];

    private static Task<(int Exit, string Out, string Err)> RunGitAsync(string directory, CancellationToken ct, params string[] args) =>
        RunAsync("git", ["-c", "core.quotepath=off", "-c", "maintenance.auto=false", "-c", "gc.auto=0", .. args], directory, ct);

    private static async Task<(int Exit, string Out, string Err)> RunAsync(string file, IReadOnlyList<string> args, string directory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {file}");
        }
        catch (Win32Exception)
        {
            return (-1, "", $"{file} is not installed");
        }
        using (process)
        {
            process.StandardInput.Close();
            var (stdout, stderr) = (process.StandardOutput.ReadToEndAsync(CancellationToken.None), process.StandardError.ReadToEndAsync(CancellationToken.None));
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            return (process.ExitCode, await stdout, await stderr);
        }
    }
}
