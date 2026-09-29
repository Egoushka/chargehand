using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Budget;
using Chargehand.Contracts;

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
    string? Runtime = null)
{
    /// <summary>A fixed directory outside $HOME (ADR 0003 forbids worker checkouts under it), created on first use.</summary>
    public const string DefaultWorkerRoot = "/var/tmp/chargehand/work";

    /// <summary>Where a request's repository may live (ADR 0023); worker_root alone when the profile names none.</summary>
    public IReadOnlyList<string> Roots => RepositoryRoots ?? [WorkerRoot];

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
    public static Profile Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: empty profile")
            : new Profile("profile/v1");

    /// <summary>The provider segment of the placeholder models presets name (e.g. provider/worker-model).</summary>
    public const string PlaceholderProvider = "provider/";

    /// <summary>The profile maps placeholder models to real ids. An unmapped placeholder is unset, so the runtime uses
    /// its own default model (ADR 0026); any other id passes through.</summary>
    public string? ResolveModel(string model) =>
        Models?.GetValueOrDefault(model) ?? (model.StartsWith(PlaceholderProvider, StringComparison.Ordinal) ? null : model);

    /// <summary>Tries each source in order; the first that resolves the item wins (ADR 0026). Default with no profile: env only.</summary>
    public string Secret(string item)
    {
        foreach (var source in Secrets ?? [new SecretSource(Env: true)])
        {
            if (source.Env == true && Environment.GetEnvironmentVariable(item.ToUpperInvariant().Replace('-', '_')) is { Length: > 0 } value)
                return value;
            if (source.Command is { Count: > 0 } template && RunCommand(template, item) is { } output)
                return output;
        }
        throw new InvalidOperationException($"no secret source resolved '{item}'");
    }

    /// <summary>An argv array run directly via Process, no shell; "{item}" is substituted per array element.</summary>
    private static string? RunCommand(IReadOnlyList<string> template, string item)
    {
        var args = template.Select(a => a.Replace("{item}", item)).ToList();
        var psi = new ProcessStartInfo(args[0]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args.Skip(1))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close(); // under `chargehand mcp` the parent's stdin carries the protocol
        var value = p.StandardOutput.ReadToEnd().TrimEnd('\n');
        p.WaitForExit();
        return p.ExitCode == 0 && value.Length > 0 ? value : null;
    }
}

/// <summary>One secret lookup: environment variable, or a command template ("{item}" substituted per array element).</summary>
public sealed record SecretSource(bool? Env = null, IReadOnlyList<string>? Command = null);

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
