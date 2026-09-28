using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Budget;

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
    string RunLog = "runs/run-log.jsonl",
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

    /// <summary>Presets name placeholder models (e.g. provider/worker-model); the profile maps them to real ids.</summary>
    public string ResolveModel(string model) => Models?.GetValueOrDefault(model) ?? model;

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
        var psi = new ProcessStartInfo(args[0]) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args.Skip(1))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var value = p.StandardOutput.ReadToEnd().TrimEnd('\n');
        p.WaitForExit();
        return p.ExitCode == 0 && value.Length > 0 ? value : null;
    }
}

/// <summary>One secret lookup: environment variable, or a command template ("{item}" substituted per array element).</summary>
public sealed record SecretSource(bool? Env = null, IReadOnlyList<string>? Command = null);

public sealed record OpenCodeSettings(string Url, string PasswordSecret, string Version, string? Binary = null);

/// <summary>Claude Code CLI as the worker runtime (ADR 0020); used instead of OpenCode when set. Exactly one
/// credential: an API key (per-token billing) or a subscription OAuth token from <c>claude setup-token</c>. BaseUrl
/// routes the workers through a gateway that speaks the Anthropic API; unset, they call Anthropic directly.</summary>
public sealed record ClaudeCodeSettings(string Version, string? ApiKeySecret = null, string? OauthTokenSecret = null, string Binary = "claude",
    string? BaseUrl = null);

public sealed record MemorySettings(string Backend, string Url, string Namespace, string? ApiKeySecret = null, int MaxTokens = 1024, bool Retain = false);

/// <param name="UsageOnSpans">Call spans carry tokens and cost (ADR 0021). Only for calls no gateway records, e.g. Claude
/// Code on a subscription; with a LiteLLM gateway, usage stays on its generations (ADR 0012).</param>
public sealed record TelemetrySettings(string OtlpEndpoint, string PublicKeySecret, string SecretKeySecret, bool UsageOnSpans = false);

/// <summary>chargehand serve (ADR 0018): a port, and the secret-store item holding the API key callers send. Listen and
/// AllowedHosts open it beyond loopback, for a private network only (ADR 0024).</summary>
public sealed record HttpSettings(string ApiKeySecret, int Port = 4300, string Listen = "127.0.0.1", IReadOnlyList<string>? AllowedHosts = null);
