using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Runtime;

namespace Chargehand.Onboarding;

public enum CheckStatus { Ok, Warn, Fail }

/// <summary>One line of <c>chargehand doctor</c>. A message names items, paths and counts, never a secret's value.</summary>
public sealed record CheckResult(CheckStatus Status, string Name, string Message, string? Fix = null);

/// <param name="Status">The HTTP status the service answered with; null when it did not answer.</param>
/// <param name="Failure">Why it did not answer.</param>
public sealed record ProbeOutcome(int? Status, string? Failure = null);

/// <summary>A repository found under a repository root, with what <see cref="RepositoryDetector"/> read from it.</summary>
public sealed record FoundRepository(string Name, RepositoryScan Scan);

/// <summary>Everything doctor reads from the machine, so a test can stand in for the file system, the secret store and the network. Secrets
/// are asked about (<see cref="SecretResolves"/>) and never returned; the probes resolve their own keys.</summary>
public interface IDoctorEnvironment
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>The profile at the path; defaults when the file is absent. Throws as <see cref="Profile.Load"/> does.</summary>
    Profile LoadProfile(string path);

    bool SecretResolves(Profile profile, string item);

    string? EnvironmentVariable(string name);

    bool OnPath(string binary);

    /// <summary>The repositories at or directly under <paramref name="root"/>.</summary>
    IReadOnlyList<FoundRepository> Repositories(string root);

    /// <summary>An authenticated read of the chargehand server: it has no separate health route, so <c>GET /v1/runs?limit=1</c> is the cheapest call that needs the key.</summary>
    Task<ProbeOutcome> ProbeServerAsync(Profile profile, HttpSettings http, CancellationToken ct);

    /// <summary><c>GET /count</c> on the runner with its key.</summary>
    Task<ProbeOutcome> ProbeRunnerAsync(Profile profile, DrivenRunner runner, CancellationToken ct);
}

/// <summary>
/// <c>chargehand doctor</c>: checks a setup and reports one result per check. Order: profile, runtime and model credential, server, runner,
/// driven images, secrets, repository roots, egress allowlist. A profile that does not load ends the run after its own line.
/// </summary>
public static partial class Doctor
{
    private static readonly string[] OpenCodeProviderItems = ["anthropic-api-key", "openai-api-key", "openrouter-api-key", "gemini-api-key"];

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(string profilePath, IDoctorEnvironment env, IReadOnlyList<string> presetAllow, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        Profile profile;
        if (!env.FileExists(profilePath))
            results.Add(new(CheckStatus.Warn, "profile", $"no file at {profilePath}; defaults apply", "run chargehand init to write a starter profile"));
        try
        {
            profile = env.LoadProfile(profilePath);
        }
        catch (ChargehandException e)
        {
            results.Add(new(CheckStatus.Fail, "profile", $"{profilePath}: {e.Message}", e.Action));
            return results;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            results.Add(new(CheckStatus.Fail, "profile", $"{profilePath}: {e.Message}", "fix the JSON; profiles/profile.schema.json describes every field"));
            return results;
        }
        if (env.FileExists(profilePath))
            results.Add(new(CheckStatus.Ok, "profile", $"{profilePath} loads and validates"));

        CheckModelCredential(profile, env, results);
        if (profile.Http is { } http)
            results.Add(await CheckServerAsync(profile, http, env, ct));
        var runners = profile.Driven?.ResolvedRunners() ?? [];
        foreach (var runner in runners)
            results.Add(await CheckRunnerAsync(profile, runner, runners.Count == 1 && !profile.Driven!.NamesRunners ? "runner" : $"runner {runner.Name}", env, ct));
        CheckImages(profile.Driven, results);
        CheckPlatforms(profile.Driven, results);
        CheckSecrets(profile, env, results);
        CheckRoots(profile, env, results);
        CheckEgress(profile, env, presetAllow, results);
        return results;
    }

    /// <summary>0 when no check failed; a warning does not fail the run.</summary>
    public static int ExitCode(IEnumerable<CheckResult> results) => results.Any(r => r.Status == CheckStatus.Fail) ? 1 : 0;

    public static string Format(CheckResult r) =>
        $"{r.Status.ToString().ToLowerInvariant(),-4} {r.Name}: {r.Message}{(r.Fix is null ? "" : $"; fix: {r.Fix}")}";

