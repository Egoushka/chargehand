using System.Diagnostics;

namespace AgentClientProtocol;

/// <summary>An agent started as a child process: its pipes, the tail of its standard error, and a way to stop it.</summary>
internal sealed class AgentProcess : IAsyncDisposable
{
    private const int TailLines = 40;

    private readonly Process _process;
    private readonly TimeSpan _shutdownGrace;
    private readonly Queue<string> _tail = new();
    private readonly Task _stderrPump;

    private AgentProcess(Process process, TimeSpan shutdownGrace, Action<string>? onStderr)
    {
        _process = process;
        _shutdownGrace = shutdownGrace;
        _stderrPump = Task.Run(() => PumpStderrAsync(onStderr));
    }

    public Stream FromAgent => _process.StandardOutput.BaseStream;

    public Stream ToAgent => _process.StandardInput.BaseStream;

    public static AgentProcess Start(AcpLaunchOptions options)
    {
        var info = new ProcessStartInfo(options.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in options.Arguments)
            info.ArgumentList.Add(argument);
        if (options.WorkingDirectory is { } directory)
            info.WorkingDirectory = directory;
        if (options.ClearEnvironment)
            info.Environment.Clear();
        foreach (var (name, value) in options.Environment)
        {
            if (value is null)
                info.Environment.Remove(name);
            else
                info.Environment[name] = value;
        }

        var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new AcpConnectionException($"cannot start {options.FileName}: {e.Message}");
        }

        return new AgentProcess(process, options.ShutdownGrace, options.OnStderr);
    }

    public string StderrTail
    {
        get
        {
            lock (_tail)
            {
                return string.Join('\n', _tail);
            }
        }
    }

    /// <summary>Waits briefly for the process to exit after its output closed, so the error can carry the exit code.</summary>
    public async Task<AcpConnectionException> DescribeExitAsync(CancellationToken ct)
    {
        int? code = null;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            code = _process.ExitCode;
            await _stderrPump.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException)
        {
            // Still running with its output closed, or the stderr pump is slow: describe what is known.
        }

        var tail = StderrTail;
        var exit = code is { } c ? $"exited with code {c}" : "closed its output";
        return new AcpConnectionException(tail.Length == 0 ? $"the agent {exit}" : $"the agent {exit}: {tail}", code, tail);
    }

    private async Task PumpStderrAsync(Action<string>? onStderr)
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_tail)
                {
                    _tail.Enqueue(line);
                    while (_tail.Count > TailLines)
                        _tail.Dequeue();
                }

                onStderr?.Invoke(line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe closed under the pump: the process is gone, and the tail holds what it said.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ToAgent.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Closing the agent's input is a courtesy; it may be closed already.
        }

        using (var grace = new CancellationTokenSource(_shutdownGrace))
        {
            try
            {
                await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill();
            }
        }

        await Task.WhenAny(_stderrPump, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        _process.Dispose();
    }

    private void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited between the wait and the kill.
        }
    }
}
