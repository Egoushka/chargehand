using System.Diagnostics;
using Chargehand.Containers;
using Chargehand.Egress;

namespace Chargehand.Tests;

/// <summary>ADR 0039: what a session container can and cannot reach, on a real engine. Needs Docker, CHARGEHAND_TEST_IMAGE (a local image
/// with <c>sh</c>, and busybox <c>nc</c> or else <c>bash</c> and <c>timeout</c> (alpine; ubuntu), by digest) and CHARGEHAND_TEST_EGRESS_IMAGE (from scripts/egress-test-image.sh).
/// The allowed-host test also needs the internet: it connects the egress container to example.com.</summary>
public class EgressTests
{
    private static readonly string? EgressImage = Environment.GetEnvironmentVariable("CHARGEHAND_TEST_EGRESS_IMAGE");

    /// <summary>Stands in for <c>nc [-w secs] host port</c> (stdin to the socket, the reply to stdout) where the image has none, with bash's /dev/tcp.</summary>
    private const string NcShim = """
        command -v nc >/dev/null 2>&1 || nc() { w=5; [ "$1" = -w ] && { w=$2; shift 2; }; timeout "$w" bash -c 'exec 3<>/dev/tcp/$0/$1 || exit 1; cat >&3; cat <&3' "$1" "$2"; }; 
        """;

    private static async Task<(int Exit, string Logs)> RunSession(DockerCliEngine engine, BatchNetworkInfo net, string batch, string run, string script)
    {
        var spec = new ContainerSpec(run, DockerFactAttribute.Image!, $"cht-work-{run}", $"cht-out-{run}", net.Network, new Dictionary<string, string>(),
            256, 1, 64, ["sh", "-c", NcShim + script]);
        await engine.CreateVolumeAsync(spec.WorkVolume, batch, default);
        await engine.CreateVolumeAsync(spec.OutVolume, batch, default);
        var id = await engine.StartAsync(spec, default);
        try
        {
            ContainerState state;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            do
            {
                await Task.Delay(300);
                state = await engine.InspectAsync(id, default);
            } while (state.Status == ContainerStatus.Running && DateTime.UtcNow < deadline);
            return (state.ExitCode ?? -1, await engine.LogsTailAsync(id, 4096, default));
        }
        finally
        {
            await engine.RemoveAsync(id, default);
            await engine.RemoveVolumeAsync(spec.WorkVolume, default);
            await engine.RemoveVolumeAsync(spec.OutVolume, default);
        }
    }

    [DockerEgressFact]
    public async Task Direct_ip_and_public_dns_fail_on_the_internal_network()
    {
        var engine = new DockerCliEngine();
        var batch = "e" + Guid.NewGuid().ToString("N")[..8];
        var batchNetwork = new BatchNetwork(engine);
        var net = await batchNetwork.CreateAsync(batch, EgressImage!, ["example.com"], default);
        try
        {
            var (_, logs) = await RunSession(engine, net, batch, batch + "a",
                "nc -w 3 1.1.1.1 443 </dev/null >/dev/null 2>&1; echo direct=$?; nc -w 3 8.8.8.8 53 </dev/null >/dev/null 2>&1; echo direct2=$?; nslookup example.com >/dev/null 2>&1; echo dns=$?");
            Assert.DoesNotContain("direct=0", logs);
            Assert.DoesNotContain("direct2=0", logs);
            Assert.DoesNotContain("dns=0", logs);
            Assert.Contains("direct=", logs);
            Assert.Contains("dns=", logs);
        }
        finally
        {
            await batchNetwork.RemoveAsync(net, default);
        }
    }

