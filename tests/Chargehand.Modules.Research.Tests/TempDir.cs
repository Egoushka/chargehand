namespace Chargehand.Modules.Research.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("chargehand-test-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
