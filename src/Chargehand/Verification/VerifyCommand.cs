using System.Text.Json;
using Chargehand.Sandbox;

namespace Chargehand.Verification;

/// <param name="Source">"request" (the caller named the command) or "detected".</param>
public sealed record VerifyPlan(IReadOnlyList<string> Argv, string Source);

/// <summary>The command that verifies a writing node's change (ADR 0035): the request's, else the repository's own test command.</summary>
public static class VerifyCommand
{
    /// <summary>Null when the caller named none and the directory has no recognisable test setup.</summary>
    public static VerifyPlan? Resolve(IReadOnlyList<string>? requested, string directory)
    {
        if (requested is { Count: > 0 })
            return new VerifyPlan(requested, "request");
        return Detect(directory) is { } argv ? new VerifyPlan(argv, "detected") : null;
    }

    private static string[]? Detect(string dir)
    {
        bool Has(string pattern) => Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).Any();
        if (Has("*.sln") || Has("*.slnx") || Has("*.csproj"))
            return ["dotnet", "test"];
        if (File.Exists(Path.Combine(dir, "package.json")) && HasNpmTest(Path.Combine(dir, "package.json")))
            return ["npm", "test"];
        if (File.Exists(Path.Combine(dir, "pytest.ini")) || PyprojectUsesPytest(dir))
            return ["python3", "-m", "pytest"];
        if (File.Exists(Path.Combine(dir, "pyproject.toml")) || HasPythonTests(dir))
            return ["python3", "-m", "unittest"];
        if (File.Exists(Path.Combine(dir, "Cargo.toml")))
            return ["cargo", "test"];
        if (File.Exists(Path.Combine(dir, "go.mod")))
            return ["go", "test", "./..."];
        return null;
    }

    private static bool HasNpmTest(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            return doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.TryGetProperty("test", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool PyprojectUsesPytest(string dir) =>
        File.Exists(Path.Combine(dir, "pyproject.toml")) && File.ReadAllText(Path.Combine(dir, "pyproject.toml")).Contains("[tool.pytest", StringComparison.Ordinal);

    private static bool HasPythonTests(string dir) =>
        Directory.Exists(Path.Combine(dir, "tests")) && Directory.EnumerateFiles(Path.Combine(dir, "tests"), "*.py", SearchOption.AllDirectories).Any();
}

public sealed record VerifyOutcome(VerifyPlan Plan, int ExitCode, string OutputTail, bool TimedOut, TimeSpan Duration)
{
    public bool Passed => ExitCode == 0 && !TimedOut;
}

/// <summary>Runs a <see cref="VerifyPlan"/> in a sandbox with the profile's network and environment settings.</summary>
public sealed class Verifier(ISandbox sandbox, bool network, IReadOnlyList<string> passEnv)
{
    public string SandboxKind => sandbox.Kind;

    public bool Network => network;

    public async Task<VerifyOutcome> RunAsync(VerifyPlan plan, string directory, TimeSpan timeout, CancellationToken ct)
    {
        var r = await sandbox.RunAsync(new SandboxSpec(plan.Argv, directory, timeout, network, passEnv), ct);
        return new VerifyOutcome(plan, r.ExitCode, r.OutputTail, r.TimedOut, r.Duration);
    }
}
