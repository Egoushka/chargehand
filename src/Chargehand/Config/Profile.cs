using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Console.Api;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Memory.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.PeerClones.Api;
using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using Chargehand.Runtime;
using Chargehand.Runtimes.Acp.Api;
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
    AutomationSettings? Automation = null,
    IReadOnlyDictionary<string, IReadOnlyList<ServiceUse>>? PresetServices = null,
    DeliverySettings? Delivery = null,
    AcpSettings? Acp = null,
    InboxSettings? Inbox = null,
    PublicFreeSettings? PublicFree = null,
    bool SkipStructuredIntake = false,
    PeerCloneSettings? PeerClone = null,
    PoolsSettings? Pools = null)
{
    /// <summary>A fixed directory outside $HOME (ADR 0003 forbids worker checkouts under it), created on first use.</summary>
    public const string DefaultWorkerRoot = "/var/tmp/chargehand/work";

    /// <summary>Where a request's repository may live (ADR 0023); worker_root alone when the profile names none. The managed clones (ADR 0051) are among them when GitHub intake or schedules is configured.</summary>
    public IReadOnlyList<string> Roots => RepositoryRoots is null ? [WorkerRoot]
        : Automation is { Github: not null } or { Schedules: not null } ? [.. RepositoryRoots, ClonesRoot] : RepositoryRoots;

    /// <summary>Where intake and the scheduler clone the repositories they discover and those whose checkout they cannot write.</summary>
    [JsonIgnore]
    public string ClonesRoot => Path.Combine(WorkerRoot, AutomationGitHub.ClonesDirectory);

    /// <summary>The services a preset's node may use: the profile's list for the preset (<c>preset_services</c>), else the preset's own (ADR 0034).
    /// A shipped preset names no server of anyone's profile, so the profile maps its own.</summary>
    public IReadOnlyList<ServiceUse>? ServicesFor(string preset, IReadOnlyList<ServiceUse>? presetServices) =>
        PresetServices is not null && PresetServices.TryGetValue(preset, out var mapped) ? mapped : presetServices;

    /// <summary>How long one command secret source may run before it is killed and the next source is tried (ADR 0034).</summary>
    internal TimeSpan CommandTimeout { get; init; } = SecretSources.DefaultCommandTimeout;

    /// <summary>The runtimes this profile has a block for; the only one names the runtime when neither the runtime field
    /// nor CHARGEHAND_RUNTIME does (ADR 0032).</summary>
    public IReadOnlyList<RuntimeKind> RuntimeBlocks =>
        [.. Opencode is null ? [] : new[] { RuntimeKind.Opencode }, .. ClaudeCode is null ? [] : new[] { RuntimeKind.ClaudeCode }];
    // The acp block is not a runtime block: it only holds credentials, and a request names its agent (acp:<agent>).

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

    /// <summary>What <see cref="Load"/> reads with (ADR 0060): <see cref="Json"/>'s options and no reflection. Each module's source-generated
    /// context supplies its own sections; <see cref="ProfileJsonContext"/> the root and the sections declared here.</summary>
    internal static readonly JsonSerializerOptions LoadJson = new(Json)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(LedgerSettingsJsonContext.Default, AccountsSettingsJsonContext.Default,
            MemorySettingsJsonContext.Default, ForgeSettingsJsonContext.Default, HandoverSettingsJsonContext.Default, IntakeSettingsJsonContext.Default,
            BatchesSettingsJsonContext.Default, OversightSettingsJsonContext.Default, RunnersSettingsJsonContext.Default, SessionsSettingsJsonContext.Default, TasksSettingsJsonContext.Default,
            OpenCodeSettingsJsonContext.Default, ClaudeCodeSettingsJsonContext.Default, AcpSettingsJsonContext.Default, PeerCloneSettingsJsonContext.Default, EconomicsSettingsJsonContext.Default, KernelSettingsJsonContext.Default, GatewaySettingsJsonContext.Default, ResearchSettingsJsonContext.Default, ConsoleSettingsJsonContext.Default, ProfileJsonContext.Default),
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
            enhancer.Validate(servers.Keys);
        profile.Http?.Validate();
        foreach (var (preset, uses) in profile.PresetServices ?? new Dictionary<string, IReadOnlyList<ServiceUse>>())
            foreach (var use in uses)
                if (!servers.ContainsKey(use.Server) || use.Tools.Count == 0 || use.Tools.Any(t => t.Length == 0 || t.All(c => c == '*')))
                    throw new ChargehandException(ErrorCode.InvalidRequest,
                        $"preset_services.{preset}: '{use.Server}' " + (servers.ContainsKey(use.Server) ? "needs explicit tool names (a glob is fine, a bare * is not)" : "is not an entry of mcp_servers"),
                        "Fix preset_services in the profile; docs/guide/memory-and-services.md has an example.");
        profile.Driven?.Limits?.Validate();
        profile.Driven?.Governor?.Validate();
        profile.Driven?.Admission?.Validate();
        profile.Driven?.Planning?.Validate();
        profile.Driven?.Learning?.Validate();
        profile.Driven?.Integrator?.Validate();
        profile.Driven?.Services?.Validate(profile.Driven.Images);
        profile.Driven?.Forge?.Validate();
        profile.Driven?.ValidateRunners();
        profile.Driven?.ValidateEnvironment();
        profile.Automation?.Validate(profile.Driven?.Enabled == true, profile.Driven?.Integrator);
        if (profile.Automation?.Account is { } charged && profile.Http?.Accounts?.FirstOrDefault(a => a.Name == charged) is not { Driven: true })
            throw new ChargehandException(ErrorCode.InvalidRequest, $"automation.account: '{charged}' must be an account of http.accounts with driven: true",
                "Declare the account under http.accounts with driven: true, or leave automation.account out to run automated starts as the owner.");
        profile.Delivery?.Validate(profile.Automation?.Github?.Repositories?.Select(r => ("automation.github.repositories", r.Key, r.Value.Path)) ?? []);
        if (profile.PublicFree?.Problems().ToList() is { Count: > 0 } poolProblems)
            throw new ChargehandException(ErrorCode.InvalidRequest, "public_free: " + string.Join("; ", poolProblems),
                "Fix public_free in the profile; docs/adr/0093-runs-on-public-repositories-use-a-free-model-pool.md lists the fields.");
        profile.PeerClone?.Validate();
        if (profile.Http?.Accounts?.Any(a => a.PublicClone) == true && profile.PeerClone is null)
            throw new ChargehandException(ErrorCode.InvalidRequest, "http.accounts: an account has public_clone: true, but the profile has no peer_clone",
                "Add peer_clone to the profile, or set public_clone to false on the account.");
        if (profile.Pools?.Problems().ToList() is { Count: > 0 } routerProblems)
            throw new ChargehandException(ErrorCode.InvalidRequest, "pools: " + string.Join("; ", routerProblems),
                "Fix pools in the profile; docs/adr/0095-a-budget-aware-model-router-drains-pools-in-order-and-paces-each-day.md lists the fields.");
        return profile;
    }

    /// <summary>Which repositories are local-only (ADR 0069); none when the profile has no <c>delivery</c> block.</summary>
    [JsonIgnore]
    public DeliveryPolicy DeliveryPolicy => Delivery?.Policy ?? DeliveryPolicy.None;

    /// <summary>The provider segment of the placeholder models presets name (e.g. provider/worker-model).</summary>
    public const string PlaceholderProvider = "provider/";

    /// <summary>The profile maps placeholder models to real ids. An unmapped placeholder is unset, so the runtime uses
    /// its own default model (ADR 0026); any other id passes through.</summary>
    public string? ResolveModel(string model) =>
        Models?.GetValueOrDefault(model) ?? (model.StartsWith(PlaceholderProvider, StringComparison.Ordinal) ? null : model);

    /// <summary>The price of the id a model resolves to, else of the model as written (a <c>prices</c> entry may name a placeholder).</summary>
    public ModelPrice? PriceOf(string model) =>
        (ResolveModel(model) is { } id ? Prices?.GetValueOrDefault(id) : null) ?? Prices?.GetValueOrDefault(model);

    /// <summary>Tries each source in order; the first that resolves the item wins (ADR 0026). Default with no profile: env only.
    /// A command source that outruns <see cref="CommandTimeout"/> is killed and counts as not resolving (ADR 0034).</summary>
    public string Secret(string item) => SecretSources.Resolve(Secrets, item, CommandTimeout);
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

