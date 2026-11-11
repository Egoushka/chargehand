using System.Diagnostics;

namespace Chargehand.Warming;

/// <summary>Runs <c>dotnet</c> as a plain child process, for the checks the warmer does outside the sandbox on files it chose itself.</summary>
public static class DotnetProcess
{
    public static async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, string workDirectory, CancellationToken ct)
    {
        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = workDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("dotnet did not start");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }
}
