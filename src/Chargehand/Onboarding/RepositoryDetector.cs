using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Onboarding;

/// <summary>One ecosystem found in a repository: the marker files that gave it away and the package hosts its restore needs.</summary>
public sealed record DetectedEcosystem(string Name, IReadOnlyList<string> Markers, IReadOnlyList<string> Hosts);

/// <summary>A proposed test command (<c>context.verify</c>, an argv) and where it came from.</summary>
public sealed record VerifyCandidate(IReadOnlyList<string> Command, string Source);

/// <param name="Ecosystems">In the fixed detection order: dotnet, node, python, go, rust, gradle, maven.</param>
/// <param name="Verify">Best first, in the fixed priority order of <see cref="RepositoryDetector"/>.</param>
public sealed record RepositoryScan(IReadOnlyList<DetectedEcosystem> Ecosystems, IReadOnlyList<VerifyCandidate> Verify)
{
    public VerifyCandidate? BestVerify => Verify.Count > 0 ? Verify[0] : null;

    /// <summary>Every package host the ecosystems need, without repeats.</summary>
    public IReadOnlyList<string> Hosts => [.. Ecosystems.SelectMany(e => e.Hosts).Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// Reads the top level of a repository (and its <c>scripts/</c> directory) for marker files and says which ecosystems it uses, which
/// command probably runs its tests and which package hosts a restore needs. It reads names, <c>package.json</c> and the
/// <c>Makefile</c>; it never reads <c>.env</c> files and never runs anything. The test command is the first of: <c>scripts/check.sh</c>,
/// another <c>scripts/test*</c>, a <c>Makefile</c> <c>test</c> target, then dotnet, node, python, go, rust, gradle, maven.
/// </summary>
public static partial class RepositoryDetector
{
    private static readonly string[] DotNetHosts = ["api.nuget.org", "globalcdn.nuget.org"];
    private static readonly string[] NodeHosts = ["registry.npmjs.org"];
    private static readonly string[] YarnHosts = ["registry.npmjs.org", "registry.yarnpkg.com"];
    private static readonly string[] PythonHosts = ["pypi.org", "files.pythonhosted.org"];
    private static readonly string[] GoHosts = ["proxy.golang.org", "sum.golang.org"];
    private static readonly string[] RustHosts = ["crates.io", "index.crates.io", "static.crates.io"];
    private static readonly string[] GradleHosts = ["services.gradle.org", "plugins.gradle.org", "repo.maven.apache.org", "repo1.maven.org"];
    private static readonly string[] MavenHosts = ["repo.maven.apache.org", "repo1.maven.org"];

    private static readonly string[] LockFiles = ["package-lock.json", "pnpm-lock.yaml", "yarn.lock", "bun.lock", "bun.lockb"];

    private const long MaxFileBytes = 1_000_000;

    public static RepositoryScan Scan(string directory)
    {
        var files = Names(directory);
        var scripts = Names(Path.Combine(directory, "scripts"));
        var ecosystems = new List<DetectedEcosystem>();
        var verify = new List<VerifyCandidate>();

        if (scripts.Contains("check.sh"))
            verify.Add(new(["sh", "scripts/check.sh"], "scripts/check.sh"));
        foreach (var script in scripts.Where(s => s.StartsWith("test", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            verify.Add(new(script.EndsWith(".sh", StringComparison.Ordinal) ? ["sh", $"scripts/{script}"] : [$"./scripts/{script}"], $"scripts/{script}"));
        if (files.Contains("Makefile") && HasTestTarget(Path.Combine(directory, "Makefile")))
            verify.Add(new(["make", "test"], "Makefile test target"));

        if (files.Where(f => f.EndsWith(".sln", StringComparison.Ordinal) || f.EndsWith(".slnx", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } dotnet)
        {
            ecosystems.Add(new("dotnet", dotnet, DotNetHosts));
            verify.Add(new(["dotnet", "test", "--nologo"], dotnet[0]));
        }

        if (files.Contains("package.json"))
        {
            var lockFile = LockFiles.FirstOrDefault(files.Contains);
            ecosystems.Add(new("node", lockFile is null ? ["package.json"] : ["package.json", lockFile], lockFile == "yarn.lock" ? YarnHosts : NodeHosts));
            if (HasTestScript(Path.Combine(directory, "package.json")))
                verify.Add(new(NodeCommand(lockFile), lockFile ?? "package.json"));
        }

        var requirements = files.Where(f => RequirementsFile().IsMatch(f)).Order(StringComparer.Ordinal).ToList();
        if (files.Contains("pyproject.toml") || requirements.Count > 0)
        {
            List<string> markers = files.Contains("pyproject.toml") ? ["pyproject.toml", .. requirements] : requirements;
            ecosystems.Add(new("python", markers, PythonHosts));
            var install = requirements.Count > 0
                ? $"pip install -r {(requirements.Contains("requirements-dev.txt") ? "requirements-dev.txt" : requirements.Contains("requirements.txt") ? "requirements.txt" : requirements[0])} pytest"
                : "pip install -e . pytest";
            verify.Add(new(["sh", "-c", $"{install} && python3 -m pytest -q"], markers[0]));
        }

        if (files.Contains("go.mod"))
        {
            ecosystems.Add(new("go", ["go.mod"], GoHosts));
            verify.Add(new(["go", "test", "./..."], "go.mod"));
        }

        if (files.Contains("Cargo.toml"))
        {
            ecosystems.Add(new("rust", ["Cargo.toml"], RustHosts));
            verify.Add(new(["cargo", "test"], "Cargo.toml"));
        }

        if (files.Where(f => GradleFile().IsMatch(f) || f == "gradlew").Order(StringComparer.Ordinal).ToList() is { Count: > 0 } gradle)
        {
            ecosystems.Add(new("gradle", gradle, GradleHosts));
            verify.Add(new(files.Contains("gradlew") ? ["./gradlew", "test", "--no-daemon"] : ["gradle", "test", "--no-daemon"], gradle[0]));
        }

        if (files.Contains("pom.xml"))
        {
            ecosystems.Add(new("maven", ["pom.xml"], MavenHosts));
            verify.Add(new(["mvn", "-B", "test"], "pom.xml"));
        }

        return new RepositoryScan(ecosystems, verify);
    }

    /// <summary>The package hosts an ecosystem's restore needs (<c>dotnet</c>, <c>node</c>, <c>python</c>, <c>go</c>, <c>rust</c>, <c>gradle</c>, <c>maven</c>); none for any other name.</summary>
    public static IReadOnlyList<string> HostsFor(string ecosystem) => ecosystem switch
    {
        "dotnet" => DotNetHosts,
        "node" => NodeHosts,
        "python" => PythonHosts,
        "go" => GoHosts,
        "rust" => RustHosts,
        "gradle" => GradleHosts,
        "maven" => MavenHosts,
        _ => [],
    };

    /// <summary>Hosts of <paramref name="needed"/> that no entry of <paramref name="allow"/> lets through, in the order given. An entry that is not a valid
    /// allowlist entry is ignored, as the egress proxy would refuse it.</summary>
    public static IReadOnlyList<string> Uncovered(IEnumerable<string> needed, IEnumerable<string> allow)
    {
        var valid = allow.Where(Valid).ToList();
        if (valid.Count == 0)
            return [.. needed];
        var matcher = new AllowlistMatcher(valid);
        return [.. needed.Where(host => !matcher.Check(host, 443).Allowed)];
    }

    private static bool Valid(string entry)
    {
        try
        {
            _ = new AllowlistMatcher([entry]);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string[] NodeCommand(string? lockFile) => lockFile switch
    {
        "package-lock.json" => ["sh", "-c", "npm ci && npm test"],
        "pnpm-lock.yaml" => ["sh", "-c", "corepack enable && pnpm install --frozen-lockfile && pnpm test"],
        "yarn.lock" => ["sh", "-c", "corepack enable && yarn install --frozen-lockfile && yarn test"],
        "bun.lock" or "bun.lockb" => ["sh", "-c", "bun install --frozen-lockfile && bun run test"],
        _ => ["sh", "-c", "npm install && npm test"],
    };

    private static HashSet<string> Names(string directory) =>
        Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory).Select(f => Path.GetFileName(f)!)]
            : [];

    private static bool HasTestScript(string packageJson)
    {
        try
        {
            if (new FileInfo(packageJson).Length > MaxFileBytes)
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.String
                && test.GetString() is { } text && !text.Contains("no test specified", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasTestTarget(string makefile) =>
        new FileInfo(makefile).Length <= MaxFileBytes && TestTarget().IsMatch(File.ReadAllText(makefile));

    [GeneratedRegex(@"^requirements[A-Za-z0-9._-]*\.txt$")]
    private static partial Regex RequirementsFile();

    [GeneratedRegex(@"^(build|settings)\.gradle(\.kts)?$")]
    private static partial Regex GradleFile();

    [GeneratedRegex(@"^test\s*:", RegexOptions.Multiline)]
    private static partial Regex TestTarget();
}
