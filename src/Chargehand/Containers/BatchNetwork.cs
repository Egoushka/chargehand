namespace Chargehand.Containers;

/// <param name="ProxyHost">The egress container's name, which the internal network's DNS resolves.</param>
public sealed record BatchNetworkInfo(string Network, string EgressContainer, string ProxyHost, int ProxyPort)
{
    /// <summary>The value of a session container's <c>HTTPS_PROXY</c>; the run id is the proxy log's label, not a credential.</summary>
    public string ProxyUrl(string runId) => $"http://{runId}:x@{ProxyHost}:{ProxyPort}";
}

/// <summary>A batch's network (ADR 0039): an internal Docker network, which has no route out, plus one egress container on it that is also
/// joined to the outside network. A session container attached to the internal network reaches the allowlist through the egress container
/// and nothing else, even if it ignores its proxy variables.</summary>
public sealed class BatchNetwork(IContainerEngine engine)
{
    /// <summary>Docker's default outside network.</summary>
    private const string Outside = "bridge";

    public async Task<BatchNetworkInfo> CreateAsync(string batchId, string egressImage, IReadOnlyList<string> allow, CancellationToken ct)
    {
        var network = $"chargehand-net-{batchId}";
        var spec = new EgressSpec(batchId, egressImage, network, allow);
        _ = ContainerTemplate.EgressArgs(spec); // validates every value before anything is created
        var info = new BatchNetworkInfo(network, "", $"chargehand-egress-{batchId}", spec.Port);
        await engine.CreateNetworkAsync(network, batchId, ct);
        string? egress = null;
        try
        {
            egress = await engine.StartEgressAsync(spec, ct);
            await engine.ConnectNetworkAsync(egress, Outside, ct);
            return info with { EgressContainer = egress };
        }
        catch
        {
            if (egress is not null)
                await engine.RemoveAsync(egress, CancellationToken.None);
            await engine.RemoveNetworkAsync(network, CancellationToken.None);
            throw;
        }
    }

    public async Task RemoveAsync(BatchNetworkInfo info, CancellationToken ct)
    {
        await engine.RemoveAsync(info.EgressContainer, ct);
        await engine.RemoveNetworkAsync(info.Network, ct);
    }
}
