using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Verification;

public sealed record VerifyOutcome(VerifyPlan Plan, int ExitCode, string OutputTail, bool TimedOut, TimeSpan Duration)
{
    public bool Passed => ExitCode == 0 && !TimedOut;
}

/// <summary>Runs a <see cref="VerifyPlan"/> in a sandbox with the profile's network and environment settings.</summary>
public sealed class Verifier(ISandbox sandbox, bool network, IReadOnlyList<string> passEnv)
{
    /// <summary>Refuses an npx auto-install (issue #911): with no local binary, npx would otherwise fetch and run any registry
    /// package that happens to share the missing command's name.</summary>
    private static readonly IReadOnlyDictionary<string, string> NpxNoInstall = new Dictionary<string, string> { ["npm_config_yes"] = "false" };

    public string SandboxKind => sandbox.Kind;

    public bool Network => network;

    public async Task<VerifyOutcome> RunAsync(VerifyPlan plan, string directory, TimeSpan timeout, CancellationToken ct)
    {
        var r = await sandbox.RunAsync(new SandboxSpec(plan.Argv, directory, timeout, network, passEnv, NpxNoInstall), ct);
        return new VerifyOutcome(plan, r.ExitCode, r.OutputTail, r.TimedOut, r.Duration);
    }
}
