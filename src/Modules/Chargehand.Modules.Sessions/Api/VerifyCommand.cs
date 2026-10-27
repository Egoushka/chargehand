using System.Text.Json;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Sessions.Api;

/// <param name="Source">"request" (the caller named the command), "ci" (read from the repository's own CI workflow), "detected", or "declared" (<c>scripts/check.sh</c>, ADR 0078).</param>
public sealed record VerifyPlan(IReadOnlyList<string> Argv, string Source);

/// <summary>The command that verifies a writing node's change (ADR 0035): the request's, else the repository's own test command.</summary>
public static class VerifyCommand
{
    private static readonly string[] NoOpBodies = ["true", "/bin/true", "/usr/bin/true", ":", "exit 0"];

    private const long MaxWorkflowBytes = 500_000;

    /// <summary>A command that provably checks nothing: empty, blank, or a shell no-op such as <c>true</c> or <c>:</c> (CHARGEHAND-431). A caller
    /// that names one is not believed; chargehand works out its own command instead.</summary>
    public static bool IsNoOp(IReadOnlyList<string> argv) =>
        argv.Count == 0 || argv.All(a => a.Trim().Length == 0) || NoOpBodies.Contains(argv[^1].Trim(), StringComparer.Ordinal);

    /// <summary>Null when the caller named none (or a no-op) and the directory has no recognisable test setup.</summary>
    public static VerifyPlan? Resolve(IReadOnlyList<string>? requested, string directory) =>
        requested is { Count: > 0 } && !IsNoOp(requested) ? new VerifyPlan(requested, "request") : Detect(directory);

    /// <summary>ADR 0078: the check a repository declares, with nothing detected: the request's (the profile's <c>driven.services.verify</c> is merged into it), then
    /// <c>scripts/check.sh</c>. Null: none, and CI is the only check.</summary>
    public static VerifyPlan? Declared(IReadOnlyList<string>? requested, string directory) =>
        requested is { Count: > 0 } && !IsNoOp(requested) ? new VerifyPlan(requested, "request")
        : File.Exists(Path.Combine(directory, "scripts", "check.sh")) ? new VerifyPlan(["sh", "scripts/check.sh"], "declared") : null;

    private static VerifyPlan? Detect(string dir)
    {
        // A repository's own check script is its real test command (docs/guide/driven.md, "scripts/check.sh"); prefer it
        // over a guessed ecosystem command, the way chargehand's own onboarding (RepositoryDetector.AddScripts) already does.
        if (File.Exists(Path.Combine(dir, "scripts", "check.sh")))
            return new(["sh", "scripts/check.sh"], "detected");
        if (DetectFromCi(dir) is { } ci)
            return new(ci, "ci");
        bool Has(string pattern) => Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).Any();
        if (Has("*.sln") || Has("*.slnx") || Has("*.csproj"))
            return new(["dotnet", "test"], "detected");
        if (File.Exists(Path.Combine(dir, "package.json")) && HasNpmTest(Path.Combine(dir, "package.json")))
            return new(["npm", "test"], "detected");
        if (File.Exists(Path.Combine(dir, "pytest.ini")) || PyprojectUsesPytest(dir))
            return new(["python3", "-m", "pytest"], "detected");
        if (File.Exists(Path.Combine(dir, "pyproject.toml")) || HasPythonTests(dir))
            return new(["python3", "-m", "unittest"], "detected");
        if (File.Exists(Path.Combine(dir, "Cargo.toml")))
            return new(["cargo", "test"], "detected");
        if (File.Exists(Path.Combine(dir, "go.mod")))
            return new(["go", "test", "./..."], "detected");
        return DetectJvm(dir) is { } jvm ? new(jvm, "detected") : null;
    }

    /// <summary>check, not test: ktlint and detekt (the lint tools of a JVM/Android build, the session-jdk image has the JDK and
    /// Gradle for, ADR 0048) bind their verification tasks to Gradle's check task by default.</summary>
    private static string[]? DetectJvm(string dir)
    {
        bool Has(string pattern) => Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).Any();
        if (File.Exists(Path.Combine(dir, "gradlew")))
            return ["./gradlew", "check", "--no-daemon"];
        if (Has("build.gradle") || Has("build.gradle.kts") || Has("settings.gradle") || Has("settings.gradle.kts"))
            return ["gradle", "check", "--no-daemon"];
        if (File.Exists(Path.Combine(dir, "pom.xml")))
            return ["mvn", "-B", "test"];
        return null;
    }

    /// <summary>The first <c>run:</c> step of <c>.github/workflows/*.yml</c> whose text names a test runner or a check (<see cref="RepositoryReadiness.RunsTests"/>), read as a
    /// shell command; null when the repository has no workflow, or none of its steps' commands look like tests.</summary>
    private static string[]? DetectFromCi(string dir)
    {
        var workflows = Path.Combine(dir, ".github", "workflows");
        if (!Directory.Exists(workflows))
            return null;
        var files = Directory.EnumerateFiles(workflows, "*.yml").Concat(Directory.EnumerateFiles(workflows, "*.yaml")).Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (new FileInfo(file).Length > MaxWorkflowBytes)
                continue;
            foreach (var run in RunSteps(File.ReadAllLines(file)))
                if (RepositoryReadiness.RunsTests([run]))
                    return ["sh", "-c", run];
        }
        return null;
    }

    /// <summary>The body of every <c>run:</c> step of a workflow file: a single line as is, or a <c>|</c>/<c>&gt;</c> block joined with <c>&amp;&amp;</c>.</summary>
    private static IEnumerable<string> RunSteps(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var indented = lines[i].TrimStart();
            if (!indented.StartsWith("run:", StringComparison.Ordinal))
                continue;
            var rest = indented["run:".Length..].Trim();
            if (rest.Length > 0 && rest is not "|" and not ">" and not "|-" and not ">-")
            {
                yield return rest;
                continue;
            }
            var (block, next) = ReadBlock(lines, i + 1);
            if (block.Count > 0)
                yield return string.Join(" && ", block);
            i = next - 1;
        }
    }

    /// <summary>The indented lines right after a <c>run: |</c>/<c>run: &gt;</c> header, and the index of the first line after them.</summary>
    private static (List<string> Lines, int Next) ReadBlock(string[] lines, int start)
    {
        List<string> block = [];
        int? indent = null;
        var i = start;
        for (; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
                continue;
            var own = lines[i].Length - lines[i].TrimStart().Length;
            indent ??= own;
            if (own < indent)
                break;
            block.Add(lines[i].Trim());
        }
        return (block, i);
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
                or "pom.xml" or "gradlew" or "build.gradle" or "build.gradle.kts" or "settings.gradle" or "settings.gradle.kts"
            || name.EndsWith(".csproj", StringComparison.Ordinal) || name.EndsWith(".sln", StringComparison.Ordinal) || name.EndsWith(".slnx", StringComparison.Ordinal)
            || (plan is not null && plan.Argv.Count > 0 && (plan.Argv[0] == path || plan.Argv[0] == "./" + path));
    }
}
