using Chargehand.Hosts.Session;
using Chargehand.Kernel;

// The in-container session driver (ADR 0060): a small Native AOT binary in every session image. The driver moves here
// from src/Chargehand.Driven with the Sessions module; verify-branch is what a verification container runs.
if (args is ["--version"])
{
    Console.WriteLine(BuildInfo.Version);
    return 0;
}

if (args is ["verify-branch", .. var verifyBranchArgs])
{
    using var stop = new CancellationTokenSource();
    using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, _ => stop.Cancel());
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    return await VerifyBranchCli.RunAsync(verifyBranchArgs, Console.Out, Console.Error, stop.Token);
}

Console.Error.WriteLine("usage: chargehand-session --version | verify-branch ...");
return 2;
