using Chargehand.Config;

namespace Chargehand.Tests;

/// <summary>A temporary install root: the repository's prompts, and one preset derived from cheap with a services block.</summary>
internal sealed class PresetRoot : IDisposable
{
    private readonly TempDir _dir = new();

    public PresetRoot(string presetName, string servicesYaml)
    {
        Copy(Repo.Path("prompts"), System.IO.Path.Combine(_dir.Path, "prompts"));
        Directory.CreateDirectory(System.IO.Path.Combine(_dir.Path, "presets"));
        var yaml = File.ReadAllText(Repo.Path("presets", "cheap.yaml"))
            .Replace("name: cheap", $"name: {presetName}", StringComparison.Ordinal)
            .Replace("    budget:", servicesYaml + "    budget:", StringComparison.Ordinal);
        File.WriteAllText(System.IO.Path.Combine(_dir.Path, "presets", presetName + ".yaml"), yaml);
        File.Copy(Repo.Path("prompts", "preset", "cheap.md"), System.IO.Path.Combine(_dir.Path, "prompts", "preset", presetName + ".md"));
    }

    public string Path => _dir.Path;

    public static Preset Load(string presetName, string servicesYaml)
    {
        using var root = new PresetRoot(presetName, servicesYaml);
        return Preset.Load(System.IO.Path.Combine(root.Path, "presets"), presetName);
    }

    public void Dispose() => _dir.Dispose();

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, System.IO.Path.Combine(to, System.IO.Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from))
            Copy(dir, System.IO.Path.Combine(to, System.IO.Path.GetFileName(dir)));
    }
}
