using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chargehand.Budget;
using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.Config;

/// <summary>
/// profile/v1 (profiles/profile.schema.json): environment-specific settings. Fully optional (ADR 0026): <see cref="Load"/>
/// defaults an absent file, and every field below has a default with no profile at all.
/// </summary>
public sealed record Profile(
    string Schema,
    OpenCodeSettings? Opencode = null,
    string WorkerRoot = Profile.DefaultWorkerRoot,
    string DefaultPreset = "cheap",
    string? IntakeModel = null,
    IReadOnlyDictionary<string, ModelPrice>? Prices = null,
    IReadOnlyList<SecretSource>? Secrets = null,
    decimal RunCapUsd = 1.00m,
    string? RunLog = null,
    TelemetrySettings? Telemetry = null,
    IReadOnlyDictionary<string, string>? Models = null,
    MemorySettings? Memory = null,
    HttpSettings? Http = null,
    ClaudeCodeSettings? ClaudeCode = null,
    IReadOnlyList<string>? RepositoryRoots = null,
    string? Runtime = null,
    IReadOnlyDictionary<string, McpServerSettings>? McpServers = null)
{
    /// <summary>A fixed directory outside $HOME (ADR 0003 forbids worker checkouts under it), created on first use.</summary>
    public const string DefaultWorkerRoot = "/var/tmp/chargehand/work";

    /// <summary>Where a request's repository may live (ADR 0023); worker_root alone when the profile names none.</summary>
    public IReadOnlyList<string> Roots => RepositoryRoots ?? [WorkerRoot];

    /// <summary>How long one command secret source may run before it is killed and the next source is tried (ADR 0034).</summary>
    internal TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The runtimes this profile has a block for; the only one names the runtime when neither the runtime field
    /// nor CHARGEHAND_RUNTIME does (ADR 0032).</summary>
    public IReadOnlyList<RuntimeKind> RuntimeBlocks =>
        [.. Opencode is null ? [] : new[] { RuntimeKind.Opencode }, .. ClaudeCode is null ? [] : new[] { RuntimeKind.ClaudeCode }];

    /// <summary>The CLI's default when the profile names no repository_roots (ADR 0028): worker_root and the directory
    /// chargehand was launched in, the user's own choice at their shell. serve never calls this; explicit roots win.</summary>
    public Profile WithLaunchDirectory(string directory) =>
        RepositoryRoots is null ? this with { RepositoryRoots = [WorkerRoot, directory] } : this;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>Owner-visible defaults (this record's own default values) when the file does not exist.</summary>
    public static Profile Load(string path)
    {
        if (!File.Exists(path))
            return new Profile("profile/v1");
        var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: empty profile");
        foreach (var (name, server) in profile.McpServers ?? new Dictionary<string, McpServerSettings>())
            server.Validate(name);
        return profile;
    }

    /// <summary>The provider segment of the placeholder models presets name (e.g. provider/worker-model).</summary>
    public const string PlaceholderProvider = "provider/";

    /// <summary>The profile maps placeholder models to real ids. An unmapped placeholder is unset, so the runtime uses
    /// its own default model (ADR 0026); any other id passes through.</summary>
    public string? ResolveModel(string model) =>
        Models?.GetValueOrDefault(model) ?? (model.StartsWith(PlaceholderProvider, StringComparison.Ordinal) ? null : model);

    /// <summary>Tries each source in order; the first that resolves the item wins (ADR 0026). Default with no profile: env only.
    /// A command source that outruns <see cref="CommandTimeout"/> is killed and counts as not resolving (ADR 0034).</summary>
    public string Secret(string item)
    {
        var timedOut = false;
        foreach (var source in Secrets ?? [new SecretSource(Env: true)])
        {
            if (source.Env == true && Environment.GetEnvironmentVariable(item.ToUpperInvariant().Replace('-', '_')) is { Length: > 0 } value)
                return value;
            if (source.Command is { Count: > 0 } template)
            {
                var (output, expired) = RunCommand(template, item, CommandTimeout);
                if (output is not null)
                    return output;
                timedOut |= expired;
            }
        }
        throw new InvalidOperationException($"no secret source resolved '{item}'" + (timedOut ? $" (a command source timed out after {CommandTimeout.TotalSeconds:0.#} s)" : ""));
    }

    /// <summary>An argv array run directly via Process, no shell; "{item}" is substituted per array element. The output is null
    /// when the command failed, printed nothing or was killed at <paramref name="timeout"/> (then <c>TimedOut</c>).</summary>
    private static (string? Output, bool TimedOut) RunCommand(IReadOnlyList<string> template, string item, TimeSpan timeout)
    {
        var args = template.Select(a => a.Replace("{item}", item)).ToList();
        var psi = new ProcessStartInfo(args[0]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args.Skip(1))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close(); // under `chargehand mcp` the parent's stdin carries the protocol
        var clock = Stopwatch.StartNew();
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync(); // drained so a chatty command cannot fill the pipe, never read
        if (!output.Wait(timeout) || !p.WaitForExit(TimeSpan.FromTicks(Math.Max(0, (timeout - clock.Elapsed).Ticks))))
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it exited between the wait and the kill
            }
            return (null, true);
        }
        var value = output.Result.TrimEnd('\n');
        return (p.ExitCode == 0 && value.Length > 0 ? value : null, false);
    }
}

