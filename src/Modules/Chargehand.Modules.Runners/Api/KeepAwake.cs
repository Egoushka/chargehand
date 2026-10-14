using System.Diagnostics;

namespace Chargehand.Modules.Runners.Api;

/// <summary>Holds the machine awake (ADR 0058).</summary>
public interface IAwakeHolder
{
    bool Held { get; }

    void Hold();

    void Release();
}

/// <summary><c>caffeinate -i -m -s -w &lt;this process&gt;</c>: no idle sleep, no disk idle sleep, and no system sleep while on AC power. <c>-w</c> ends it when the runner does, so a runner that crashes
/// does not leave the Mac awake. It does not keep a MacBook awake on battery power with the lid closed (macOS offers no way short of changing power settings as root), which is why the runner drains in those states.</summary>
public sealed class CaffeinateHolder : IAwakeHolder, IDisposable
{
    private Process? _process;

    public bool Held => _process is { HasExited: false };

    public void Hold()
    {
        if (Held)
            return;
        var psi = new ProcessStartInfo("caffeinate") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-i", "-m", "-s", "-w", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        _process = Process.Start(psi);
    }

    public void Release()
    {
        if (_process is null)
            return;
        try
        {
            if (!_process.HasExited)
                _process.Kill();
        }
        catch (InvalidOperationException)
        {
            // It ended on its own.
        }
        _process.Dispose();
        _process = null;
    }

    public void Dispose() => Release();
}

/// <summary>While a session container runs, the machine is held awake; when none does, it is let go (ADR 0058). The count is of session containers only: a batch's egress proxy and test services
/// exist for the whole batch and are not a reason to keep a laptop awake.</summary>
/// <param name="sessions">How many session containers are running now.</param>
/// <param name="log">Told when the hold starts and ends, and once when the count cannot be read.</param>
public sealed class KeepAwake(Func<CancellationToken, Task<int>> sessions, IAwakeHolder holder, TimeSpan? every = null, Action<string>? log = null)
{
    private bool _saidUnreadable;

    /// <summary>One look at the sessions. A count that cannot be read (the engine is away) leaves the hold as it is.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        int running;
        try
        {
            running = await sessions(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (!_saidUnreadable)
            {
                _saidUnreadable = true;
                log?.Invoke($"keep-awake: the sessions could not be counted ({e.Message}); the hold stays as it is");
            }
            return;
        }
        _saidUnreadable = false;
        if (running > 0 && !holder.Held)
        {
            holder.Hold();
            log?.Invoke($"keep-awake: {running} session(s) running, holding the machine awake");
        }
        else if (running == 0 && holder.Held)
        {
            holder.Release();
            log?.Invoke("keep-awake: no session running, letting the machine sleep");
        }
    }

    /// <summary>Looks every <paramref name="every"/> (10 seconds) until cancelled, then lets go.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await TickAsync(ct);
                await Task.Delay(every ?? TimeSpan.FromSeconds(10), ct);
            }
        }
        catch (OperationCanceledException)
        {
            holder.Release();
        }
    }
}
