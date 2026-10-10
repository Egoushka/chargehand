namespace Chargehand.Containers;

/// <param name="ProxyHost">The egress container's name, which the internal network's DNS resolves.</param>
/// <param name="ModelPort">The port of the egress container's model endpoint; 0: it has none.</param>
public sealed record BatchNetworkInfo(string Network, string EgressContainer, string ProxyHost, int ProxyPort, int ModelPort = 0)
{
    /// <summary>The value of a session container's <c>ANTHROPIC_BASE_URL</c> when the egress container exchanges run tokens; null: it does not.</summary>
    public string? ModelUrl => ModelPort == 0 ? null : $"http://{ProxyHost}:{ModelPort}";

    /// <summary>The value of a session container's <c>HTTPS_PROXY</c>; the run id is the proxy log's label, not a credential.</summary>
    public string ProxyUrl(string runId) => $"http://{runId}:x@{ProxyHost}:{ProxyPort}";

    /// <summary>Where a session reaches an operator-named forward (the chargehand server's MCP endpoint): the egress container's alias on the batch network and the forward's listen port.</summary>
    public static string ServiceUrl(int listenPort) => $"http://{ContainerTemplate.CallbackAlias}:{listenPort}";
}

/// <summary>A batch's network (ADR 0039): an internal Docker network, which has no route out, plus one egress container on it that is also
/// joined to the outside network. A session container attached to the internal network reaches the allowlist through the egress container
/// and nothing else, even if it ignores its proxy variables.</summary>
public sealed class BatchNetwork(IContainerEngine engine)
{
    /// <summary>Docker's default outside network.</summary>
    public const string DefaultOutside = "bridge";

    /// <param name="outsideNetwork">The network the egress container also joins: the way out to the registries, and to the chargehand server when that sits on a network of its own.</param>
    /// <param name="forwards">Operator-named forwards (<c>listen-port=host:port</c>), for reaching the chargehand server from the internal network.</param>
    /// <param name="gateway">Set, the egress container also serves the model endpoint and holds the real credential (ADR 0039, decision 9).</param>
    public async Task<BatchNetworkInfo> CreateAsync(string batchId, string egressImage, IReadOnlyList<string> allow, CancellationToken ct, string outsideNetwork = DefaultOutside,
        IReadOnlyList<string>? forwards = null, ModelGatewaySpec? gateway = null)
    {
        var network = $"chargehand-net-{batchId}";
        var spec = new EgressSpec(batchId, egressImage, network, allow, Forwards: forwards, Gateway: gateway);
        _ = ContainerTemplate.EgressArgs(spec, "env-file-placeholder"); // validates every value before anything is created
        var info = new BatchNetworkInfo(network, "", $"chargehand-egress-{batchId}", spec.Port, gateway?.Port ?? 0);
        await engine.CreateNetworkAsync(network, batchId, ct);
        string? egress = null;
        try
        {
            egress = await engine.StartEgressAsync(spec, ct);
            await engine.ConnectNetworkAsync(egress, outsideNetwork, ct);
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
