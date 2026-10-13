using System.Diagnostics;
using System.Text.Json;
using Chargehand.Sandbox;

namespace Chargehand.Driven;

/// <summary><c>chargehand verify-branch --bundle &lt;file&gt; --branch chargehand/&lt;run&gt; --timeout-seconds N [--work &lt;dir&gt;] -- &lt;command...&gt;</c>: what a fresh
/// verification container runs (ADR 0039). The workspace is a clean clone at the base commit; this puts the session's branch on it from the bundle, checks it out detached,
/// runs the command, and prints one line, <see cref="Marker"/> followed by JSON, which is the only thing the host reads. The tests' own output goes into that JSON as a
/// tail, never to stdout, so nothing the tests print can forge the record. Exit code 0 when the command ran (its own verdict is in the record), 2 when it could not.</summary>
public static class VerifyBranchCli
{
    public const string Marker = "CHARGEHAND_VERIFICATION ";

    public const string Usage = "usage: chargehand verify-branch --bundle <file> --branch chargehand/<run> --timeout-seconds <n> [--work <dir>] -- <command...>";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var separator = args.ToList().IndexOf("--");
        if (separator < 0 || separator == args.Count - 1)
            return Fail(error);
        var options = args.Take(separator).ToList();
        string? bundle = null, branch = null;
        var work = "/work";
        var seconds = 0;
        for (var i = 0; i < options.Count; i += 2)
        {
            if (i + 1 >= options.Count)
                return Fail(error);
            switch (options[i])
            {
                case "--bundle": bundle = options[i + 1]; break;
                case "--branch": branch = options[i + 1]; break;
                case "--work": work = options[i + 1]; break;
                case "--timeout-seconds" when int.TryParse(options[i + 1], out var n): seconds = n; break;
                default: return Fail(error);
            }
        }
        if (bundle is null || branch is null || !branch.StartsWith("chargehand/", StringComparison.Ordinal) || branch.Contains("..", StringComparison.Ordinal) || seconds < 1)
            return Fail(error);
        List<string> argv = [.. args.Skip(separator + 1)];

        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)(e.Value ?? ""));
        var fetch = await Git(work, env, ["-c", "safe.directory=*", "-c", "fetch.fsckObjects=true", "-c", "core.hooksPath=/dev/null", "fetch", "--quiet", bundle, $"refs/heads/{branch}:refs/heads/verify-target"], ct);
        if (fetch.ExitCode != 0)
            return Record(output, argv, -1, $"the bundle does not apply to the workspace: {fetch.Tail}", false, TimeSpan.Zero, "", exitCode: 2);
        var checkout = await Git(work, env, ["-c", "safe.directory=*", "-c", "core.hooksPath=/dev/null", "checkout", "--quiet", "--detach", "verify-target"], ct);
        if (checkout.ExitCode != 0)
            return Record(output, argv, -1, $"the branch could not be checked out: {checkout.Tail}", false, TimeSpan.Zero, "", exitCode: 2);
        var commit = (await Git(work, env, ["-c", "safe.directory=*", "rev-parse", "HEAD"], ct)).Stdout.Trim();

        var result = await ProcessRunner.RunAsync(argv[0], argv.Skip(1), env, work, TimeSpan.FromSeconds(seconds), ct);
        return Record(output, argv, result.TimedOut ? -1 : result.ExitCode, result.OutputTail, result.TimedOut, result.Duration, commit, exitCode: 0);
    }

    private static int Record(TextWriter output, IReadOnlyList<string> argv, int exit, string tail, bool timedOut, TimeSpan duration, string commit, int exitCode)
    {
        output.WriteLine(Marker + JsonSerializer.Serialize(new { argv, exit_code = exit, output_tail = tail, timed_out = timedOut, duration_ms = (long)duration.TotalMilliseconds, commit }));
        return exitCode;
    }

    private static int Fail(TextWriter error)
    {
        error.WriteLine(Usage);
        return 2;
    }

    private static async Task<(int ExitCode, string Stdout, string Tail)> Git(string directory, IReadOnlyDictionary<string, string> env, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in env)
            psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var err = (await stderr).Trim();
        return (p.ExitCode, await stdout, err.Length > 400 ? err[^400..] : err);
    }
}