/// <summary>One secret lookup: environment variable, or a command template ("{item}" substituted per array element).</summary>
public sealed record SecretSource(bool? Env = null, IReadOnlyList<string>? Command = null);

/// <summary>
/// One MCP server the profile names (ADR 0034); memory providers and preset services refer to it by name. Exactly one of
/// <paramref name="Url"/> (Streamable HTTP or legacy SSE, see <paramref name="Transport"/>) and <paramref name="Command"/>
/// (a stdio server's argv). <c>{secret:item}</c> is allowed in header and environment values only: the connection resolves
/// it through <see cref="Profile.Secret"/> when it opens.
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
    internal void Validate(string name)
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

public sealed record OpenCodeSettings(string Url, string PasswordSecret, string Version, string? Binary = null);

/// <summary>Claude Code CLI as the worker runtime (ADR 0020); used instead of OpenCode when set. At most one
/// credential: an API key (per-token billing) or a subscription OAuth token from <c>claude setup-token</c>; with
/// neither, the CLI's own signed-in login. BaseUrl
/// routes the workers through a gateway that speaks the Anthropic API; unset, they call Anthropic directly.</summary>
public sealed record ClaudeCodeSettings(string Version, string? ApiKeySecret = null, string? OauthTokenSecret = null, string Binary = "claude",
    string? BaseUrl = null)
{
    /// <summary>The CLI version the adapter's event mapping was verified against (ADR 0020).</summary>
    public const string PinnedVersion = "2.1.283";

    /// <summary>
    /// No claude_code block (ADR 0026): claude from PATH at <see cref="PinnedVersion"/>, and the one credential the
    /// secrets chain resolves under the names the CLI itself reads (ANTHROPIC_API_KEY, CLAUDE_CODE_OAUTH_TOKEN with the
    /// default env source). Both is an error; neither leaves the CLI on its own signed-in login (ADR 0020).
    /// </summary>
    public static ClaudeCodeSettings Detect(Func<string, bool> resolves) =>
        (resolves("anthropic-api-key"), resolves("claude-code-oauth-token")) switch
        {
            (true, false) => new(PinnedVersion, ApiKeySecret: "anthropic-api-key"),
            (false, true) => new(PinnedVersion, OauthTokenSecret: "claude-code-oauth-token"),
            (true, true) => throw new ChargehandException(ErrorCode.InvalidRequest, "both ANTHROPIC_API_KEY and CLAUDE_CODE_OAUTH_TOKEN are set",
                "Unset one, or add claude_code to the profile naming one of api_key_secret and oauth_token_secret."),
            _ => new(PinnedVersion),
        };
}

public sealed record MemorySettings(string Backend, string Url, string Namespace, string? ApiKeySecret = null, int MaxTokens = 1024, bool Retain = false);

/// <param name="UsageOnSpans">Call spans carry tokens and cost (ADR 0021). Only for calls no gateway records, e.g. Claude
/// Code on a subscription; with a LiteLLM gateway, usage stays on its generations (ADR 0012).</param>
public sealed record TelemetrySettings(string OtlpEndpoint, string PublicKeySecret, string SecretKeySecret, bool UsageOnSpans = false);

/// <summary>chargehand serve (ADR 0018): a port, and the secret-store item holding the API key callers send. Listen and
/// AllowedHosts open it beyond loopback, for a private network only (ADR 0024).</summary>
public sealed record HttpSettings(string ApiKeySecret, int Port = 4300, string Listen = "127.0.0.1", IReadOnlyList<string>? AllowedHosts = null);
