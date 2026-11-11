using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Warmer.Api;

namespace Chargehand.Warming;

/// <summary>Runs a warm step through the sandbox agent sessions use (ADR 0035): the checkout and the shared caches are the only writable
/// places, credential locations are hidden, the network is the profile's own switch, and the environment is the sandbox's cut one plus
/// <paramref name="env"/> and the variables named in <paramref name="passEnv"/>.</summary>
public sealed class SandboxedStepRunner(ISandbox sandbox, bool network, IReadOnlyList<string> passEnv, IReadOnlyDictionary<string, string> env, IReadOnlyList<string> sharedCaches) : IStepRunner
{
    public static TimeSpan Timeout { get; } = TimeSpan.FromMinutes(10);

    public async Task<int> RunAsync(WarmStep step, CancellationToken ct)
    {
        foreach (var cache in sharedCaches)
            Directory.CreateDirectory(cache);
        var spec = new SandboxSpec([step.File!, .. step.Args], step.WorkDir, Timeout, network, passEnv, env, sharedCaches);
        var result = await sandbox.RunAsync(spec, ct).ConfigureAwait(false);
        return result.TimedOut ? -1 : result.ExitCode;
    }
}
