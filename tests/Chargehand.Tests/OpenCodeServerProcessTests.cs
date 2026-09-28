using System.Net.Sockets;
using Chargehand.Contracts;
using Chargehand.OpenCode;

namespace Chargehand.Tests;

/// <summary>With no opencode block chargehand starts its own server (ADR 0030); a stand-in opencode answers /api/info.</summary>
public sealed class OpenCodeServerProcessTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Starts_an_isolated_loopback_server_that_answers_the_pinned_version()
    {
        var state = Path.Combine(_dir.Path, "state");
        using var server = await OpenCodeServerProcess.StartAsync(FakeOpencode(OpenCodeWorkerRuntime.PinnedVersion), state, CancellationToken.None);

        var runtime = await OpenCodeWorkerRuntime.ConnectAsync(server.Client(), OpenCodeWorkerRuntime.PinnedVersion, CancellationToken.None);

        Assert.Equal(OpenCodeWorkerRuntime.PinnedVersion, runtime.Version);
        Assert.Equal("127.0.0.1", server.Url.Host);
        var seen = File.ReadAllLines(Path.Combine(_dir.Path, "seen.txt"));
        Assert.Equal($"args=serve --hostname 127.0.0.1 --port {server.Url.Port}", seen[0]);
        Assert.Matches("^password=[0-9a-f]{64}$", seen[1]);
        Assert.Equal($"home={state}/home config={state}/xdg/config data={state}/xdg/data autoupdate=1", seen[2]);
        Assert.Contains("\"title\"", File.ReadAllText(Path.Combine(state, "xdg", "config", "opencode", "opencode.json")));
    }

    [Fact]
    public async Task Keeps_a_config_the_user_wrote_and_stops_the_server_on_dispose()
    {
        var state = Path.Combine(_dir.Path, "state");
        var config = _dir.Write("state/xdg/config/opencode/opencode.json", """{"model":"mine/model"}""");
        var server = await OpenCodeServerProcess.StartAsync(FakeOpencode(OpenCodeWorkerRuntime.PinnedVersion), state, CancellationToken.None);
        var port = server.Url.Port;

        server.Dispose();

        Assert.Equal("""{"model":"mine/model"}""", File.ReadAllText(config));
        using var tcp = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => tcp.ConnectAsync("127.0.0.1", port));
    }

    [Fact]
    public async Task A_missing_binary_is_runtime_unavailable()
    {
        var e = await Assert.ThrowsAsync<ChargehandException>(() =>
            OpenCodeServerProcess.StartAsync(Path.Combine(_dir.Path, "no-opencode"), Path.Combine(_dir.Path, "state"), CancellationToken.None));
        Assert.Equal(ErrorCode.RuntimeUnavailable, e.Code);
        Assert.Contains("opencode", e.Action);
    }

    [Fact]
    public async Task A_server_that_exits_before_answering_reports_its_last_output()
    {
        var script = Executable("opencode", "#!/bin/sh\necho 'bad config: provider x' >&2\nexit 1\n");

        var e = await Assert.ThrowsAsync<ChargehandException>(() => OpenCodeServerProcess.StartAsync(script, Path.Combine(_dir.Path, "state"), CancellationToken.None));

        Assert.Equal(ErrorCode.RuntimeUnavailable, e.Code);
        Assert.Contains("bad config: provider x", e.Message);
    }

    /// <summary>A stand-in CLI: reads stdin to its end (hangs unless stdin is closed), records what it was given, then
    /// serves {"version": ...} at /api/info on the port it was told.</summary>
    private string FakeOpencode(string version)
    {
        _dir.Write("www/api/info", $$"""{"version":"{{version}}"}""");
        return Executable("opencode", $$"""
            #!/bin/sh
            cat > /dev/null
            echo "args=$*" > "{{_dir.Path}}/seen.txt"
            echo "password=$OPENCODE_SERVER_PASSWORD" >> "{{_dir.Path}}/seen.txt"
            echo "home=$HOME config=$XDG_CONFIG_HOME data=$XDG_DATA_HOME autoupdate=$OPENCODE_DISABLE_AUTOUPDATE" >> "{{_dir.Path}}/seen.txt"
            exec python3 -m http.server --bind 127.0.0.1 --directory "{{_dir.Path}}/www" "$5"
            """);
    }

    private string Executable(string name, string content)
    {
        var script = _dir.Write(name, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
