namespace Chargehand.Mcp;

/// <summary>
/// An MCP server cannot be used for now (ADR 0034): it is not in the profile, a secret it needs no source resolves, or it did
/// not connect. Memory and services skip the server; nothing here is a reason to stop a run. The detail never holds a
/// secret value, and the message passes through <see cref="ChargehandException.Scrub"/>.
/// </summary>
/// <param name="code"><see cref="UnknownServer"/>, <see cref="SecretUnresolved"/> or <see cref="Unreachable"/>.</param>
/// <param name="server">The profile's name for the server.</param>
/// <param name="detail">What went wrong, for a person reading the run log.</param>
public sealed class McpUnavailableException(string code, string server, string detail) : Exception($"{server}: {ChargehandException.Scrub(detail)}")
{
    public const string UnknownServer = "unknown_server";
    public const string SecretUnresolved = "secret_unresolved";
    public const string Unreachable = "unreachable";

    public string Code => code;

    public string Server => server;

    /// <summary>What went wrong, scrubbed, without the server's name.</summary>
    public string Detail { get; } = ChargehandException.Scrub(detail);
}
