using Chargehand.Kernel;

// The rewrite's CLI host (ADR 0060). Verbs move here from src/Chargehand.Cli as their modules are ported.
if (args is ["--version"])
{
    Console.WriteLine(BuildInfo.Version);
    return 0;
}

Console.Error.WriteLine("usage: chargehand --version");
return 2;
