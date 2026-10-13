using System.Reflection;

namespace Chargehand.Architecture.Tests;

/// <summary>ADR 0060: the rules that keep the feature modules apart. The compiler cannot see them; these tests do.</summary>
public class ModuleRuleTests
{
    private const int MaxFileLines = 400;

    public static TheoryData<string> Projects() => new(Skeleton.ProjectFiles().Select(Skeleton.Name));

    [Theory]
    [MemberData(nameof(Projects))]
    public void Every_project_has_a_layer(string project) =>
        Assert.True(Skeleton.Layers.ContainsKey(project), $"{project} has no row in Skeleton.Layers");

    [Fact]
    public void References_point_down_only()
    {
        var upward = (
            from file in Skeleton.ProjectFiles()
            let name = Skeleton.Name(file)
            from reference in Skeleton.References(file)
            where !Skeleton.Layers.TryGetValue(reference, out var layer) || layer >= Skeleton.Layers[name]
            select $"{name} -> {reference}").ToList();

        // A reference outside the table is an old src/Chargehand* project: the port goes old -> new, never back.
        Assert.Empty(upward);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void A_module_is_public_only_in_its_Api_namespace(string project)
    {
        if (project.StartsWith("Chargehand.Hosts.", StringComparison.Ordinal) || project == "Chargehand.Kernel")
        {
            return;
        }

        var leaks = Assembly.Load(project).GetExportedTypes()
            .Where(t => t.Namespace is not { } ns || !(ns == $"{project}.Api" || ns.StartsWith($"{project}.Api.", StringComparison.Ordinal)))
            .Select(t => t.FullName);

        Assert.Empty(leaks);
    }

    [Fact]
    public void Source_files_stay_under_the_line_budget()
    {
        var tooLong = Skeleton.Folders
            .SelectMany(f => Directory.EnumerateFiles(Path.Combine(Skeleton.Root, f), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (File: Path.GetRelativePath(Skeleton.Root, f), Lines: File.ReadLines(f).Count()))
            .Where(f => f.Lines > MaxFileLines)
            .Select(f => $"{f.File}: {f.Lines} lines");

        Assert.Empty(tooLong);
    }
}
