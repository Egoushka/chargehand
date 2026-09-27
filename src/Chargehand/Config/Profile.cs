using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Budget;

namespace Chargehand.Config;

/// <summary>profile/v1 (profiles/profile.schema.json): environment-specific settings; secrets by item name only.</summary>
public sealed record Profile(
    string Schema,
    OpenCodeSettings? Opencode,
    string WorkerRoot,
    string DefaultPreset,
    string IntakeModel,
    IReadOnlyDictionary<string, ModelPrice> Prices,
    string SecretStore = "keychain",
    decimal RunCapUsd = 1.00m,
    string RunLog = "runs/run-log.jsonl",
    TelemetrySettings? Telemetry = null,
    IReadOnlyDictionary<string, string>? Models = null,
    MemorySettings? Memory = null,
    HttpSettings? Http = null,
    ClaudeCodeSettings? ClaudeCode = null)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static Profile Load(string path) =>
        JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: empty profile");

    /// <summary>Presets name placeholder models (e.g. provider/worker-model); the profile maps them to real ids.</summary>
    public string ResolveModel(string model) => Models?.GetValueOrDefault(model) ?? model;

    public string Secret(string item) => SecretStore switch
    {
        "env" => Environment.GetEnvironmentVariable(item.ToUpperInvariant().Replace('-', '_'))
                 ?? throw new InvalidOperationException($"secret env var for '{item}' is not set"),
        _ => Keychain(item),
    };

    private static string Keychain(string item)
    {
        var psi = new ProcessStartInfo("security", ["find-generic-password", "-s", item, "-w"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var value = p.StandardOutput.ReadToEnd().TrimEnd('\n');
        p.WaitForExit();
        return p.ExitCode == 0 && value.Length > 0 ? value : throw new InvalidOperationException($"Keychain item '{item}' not found");
    }
}

public sealed record OpenCodeSettings(string Url, string PasswordSecret, string Version, string? Binary = null);

/// <summary>Claude Code CLI as the worker runtime (ADR 0020); used instead of OpenCode when set. Exactly one
/// credential: an API key (per-token billing) or a subscription OAuth token from <c>claude setup-token</c>. BaseUrl
/// routes the workers through a gateway that speaks the Anthropic API; unset, they call Anthropic directly.</summary>
public sealed record ClaudeCodeSettings(string Version, string? ApiKeySecret = null, string? OauthTokenSecret = null, string Binary = "claude",
    string? BaseUrl = null);

public sealed record MemorySettings(string Backend, string Url, string Namespace, string? ApiKeySecret = null, int MaxTokens = 1024, bool Retain = false);

public sealed record TelemetrySettings(string OtlpEndpoint, string PublicKeySecret, string SecretKeySecret);

/// <summary>chargehand serve (ADR 0018): a loopback port, and the secret-store item holding the API key callers send.</summary>
public sealed record HttpSettings(string ApiKeySecret, int Port = 4300);
