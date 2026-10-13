using System.Xml.Linq;

namespace Chargehand.Architecture.Tests;

/// <summary>The projects of the feature-module skeleton (ADR 0060), found on disk, with their layer.</summary>
internal static class Skeleton
{
    /// <summary>Bottom to top. A project references only projects in a lower layer; a new project needs a row here.</summary>
    public static readonly IReadOnlyDictionary<string, int> Layers = new Dictionary<string, int>
    {
        ["Chargehand.Kernel"] = 0,
        // The published wire contract (schemas, result/v1): the one project outside the skeleton new code may use.
        ["Chargehand.Contracts"] = 0,
        ["Chargehand.Modules.Ledger"] = 1,
        ["Chargehand.Modules.Memory"] = 1,
        ["Chargehand.Modules.Journal"] = 1,
        // Accounts prices a request against the account's spend, which the Ledger keeps.
        ["Chargehand.Modules.Accounts"] = 2,
        ["Chargehand.Modules.Runners"] = 2,
        ["Chargehand.Modules.Sessions"] = 2,
        ["Chargehand.Modules.Gateway"] = 2,
        ["Chargehand.Modules.Tasks"] = 3,
        ["Chargehand.Modules.Handover"] = 3,
        ["Chargehand.Modules.Oversight"] = 3,
        ["Chargehand.Modules.Batches"] = 4,
        ["Chargehand.Modules.Research"] = 4,
        ["Chargehand.Modules.Intake"] = 5,
        ["Chargehand.Modules.Console"] = 5,
        ["Chargehand.Modules.Evaluation"] = 5,
        ["Chargehand.Modules.Onboarding"] = 5,
        ["Chargehand.Modules.Watch"] = 5,
        ["Chargehand.Runtimes.ClaudeCode"] = 5,
        ["Chargehand.Runtimes.OpenCode"] = 5,
        ["Chargehand.Hosts.Cli"] = 6,
        ["Chargehand.Hosts.Server"] = 6,
        ["Chargehand.Hosts.Runner"] = 6,
        ["Chargehand.Hosts.Session"] = 6,
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
