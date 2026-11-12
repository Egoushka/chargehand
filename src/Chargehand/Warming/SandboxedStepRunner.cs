using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Warmer.Api;

namespace Chargehand.Warming;

/// <summary>Runs a warm step through the sandbox agent sessions use (ADR 0035): the checkout and the shared caches are the only writable
/// places, credential locations are hidden, the network is the profile's own switch, and the environment is the sandbox's cut one plus
/// <paramref name="env"/> and the variables named in <paramref name="passEnv"/>. A step with a <see cref="WarmStep.Staging"/> folder writes NuGet
/// packages there (<c>NUGET_PACKAGES</c>) and nowhere shared: NuGet checks signatures only when it extracts a package, so a step that could write
/// the shared folder directly would skip the check. With <paramref name="pnpmStoreRoot"/>, a step with a <see cref="WarmStep.Scope"/> gets a pnpm
/// store of its own under it (<c>pnpm_config_store_dir</c>), so one repository's install cannot plant files another repository's install links.
/// Every step runs with pnpm's package-manager switch off, so a <c>packageManager</c> field cannot make pnpm download and run another pnpm.</summary>
public sealed class SandboxedStepRunner(ISandbox sandbox, bool network, IReadOnlyList<string> passEnv, IReadOnlyDictionary<string, string> env, IReadOnlyList<string> sharedCaches, string? pnpmStoreRoot = null) : IStepRunner
{
    public static TimeSpan Timeout { get; } = TimeSpan.FromMinutes(10);

    public async Task<StepExit> RunAsync(WarmStep step, CancellationToken ct)
    {
        foreach (var cache in sharedCaches)
            Directory.CreateDirectory(cache);
        // Only the environment turns this off: pnpm reads `packageManager` before its command line, and the variable outranks .npmrc and pnpm-workspace.yaml.
        var environment = new Dictionary<string, string>(env) { ["pnpm_config_manage_package_manager_versions"] = "false" };
        List<string> writable = [.. sharedCaches];
        if (pnpmStoreRoot is not null && step.Scope is { } scope)
        {
            var store = Path.Combine(Path.GetFullPath(pnpmStoreRoot), PlainName(scope));
            Directory.CreateDirectory(store);
            environment["pnpm_config_store_dir"] = store;
            writable.Add(store);
        }
        if (step.Staging is { } staging)
        {
            Directory.CreateDirectory(staging);
            environment["NUGET_PACKAGES"] = staging;
            writable.Add(staging);
        }
        var spec = new SandboxSpec([step.File!, .. step.Args], step.WorkDir, Timeout, network, passEnv, environment, writable);
        var result = await sandbox.RunAsync(spec, ct).ConfigureAwait(false);
        return new StepExit(result.ExitCode, result.TimedOut, result.OutputTail);
    }

    private static string PlainName(string scope) =>
        scope.Length == 0 || scope is "." or ".." || scope.IndexOfAny(['/', '\\', '\0']) >= 0
            ? throw new ArgumentException($"the warm step scope '{scope}' is not one plain name", nameof(scope))
            : scope;
}
