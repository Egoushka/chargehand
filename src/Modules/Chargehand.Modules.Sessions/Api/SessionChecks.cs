using System.Diagnostics;
using System.Globalization;
using System.Text;
using Chargehand.Contracts;

namespace Chargehand.Modules.Sessions.Api;

/// <param name="Kind"><c>rule</c> (a line of the repository's <see cref="SessionChecks.RulesFile"/>) or <c>acceptance</c> (one of the task's criteria).</param>
/// <param name="ExitCode">What the command ended with; null when it timed out or could not start.</param>
/// <param name="OutputTail">The end of its stdout and stderr, secrets redacted, at most <see cref="SessionChecks.MaxTail"/> characters.</param>
/// <param name="Attempt">The gate round of this run: 1 is the check after the session's first finish.</param>
public sealed record CheckResult(string Kind, string Command, bool Passed, int? ExitCode, string OutputTail, int Attempt, int ExpectedExitCode = 0, string? ExpectedOutput = null);

/// <summary>The gate a finished driven session's branch must pass: the repository's own checks, then the task's acceptance criteria, each a shell command in the working tree.
/// The rules are read from <see cref="RulesFile"/> at the commit the workspace started at, so the session cannot weaken the rules it is held to.</summary>
public static class SessionChecks
{
    /// <summary>One shell command per line; blank lines and lines starting with <c>#</c> are skipped.</summary>
    public const string RulesFile = ".chargehand/checks";

    public const int DefaultRetries = 2;

    public const int MaxTail = 4000;

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<string> ParseRules(string text) =>
        [.. text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];

    /// <summary>The rules at <paramref name="commit"/>; none when the file is absent there or git fails.</summary>
    public static async Task<IReadOnlyList<string>> RulesAsync(string workDirectory, string? commit, CancellationToken ct)
    {
        if (commit is null)
            return [];
        var (exit, stdout) = await RunAsync("git", ["show", $"{commit}:{RulesFile}"], workDirectory, TimeSpan.FromSeconds(20), ct);
        return exit == 0 ? ParseRules(stdout) : [];
    }

    /// <summary>The repository's rules, then the task's criteria, as checks to run.</summary>
    public static IReadOnlyList<AcceptanceCheck> All(IReadOnlyList<string> rules, IReadOnlyList<AcceptanceCheck>? acceptance) =>
        [.. rules.Select(r => new AcceptanceCheck(r)), .. acceptance ?? []];

    public static async Task<IReadOnlyList<CheckResult>> RunAllAsync(IReadOnlyList<string> rules, IReadOnlyList<AcceptanceCheck>? acceptance, string workDirectory, int attempt,
        IReadOnlyList<string> secrets, CancellationToken ct, TimeSpan? timeout = null)
    {
        List<CheckResult> results = [];
        foreach (var (check, i) in All(rules, acceptance).Select((c, i) => (c, i)))
        {
            var (exit, output) = await RunAsync("/bin/sh", ["-c", check.Command], workDirectory, timeout ?? Timeout, ct);
            var passed = exit == check.ExitCode && (check.Output is null || output.Contains(check.Output, StringComparison.Ordinal));
            results.Add(new CheckResult(i < rules.Count ? "rule" : "acceptance", SessionDriver.Redact(check.Command, secrets), passed, exit, Tail(SessionDriver.Redact(output, secrets)), attempt, check.ExitCode, check.Output));
        }
        return results;
    }

    /// <summary>The next turn of a session whose branch failed the gate: each failed command, what it was expected to do and the end of its output.</summary>
    public static string Feedback(IReadOnlyList<CheckResult> results, int attempt, int retries)
    {
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"chargehand ran the repository's checks and the task's acceptance criteria on your branch, and some failed (round {attempt} of {retries + 1}). ")
            .Append("The task is not done until they pass. Fix the change, not the checks: do not edit a test, a check or ").Append(RulesFile).Append(" to make it pass. ")
            .Append("Commit the fix on the same branch, then finish with the JSON report as before.\n");
        foreach (var r in results.Where(r => !r.Passed))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n## {r.Kind}: `{r.Command}`\nexpected exit {r.ExpectedExitCode}");
            if (r.ExpectedOutput is not null)
                text.Append(CultureInfo.InvariantCulture, $" with output containing `{r.ExpectedOutput}`");
            text.Append(CultureInfo.InvariantCulture, $", got {(r.ExitCode is { } code ? $"exit {code}" : "no exit (timed out or did not start)")}\n```\n{r.OutputTail}\n```\n");
        }
        return text.ToString();
    }

    private static string Tail(string text) => text.Length <= MaxTail ? text : "…" + text[^MaxTail..];

    private static async Task<(int? Exit, string Output)> RunAsync(string file, IReadOnlyList<string> args, string directory, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process p;
        try
        {
            p = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (null, e.Message);
        }
        using (p)
        {
            p.StandardInput.Close();
            var output = new StringBuilder();
            var gate = new object();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) output.Append(e.Data).Append('\n'); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (gate) output.Append(e.Data).Append('\n'); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                await p.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                p.Kill(entireProcessTree: true);
                lock (gate)
                    return (null, output.Append(CultureInfo.InvariantCulture, $"[timed out after {timeout.TotalMinutes:0.#} minutes]").ToString());
            }
            p.WaitForExit();
            lock (gate)
                return (p.ExitCode, output.ToString());
        }
    }
}
