using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chargehand.Budget;
using Chargehand.Contracts;
using Chargehand.Memory;
using Chargehand.Runtime;
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
    PromptEnhancerSettings? PromptEnhancer = null)
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
        var servers = profile.McpServers ?? new Dictionary<string, McpServerSettings>();
        foreach (var (name, server) in servers)
            server.Validate(name);
        if (profile.Memory is { Count: > 0 } memory && MemoryMapping.Validate(memory, servers) is { Count: > 0 } problems)
            throw new ChargehandException(ErrorCode.InvalidRequest, string.Join("; ", problems),
                "Fix memory in the profile; docs/guide/reference.md lists the fields.");
        if (profile.PromptEnhancer is { } enhancer)
            enhancer.Validate(servers);
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

/// <summary>
/// One memory provider of the profile's <c>memory</c> list (ADR 0034, spec decisions 5, 6 and 17): an MCP server named in
/// <c>mcp_servers</c>, the tools that give it recall (and retain, invalidate) and how their arguments and results map onto
/// <c>IMemoryProvider</c>. <see cref="Profile.Load"/> has run <see cref="MemoryMapping.Validate(MemoryProviderSettings, IReadOnlyDictionary{string, McpServerSettings})"/>
/// on every entry it returns, so a reader may rely on a defined server, a recall tool, placeholders from the allowed sets
/// and, when <paramref name="Retain"/> is true, a retain tool. An entry built any other way carries no such promise.
/// </summary>
/// <param name="Name">Unique in the list, <c>[a-z][a-z0-9-]*</c>. The source's label in the prompt, in the chain block
/// <c>memory/recall/&lt;name&gt;</c> and in span tags.</param>
/// <param name="Server">A key of <c>mcp_servers</c>.</param>
/// <param name="Namespace">The value of <c>{namespace}</c> (a Hindsight bank). Null for a source with no banks; see <see cref="EffectiveNamespace"/>.</param>
/// <param name="MaxFacts">Facts kept per recall; null is the default of <see cref="MemoryLimits"/>. Also the value of <c>{max_facts}</c>.</param>
/// <param name="MaxChars">Characters kept per recall, all facts together; null is the default of <see cref="MemoryLimits"/>.</param>
/// <param name="MaxFactChars">Characters kept per fact, cut with an ellipsis; null is the default of <see cref="MemoryLimits"/>.</param>
/// <param name="TimeoutSeconds">How long one recall or retain call may take; null is the default of <see cref="MemoryLimits"/>.</param>
/// <param name="Retain">A completed run writes to this provider. Needs <c>Tools.Retain</c>; false (the default) for a recall-only source.</param>
/// <param name="RetainTags">Tags of a retained item; null is <see cref="DefaultRetainTags"/>.</param>
public sealed record MemoryProviderSettings(
    string Name,
    string Server,
    MemoryTools Tools,
    string? Namespace = null,
    int? MaxFacts = null,
    int? MaxChars = null,
    int? MaxFactChars = null,
    int? TimeoutSeconds = null,
    bool Retain = false,
    IReadOnlyList<string>? RetainTags = null)
{
    /// <summary>The tags of a retained item when the entry names none.</summary>
    public static readonly IReadOnlyList<string> DefaultRetainTags = ["chargehand"];

    /// <summary>The value of <c>{namespace}</c> and the namespace of the memory scope: the entry's own, else its name.</summary>
    public string EffectiveNamespace => Namespace ?? Name;

    /// <summary>The entry's caps and timeout, each falling back to the default of <see cref="MemoryLimits"/> (spec, decisions 6 and 17).</summary>
    public MemoryLimits Limits
    {
        get
        {
            var defaults = new MemoryLimits();
            return new MemoryLimits(MaxFacts ?? defaults.MaxFacts, MaxChars ?? defaults.MaxChars,
                TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, MaxFactChars ?? defaults.MaxFactChars);
        }
    }

    public IReadOnlyList<string> EffectiveRetainTags => RetainTags ?? DefaultRetainTags;
}

