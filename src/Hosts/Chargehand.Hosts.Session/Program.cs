using Chargehand.Kernel;

// The in-container session driver (ADR 0060): a small Native AOT binary in every session image. The driver moves here
// from src/Chargehand.Driven with the Sessions module.
if (args is ["--version"])
{
    Console.WriteLine(BuildInfo.Version);
    return 0;
}

Console.Error.WriteLine("usage: chargehand-session --version");
return 2;
