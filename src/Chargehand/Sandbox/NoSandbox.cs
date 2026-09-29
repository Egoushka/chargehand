namespace Chargehand.Sandbox;

/// <summary>Runs the command with no confinement, only the cut environment and the timeout. Chosen only by
/// <c>sandbox.kind: none</c> (ADR 0035), and used by tests.</summary>
public sealed class NoSandbox : ISandbox
{
    public string Kind => "none";

    public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken ct)
    {
        var temp = Directory.CreateTempSubdirectory("chargehand-sbx-");
        try
        {
            return await ProcessRunner.RunAsync(spec.Argv[0], spec.Argv.Skip(1), ProcessRunner.Environment(temp.FullName, spec.PassEnv), spec.WorkDirectory, spec.Timeout, ct);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