/// <summary>The tools of one memory provider. <c>Recall</c> is required (a provider takes part in recall through it);
/// <c>Retain</c> is required when the entry retains; <c>Invalidate</c> is optional and no run calls it.</summary>
public sealed record MemoryTools(ToolCall Recall, ToolCall? Retain = null, ToolCall? Invalidate = null);

/// <summary>
/// One MCP tool call of a mapping: the tool's name and the template of its arguments, expanded per call. In the template a
/// string that is exactly one placeholder (<c>"{tags}"</c>) becomes the value with its own type (an array, an integer, an
/// ISO 8601 string), and a null value drops the argument; a placeholder inside longer text is replaced as text; numbers,
/// booleans and nulls are literal; objects and arrays are walked, and only their string values hold placeholders. The
/// placeholders each tool may use are <see cref="MemoryMapping.RecallPlaceholders"/>, <see cref="MemoryMapping.RetainPlaceholders"/>
/// and <see cref="MemoryMapping.InvalidatePlaceholders"/>.
/// </summary>
/// <param name="Results">Recall only (retain and invalidate answers are not read): how to read the tool's answer. Null means
/// <see cref="ResultMapping.Default"/>; see <see cref="EffectiveResults"/>.</param>
public sealed record ToolCall(string Tool, IReadOnlyDictionary<string, JsonElement> Arguments, ResultMapping? Results = null)
{
    public ResultMapping EffectiveResults => Results ?? ResultMapping.Default;
}

/// <summary>
/// How to read a recall tool's answer as facts. With <see cref="Format"/> <c>json</c>: the answer is the tool's
/// <c>structuredContent</c> if <see cref="Path"/> leads to an array in it, else its first text block parsed as JSON (a Python
/// MCP tool that returns <c>str</c> sends its text in both, wrapped as <c>{"result": "…"}</c> in the structured content);
/// <see cref="Path"/> leads from the root to the array of results; each element of the array is one candidate fact, whose
/// <see cref="Id"/> property is the fact's id (a missing one becomes the first 12 hex characters of the SHA-256 of the fact
/// text) and whose text is the first of <see cref="EffectiveText"/> that qualifies. With <see cref="FormatText"/> the whole first text block is one fact and the
/// other fields are unused. An error result, a path that does not lead to an array, or JSON that does not parse is a failure of
/// the provider (the stack skips it), never a fact.
/// </summary>
/// <param name="Path">Dotted property names leading to the array (<c>data.items</c>); null or empty means the root is the
/// array. A property name holds no dot and no index.</param>
/// <param name="Id">The element property holding the fact's id; default <c>id</c>.</param>
/// <param name="Text">Ordered entries, JSON: one string or an array of strings. An entry without a brace is a field name; an
/// entry with braces is a template in which <c>{field}</c> is replaced by that field's value. Fields are properties of the
/// element (any characters but braces; no path). An entry qualifies only if every field it names is present and non-empty
/// (a string with characters, or a number); the first that qualifies is the fact, and an element with none is skipped. Null
/// means <c>["text"]</c>; see <see cref="EffectiveText"/>.</param>
/// <param name="Format"><see cref="FormatJson"/> (default) or <see cref="FormatText"/>.</param>
public sealed record ResultMapping(
    string? Path = null,
    string Id = "id",
    [property: JsonConverter(typeof(StringOrListConverter))] IReadOnlyList<string>? Text = null,
    string Format = "json")
{
    public const string FormatJson = "json";
    public const string FormatText = "text";

    /// <summary>What a recall tool without a <c>results</c> block is read with: the shape Hindsight answers, <c>{"results":[{"id":…,"text":…}]}</c>.
    /// A <c>results</c> block that leaves out <c>path</c> reads the root instead.</summary>
    public static readonly ResultMapping Default = new(Path: "results");

    public IReadOnlyList<string> EffectiveText => Text ?? DefaultText;

    private static readonly string[] DefaultText = ["text"];
}