/// <summary>chargehand serve (ADR 0018): a port, and the secret-store item holding the API key callers send. Listen and
/// AllowedHosts open it beyond loopback, for a private network only (ADR 0024).</summary>
/// <param name="MaxConcurrentRuns">Runs executing at once; 1 (the default) runs them one at a time, as ADR 0011's per-model limit assumes.
/// Raise it only when the model's rate limit allows: every run's workers draw on it.</param>
/// <param name="Accounts">Accounts besides the implicit <c>owner</c> that <c>ApiKeySecret</c> opens (ADR 0050); their keys come from <c>chargehand accounts add</c>.</param>
/// <param name="Ui">Serve the operator console at <c>/console/</c> (ADR 0056); <c>/ui/</c> redirects to it (ADR 0072). Off by default.</param>
/// <param name="CloudflareAccess">Sign in by the email Cloudflare Access checked, with no key (ADR 0071); null: keys only.</param>
/// <param name="ProxySignIn">Sign in by the email a trusted single-sign-on proxy forwards, with no key (ADR 0071); null: keys only.</param>
/// <param name="Conversations">Turns on <c>/v1/conversations</c> (ADR 0086): a person steers an agent while it works. Null: the routes are not there.</param>
/// <param name="Events">Turns on the event log (ADR 0090): <c>/v1/events</c>, its stream and <c>/v1/threads</c>, in <c>events.db</c> beside the run log. Null: the routes are not there.</param>
public sealed record HttpSettings(string ApiKeySecret, int Port = 4300, string Listen = "127.0.0.1", IReadOnlyList<string>? AllowedHosts = null,
    int MaxConcurrentRuns = 1, IReadOnlyList<AccountSettings>? Accounts = null, bool Ui = false, CloudflareAccessSettings? CloudflareAccess = null,
    ProxySignInSettings? ProxySignIn = null, ConversationSettings? Conversations = null, EventsSettings? Events = null)
{
    /// <summary>Throws an invalid_request error naming the key when an account cannot work.</summary>
    internal void Validate()
    {
        if (AccountSettings.Problems(Accounts ?? []) is { Count: > 0 } problems)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"http: {string.Join("; ", problems)}", "Fix http.accounts in the profile; docs/guide/server.md lists the keys.");
        if (CloudflareAccess?.Problems(Accounts ?? []).ToList() is { Count: > 0 } access)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"http.cloudflare_access: {string.Join("; ", access)}", "Fix http.cloudflare_access in the profile; docs/guide/server.md lists the keys.");
        if (ProxySignIn?.Problems(Accounts ?? []).ToList() is { Count: > 0 } proxy)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"http.proxy_sign_in: {string.Join("; ", proxy)}", "Fix http.proxy_sign_in in the profile; docs/guide/server.md lists the keys.");
        if (Conversations?.Problems().ToList() is { Count: > 0 } conversations)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"http.conversations: {string.Join("; ", conversations)}", "Fix http.conversations in the profile; docs/guide/server.md lists the keys.");
        if (Events?.Problems() is { Count: > 0 } events)
            throw new ChargehandException(ErrorCode.InvalidRequest, string.Join("; ", events), "Fix http.events in the profile; docs/guide/reference.md lists the keys.");
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
/// <param name="Environment">ADR 0078: <c>image</c> (the default, also when unset): the toolchains are the session image's. <c>mise</c>: the first of <c>Images</c> is the mise base image; an install helper installs what the repository
/// pins into a per-repository home volume before each session, <c>Services.Caches</c> and <c>Services.Images</c> are ignored, and the repository's CI verifies (the driver runs the declared check before the push).</param>
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
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? ImageVariants = null,
    string? Environment = null);


