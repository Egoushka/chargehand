using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Config;

/// <summary>How a runner gets the repository a session works on (ADR 0058): <c>shared</c> reads the server's checkout from a disk both see, <c>bundle</c> is sent a git bundle of it.</summary>
[JsonConverter(typeof(SnakeCaseEnum<WorkspaceMode>))]
public enum WorkspaceMode { Shared, Bundle }

/// <summary>The server's address as one runner's egress containers reach it (ADR 0058). Each key replaces the same key of <c>driven.network</c> for that runner and inherits it when null.</summary>
/// <param name="Mcp"><c>host:port</c> of the chargehand server's MCP endpoint.</param>
/// <param name="ModelUrl">The model gateway, as <see cref="DrivenNetwork.ModelUrl"/>.</param>
/// <param name="OtlpUrl">The OTLP collector, as <see cref="DrivenNetwork.OtlpUrl"/>.</param>
/// <param name="Outside">The network the egress container joins besides the batch's own, as <see cref="DrivenNetwork.Outside"/>.</param>
public sealed record DrivenRunnerForwards(string? Mcp = null, string? ModelUrl = null, string? OtlpUrl = null, string? Outside = null);

/// <summary>One runner service (ADR 0058). <c>driven.runner</c> is the same thing with every other key at its default, named <see cref="DrivenSettings.DefaultRunnerName"/>.</summary>
/// <param name="Name">Lowercase letters, digits and hyphens; it is in run events and in the batch state.</param>
/// <param name="MaxSessions">Sessions this server runs on the runner at once; null: no limit besides the batch's and the server's.</param>
/// <param name="Platform"><c>linux/amd64</c> or <c>linux/arm64</c>: the architecture of the runner's engine, which picks the image (<c>driven.image_variants</c>).</param>
/// <param name="Weight">Multiplies the runner's free memory when two runners are compared; above 0.</param>
/// <param name="ReserveMb">Replaces <c>driven.admission.reserve_mb</c> for this runner.</param>
/// <param name="Workspace">See <see cref="WorkspaceMode"/>.</param>
public sealed record DrivenRunnerEntry(string Name, string Url, string ApiKeySecret, int? MaxSessions = null, string Platform = DrivenRunnerEntry.DefaultPlatform, double Weight = 1,
    int? ReserveMb = null, WorkspaceMode Workspace = WorkspaceMode.Shared, DrivenRunnerForwards? Forwards = null)
{
    public const string DefaultPlatform = "linux/amd64";

    public static readonly IReadOnlyList<string> Platforms = [DefaultPlatform, "linux/arm64"];

    public const double MaxWeight = 100;
}

public sealed partial record DrivenSettings
{
    /// <summary>The name of the runner that <c>driven.runner</c> declares.</summary>
    public const string DefaultRunnerName = "default";

    /// <summary>The runners this server may use (ADR 0058), in the order the profile lists them: <c>runners</c>, else <c>runner</c> as one named <see cref="DefaultRunnerName"/>, else none (the server calls the engine itself).</summary>
    public IReadOnlyList<DrivenRunnerEntry> ResolvedRunners() =>
        Runners is { Count: > 0 } runners ? runners : Runner is { } one ? [new DrivenRunnerEntry(DefaultRunnerName, one.Url, one.ApiKeySecret)] : [];

    /// <summary>True when the profile uses <c>runners</c>: run events and the batch result then say which runner a session ran on.</summary>
    public bool NamesRunners => Runners is { Count: > 0 };

    /// <summary>The image to start on a runner of <paramref name="platform"/>: the reference as the profile writes it, or its variant for that platform (<c>image_variants</c>).</summary>
    public string ImageFor(string reference, string platform) =>
        ImageVariants?.GetValueOrDefault(reference)?.GetValueOrDefault(platform) ?? reference;

