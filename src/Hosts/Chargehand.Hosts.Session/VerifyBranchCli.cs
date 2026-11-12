using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Research.Api;

namespace Chargehand.Hosts.Session;

/// <summary><c>chargehand verify-branch --bundle &lt;file&gt; --branch chargehand/&lt;run&gt; --timeout-seconds N [--work &lt;dir&gt;] -- &lt;command...&gt;</c>: what a fresh
/// verification container runs (ADR 0039). The workspace is a clean clone at the base commit; this puts the session's branch on it from the bundle, checks it out detached,
/// runs the command, and prints one line, <see cref="Marker"/> followed by JSON, which is the only thing the host reads. The tests' own output goes into that JSON as a
/// tail, never to stdout, so nothing the tests print can forge the record. The command's environment carries the workspace's base commit (read before the bundle is applied)
/// as <c>CHARGEHAND_BASE_COMMIT</c> and as <c>AGENT_CHECKS_BASE</c>, the default base of agent-checks, so a checker in the image can diff the branch without an origin. Exit code 0 when the command ran (its own verdict is in the record; 127 when the program is not in the image), 2 when it could not.</summary>
public static partial class VerifyBranchCli
{
    public const string Marker = ContainerVerifier.Marker;

    public const string Usage = "usage: chargehand verify-branch --bundle <file> --branch chargehand/<run> --timeout-seconds <n> [--work <dir>] [--tests-bundle <file>@chargehand/<run>]... -- <command...>";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var separator = args.ToList().IndexOf("--");
        if (separator < 0 || separator == args.Count - 1)
            return Fail(error);
        if (Options(args.Take(separator).ToList()) is not { } o)
            return Fail(error);
        List<string> argv = [.. args.Skip(separator + 1)];

        var (bundle, branch, work, seconds) = (o.Bundle, o.Branch, o.Work, o.Seconds);

        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)(e.Value ?? ""));
        // The workspace is still at the base commit here. The container has no origin to diff against, so a checker needs the commit itself.
        var baseCommit = (await Git(work, env, ["-c", "safe.directory=*", "rev-parse", "HEAD"], ct)).Stdout.Trim();
        if (baseCommit.Length > 0)
        {
            env["CHARGEHAND_BASE_COMMIT"] = baseCommit;
            env["AGENT_CHECKS_BASE"] = baseCommit;
        }
        var fetch = await Git(work, env, ["-c", "safe.directory=*", "-c", "fetch.fsckObjects=true", "-c", "core.hooksPath=/dev/null", "fetch", "--quiet", bundle, $"refs/heads/{branch}:refs/heads/verify-target"], ct);
        if (fetch.ExitCode != 0)
            return Record(output, argv, -1, $"the bundle does not apply to the workspace: {fetch.Tail}", false, TimeSpan.Zero, "", exitCode: 2);
        var checkout = await Git(work, env, ["-c", "safe.directory=*", "-c", "core.hooksPath=/dev/null", "checkout", "--quiet", "--detach", "verify-target"], ct);
        if (checkout.ExitCode != 0)
            return Record(output, argv, -1, $"the branch could not be checked out: {checkout.Tail}", false, TimeSpan.Zero, "", exitCode: 2);
        var commit = (await Git(work, env, ["-c", "safe.directory=*", "rev-parse", "HEAD"], ct)).Stdout.Trim();

        return await RunChecksAsync(output, argv, o, env, baseCommit, commit, ct);
    }

    private static async Task<int> RunChecksAsync(TextWriter output, List<string> argv, VerifyOptions o, Dictionary<string, string> env, string baseCommit, string commit, CancellationToken ct)
    {
        var (work, timeout) = (o.Work, TimeSpan.FromSeconds(o.Seconds));
        var (usable, dropped) = o.Tests.Count > 0 ? await ProbeBlindAsync(o.Tests, baseCommit, work, env, argv, timeout, ct) : ([], []);
        if (o.Tests.Count > 0 && !await CheckOutAsync(work, env, "verify-target", ct))
            return Record(output, argv, -1, "the branch could not be checked out again after the blind probe", false, TimeSpan.Zero, "", exitCode: 2);

        var result = await RunCommandAsync(argv, env, work, timeout, ct);
        if (result is null)
            return Record(output, argv, 127, $"{argv[0]}: command not found", false, TimeSpan.Zero, commit, exitCode: 0);
        var blind = o.Tests.Count == 0 ? null
            : result is { ExitCode: 0, TimedOut: false } && usable.Count > 0 ? await RunBlindAsync(usable, baseCommit, work, env, argv, timeout, dropped, ct)
            : new BlindTestResult(0, 0, [], dropped);
        return Record(output, argv, result.TimedOut ? -1 : result.ExitCode, result.OutputTail, result.TimedOut, result.Duration, commit, exitCode: 0, blind);
    }

    private static async Task<SandboxResult?> RunCommandAsync(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> env, string work, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await ProcessRunner.RunAsync(argv[0], argv.Skip(1), env, work, timeout, ct);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The options before <c>--</c>; null when one is unknown, has no value, or the bundle, a <c>chargehand/</c> branch or a positive timeout is missing.</summary>
    private static VerifyOptions? Options(List<string> options)
    {
        string? bundle = null, branch = null;
        List<OverlayBundle> tests = [];
        var work = "/work";
        var seconds = 0;
        for (var i = 0; i < options.Count; i += 2)
        {
            if (i + 1 >= options.Count)
                return null;
            switch (options[i])
            {
                case "--bundle": bundle = options[i + 1]; break;
                case "--branch": branch = options[i + 1]; break;
                case "--work": work = options[i + 1]; break;
                case "--tests-bundle" when OverlayBundle.Parse(options[i + 1]) is { } t: tests.Add(t); break;
                case "--timeout-seconds" when int.TryParse(options[i + 1], out var n): seconds = n; break;
                default: return null;
            }
        }
        if (bundle is null || branch is null || !branch.StartsWith("chargehand/", StringComparison.Ordinal) || branch.Contains("..", StringComparison.Ordinal) || seconds < 1)
            return null;
        return new VerifyOptions(bundle, branch, work, seconds, tests);
    }

    private static int Record(TextWriter output, IReadOnlyList<string> argv, int exit, string tail, bool timedOut, TimeSpan duration, string commit, int exitCode, BlindTestResult? blind = null)
    {
        output.WriteLine(Marker + JsonSerializer.Serialize(new VerificationRecord(argv, exit, tail, timedOut, (long)duration.TotalMilliseconds, commit, blind), VerificationJsonContext.Default.VerificationRecord));
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

/// <summary>The record of <see cref="VerifyBranchCli"/> without reflection (ADR 0060), as the anonymous type before it: snake_case, in declaration order.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(VerificationRecord))]
[JsonSerializable(typeof(BlindTestResult))]
internal sealed partial class VerificationJsonContext : JsonSerializerContext;

internal sealed record VerificationRecord(IReadOnlyList<string> Argv, int ExitCode, string OutputTail, bool TimedOut, long DurationMs, string Commit, BlindTestResult? Blind = null);