    [DockerEgressFact]
    public async Task An_allowed_host_is_tunnelled_and_any_other_is_refused_through_the_proxy()
    {
        var engine = new DockerCliEngine();
        var batch = "e" + Guid.NewGuid().ToString("N")[..8];
        var batchNetwork = new BatchNetwork(engine);
        var net = await batchNetwork.CreateAsync(batch, EgressImage!, ["example.com"], default);
        try
        {
            var proxy = $"{net.ProxyHost} {net.ProxyPort}";
            var script = $"for i in 1 2 3 4 5 6 7 8 9 10; do printf 'CONNECT example.com:443 HTTP/1.1\\r\\nProxy-Authorization: Basic cnVuLTc6eA==\\r\\n\\r\\n' | nc -w 5 {proxy} > /tmp/ok 2>/dev/null; [ -s /tmp/ok ] && break; sleep 1; done; head -1 /tmp/ok; "
                + $"printf 'CONNECT evil.example:443 HTTP/1.1\\r\\n\\r\\n' | nc -w 5 {proxy} | head -1; printf 'CONNECT 1.1.1.1:443 HTTP/1.1\\r\\n\\r\\n' | nc -w 5 {proxy} | head -1";
            var (_, logs) = await RunSession(engine, net, batch, batch + "b", script);
            var lines = logs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains(lines, l => l.StartsWith("HTTP/1.1 200", StringComparison.Ordinal));
            Assert.Equal(2, lines.Count(l => l.StartsWith("HTTP/1.1 403", StringComparison.Ordinal)));
            var proxyLog = await engine.LogsTailAsync(net.EgressContainer, 8192, default);
            Assert.Contains("\"run\":\"run-7\"", proxyLog);
            Assert.Contains("\"allowed\":false", proxyLog);
        }
        finally
        {
            await batchNetwork.RemoveAsync(net, default);
        }
    }

    [DockerEgressFact]
    public async Task The_model_endpoint_refuses_a_bad_token_and_forwards_a_good_one_to_the_api_host_with_the_credential_swapped()
    {
        const string fake = "fake-credential-for-the-egress-test-0123456789";
        var engine = new DockerCliEngine();
        var batch = "e" + Guid.NewGuid().ToString("N")[..8];
        var gateway = new ModelGatewaySpec(fake, ModelTokens.NewKey());
        var net = await new BatchNetwork(engine).CreateAsync(batch, EgressImage!, ["api.anthropic.com"], default, gateway: gateway);
        try
        {
            var good = ModelTokens.Mint(gateway.TokenKey, "run-m", DateTimeOffset.UtcNow.AddMinutes(10), 0);
            static string Post(string host, string token) =>
                $"printf 'POST /v1/messages HTTP/1.1\\r\\nHost: x\\r\\nAuthorization: Bearer {token}\\r\\nContent-Type: application/json\\r\\nContent-Length: 2\\r\\nConnection: close\\r\\n\\r\\n{{}}' | nc -w 20 {host} 3129 | tr -d '\\r' | grep -E 'HTTP/1.1|message'; ";
            var script = "sleep 4; echo BAD; " + Post(net.ProxyHost, "chm-bad.token") + "echo GOOD; " + Post(net.ProxyHost, good);
            var (_, logs) = await RunSession(engine, net, batch, batch + "m", script);
            var bad = logs[(logs.IndexOf("BAD", StringComparison.Ordinal))..logs.IndexOf("GOOD", StringComparison.Ordinal)];
            var forwarded = logs[logs.IndexOf("GOOD", StringComparison.Ordinal)..];
            Assert.Contains("HTTP/1.1 401", bad);
            Assert.Contains("run token", bad);                                   // refused by the gateway itself
            Assert.Contains("HTTP/1.1 401", forwarded);
            Assert.DoesNotContain("run token", forwarded);                       // answered by the API host, which rejected the fake credential: the swap happened
            Assert.DoesNotContain(fake, logs);
            Assert.DoesNotContain(fake, await engine.LogsTailAsync(net.EgressContainer, 16384, default));
            Assert.Contains("\"model_run\":\"run-m\"", await engine.LogsTailAsync(net.EgressContainer, 16384, default));
        }
        finally
        {
            await new BatchNetwork(engine).RemoveAsync(net, default);
        }
    }

    [DockerEgressFact]
    public async Task The_batch_network_and_egress_container_are_gone_after_remove()
    {
        var engine = new DockerCliEngine();
        var batch = "e" + Guid.NewGuid().ToString("N")[..8];
        var batchNetwork = new BatchNetwork(engine);
        var net = await batchNetwork.CreateAsync(batch, EgressImage!, ["example.com"], default);
        Assert.Equal(ContainerStatus.Running, (await engine.InspectAsync(net.EgressContainer, default)).Status);
        await batchNetwork.RemoveAsync(net, default);
        Assert.Equal(ContainerStatus.Missing, (await engine.InspectAsync(net.EgressContainer, default)).Status);
        Assert.DoesNotContain(net.Network, Docker("network", "ls", "--format", "{{.Name}}"));
    }

    private static string Docker(params string[] args)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