    /// <summary>Throws an invalid_request error naming the key when <c>runner</c>, <c>runners</c> or <c>image_variants</c> cannot work.</summary>
    internal void ValidateRunners()
    {
        var problems = new List<string>();
        if (Runner is not null && Runners is not null)
            problems.Add("name the runner in either runner or runners, not both");
        if (Runners is { Count: 0 })
            problems.Add("runners must list at least one runner (leave it out for none)");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (runner, index) in (Runners ?? []).Select((r, i) => (r, i)))
        {
            var runnerName = runner.Name ?? "";
            var label = runnerName.Length > 0 ? $"runners.{runnerName}" : $"runners[{index}]";
            if (!RunnerName().IsMatch(runnerName))
                problems.Add($"{label}: name must be 1 to 32 lowercase letters, digits and hyphens, starting with a letter or digit");
            else if (!names.Add(runnerName))
                problems.Add($"{label}: name is used twice");
            if (!Uri.TryCreate(runner.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
                problems.Add($"{label}: url must be an absolute http or https URL");
            else if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                problems.Add($"{label}: url carries credentials, a query or a fragment; give the runner's address only");
            else if (!urls.TryAdd(uri.GetLeftPart(UriPartial.Authority), runnerName))
                problems.Add($"{label}: url is the same as runner '{urls[uri.GetLeftPart(UriPartial.Authority)]}'");
            if (string.IsNullOrWhiteSpace(runner.ApiKeySecret))
                problems.Add($"{label}: api_key_secret is required");
            if (runner.MaxSessions is < 1)
                problems.Add($"{label}: max_sessions must be at least 1");
            if (!DrivenRunnerEntry.Platforms.Contains(runner.Platform))
                problems.Add($"{label}: platform must be one of {string.Join(", ", DrivenRunnerEntry.Platforms)}");
            if (!(runner.Weight > 0 && runner.Weight <= DrivenRunnerEntry.MaxWeight))
                problems.Add($"{label}: weight must be above 0 and at most {DrivenRunnerEntry.MaxWeight}");
            if (runner.ReserveMb is < 0)
                problems.Add($"{label}: reserve_mb must be at least 0");
            ForwardProblems(runner, label, problems);
        }
        foreach (var (reference, variants) in ImageVariants ?? new Dictionary<string, IReadOnlyDictionary<string, string>>())
        {
            var label = $"image_variants.{reference}";
            if (!ImageReference().IsMatch(reference))
                problems.Add($"{label}: the key must be pinned by digest (name@sha256:<64 hex>)");
            else if (Images?.Contains(reference, StringComparer.Ordinal) != true)
                problems.Add($"{label}: the key must also be listed in driven.images, which is what the server starts");
            if (variants.Count == 0)
                problems.Add($"{label}: name at least one platform");
            foreach (var (platform, image) in variants)
            {
                if (!DrivenRunnerEntry.Platforms.Contains(platform))
                    problems.Add($"{label}: platform '{platform}' must be one of {string.Join(", ", DrivenRunnerEntry.Platforms)}");
                if (!ImageReference().IsMatch(image))
                    problems.Add($"{label}.{platform}: must be pinned by digest (name@sha256:<64 hex>)");
            }
        }
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"driven: {string.Join("; ", problems)}", "Fix driven.runners and driven.image_variants in the profile; docs/guide/driven.md lists the keys.");
    }

    /// <summary>A runner that is not on the server's host cannot use the Docker-network names <c>driven.network</c> may hold; it has to say what to use instead.</summary>
    private void ForwardProblems(DrivenRunnerEntry runner, string label, List<string> problems)
    {
        var forwards = runner.Forwards;
        if (forwards?.Mcp is { } mcp && !HostAndPort().IsMatch(mcp))
            problems.Add($"{label}: forwards.mcp must be host:port");
        if (forwards?.ModelUrl is { } model && !HttpUrl().IsMatch(model))
            problems.Add($"{label}: forwards.model_url must be an http or https URL");
        if (forwards?.OtlpUrl is { } otlp && !PlainHttpUrl().IsMatch(otlp))
            problems.Add($"{label}: forwards.otlp_url must be an http URL");
        if (forwards?.Outside is { } outside && !NetworkName().IsMatch(outside))
            problems.Add($"{label}: forwards.outside must be a network name");
        if (runner.Workspace != WorkspaceMode.Bundle)
            return;
        if (Network?.McpForward is not null && forwards?.Mcp is null)
            problems.Add($"{label}: workspace is bundle, so the runner is not on the server's host, but network.mcp_forward is set; name the server's address as that runner reaches it in forwards.mcp");
        if (Network?.ModelUrl is { } modelUrl && modelUrl.StartsWith("http://", StringComparison.Ordinal) && forwards?.ModelUrl is null)
            problems.Add($"{label}: workspace is bundle, but network.model_url is an http address of the server's network; name the one that runner reaches in forwards.model_url");
        if (Network?.OtlpUrl is not null && forwards?.OtlpUrl is null)
            problems.Add($"{label}: workspace is bundle, but network.otlp_url is set; name the collector as that runner reaches it in forwards.otlp_url");
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex RunnerName();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._/-]*(:[0-9]+)?(/[a-z0-9._-]+)*@sha256:[0-9a-f]{64}$")]
    private static partial Regex ImageReference();

    [GeneratedRegex(@"^[^\s:]+:[0-9]{1,5}$")]
    private static partial Regex HostAndPort();

    [GeneratedRegex(@"^https?://[^\s?#@]+$")]
    private static partial Regex HttpUrl();

    [GeneratedRegex(@"^http://[^\s?#@]+$")]
    private static partial Regex PlainHttpUrl();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$")]
    private static partial Regex NetworkName();
}
