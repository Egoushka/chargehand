using System.Text.Json;

namespace Chargehand.Modules.Sessions.Api;

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
        // A repository's own check script is its real test command (docs/guide/driven.md, "scripts/check.sh"); prefer it
        // over a guessed ecosystem command, the way chargehand's own onboarding (RepositoryDetector.AddScripts) already does.
        if (File.Exists(Path.Combine(dir, "scripts", "check.sh")))
            return ["sh", "scripts/check.sh"];
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

    /// <summary>Changed paths that decide whether the verification means anything: tests, build files, scripts, workflows and the
    /// verification command's own entry point. The worker may edit them; the artifact lists them so a reader can see it.</summary>
    public static IReadOnlyList<string> VerificationPaths(IReadOnlyList<string> changed, VerifyPlan? plan) =>
        [.. changed.Where(p => IsVerificationPath(p, plan))];

    private static bool IsVerificationPath(string path, VerifyPlan? plan)
    {
        var name = Path.GetFileName(path);
        var segments = path.Split('/');
        return segments[..^1].Any(s => s is "test" or "tests" or "scripts" or ".github" or "__tests__")
            || name.Contains("test", StringComparison.OrdinalIgnoreCase) || name.Contains("spec", StringComparison.OrdinalIgnoreCase)
            || name is "Makefile" or "package.json" or "pyproject.toml" or "Cargo.toml" or "go.mod" or "pytest.ini" or "Directory.Build.props"
            || name.EndsWith(".csproj", StringComparison.Ordinal) || name.EndsWith(".sln", StringComparison.Ordinal) || name.EndsWith(".slnx", StringComparison.Ordinal)
            || (plan is not null && plan.Argv.Count > 0 && (plan.Argv[0] == path || plan.Argv[0] == "./" + path));
    }
}