    /// <summary>The address <c>serve</c> answers on, as the CLI's client commands compute it.</summary>
    public static Uri ServerUrl(HttpSettings http) => new($"http://{(http.Listen is "0.0.0.0" or "::" ? "127.0.0.1" : http.Listen)}:{http.Port}");

    private static void CheckModelCredential(Profile profile, IDoctorEnvironment env, List<CheckResult> results)
    {
        RuntimeKind kind;
        try
        {
            kind = RuntimeSelector.Select(profile.Runtime, env.EnvironmentVariable("CHARGEHAND_RUNTIME"), env.OnPath, profile.RuntimeBlocks);
        }
        catch (ChargehandException e)
        {
            results.Add(new(CheckStatus.Fail, "runtime", e.Message, e.Action));
            return;
        }
        if (kind == RuntimeKind.Opencode)
        {
            var set = OpenCodeProviderItems.Where(item => env.SecretResolves(profile, item)).Select(EnvName).ToList();
            results.Add(profile.Opencode is not null
                ? new(CheckStatus.Warn, "model credential", "opencode runs on the server the profile names; its provider credentials live in that server's own config, which doctor cannot read",
                    "start a session there once to confirm a provider answers")
                : set.Count > 0
                    ? new(CheckStatus.Ok, "model credential", $"opencode runtime; provider variable set: {string.Join(", ", set)}")
                    : new(CheckStatus.Warn, "model credential", $"opencode runtime; none of {string.Join(", ", OpenCodeProviderItems.Select(EnvName))} is set, so only credentials in OpenCode's own auth file can work",
                        "set a provider variable, or run opencode auth login"));
            return;
        }
        ClaudeCodeSettings cc;
        try
        {
            cc = profile.ClaudeCode ?? ClaudeCodeSettings.Detect(item => env.SecretResolves(profile, item));
        }
        catch (ChargehandException e)
        {
            results.Add(new(CheckStatus.Fail, "model credential", e.Message, e.Action));
            return;
        }
        results.Add((cc.ApiKeySecret, cc.OauthTokenSecret) switch
        {
            ({ } key, { }) => new(CheckStatus.Fail, "model credential", $"claude_code names both api_key_secret ({key}) and oauth_token_secret",
                "set only one of them in the profile"),
            ({ } key, null) => env.SecretResolves(profile, key)
                ? new(CheckStatus.Ok, "model credential", $"claude_code runtime; API key from item {key} (per-token billing)")
                : Missing("model credential", key, "claude_code.api_key_secret", CheckStatus.Fail),
            (null, { } token) => env.SecretResolves(profile, token)
                ? new(CheckStatus.Ok, "model credential", $"claude_code runtime; subscription token from item {token}")
                : Missing("model credential", token, "claude_code.oauth_token_secret", CheckStatus.Fail),
            _ => new(CheckStatus.Warn, "model credential", "claude_code runtime; no API key or subscription token is set, so the CLI's own login is used, which doctor cannot check",
                "run claude once and sign in, or set ANTHROPIC_API_KEY or CLAUDE_CODE_OAUTH_TOKEN"),
        });
    }

    private static async Task<CheckResult> CheckServerAsync(Profile profile, HttpSettings http, IDoctorEnvironment env, CancellationToken ct)
    {
        if (!env.SecretResolves(profile, http.ApiKeySecret))
            return new(CheckStatus.Warn, "server", $"not probed: the key item {http.ApiKeySecret} does not resolve", "see the secret line below");
        var url = ServerUrl(http);
        return Judge("server", url.ToString(), await env.ProbeServerAsync(profile, http, ct), http.ApiKeySecret, "start it with chargehand serve, and check http.listen and http.port");
    }

    private static async Task<CheckResult> CheckRunnerAsync(Profile profile, DrivenRunnerEntry runner, string name, IDoctorEnvironment env, CancellationToken ct)
    {
        if (!env.SecretResolves(profile, runner.ApiKeySecret))
            return new(CheckStatus.Warn, name, $"not probed: the key item {runner.ApiKeySecret} does not resolve", "see the secret line below");
        return Judge(name, runner.Url, await env.ProbeRunnerAsync(profile, new DrivenRunner(runner.Url, runner.ApiKeySecret), ct), runner.ApiKeySecret,
            $"start it with chargehand runner, and check {(profile.Driven!.NamesRunners ? $"driven.runners.{runner.Name}.url" : "driven.runner.url")}");
    }

