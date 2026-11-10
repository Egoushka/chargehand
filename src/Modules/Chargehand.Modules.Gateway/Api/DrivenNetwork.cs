namespace Chargehand.Modules.Gateway.Api;

/// <summary>The profile's <c>driven.network</c> (ADR 0039, ADR 0042): what a batch's egress container may reach besides the preset's allowlist, and how it reaches the server.</summary>
/// <param name="Allow">Hosts added to the preset's allowlist.</param>
/// <param name="Outside">A network besides the default one that the batch's egress container joins: the one the chargehand server sits on.</param>
/// <param name="McpForward"><c>host:port</c> of the chargehand server as the egress container reaches it. Set, a session can call chargehand for research and review through a forward on its batch network; unset, it cannot.</param>
/// <param name="ModelUrl">An Anthropic-compatible gateway as the egress container reaches it (<c>http://host:port/path</c> through a forward, or <c>https://host/path</c> through the proxy). Set, a session gets
/// <c>claude_code.api_key_secret</c> (the key the gateway issued, with a budget) and a base URL instead of the real credential; unset, the real credential is in the container's environment.</param>
/// <param name="OtlpUrl">An OTLP/HTTP collector as the egress container reaches it (<c>http://host:port</c>, through a forward). Set, each session's Claude Code exports its own logs and
/// metrics there, tagged with the batch, task and trace (ADR 0042); unset, it exports nothing.</param>
public sealed record DrivenNetwork(IReadOnlyList<string>? Allow = null, string? Outside = null, string? McpForward = null, string? ModelUrl = null, string? OtlpUrl = null);
