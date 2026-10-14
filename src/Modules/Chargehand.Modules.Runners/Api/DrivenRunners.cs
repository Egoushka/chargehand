using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chargehand.Contracts;

namespace Chargehand.Modules.Runners.Api;

/// <summary>How a runner gets the repository a session works on (ADR 0058): <c>shared</c> reads the server's checkout from a disk both see, <c>bundle</c> is sent a git bundle of it.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<WorkspaceMode>))]
public enum WorkspaceMode { Shared, Bundle }

/// <summary>The runner service that holds the container engine's socket; null means the server calls the engine itself. One runner, named <c>default</c>: <c>driven.runners</c> (ADR 0058) lists several.</summary>
public sealed record DrivenRunner(string Url, string ApiKeySecret);

/// <summary>The server's address as one runner's egress containers reach it (ADR 0058). Each key replaces the same key of <c>driven.network</c> for that runner and inherits it when null.</summary>
/// <param name="Mcp"><c>host:port</c> of the chargehand server's MCP endpoint.</param>
/// <param name="ModelUrl">The model gateway, as <c>driven.network.model_url</c>.</param>
/// <param name="OtlpUrl">The OTLP collector, as <c>driven.network.otlp_url</c>.</param>
/// <param name="Outside">The network the egress container joins besides the batch's own, as <c>driven.network.outside</c>.</param>
public sealed record DrivenRunnerForwards(string? Mcp = null, string? ModelUrl = null, string? OtlpUrl = null, string? Outside = null);

