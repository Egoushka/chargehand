using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Warmer.Api;

namespace Chargehand.Warming;

/// <summary>Runs a warm step through the sandbox agent sessions use (ADR 0035): the checkout and the shared caches are the only writable
/// places, credential locations are hidden, the network is the profile's own switch, and the environment is the sandbox's cut one plus
/// <paramref name="env"/> and the variables named in <paramref name="passEnv"/>. A step with a <see cref="WarmStep.Staging"/> folder writes NuGet
/// packages there (<c>NUGET_PACKAGES</c>) and nowhere shared: NuGet checks signatures only when it extracts a package, so a step that could write
/// the shared folder directly would skip the check.</summary>
public sealed class SandboxedStepRunner(ISandbox sandbox, bool network, IReadOnlyList<string> passEnv, IReadOnlyDictionary<string, string> env, IReadOnlyList<string> sharedCaches) : IStepRunner
{
    public static TimeSpan Timeout { get; } = TimeSpan.FromMinutes(10);

    public async Task<StepExit> RunAsync(WarmStep step, CancellationToken ct)
    {
        foreach (var cache in sharedCaches)
            Directory.CreateDirectory(cache);
        var environment = env;
        var writable = sharedCaches;
        if (step.Staging is { } staging)
        {
            Directory.CreateDirectory(staging);
            environment = new Dictionary<string, string>(env) { ["NUGET_PACKAGES"] = staging };
            writable = [.. sharedCaches, staging];
        }
        var spec = new SandboxSpec([step.File!, .. step.Args], step.WorkDir, Timeout, network, passEnv, environment, writable);
        var result = await sandbox.RunAsync(spec, ct).ConfigureAwait(false);
        return new StepExit(result.ExitCode, result.TimedOut, result.OutputTail);
    }
}
