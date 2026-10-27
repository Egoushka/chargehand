using System.Diagnostics;

namespace Chargehand.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("chargehand-test-").FullName;

    public string Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>A script the test then runs. See <see cref="ScriptFile.Seal"/>.</summary>
    public string WriteExecutable(string relative, string content)
    {
        var full = Write(relative, content);
        ScriptFile.Seal(full);
        return full;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class ScriptFile
{
    /// <summary>Writes a file a test will exec: <see cref="Seal"/> after the write.</summary>
    public static void Write(string path, string content)
    {
        File.WriteAllText(path, content);
        Seal(path);
    }

    /// <summary>Makes a file this test wrote startable. A file this process wrote can fail to start with "Text file busy" (ETXTBSY) while another test's fork, between fork
    /// and exec, still holds a copy of the write descriptor. So the file is replaced by a copy made by a child (<c>cp</c>) that has exited before the file is used: no
    /// descriptor of this process ever pointed at it. The old file is renamed away and never executed.</summary>
    public static void Seal(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".staging";
        File.Move(path, staging);
        try
        {
            var psi = new ProcessStartInfo("cp") { RedirectStandardError = true };
            psi.ArgumentList.Add(staging);
            psi.ArgumentList.Add(path);
            using var cp = Process.Start(psi)!;
            var error = cp.StandardError.ReadToEnd();
            cp.WaitForExit();
            if (cp.ExitCode != 0)
                throw new IOException($"cp {staging} {path} exited {cp.ExitCode}: {error}");
        }
        finally
        {
            File.Delete(staging);
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
