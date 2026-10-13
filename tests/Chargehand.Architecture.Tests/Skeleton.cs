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
        // Accounts prices a request against the account's spend, which the Ledger keeps.
        ["Chargehand.Modules.Accounts"] = 3,
        ["Chargehand.Modules.Runners"] = 3,
        ["Chargehand.Modules.Sessions"] = 3,
        ["Chargehand.Modules.Gateway"] = 3,
        ["Chargehand.Modules.Tasks"] = 4,
        ["Chargehand.Modules.Handover"] = 4,
        ["Chargehand.Modules.Oversight"] = 4,
        ["Chargehand.Modules.Batches"] = 5,
        ["Chargehand.Modules.Research"] = 5,
        ["Chargehand.Modules.Intake"] = 6,
        ["Chargehand.Modules.Console"] = 6,
        ["Chargehand.Modules.Evaluation"] = 6,
        ["Chargehand.Modules.Onboarding"] = 6,
        ["Chargehand.Modules.Watch"] = 6,
        ["Chargehand.Runtimes.ClaudeCode"] = 6,
        ["Chargehand.Runtimes.OpenCode"] = 6,
        ["Chargehand.Hosts.Cli"] = 7,
        ["Chargehand.Hosts.Server"] = 7,
        ["Chargehand.Hosts.Runner"] = 7,
        ["Chargehand.Hosts.Session"] = 7,
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
