using System.Diagnostics;

namespace Chargehand.Kernel;

/// <summary>One secret lookup of the profile's <c>secrets</c> (ADR 0026): an environment variable, or a command template ("{item}" substituted per array element).</summary>
public sealed record SecretSource(bool? Env = null, IReadOnlyList<string>? Command = null);

/// <summary>Resolves a secret-store item through the profile's sources, in order; the first that resolves the item wins (ADR 0026).</summary>
public static class SecretSources
{
    /// <summary>How long one command source may run before it is killed and the next source is tried (ADR 0034).</summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The sources with no profile: the environment only.</summary>
    public static readonly IReadOnlyList<SecretSource> Default = [new SecretSource(Env: true)];

    /// <summary>The first value a source resolves <paramref name="item"/> to. A command source that outruns <paramref name="commandTimeout"/>
    /// (null: <see cref="DefaultCommandTimeout"/>) is killed and counts as not resolving (ADR 0034). Throws when no source resolves it.</summary>
    public static string Resolve(IReadOnlyList<SecretSource>? sources, string item, TimeSpan? commandTimeout = null)
    {
        var timeout = commandTimeout ?? DefaultCommandTimeout;
        var timedOut = false;
        foreach (var source in sources ?? Default)
        {
            if (source.Env == true && Environment.GetEnvironmentVariable(item.ToUpperInvariant().Replace('-', '_')) is { Length: > 0 } value)
                return value;
            if (source.Command is { Count: > 0 } template)
            {
                var (output, expired) = RunCommand(template, item, timeout);
                if (output is not null)
                    return output;
                timedOut |= expired;
            }
        }
        throw new InvalidOperationException($"no secret source resolved '{item}'" + (timedOut ? $" (a command source timed out after {timeout.TotalSeconds:0.#} s)" : ""));
    }

    /// <summary>An argv array run directly via Process, no shell; "{item}" is substituted per array element. The output is null
    /// when the command failed, printed nothing or was killed at <paramref name="timeout"/> (then <c>TimedOut</c>).</summary>
    private static (string? Output, bool TimedOut) RunCommand(IReadOnlyList<string> template, string item, TimeSpan timeout)
    {
        var args = template.Select(a => a.Replace("{item}", item)).ToList();
        var psi = new ProcessStartInfo(args[0]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args.Skip(1))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close(); // under `chargehand mcp` the parent's stdin carries the protocol
        var clock = Stopwatch.StartNew();
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync(); // drained so a chatty command cannot fill the pipe, never read
        if (!output.Wait(timeout) || !p.WaitForExit(TimeSpan.FromTicks(Math.Max(0, (timeout - clock.Elapsed).Ticks))))
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it exited between the wait and the kill
            }
            return (null, true);
        }
        var value = output.Result.TrimEnd('\n');
        return (p.ExitCode == 0 && value.Length > 0 ? value : null, false);
    }
}
