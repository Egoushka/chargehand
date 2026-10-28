namespace Chargehand.Sandbox;

/// <summary>Runs the command with no confinement, only the cut environment and the timeout. Chosen only by
/// <c>sandbox.kind: none</c> (ADR 0035), and used by tests.</summary>
public sealed class NoSandbox : ISandbox
{
    public string Kind => "none";

    public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken ct)
    {
        using var launch = Wrap(spec);
        return await ProcessRunner.RunAsync(launch.FileName, launch.Arguments, launch.Environment, launch.WorkingDirectory, spec.Timeout, ct);
    }

    public SandboxLaunch Wrap(SandboxSpec spec)
    {
        var temp = Directory.CreateTempSubdirectory("chargehand-sbx-");
        return new SandboxLaunch(spec.Argv[0], [.. spec.Argv.Skip(1)], ProcessRunner.Environment(temp.FullName, spec.PassEnv, spec.Env), spec.WorkDirectory, temp.FullName);
    }
}
