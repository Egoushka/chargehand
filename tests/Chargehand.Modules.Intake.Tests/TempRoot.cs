using System.Diagnostics;
using Chargehand.Contracts;

namespace Chargehand.Modules.Intake.Tests;

/// <summary>A temporary directory that can hold one committed repository.</summary>
internal sealed class TempRoot : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("chargehand-test-").FullName;

    /// <summary>A checkout under the root with one committed README.</summary>
    public RepositoryRef GitRepo()
    {
        var path = Directory.CreateDirectory(System.IO.Path.Combine(Path, "repo")).FullName;
        File.WriteAllText(System.IO.Path.Combine(path, "README.md"), "hello\n");
        Run(path, "init", "-q");
        Run(path, "add", "README.md");
        Run(path, "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "init");
        return new RepositoryRef(path, Run(path, "rev-parse", "HEAD").Trim());
    }

    public static string Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', args)} failed");
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
