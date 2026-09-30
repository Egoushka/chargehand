using System.Diagnostics;
using Chargehand.Containers;

namespace Chargehand.Tests;

/// <summary>ADR 0039: what a session container can and cannot reach, on a real engine. Needs Docker, CHARGEHAND_TEST_IMAGE (a local image
/// with busybox <c>nc</c> and <c>nslookup</c>, by digest, e.g. alpine) and CHARGEHAND_TEST_EGRESS_IMAGE (from scripts/egress-test-image.sh).
/// The allowed-host test also needs the internet: it connects the egress container to example.com.</summary>
public class EgressTests
{
    private static readonly string? EgressImage = Environment.GetEnvironmentVariable("CHARGEHAND_TEST_EGRESS_IMAGE");

    private static async Task<(int Exit, string Logs)> RunSession(DockerCliEngine engine, BatchNetworkInfo net, string batch, string run, string script)
    {
        var spec = new ContainerSpec(run, DockerFactAttribute.Image!, $"cht-work-{run}", $"cht-out-{run}", net.Network, new Dictionary<string, string>(),
            256, 1, 64, ["sh", "-c", script]);
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
            var script = $"for i in 1 2 3 4 5 6 7 8 9 10; do printf 'CONNECT example.com:443 HTTP/1.1\\r\\nProxy-Authorization: Basic cnVuLTc6eA==\\r\\n\\r\\n' | nc -w 5 {proxy} > /tmp/ok 2>/dev/null && [ -s /tmp/ok ] && break; sleep 1; done; head -1 /tmp/ok; "
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