    /// <summary>A runner of another architecture than amd64 starts the image as written unless <c>driven.image_variants</c> names one for it (ADR 0058); doctor cannot read a registry, so it says what applies.</summary>
    private static void CheckPlatforms(DrivenSettings? driven, List<CheckResult> results)
    {
        foreach (var runner in (driven?.ResolvedRunners() ?? []).Where(r => r.Platform != DrivenRunnerEntry.DefaultPlatform))
        {
            var mapped = (driven!.Images ?? []).Where(i => driven.ImageFor(i, runner.Platform) != i).ToList();
            results.Add(mapped.Count > 0
                ? new(CheckStatus.Ok, $"runner {runner.Name} images", $"{runner.Platform}: {mapped.Count} of {driven.Images!.Count} driven.images have a variant for it")
                : new(CheckStatus.Ok, $"runner {runner.Name} images", $"{runner.Platform}: driven.images are used as written, so they must be multi-platform (an index digest); otherwise add driven.image_variants"));
        }
    }

    private static CheckResult Judge(string name, string where, ProbeOutcome outcome, string keyItem, string unreachableFix) => outcome switch
    {
        { Status: >= 200 and < 300 } => new(CheckStatus.Ok, name, $"answers at {where} and accepts the key"),
        { Status: 401 or 403 } => new(CheckStatus.Fail, name, $"answers at {where} but refuses the key", $"the running process uses a different key than item {keyItem}; restart it or fix the item"),
        { Status: { } status } => new(CheckStatus.Fail, name, $"answers at {where} with HTTP {status}", "read its log"),
        _ => new(CheckStatus.Fail, name, $"does not answer at {where}: {outcome.Failure}", unreachableFix),
    };

    private static void CheckImages(DrivenSettings? driven, List<CheckResult> results)
    {
        if (driven is null)
            return;
        var images = driven.Images ?? [];
        if (images.Count == 0)
        {
            results.Add(driven.Enabled
                ? new(CheckStatus.Fail, "driven.images", "driven is enabled but no session image is named", "pin one by digest: \"images\": [\"name@sha256:<64 hex>\"]")
                : new(CheckStatus.Ok, "driven.images", "driven is off and no image is named"));
            return;
        }
        for (var i = 0; i < images.Count; i++)
            results.Add(Pinned().IsMatch(images[i])
                ? new(CheckStatus.Ok, $"driven.images[{i}]", "pinned by digest")
                : new(CheckStatus.Fail, $"driven.images[{i}]", $"{images[i]} is not pinned by digest", "use name@sha256:<64 hex>; a tag can move"));
    }

    private static void CheckSecrets(Profile profile, IDoctorEnvironment env, List<CheckResult> results)
    {
        var uses = new List<(string Item, string Field, bool Required)>();
        if (profile.Opencode is { } oc)
            uses.Add((oc.PasswordSecret, "opencode.password_secret", true));
        if (profile.Http is { } http)
        {
            uses.Add((http.ApiKeySecret, "http.api_key_secret", true));
            foreach (var account in http.Accounts ?? [])
                if (account.Model?.ApiKeySecret is { } accountKey)
                    uses.Add((accountKey, $"http.accounts.{account.Name}.model.api_key_secret", true));
        }
        if (profile.Telemetry is { } t)
        {
            uses.Add((t.PublicKeySecret, "telemetry.public_key_secret", true));
            uses.Add((t.SecretKeySecret, "telemetry.secret_key_secret", true));
        }
        if (profile.Driven is { } driven)
        {
            if (driven.PushSecret is { } push)
                uses.Add((push, "driven.push_secret", driven.Enabled));
            foreach (var runner in driven.ResolvedRunners())
                uses.Add((runner.ApiKeySecret, driven.NamesRunners ? $"driven.runners.{runner.Name}.api_key_secret" : "driven.runner.api_key_secret", driven.Enabled));
            if (driven.Forge?.TokenSecret is { } forgeToken)
                uses.Add((forgeToken, "driven.forge.token_secret", driven.Enabled));
        }
        foreach (var (name, server) in profile.McpServers ?? new Dictionary<string, McpServerSettings>())
            foreach (var item in (server.Headers?.Values ?? []).Concat(server.Env?.Values ?? []).SelectMany(v => SecretPlaceholder().Matches(v).Select(m => m.Groups["item"].Value)))
                uses.Add((item, $"mcp_servers.{name}", true));

        foreach (var group in uses.GroupBy(u => u.Item, StringComparer.Ordinal))
        {
            var fields = string.Join(", ", group.Select(g => g.Field).Distinct());
            results.Add(env.SecretResolves(profile, group.Key)
                ? new(CheckStatus.Ok, $"secret {group.Key}", $"present ({fields})")
                : Missing($"secret {group.Key}", group.Key, fields, group.Any(g => g.Required) ? CheckStatus.Fail : CheckStatus.Warn));
        }
    }

