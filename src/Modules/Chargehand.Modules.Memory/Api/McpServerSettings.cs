using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Memory.Api;

/// <summary>
/// One MCP server the profile names (ADR 0034); memory providers and preset services refer to it by name. Exactly one of
/// <paramref name="Url"/> (Streamable HTTP or legacy SSE, see <paramref name="Transport"/>) and <paramref name="Command"/>
/// (a stdio server's argv). <c>{secret:item}</c> is allowed in header and environment values only: the connection resolves
/// it through the profile's secret sources when it opens.
/// </summary>
/// <param name="Transport">Only with <paramref name="Url"/>: <see cref="TransportAuto"/> (the default; Streamable HTTP, then
/// SSE if the server does not support it), <see cref="TransportStreamableHttp"/> or <see cref="TransportSse"/>.</param>
public sealed partial record McpServerSettings(
    string? Url = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<string>? Command = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? Transport = null)
{
    public const string TransportAuto = "auto";
    public const string TransportStreamableHttp = "streamable-http";
    public const string TransportSse = "sse";

    /// <summary>The names <c>memory</c> and presets refer to servers by; the pattern keeps Claude Code's
    /// <c>mcp__server__tool</c> names unambiguous.</summary>
    public static bool ValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>Throws an invalid_request error, naming the server, when the entry breaks a rule of the profile schema. The
    /// messages never quote a URL, argv or value: any of them may carry a credential.</summary>
    public void Validate(string name)
    {
        if (!ValidName(name))
            Fail(name, "the name must be lowercase letters, digits and hyphens, starting with a letter");
        if (Url is not null && Command is not null)
            Fail(name, "set only one of url and command");
        if (Url is null && Command is not { Count: > 0 })
            Fail(name, "set one of url and command");
        if (Url is not null)
        {
            if (HasSecret(Url))
                Fail(name, "url must not hold {secret:...}; put the credential in a header");
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                Fail(name, "url must be an absolute http or https URL");
            if (Env is not null)
                Fail(name, "env applies to a command server only");
            if (Transport is not (null or TransportAuto or TransportStreamableHttp or TransportSse))
                Fail(name, $"transport must be {TransportAuto}, {TransportStreamableHttp} or {TransportSse}");
            return;
        }
        if (Command!.Any(HasSecret))
            Fail(name, "command must not hold {secret:...}; put the credential in env");
        if (Headers is not null)
            Fail(name, "headers apply to a url server only");
        if (Transport is not null)
            Fail(name, "transport applies to a url server only");
    }

    private static bool HasSecret(string text) => text.Contains("{secret:", StringComparison.Ordinal);

    private static void Fail(string name, string reason) =>
        throw new ChargehandException(ErrorCode.InvalidRequest, $"mcp_servers.{name}: {reason}",
            "Fix the entry in the profile; profiles/example.json shows the shape.");

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex NamePattern();
}
