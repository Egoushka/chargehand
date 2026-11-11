namespace Chargehand.Modules.Research.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("chargehand-test-").FullName;

    public void Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
