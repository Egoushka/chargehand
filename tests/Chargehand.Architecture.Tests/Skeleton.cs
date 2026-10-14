using System.Xml.Linq;

namespace Chargehand.Architecture.Tests;

/// <summary>The projects of the feature-module skeleton (ADR 0060), found on disk, with their layer.</summary>
internal static class Skeleton
{
    /// <summary>Bottom to top. A project references only projects in a lower layer; a new project needs a row here.</summary>
    public static readonly IReadOnlyDictionary<string, int> Layers = new Dictionary<string, int>
    {
        // The published wire contract (schemas, result/v1): the one project outside the skeleton new code may use.
        ["Chargehand.Contracts"] = 0,
        ["Chargehand.Kernel"] = 1,
        ["Chargehand.Modules.Ledger"] = 2,
        ["Chargehand.Modules.Memory"] = 2,
        ["Chargehand.Modules.Journal"] = 2,
        // driven.forge: Handover (push, pull request) and Oversight (pull-request outcomes) both read it.
        ["Chargehand.Modules.Forge"] = 2,
        // Accounts prices a request against the account's spend, which the Ledger keeps.
        ["Chargehand.Modules.Accounts"] = 3,
        // Below Sessions, where the governor runs (ADR 0060, amendment of 2026-10-14).
        ["Chargehand.Modules.Oversight"] = 3,
        ["Chargehand.Modules.Runners"] = 4,
        ["Chargehand.Modules.Sessions"] = 4,
        ["Chargehand.Modules.Gateway"] = 4,
        // Below Tasks, which calls the handover and integrator mid-task.
        ["Chargehand.Modules.Handover"] = 5,
        ["Chargehand.Modules.Tasks"] = 6,
        ["Chargehand.Modules.Batches"] = 7,
        ["Chargehand.Modules.Research"] = 7,
        ["Chargehand.Modules.Intake"] = 8,
        ["Chargehand.Modules.Console"] = 8,
        ["Chargehand.Modules.Evaluation"] = 8,
        ["Chargehand.Modules.Onboarding"] = 8,
        ["Chargehand.Modules.Watch"] = 8,
        ["Chargehand.Runtimes.ClaudeCode"] = 8,
        ["Chargehand.Runtimes.OpenCode"] = 8,
        ["Chargehand.Hosts.Cli"] = 9,
        ["Chargehand.Hosts.Server"] = 9,
        ["Chargehand.Hosts.Runner"] = 9,
        ["Chargehand.Hosts.Session"] = 9,
    };

    public static readonly string[] Folders = ["src/Shared", "src/Modules", "src/Runtimes", "src/Hosts"];

    public static string Root { get; } = FindRoot();

    public static IEnumerable<string> ProjectFiles() =>
        Folders.SelectMany(f => Directory.EnumerateFiles(Path.Combine(Root, f), "*.csproj", SearchOption.AllDirectories));

    public static string Name(string projectFile) => Path.GetFileNameWithoutExtension(projectFile);

    public static IEnumerable<string> References(string projectFile) =>
        XDocument.Load(projectFile).Descendants("ProjectReference")
            .Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value.Replace('\\', '/')));

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Chargehand.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Chargehand.slnx not found above the test output.");
    }
}
