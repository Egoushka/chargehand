using Chargehand.Modules.Research.Api;

namespace Chargehand.Warming;

/// <summary>Runs <c>dotnet</c> as a plain child process, for the checks the warmer does outside the sandbox on files it chose itself.</summary>
public static class DotnetProcess
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    // The child sees only these from the daemon; tokens and keys in the daemon's environment stay out of it.
    internal static readonly string[] Inherited = ["PATH", "LANG", "LC_ALL", "DOTNET_ROOT", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "SSL_CERT_FILE", "SSL_CERT_DIR", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY"];

    public static Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, string workDirectory, CancellationToken ct) =>
        RunAsync("dotnet", args, workDirectory, DefaultTimeout, ct);

    /// <summary>Runs with a private, deleted-afterwards home, kills the tree at <paramref name="timeout"/> (exit -1) and keeps the output's tail.</summary>
    internal static async Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> args, string workDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var home = Directory.CreateTempSubdirectory("chargehand-dotnet-").FullName;
        try
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = home,
                ["DOTNET_CLI_HOME"] = home,
                ["TMPDIR"] = home,
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            };
            foreach (var name in Inherited)
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    env[name] = value;
            var result = await ProcessRunner.RunAsync(fileName, args, env, workDirectory, timeout, ct).ConfigureAwait(false);
            return (result.ExitCode, result.OutputTail);
        }
        finally
        {
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Console.Error.WriteLineAsync($"dotnet: could not remove its private home: {e.Message}").ConfigureAwait(false);
            }
        }
    }
}
