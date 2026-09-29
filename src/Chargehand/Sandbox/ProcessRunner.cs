using System.Diagnostics;
using System.Text;

namespace Chargehand.Sandbox;

/// <summary>Starts a process with an exact environment, kills its tree at the timeout and keeps the tail of its output.</summary>
internal static class ProcessRunner
{
    internal const int TailBytes = 8192;

    private static readonly string[] Allowlist = ["PATH", "LANG", "LC_ALL", "TERM"];

    /// <summary>The fixed allowlist of a sandboxed command's environment; HOME and TMPDIR point at its private directory.</summary>
    internal static Dictionary<string, string> Environment(string tempDirectory, IReadOnlyList<string> passEnv)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = tempDirectory, ["TMPDIR"] = tempDirectory };
        foreach (var name in Allowlist.Concat(passEnv))
            if (System.Environment.GetEnvironmentVariable(name) is { } value)
                env[name] = value;
        return env;
    }

    internal static async Task<SandboxResult> RunAsync(string fileName, IEnumerable<string> args, IReadOnlyDictionary<string, string> env, string workDirectory,
        TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in env)
            psi.Environment[k] = v;

        var started = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {fileName}");
        process.StandardInput.Close();
        var tail = new Tail();
        var readers = Task.WhenAll(Pump(process.StandardOutput, tail), Pump(process.StandardError, tail));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (!timedOut)
                throw;
        }
        await readers;
        return new SandboxResult(timedOut ? -1 : process.ExitCode, tail.Text(), timedOut, started.Elapsed);
    }

    private static async Task Pump(StreamReader reader, Tail tail)
    {
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer)) > 0)
            tail.Append(new string(buffer, 0, n));
    }

    private sealed class Tail
    {
        private readonly List<byte> _bytes = [];
        private readonly object _lock = new();

        public void Append(string text)
        {
            lock (_lock)
            {
                _bytes.AddRange(Encoding.UTF8.GetBytes(text));
                if (_bytes.Count > 2 * TailBytes)
                    _bytes.RemoveRange(0, _bytes.Count - TailBytes);
            }
        }

        public string Text()
        {
            lock (_lock)
                return Encoding.UTF8.GetString([.. _bytes.Skip(Math.Max(0, _bytes.Count - TailBytes))]);
        }
    }
}