    private static CheckResult Missing(string name, string item, string field, CheckStatus status) =>
        new(status, name, $"missing ({field})", $"set {EnvName(item)} in the environment, or add a source that resolves {item} under secrets in the profile");

    private static void CheckRoots(Profile profile, IDoctorEnvironment env, List<CheckResult> results)
    {
        if (profile.RepositoryRoots is not { Count: > 0 } roots)
        {
            results.Add(profile.Http is not null
                ? new(CheckStatus.Warn, "repository_roots", "not set, so serve allows worker_root only and requests for your own repositories are refused", "set repository_roots to the directories that hold them")
                : new(CheckStatus.Ok, "repository_roots", "not set; worker_root and the launch directory apply"));
            return;
        }
        foreach (var root in roots)
            results.Add(root == "/"
                ? new(CheckStatus.Ok, "repository_roots", "/ allows any repository")
                : env.DirectoryExists(root)
                    ? new(CheckStatus.Ok, "repository_roots", $"{root} exists")
                    : new(CheckStatus.Fail, "repository_roots", $"{root} does not exist", "create it, or remove or correct the entry"));
    }

    private static void CheckEgress(Profile profile, IDoctorEnvironment env, IReadOnlyList<string> presetAllow, List<CheckResult> results)
    {
        if (profile.Driven is not { } driven || profile.RepositoryRoots is not { Count: > 0 } roots)
            return;
        List<string> allow = [.. presetAllow, .. driven.Network?.Allow ?? []];
        if (Uri.TryCreate(driven.Network?.ModelUrl, UriKind.Absolute, out var model) && model.Scheme == Uri.UriSchemeHttps)
            allow.Add(model.Host);
        var failure = driven.Enabled ? CheckStatus.Fail : CheckStatus.Warn;
        foreach (var root in roots.Where(r => r != "/" && env.DirectoryExists(r)))
        {
            var repositories = env.Repositories(root).Where(r => r.Scan.Hosts.Count > 0).ToList();
            var lacking = repositories.Select(r => (r.Name, Missing: RepositoryDetector.Uncovered(r.Scan.Hosts, allow))).Where(r => r.Missing.Count > 0).ToList();
            if (repositories.Count == 0)
                results.Add(new(CheckStatus.Ok, "egress", $"{root}: no repository with a known ecosystem"));
            else if (lacking.Count == 0)
                results.Add(new(CheckStatus.Ok, "egress", $"{root}: the allowlist covers the package hosts of {repositories.Count} {(repositories.Count == 1 ? "repository" : "repositories")}"));
            else
                results.Add(new(failure, "egress",
                    $"{root}: {string.Join("; ", lacking.Take(5).Select(r => $"{r.Name} needs {string.Join(", ", r.Missing)}"))}{(lacking.Count > 5 ? $"; and {lacking.Count - 5} more" : "")} not allowed",
                    $"add {string.Join(", ", lacking.SelectMany(r => r.Missing).Distinct())} to driven.network.allow"));
        }
    }

    private static string EnvName(string item) => item.ToUpperInvariant().Replace('-', '_');

    [GeneratedRegex(@"^[^@\s]+@sha256:[0-9a-f]{64}$")]
    private static partial Regex Pinned();

    [GeneratedRegex(@"\{secret:(?<item>[A-Za-z0-9._-]+)\}")]
    private static partial Regex SecretPlaceholder();
}
