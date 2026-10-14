using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Memory.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Runtime;
using Chargehand.Runtimes.ClaudeCode.Api;
using Chargehand.Runtimes.OpenCode.Api;
using Chargehand.Sandbox;
using Chargehand.Signing;

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
    [property: JsonConverter(typeof(MemoryListConverter))] IReadOnlyList<MemoryProviderSettings>? Memory = null,
    HttpSettings? Http = null,
    ClaudeCodeSettings? ClaudeCode = null,
    IReadOnlyList<string>? RepositoryRoots = null,
    string? Runtime = null,
    IReadOnlyDictionary<string, McpServerSettings>? McpServers = null,
    SandboxSettings? Sandbox = null,
    bool SupportCheck = true,
    SigningSettings? Signing = null,
    DrivenSettings? Driven = null,
    PromptEnhancerSettings? PromptEnhancer = null,
    int MaxParallelNodes = 2,
    AutomationSettings? Automation = null)
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

    /// <summary>What <see cref="Load"/> reads with (ADR 0060): <see cref="Json"/>'s options, each module's source-generated context for its own
    /// sections first, then reflection for the sections still declared here.</summary>
    internal static readonly JsonSerializerOptions LoadJson = new(Json)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(LedgerSettingsJsonContext.Default, AccountsSettingsJsonContext.Default,
            MemorySettingsJsonContext.Default, ForgeSettingsJsonContext.Default, new DefaultJsonTypeInfoResolver()),
    };

    /// <summary>Owner-visible defaults (this record's own default values) when the file does not exist.</summary>
    public static Profile Load(string path)
    {
        if (!File.Exists(path))
            return new Profile("profile/v1");
        var profile = JsonSerializer.Deserialize(File.ReadAllText(path), (JsonTypeInfo<Profile>)LoadJson.GetTypeInfo(typeof(Profile)))
            ?? throw new InvalidDataException($"{path}: empty profile");
        var servers = profile.McpServers ?? new Dictionary<string, McpServerSettings>();
        foreach (var (name, server) in servers)
            server.Validate(name);
        if (profile.Memory is { Count: > 0 } memory && MemoryMapping.Validate(memory, servers.Keys) is { Count: > 0 } problems)
            throw new ChargehandException(ErrorCode.InvalidRequest, string.Join("; ", problems),
                "Fix memory in the profile; docs/guide/reference.md lists the fields.");
        if (profile.PromptEnhancer is { } enhancer)
            enhancer.Validate(servers);
        profile.Http?.Validate();
        profile.Driven?.Limits?.Validate();
        profile.Driven?.Governor?.Validate();
        profile.Driven?.Admission?.Validate();
        profile.Driven?.Planning?.Validate();
        profile.Driven?.Learning?.Validate();
        profile.Driven?.Integrator?.Validate();
        profile.Driven?.Services?.Validate(profile.Driven.Images);
        profile.Driven?.Forge?.Validate();
        profile.Driven?.ValidateRunners();
        profile.Automation?.Validate(profile.Driven?.Enabled == true, profile.Driven?.Integrator);
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

/// <summary>Reads <c>memory</c> as a list of providers. The object it used to be (ADR 0008) fails with the migration (ADR 0034); anything else is a JSON error.</summary>
internal sealed class MemoryListConverter : JsonConverter<IReadOnlyList<MemoryProviderSettings>>
{
    public override IReadOnlyList<MemoryProviderSettings> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartArray:
                var providers = JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<List<MemoryProviderSettings>>)options.GetTypeInfo(typeof(List<MemoryProviderSettings>))) ?? [];
                return providers.Contains(null!) ? throw new JsonException("memory: every entry must be an object") : providers;
            case JsonTokenType.StartObject:
                throw new ChargehandException(ErrorCode.InvalidRequest, "memory is a list now: the object form (backend, url, namespace) was removed",
                    "Move url and api_key_secret into mcp_servers, and list the provider under memory with its tools mapping. docs/guide/memory-and-services.md has the Hindsight entry.");
            default:
                throw new JsonException("memory must be a list of providers");
        }
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<MemoryProviderSettings> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, (JsonTypeInfo<IEnumerable<MemoryProviderSettings>>)options.GetTypeInfo(typeof(IEnumerable<MemoryProviderSettings>)));
}

/// <param name="UsageOnSpans">Call spans carry tokens and cost (ADR 0021). Only for calls no gateway records, e.g. Claude
/// Code on a subscription; with a LiteLLM gateway, usage stays on its generations (ADR 0012).</param>
public sealed record TelemetrySettings(string OtlpEndpoint, string PublicKeySecret, string SecretKeySecret, bool UsageOnSpans = false);