/// <summary>One runner service (ADR 0058). <c>driven.runner</c> is the same thing with every other key at its default, named <c>default</c>.</summary>
/// <param name="Name">Lowercase letters, digits and hyphens; it is in run events and in the batch state.</param>
/// <param name="MaxSessions">Sessions this server runs on the runner at once; null: no limit besides the batch's and the server's.</param>
/// <param name="Platform"><c>linux/amd64</c> or <c>linux/arm64</c>: the architecture of the runner's engine, which picks the image (<c>driven.image_variants</c>).</param>
/// <param name="Weight">Multiplies the runner's free memory when two runners are compared; above 0.</param>
/// <param name="ReserveMb">Replaces <c>driven.admission.reserve_mb</c> for this runner.</param>
/// <param name="Workspace">See <see cref="WorkspaceMode"/>.</param>
public sealed partial record DrivenRunnerEntry(string Name, string Url, string ApiKeySecret, int? MaxSessions = null, string Platform = DrivenRunnerEntry.DefaultPlatform, double Weight = 1,
    int? ReserveMb = null, WorkspaceMode Workspace = WorkspaceMode.Shared, DrivenRunnerForwards? Forwards = null)
{
    public const string DefaultPlatform = "linux/amd64";

    public static readonly IReadOnlyList<string> Platforms = [DefaultPlatform, "linux/arm64"];

    public const double MaxWeight = 100;

    /// <summary>What is wrong with <c>driven.runners</c>, each naming its runner and key. <paramref name="mcpForward"/>, <paramref name="modelUrl"/> and <paramref name="otlpUrl"/> are
    /// <c>driven.network</c>'s, which a runner off the server's host has to replace.</summary>
    public static List<string> Problems(IReadOnlyList<DrivenRunnerEntry>? runners, string? mcpForward, string? modelUrl, string? otlpUrl)
    {
        var problems = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (runner, index) in (runners ?? []).Select((r, i) => (r, i)))
        {
            var runnerName = runner.Name ?? "";
            var label = runnerName.Length > 0 ? $"runners.{runnerName}" : $"runners[{index}]";
            problems.AddRange(AddressProblems(runner, runnerName, label, names, urls));
            problems.AddRange(LimitProblems(runner, label));
            if (runner.Forwards is { } forwards)
                problems.AddRange(ForwardProblems(forwards, label));
            if (runner.Workspace == WorkspaceMode.Bundle)
                problems.AddRange(BundleProblems(runner.Forwards, label, mcpForward, modelUrl, otlpUrl));
        }
        return problems;
    }

    private static IEnumerable<string> AddressProblems(DrivenRunnerEntry runner, string runnerName, string label, HashSet<string> names, Dictionary<string, string> urls)
    {
        if (!RunnerName().IsMatch(runnerName))
            yield return $"{label}: name must be 1 to 32 lowercase letters, digits and hyphens, starting with a letter or digit";
        else if (!names.Add(runnerName))
            yield return $"{label}: name is used twice";
        if (!Uri.TryCreate(runner.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            yield return $"{label}: url must be an absolute http or https URL";
        else if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            yield return $"{label}: url carries credentials, a query or a fragment; give the runner's address only";
        else if (!urls.TryAdd(uri.GetLeftPart(UriPartial.Authority), runnerName))
            yield return $"{label}: url is the same as runner '{urls[uri.GetLeftPart(UriPartial.Authority)]}'";
        if (string.IsNullOrWhiteSpace(runner.ApiKeySecret))
            yield return $"{label}: api_key_secret is required";
    }

    private static IEnumerable<string> LimitProblems(DrivenRunnerEntry runner, string label)
    {
        if (runner.MaxSessions is < 1)
            yield return $"{label}: max_sessions must be at least 1";
        if (!Platforms.Contains(runner.Platform))
            yield return $"{label}: platform must be one of {string.Join(", ", Platforms)}";
        if (!(runner.Weight > 0 && runner.Weight <= MaxWeight))
            yield return $"{label}: weight must be above 0 and at most {MaxWeight}";
        if (runner.ReserveMb is < 0)
            yield return $"{label}: reserve_mb must be at least 0";
    }

    private static IEnumerable<string> ForwardProblems(DrivenRunnerForwards forwards, string label)
    {
        if (forwards.Mcp is { } mcp && !HostAndPort().IsMatch(mcp))
            yield return $"{label}: forwards.mcp must be host:port";
        if (forwards.ModelUrl is { } model && !HttpUrl().IsMatch(model))
            yield return $"{label}: forwards.model_url must be an http or https URL";
        if (forwards.OtlpUrl is { } otlp && !PlainHttpUrl().IsMatch(otlp))
            yield return $"{label}: forwards.otlp_url must be an http URL";
        if (forwards.Outside is { } outside && !NetworkName().IsMatch(outside))
            yield return $"{label}: forwards.outside must be a network name";
    }

    /// <summary>A runner that is not on the server's host cannot use the Docker-network names <c>driven.network</c> may hold; it has to say what to use instead.</summary>
    private static IEnumerable<string> BundleProblems(DrivenRunnerForwards? forwards, string label, string? mcpForward, string? modelUrl, string? otlpUrl)
    {
        if (mcpForward is not null && forwards?.Mcp is null)
            yield return $"{label}: workspace is bundle, so the runner is not on the server's host, but network.mcp_forward is set; name the server's address as that runner reaches it in forwards.mcp";
        if (modelUrl is not null && modelUrl.StartsWith("http://", StringComparison.Ordinal) && forwards?.ModelUrl is null)
            yield return $"{label}: workspace is bundle, but network.model_url is an http address of the server's network; name the one that runner reaches in forwards.model_url";
        if (otlpUrl is not null && forwards?.OtlpUrl is null)
            yield return $"{label}: workspace is bundle, but network.otlp_url is set; name the collector as that runner reaches it in forwards.otlp_url";
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex RunnerName();

    [GeneratedRegex(@"^[^\s:]+:[0-9]{1,5}$")]
    private static partial Regex HostAndPort();

    [GeneratedRegex(@"^https?://[^\s?#@]+$")]
    private static partial Regex HttpUrl();

    [GeneratedRegex(@"^http://[^\s?#@]+$")]
    private static partial Regex PlainHttpUrl();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$")]
    private static partial Regex NetworkName();
}
