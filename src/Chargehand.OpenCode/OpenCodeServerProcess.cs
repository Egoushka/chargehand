using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Chargehand.Contracts;

namespace Chargehand.OpenCode;

/// <summary>
/// The OpenCode server chargehand starts when the profile has no opencode block (ADR 0030), the way
/// scripts/opencode-serve.sh does: <c>opencode serve</c> on 127.0.0.1 and a free port, a random password in
/// OPENCODE_SERVER_PASSWORD, its own HOME and XDG directories under <c>state</c> (ADR 0003, ADR 0004). Its stdin is
/// closed and its output never reaches ours (under <c>chargehand mcp</c> both carry the protocol). Dispose stops it.
/// </summary>
public sealed class OpenCodeServerProcess : IDisposable
{
    /// <summary>Written only when the state has no config yet: what the adapter needs (ADR 0004), nothing about models.
    /// Providers come from OpenCode's own detection (provider environment variables) or the user's later edits.</summary>
    internal const string DefaultConfig = """
        {
          "$schema": "https://opencode.ai/config.json",
          "autoupdate": false,
          "share": "disabled",
          "agent": { "title": { "disable": true } }
        }
        """;

    private readonly Process _process;
    private readonly string _password;

    private OpenCodeServerProcess(Process process, Uri url, string password) => (_process, Url, _password) = (process, url, password);

    public Uri Url { get; }

    /// <summary>A client for this server; its lifetime is the caller's.</summary>
    public OpenCodeClient Client() => new(new HttpClient { BaseAddress = Url }, _password);

    /// <summary>chargehand/opencode under the per-user data directory, next to the default run log.</summary>
    public static string DefaultState(string userData) => Path.Combine(userData, "chargehand", "opencode");

    public static async Task<OpenCodeServerProcess> StartAsync(string binary, string state, CancellationToken ct, TimeSpan? timeout = null)
    {
        string Dir(params string[] parts) => Directory.CreateDirectory(Path.Combine([state, .. parts])).FullName;
        var config = Path.Combine(Dir("xdg", "config", "opencode"), "opencode.json");
        if (!File.Exists(config))
            await File.WriteAllTextAsync(config, DefaultConfig, ct);

        var port = FreePort();
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var psi = new ProcessStartInfo(binary)
        {
            WorkingDirectory = Dir("home"),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "serve", "--hostname", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        psi.Environment["OPENCODE_SERVER_PASSWORD"] = password;
        psi.Environment["OPENCODE_DISABLE_AUTOUPDATE"] = "1";
        // A checkout's opencode.json or .opencode can register an MCP server whose command the session then starts.
        // OpenCode reads the second name only when the first is unset, so set both (an inherited "0" would win).
        psi.Environment["OPENCODE_CONFIG_PROJECT_DISABLE"] = "1";
        psi.Environment["OPENCODE_DISABLE_PROJECT_CONFIG"] = "1";
        psi.Environment["HOME"] = Dir("home");
        psi.Environment["XDG_CONFIG_HOME"] = Dir("xdg", "config");
        psi.Environment["XDG_DATA_HOME"] = Dir("xdg", "data");
        psi.Environment["XDG_STATE_HOME"] = Dir("xdg", "state");
        psi.Environment["XDG_CACHE_HOME"] = Dir("xdg", "cache");

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Win32Exception e)
        {
            throw new ChargehandException(ErrorCode.RuntimeUnavailable, $"cannot start {binary}: {e.Message}",
                "Install OpenCode (opencode on PATH), or add an opencode block with url, password_secret and version to the profile.");
        }
        process.StandardInput.Close();
        string? last = null;
        DataReceivedEventHandler keep = (_, e) => last = e.Data is { Length: > 0 } line ? line : last;
        process.OutputDataReceived += keep;
        process.ErrorDataReceived += keep;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var server = new OpenCodeServerProcess(process, new Uri($"http://127.0.0.1:{port}/"), password);
        try
        {
            await server.WaitUntilAnsweringAsync(timeout ?? TimeSpan.FromSeconds(30), () => last, ct);
            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    private async Task WaitUntilAnsweringAsync(TimeSpan timeout, Func<string?> last, CancellationToken ct)
    {
        using var client = new HttpClient { BaseAddress = Url, Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (_process.HasExited)
            {
                _process.WaitForExit(); // flushes the output handlers
                throw new ChargehandException(ErrorCode.RuntimeUnavailable, $"opencode serve exited {_process.ExitCode} before answering: {last()}",
                    "Run opencode serve by hand and fix what it reports, or add an opencode block to the profile.");
            }
            try
            {
                // Any HTTP answer means it listens; the version pin is OpenCodeWorkerRuntime.ConnectAsync's check.
                using var res = await client.GetAsync("/api/info", ct);
                return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            if (DateTimeOffset.UtcNow > deadline)
                throw new ChargehandException(ErrorCode.RuntimeUnavailable, $"opencode serve did not answer on {Url} within {timeout.TotalSeconds:0}s: {last()}",
                    "Run opencode serve by hand and fix what it reports, or add an opencode block to the profile.");
            await Task.Delay(100, ct);
        }
    }

    // ponytail: the port is free when probed and taken by opencode a moment later; another process can win that race,
    // which surfaces as opencode exiting before it answers. Parse the listening URL from its output if that ever bites.
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        _process.Dispose();
    }
}