/// <summary>chargehand serve (ADR 0018): a port, and the secret-store item holding the API key callers send. Listen and
/// AllowedHosts open it beyond loopback, for a private network only (ADR 0024).</summary>
/// <param name="MaxConcurrentRuns">Runs executing at once; 1 (the default) runs them one at a time, as ADR 0011's per-model limit assumes.
/// Raise it only when the model's rate limit allows: every run's workers draw on it.</param>
/// <param name="Accounts">Accounts besides the implicit <c>owner</c> that <c>ApiKeySecret</c> opens (ADR 0050); their keys come from <c>chargehand accounts add</c>.</param>
/// <param name="Ui">Serve the web UI at <c>/ui/</c> (ADR 0054) and the operator console at <c>/console/</c> (ADR 0056). Off by default.</param>
public sealed record HttpSettings(string ApiKeySecret, int Port = 4300, string Listen = "127.0.0.1", IReadOnlyList<string>? AllowedHosts = null,
    int MaxConcurrentRuns = 1, IReadOnlyList<AccountSettings>? Accounts = null, bool Ui = false)
{
    /// <summary>Throws an invalid_request error naming the key when an account cannot work.</summary>
    internal void Validate()
    {
        if (AccountSettings.Problems(Accounts ?? []) is { Count: > 0 } problems)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"http: {string.Join("; ", problems)}", "Fix http.accounts in the profile; docs/guide/server.md lists the keys.");
    }
}

/// <summary>Driven writing sessions (ADR 0039). Off unless <c>Enabled</c>: a shell runs in a container only when the profile opts in.</summary>
/// <param name="Images">Session images by digest (<c>name@sha256:...</c>); a repository may pick one of these by name, never another.</param>
/// <param name="Network">Hosts added to the preset's allowlist.</param>
/// <param name="PushSecret">The secret item holding the credential that pushes non-default branches and opens draft pull requests.</param>
/// <param name="MaxSessionCalls">Research and review runs that sessions may have executing at once, across all sessions (default 2). With more
/// sessions than this, a session's call waits for a slot.</param>
/// <param name="Limits">Operational limits that replace the driven preset's: tuning them needs no Prompt CI run, which gates the preset file.</param>
/// <param name="Governor">ADR 0045's governor; null: the defaults of <see cref="DrivenGovernor"/> (observe).</param>
/// <param name="Admission">What the host must have free before a task starts; null: the defaults of <see cref="DrivenAdmission"/>.</param>
/// <param name="Integrator">ADR 0046: merge by policy and fix failing CI as a GitHub App; null: draft pull requests only, as before.</param>
/// <param name="Services">ADR 0048: package caches, test services and a session image by ecosystem; null: none, as before.</param>
/// <param name="Forge">Which code host the remotes are on and how to reach it; null: GitLab for <c>gitlab.com</c> and hosts named <c>gitlab.*</c>, GitHub for every other host.</param>
/// <param name="Planning">ADR 0049: size, split and number a batch's tasks with one cheap read-only run before it starts; null: off (a request can still ask with <c>driven.plan</c>).</param>
/// <param name="Learning">ADR 0045's learning loop: how long and how often pull request outcomes are read back, and the retro after a batch; null: the defaults of <see cref="DrivenLearning"/> (retro off).</param>
/// <param name="Runners">ADR 0058: several runner services, each with its own limits, platform and the server's address as its egress reaches it; set instead of <c>Runner</c>, never with it.</param>
/// <param name="ImageVariants">ADR 0058: for an image of <c>Images</c> (which <c>Services.Images</c> draws from), the image to use on a runner of another platform.</param>
public sealed partial record DrivenSettings(
    bool Enabled = false,
    int MaxParallel = 2,
    int MaxParallelTotal = 4,
    IReadOnlyList<string>? Images = null,
    DrivenNetwork? Network = null,
    DrivenRunner? Runner = null,
    string? PushSecret = null,
    DrivenTaskSource? TaskSource = null,
    int MaxSessionCalls = 2,
    DrivenLimits? Limits = null,
    DrivenGovernor? Governor = null,
    DrivenAdmission? Admission = null,
    DrivenIntegrator? Integrator = null,
    DrivenSharedServices? Services = null,
    DrivenForge? Forge = null,
    DrivenPlanning? Planning = null,
    DrivenLearning? Learning = null,
    IReadOnlyList<DrivenRunnerEntry>? Runners = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? ImageVariants = null);

