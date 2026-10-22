using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Onboarding;

/// <summary>A command <c>chargehand adopt</c> runs in the session image: <c>setup</c> (installs what the others need), <c>build</c>, <c>test</c> or <c>lint</c>.</summary>
public sealed record AdoptProbe(string Kind, IReadOnlyList<string> Command, string Source)
{
    public string Line => string.Join(' ', Command);
}

/// <summary>How a probe ended: <c>passed</c>, <c>missing_tool</c> (the image lacks the command), <c>timed_out</c> or <c>failed</c>.</summary>
/// <param name="Tail">The last lines of its output, for a probe that did not pass.</param>
/// <param name="Tool">For <c>missing_tool</c>: the command the shell could not find, else the probe's own.</param>
public sealed record AdoptResult(AdoptProbe Probe, string Outcome, TimeSpan Took, string Tail, string? Tool = null)
{
    public bool Passed => Outcome == AdoptProbes.Passed;
}

/// <summary>The commands <c>chargehand adopt</c> tries, from what <see cref="RepositoryDetector"/> found, and how it reads their results. Pure, apart from reading
/// <c>package.json</c> and the names of files at the repository's top level.</summary>
public static partial class AdoptProbes
{
    public const string Passed = "passed", MissingTool = "missing_tool", TimedOut = "timed_out", Failed = "failed";

    /// <summary>Test commands tried at most: the detector's first, then the next while one fails.</summary>
    public const int MaxTestCandidates = 3;

    private const int TailLines = 8;

    /// <summary>Setup, build and lint commands per ecosystem, in the detector's order; the tests come from the detector.</summary>
    public static IReadOnlyList<AdoptProbe> For(RepositoryScan scan, string directory)
    {
        List<AdoptProbe> probes = [];
        foreach (var ecosystem in scan.Ecosystems.Select(e => e.Name))
            probes.AddRange(ecosystem switch
            {
                "dotnet" => [new("build", ["dotnet", "build"], "dotnet"), new("lint", ["dotnet", "format", "--verify-no-changes"], "dotnet")],
                "node" => Node(directory),
                "python" => File.Exists(Path.Combine(directory, "ruff.toml")) || Mentions(directory, "pyproject.toml", "[tool.ruff") ? [new("lint", ["ruff", "check", "."], "ruff")] : [],
                "go" => [new("build", ["go", "build", "./..."], "go.mod"), new("lint", ["go", "vet", "./..."], "go.mod")],
                "rust" => [new("build", ["cargo", "build"], "Cargo.toml"), new("lint", ["cargo", "clippy"], "Cargo.toml")],
                "gradle" => [new("build", [File.Exists(Path.Combine(directory, "gradlew")) ? "./gradlew" : "gradle", "assemble"], "gradle")],
                "maven" => [new("build", ["mvn", "-q", "-DskipTests", "package"], "pom.xml")],
                _ => [],
            });
        probes.AddRange(scan.Verify.Take(MaxTestCandidates).Select(v => new AdoptProbe("test", v.Command, v.Source)));
        return probes;
    }

    private static IEnumerable<AdoptProbe> Node(string directory)
    {
        var scripts = Scripts(directory);
        if (File.Exists(Path.Combine(directory, "package-lock.json")))
            yield return new("setup", ["npm", "ci"], "package-lock.json");
        if (scripts.Contains("build"))
            yield return new("build", ["npm", "run", "build"], "package.json");
        if (scripts.Contains("lint"))
            yield return new("lint", ["npm", "run", "lint"], "package.json");
    }

    private static HashSet<string> Scripts(string directory)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
            return node?["scripts"] is JsonObject scripts ? [.. scripts.Select(s => s.Key)] : [];
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static bool Mentions(string directory, string file, string text)
    {
        var path = Path.Combine(directory, file);
        return File.Exists(path) && File.ReadAllText(path).Contains(text, StringComparison.Ordinal);
    }

    /// <summary>Exit 126 or 127, or a shell's "not found", is a tool the image lacks (the one the shell names, else the probe's command); a null exit is a probe stopped at its time limit.</summary>
    public static AdoptResult Classify(AdoptProbe probe, int? exitCode, string output, TimeSpan took)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = string.Join('\n', lines.TakeLast(TailLines));
        var named = lines.Select(l => NotFound().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        var outcome = exitCode switch
        {
            null => TimedOut,
            0 => Passed,
            126 or 127 => MissingTool,
            _ when named is not null || lines.Any(l => l.Contains("executable file not found", StringComparison.Ordinal)) => MissingTool,
            _ => Failed,
        };
        return new AdoptResult(probe, outcome, took, outcome == Passed ? "" : tail, outcome == MissingTool ? named ?? probe.Command[0] : null);
    }

    [GeneratedRegex(@"([\w.+-]+): (?:command )?not found")]
    private static partial Regex NotFound();

    /// <summary>The test command to set: the first test probe that passed.</summary>
    public static AdoptProbe? Verify(IReadOnlyList<AdoptResult> results) => results.FirstOrDefault(r => r.Probe.Kind == "test" && r.Passed)?.Probe;

    /// <summary>The profile with <c>driven.services.verify[repository]</c> set to <paramref name="command"/>, every other key as it was. Null when the text is not a JSON object
    /// (a profile with comments cannot be rewritten without losing them).</summary>
    public static string? WithVerify(string profileJson, string repository, IReadOnlyList<string> command)
    {
        JsonObject root;
        try
        {
            if (JsonNode.Parse(profileJson) is not JsonObject parsed)
                return null;
            root = parsed;
        }
        catch (JsonException)
        {
            return null;
        }
        var driven = Child(root, "driven");
        var services = Child(driven, "services");
        var verify = Child(services, "verify");
        verify[repository] = new JsonArray([.. command.Select(c => JsonValue.Create(c))]);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>The entry to paste by hand when the profile is not rewritten.</summary>
    public static string Snippet(string repository, IReadOnlyList<string> command) =>
        $"\"driven\": {{ \"services\": {{ \"verify\": {{ {JsonSerializer.Serialize(repository)}: {JsonSerializer.Serialize(command)} }} }} }}";

    private static JsonObject Child(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }
}
