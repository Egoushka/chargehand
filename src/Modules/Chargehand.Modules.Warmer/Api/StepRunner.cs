using System.Diagnostics;

namespace Chargehand.Modules.Warmer.Api;

/// <summary>Runs one step and returns its exit code. Tests give a fake; the host gives <see cref="ProcessStepRunner"/>.</summary>
public interface IStepRunner
{
    Task<int> RunAsync(WarmStep step, CancellationToken ct);
}

/// <summary>Starts the step's program with no shell, discards its output and stops it after <see cref="Timeout"/>. <paramref name="env"/> is added to the child's environment, which is how the shared pnpm store reaches it.</summary>
public sealed class ProcessStepRunner(IReadOnlyDictionary<string, string> env) : IStepRunner
{
    public static TimeSpan Timeout { get; } = TimeSpan.FromMinutes(10);

    private static readonly string[] Inherited =
    [
        "PATH", "HOME", "USER", "LANG", "TMPDIR", "XDG_CACHE_HOME", "XDG_DATA_HOME", "NUGET_PACKAGES", "DOTNET_ROOT", "DOTNET_CLI_HOME",
        "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT", "PLAYWRIGHT_BROWSERS_PATH", "SSL_CERT_FILE", "NODE_EXTRA_CA_CERTS",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy",
    ];

    /// <summary>What a step's program sees: the few variables the tools need from this process, then <paramref name="extra"/>. Tokens and every other variable of the daemon stay out.</summary>
    public static IReadOnlyDictionary<string, string> ChildEnvironment(IReadOnlyDictionary<string, string> extra, Func<string, string?> get)
    {
        var result = new Dictionary<string, string>();
        foreach (var name in Inherited)
        {
            if (get(name) is { Length: > 0 } value)
                result[name] = value;
        }
        foreach (var (key, value) in extra)
            result[key] = value;
        return result;
    }

    public async Task<int> RunAsync(WarmStep step, CancellationToken ct)
    {
        var info = new ProcessStartInfo(step.File!) { WorkingDirectory = step.WorkDir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in step.Args)
            info.ArgumentList.Add(arg);
        info.Environment.Clear();
        foreach (var (key, value) in ChildEnvironment(env, Environment.GetEnvironmentVariable))
            info.Environment[key] = value;
        using var process = Process.Start(info);
        if (process is null)
            throw new InvalidOperationException($"could not start {step.File}");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(CancellationToken.None), process.StandardError.ReadToEndAsync(CancellationToken.None));
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the timeout and the kill.
            }
            ct.ThrowIfCancellationRequested();
            return -1;
        }
    }
}
