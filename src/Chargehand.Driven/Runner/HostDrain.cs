using System.Diagnostics;
using Chargehand.Driven;

namespace Chargehand.Runner;

/// <summary>When a runner asks the server for no new session (ADR 0058). Each rule is off unless enabled: the drain file exists (an operator's switch), the machine is on battery power, or its lid is
/// closed. A laptop in either of the last two states is about to sleep or may at any moment, and a session on a sleeping machine stops with it. A draining runner keeps the sessions it has.</summary>
/// <param name="OnBattery">Drain while the machine runs on battery power.</param>
/// <param name="OnLidClosed">Drain while the lid is closed.</param>
/// <param name="File">Drain while this file exists; null: no such switch.</param>
public sealed record DrainRules(bool OnBattery = false, bool OnLidClosed = false, string? File = null)
{
    public bool Any => OnBattery || OnLidClosed || File is not null;

    /// <summary>The rules named in <c>--drain-on</c> (<c>battery</c>, <c>lid</c>, comma separated); null when a word is none of them.</summary>
    public static DrainRules? ParseOn(string list, string? file)
    {
        bool battery = false, lid = false;
        foreach (var word in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (word)
            {
                case "battery": battery = true; break;
                case "lid": lid = true; break;
                default: return null;
            }
        }
        return new DrainRules(battery, lid, file);
    }
}

/// <summary>What the machine says about its power: the raw text of <c>pmset -g batt</c> and of <c>ioreg -r -k AppleClamshellState</c>; null when the tool is not there.</summary>
public interface IPowerReader
{
    Task<string?> BatteryAsync(CancellationToken ct);

    Task<string?> ClamshellAsync(CancellationToken ct);
}

/// <summary>macOS's own tools, run without a shell and given three seconds.</summary>
public sealed class MacPowerReader : IPowerReader
{
    public Task<string?> BatteryAsync(CancellationToken ct) => RunAsync("pmset", ["-g", "batt"], ct);

    public Task<string?> ClamshellAsync(CancellationToken ct) => RunAsync("ioreg", ["-r", "-k", "AppleClamshellState", "-d", "4"], ct);

    private static async Task<string?> RunAsync(string tool, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return null;
            process.StandardInput.Close();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            _ = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? await output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Whether the runner is draining, and why. What cannot be read counts as not draining for its rule: a Mac whose <c>pmset</c> answers nothing is not taken for one on battery. The answer is kept
/// for <paramref name="keepFor"/> (10 seconds), so a server that asks often does not run the tools each time.</summary>
public sealed class HostDrain(DrainRules rules, IPowerReader power, TimeProvider? time = null, TimeSpan? keepFor = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset _at = DateTimeOffset.MinValue;
    private string? _reason;

    /// <returns>Why the runner is draining; null: it is ready.</returns>
    public async Task<string?> ReasonAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
            if (now - _at < (keepFor ?? TimeSpan.FromSeconds(10)))
                return _reason;
        // Two calls at the same moment may both run the tools; they agree, and the later one wins.
        var reason = await ReadAsync(ct);
        lock (_gate)
            (_reason, _at) = (reason, now);
        return reason;
    }

    private async Task<string?> ReadAsync(CancellationToken ct)
    {
        if (rules.File is { } file && File.Exists(file))
            return $"the drain file {Path.GetFileName(file)} exists";
        if (rules.OnLidClosed && LidClosed(await power.ClamshellAsync(ct)))
            return "the lid is closed";
        if (rules.OnBattery && OnBattery(await power.BatteryAsync(ct)))
            return "on battery power";
        return null;
    }

    /// <summary><c>Now drawing from 'Battery Power'</c> against <c>'AC Power'</c> (the first line of <c>pmset -g batt</c>).</summary>
    public static bool OnBattery(string? pmset) => pmset is not null && pmset.Contains("Now drawing from 'Battery Power'", StringComparison.Ordinal);

    /// <summary><c>"AppleClamshellState" = Yes</c> in the output of <c>ioreg -r -k AppleClamshellState</c>.</summary>
    public static bool LidClosed(string? ioreg) =>
        ioreg is not null && ioreg.Split('\n').Any(l => l.Contains("\"AppleClamshellState\"", StringComparison.Ordinal) && l.Contains("= Yes", StringComparison.Ordinal));
}

/// <summary>A host source that says <c>draining</c> while <see cref="HostDrain"/> does (ADR 0058), on top of whatever the inner source reports. The state is only added while draining, so a server older
/// than the field still reads the answer.</summary>
public sealed class DrainingHost(IHostSource inner, HostDrain drain) : IHostSource
{
    public async Task<HostSnapshot?> ReadAsync(CancellationToken ct)
    {
        var snapshot = await inner.ReadAsync(ct);
        if (await drain.ReasonAsync(ct) is not { } reason)
            return snapshot;
        return (snapshot ?? new HostSnapshot(null, null, null, null, null, null)) with { State = HostSnapshot.Draining, StateReason = reason };
    }
}