/// <summary>The preset's <c>driven</c> limits, set per server. A key left out keeps the preset's value; the preset's allowlist stays the preset's
/// (<c>network.allow</c> adds hosts). Prompts and permissions are not here: they are what Prompt CI evaluates.</summary>
public sealed record DrivenLimits(int? MaxMinutes = null, int? NoProgressMinutes = null, int? MaxTurns = null, long? MaxTokens = null, int? MemoryMb = null,
    double? Cpus = null, int? Pids = null, int? MaxContinuations = null)
{
    public DrivenPreset Apply(DrivenPreset preset) => preset with
    {
        MaxMinutes = MaxMinutes ?? preset.MaxMinutes,
        NoProgressMinutes = NoProgressMinutes ?? preset.NoProgressMinutes,
        MaxTurns = MaxTurns ?? preset.MaxTurns,
        MaxTokens = MaxTokens ?? preset.MaxTokens,
        MemoryMb = MemoryMb ?? preset.MemoryMb,
        Cpus = Cpus ?? preset.Cpus,
        Pids = Pids ?? preset.Pids,
        MaxContinuations = MaxContinuations ?? preset.MaxContinuations,
    };

    /// <summary>Throws an invalid_request error naming the key when a value is outside what the preset schema and the container template accept.</summary>
    internal void Validate()
    {
        var problems = new List<string>();
        if (MaxMinutes is < 1) problems.Add("max_minutes must be at least 1");
        if (NoProgressMinutes is < 1) problems.Add("no_progress_minutes must be at least 1");
        if (MaxTurns is < 1) problems.Add("max_turns must be at least 1");
        if (MaxTokens is < 1) problems.Add("max_tokens must be at least 1");
        if (MemoryMb is < 256 or > 65_536) problems.Add("memory_mb must be 256 to 65536");
        if (Cpus is < 0.1 or > 32) problems.Add("cpus must be 0.1 to 32");
        if (Pids is < 16 or > 4096) problems.Add("pids must be 16 to 4096");
        if (MaxContinuations is < 0 or > 5) problems.Add("max_continuations must be 0 to 5");
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"driven.limits: {string.Join("; ", problems)}", "Fix driven.limits in the profile; docs/guide/driven.md lists the keys.");
    }
}

/// <param name="Outside">A network besides the default one that the batch's egress container joins: the one the chargehand server sits on.</param>
/// <param name="McpForward"><c>host:port</c> of the chargehand server as the egress container reaches it. Set, a session can call chargehand for research and review through a forward on its batch network; unset, it cannot.</param>
/// <param name="ModelUrl">An Anthropic-compatible gateway as the egress container reaches it (<c>http://host:port/path</c> through a forward, or <c>https://host/path</c> through the proxy). Set, a session gets
/// <c>claude_code.api_key_secret</c> (the key the gateway issued, with a budget) and a base URL instead of the real credential; unset, the real credential is in the container's environment.</param>
/// <param name="OtlpUrl">An OTLP/HTTP collector as the egress container reaches it (<c>http://host:port</c>, through a forward). Set, each session's Claude Code exports its own logs and
/// metrics there, tagged with the batch, task and trace (ADR 0042); unset, it exports nothing.</param>
public sealed record DrivenNetwork(IReadOnlyList<string>? Allow = null, string? Outside = null, string? McpForward = null, string? ModelUrl = null, string? OtlpUrl = null);

/// <summary>
/// The <c>prompt-enhancer</c> extension (ADR 0040): an entry of <c>mcp_servers</c> that lists the tools <c>enhance</c> and
/// <c>feedback</c> (whetstone's contract). Unset: no enhancer, and every prompt is sent as written.
/// </summary>
/// <param name="Server">A key of <c>mcp_servers</c>.</param>
/// <param name="DeadlineMs">How long to wait for <c>enhance</c> before sending the original; null is 1500. Also the <c>deadline_ms</c> the enhancer is told.</param>
public sealed record PromptEnhancerSettings(string Server, int? DeadlineMs = null)
{
    public const int MinDeadlineMs = 50;
    public const int MaxDeadlineMs = 30_000;

    public int EffectiveDeadlineMs => DeadlineMs ?? (int)Chargehand.Enhancement.GuardedPromptEnhancer.DefaultDeadline.TotalMilliseconds;

    internal void Validate(IReadOnlyDictionary<string, McpServerSettings> servers)
    {
        if (string.IsNullOrEmpty(Server) || !servers.ContainsKey(Server))
            throw new ChargehandException(ErrorCode.InvalidRequest, $"prompt_enhancer.server '{Server}' is not defined in mcp_servers",
                "Add the server to mcp_servers, or remove prompt_enhancer; docs/guide/reference.md lists the fields.");
        if (EffectiveDeadlineMs is < MinDeadlineMs or > MaxDeadlineMs)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"prompt_enhancer.deadline_ms must be {MinDeadlineMs} to {MaxDeadlineMs}",
                "Fix deadline_ms in the profile.");
    }
}