/// <summary>Reads <c>memory</c> as a list of providers. The object it used to be (ADR 0008) fails with the migration (ADR 0034); anything else is a JSON error.</summary>
internal sealed class MemoryListConverter : JsonConverter<IReadOnlyList<MemoryProviderSettings>>
{
    public override IReadOnlyList<MemoryProviderSettings> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartArray:
                var providers = JsonSerializer.Deserialize<List<MemoryProviderSettings>>(ref reader, options) ?? [];
                return providers.Contains(null!) ? throw new JsonException("memory: every entry must be an object") : providers;
            case JsonTokenType.StartObject:
                throw new ChargehandException(ErrorCode.InvalidRequest, "memory is a list now: the object form (backend, url, namespace) was removed",
                    "Move url and api_key_secret into mcp_servers, and list the provider under memory with its tools mapping. docs/guide/memory-and-services.md has the Hindsight entry.");
            default:
                throw new JsonException("memory must be a list of providers");
        }
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<MemoryProviderSettings> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, (IEnumerable<MemoryProviderSettings>)value, options);
}

/// <summary>Reads a string as a list of one and an array of strings as is.</summary>
internal sealed class StringOrListConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return [reader.GetString()!];
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("expected a string or an array of strings");
        var items = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            items.Add(reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new JsonException("expected an array of strings"));
        return items;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

/// <param name="UsageOnSpans">Call spans carry tokens and cost (ADR 0021). Only for calls no gateway records, e.g. Claude
/// Code on a subscription; with a LiteLLM gateway, usage stays on its generations (ADR 0012).</param>
public sealed record TelemetrySettings(string OtlpEndpoint, string PublicKeySecret, string SecretKeySecret, bool UsageOnSpans = false);

/// <summary>chargehand serve (ADR 0018): a port, and the secret-store item holding the API key callers send. Listen and
/// AllowedHosts open it beyond loopback, for a private network only (ADR 0024).</summary>
public sealed record HttpSettings(string ApiKeySecret, int Port = 4300, string Listen = "127.0.0.1", IReadOnlyList<string>? AllowedHosts = null);

/// <summary>Driven writing sessions (ADR 0039). Off unless <c>Enabled</c>: a shell runs in a container only when the profile opts in.</summary>
/// <param name="Images">Session images by digest (<c>name@sha256:...</c>); a repository may pick one of these by name, never another.</param>
/// <param name="Network">Hosts added to the preset's allowlist.</param>
/// <param name="PushSecret">The secret item holding the credential that pushes non-default branches and opens draft pull requests.</param>
public sealed record DrivenSettings(
    bool Enabled = false,
    int MaxParallel = 2,
    int MaxParallelTotal = 4,
    IReadOnlyList<string>? Images = null,
    DrivenNetwork? Network = null,
    DrivenRunner? Runner = null,
    string? PushSecret = null,
    DrivenTaskSource? TaskSource = null);

/// <param name="Outside">A network besides the default one that the batch's egress container joins: the one the chargehand server sits on.</param>
/// <param name="McpForward"><c>host:port</c> of the chargehand server as the egress container reaches it. Set, a session can call chargehand for research and review through a forward on its batch network; unset, it cannot.</param>
public sealed record DrivenNetwork(IReadOnlyList<string>? Allow = null, string? Outside = null, string? McpForward = null);

/// <summary>The runner service that holds the container engine's socket; null means the server calls the engine itself.</summary>
public sealed record DrivenRunner(string Url, string ApiKeySecret);

/// <summary>How a tracker item id becomes a goal (ADR 0039): call <c>Tool</c> on <c>Server</c> (a key of <c>mcp_servers</c>) with <c>{ref}</c> in
/// <c>Arguments</c> replaced by the id, then read the goal's title and body from the JSON answer by dotted path.</summary>
/// <param name="Title">Dotted property names leading to the title string.</param>
/// <param name="Body">The same for the description; null for a title-only goal.</param>
public sealed record DrivenTaskSource(string Server, string Tool, IReadOnlyDictionary<string, JsonElement> Arguments, string Title, string? Body = null);


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
