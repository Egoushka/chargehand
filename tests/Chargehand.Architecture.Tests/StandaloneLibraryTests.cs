namespace Chargehand.Architecture.Tests;

/// <summary>ADR 0086, decision 7: a standalone library can be lifted into its own repository, so it knows nothing about chargehand.</summary>
public class StandaloneLibraryTests
{
    public static TheoryData<string> Libraries() => new(Skeleton.Standalone);

    [Theory]
    [MemberData(nameof(Libraries))]
    public void A_standalone_library_references_no_chargehand_project_or_package(string library)
    {
        var project = Skeleton.ProjectFiles().Single(f => Skeleton.Name(f) == library);
        var references = Skeleton.References(project)
            .Concat(System.Xml.Linq.XDocument.Load(project).Descendants("PackageReference").Select(r => (string)r.Attribute("Include")!))
            .Where(r => r.StartsWith("Chargehand", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(references);
    }

    [Theory]
    [MemberData(nameof(Libraries))]
    public void A_standalone_library_names_chargehand_nowhere_in_its_source(string library)
    {
        var folder = Path.GetDirectoryName(Skeleton.ProjectFiles().Single(f => Skeleton.Name(f) == library))!;
        var mentions = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".md", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("chargehand", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(Skeleton.Root, f));

        Assert.Empty(mentions);
    }
}
