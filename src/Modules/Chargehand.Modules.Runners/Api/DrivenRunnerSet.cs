using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Modules.Runners.Api;

/// <summary>The runners a profile's <c>driven</c> block names (ADR 0058), read as one set: <c>runner</c> or <c>runners</c>, the image each platform starts
/// (<c>image_variants</c>) and the toolchain environment (<c>environment</c>, ADR 0078), with the rules that refuse a block that cannot work.</summary>
public static partial class DrivenRunnerSet
{
    /// <summary>The name of the runner that <c>driven.runner</c> declares.</summary>
    public const string DefaultRunnerName = "default";

    /// <summary>ADR 0078: toolchains come from mise, installed per repository before each session, not from the session image.</summary>
    public const string MiseEnvironment = "mise";

    /// <summary>The environments a profile may name (<c>driven.environment</c>); <c>image</c> is the default.</summary>
    public static readonly IReadOnlyList<string> Environments = ["image", MiseEnvironment];

    /// <summary>The runners this server may use, in the order the profile lists them: <paramref name="runners"/>, else <paramref name="runner"/> as one named
    /// <see cref="DefaultRunnerName"/>, else none (the server calls the engine itself).</summary>
    public static IReadOnlyList<DrivenRunnerEntry> Resolve(DrivenRunner? runner, IReadOnlyList<DrivenRunnerEntry>? runners) =>
        runners is { Count: > 0 } named ? named : runner is { } one ? [new DrivenRunnerEntry(DefaultRunnerName, one.Url, one.ApiKeySecret)] : [];

    /// <summary>True when the profile uses <c>runners</c>: run events and the batch result then say which runner a session ran on.</summary>
    public static bool Names(IReadOnlyList<DrivenRunnerEntry>? runners) => runners is { Count: > 0 };

    /// <summary>The image to start on a runner of <paramref name="platform"/>: the reference as the profile writes it, or its variant for that platform.</summary>
    public static string ImageFor(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? variants, string reference, string platform) =>
        variants?.GetValueOrDefault(reference)?.GetValueOrDefault(platform) ?? reference;

    /// <summary>Every session image of <paramref name="images"/> (<c>driven.images</c>) as a runner of <paramref name="platform"/> is asked to start it: what its allowlist must carry.</summary>
    public static IReadOnlyList<string> SessionImagesFor(IReadOnlyList<string>? images, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? variants, string platform) =>
        [.. (images ?? []).Select(i => ImageFor(variants, i, platform)).Distinct(StringComparer.Ordinal)];

    public static bool UsesMise(string? environment) => environment == MiseEnvironment;

    /// <summary>Throws an invalid_request error naming the key when <paramref name="environment"/> is not one of <see cref="Environments"/>.</summary>
    public static void ValidateEnvironment(string? environment)
    {
        if (environment is { } named && !Environments.Contains(named, StringComparer.Ordinal))
            throw new ChargehandException(ErrorCode.InvalidRequest, $"driven.environment '{named}' must be one of {string.Join(", ", Environments)}",
                "Fix driven.environment in the profile, or leave it out for the session image's own toolchains.");
    }

    /// <summary>Throws an invalid_request error naming the key when <c>runner</c>, <c>runners</c> or <c>image_variants</c> cannot work. <paramref name="network"/> is
    /// <c>driven.network</c>, which a runner off the server's host has to replace; <paramref name="images"/> is <c>driven.images</c>, which every variant key must be in.</summary>
    public static void ValidateRunners(DrivenRunner? runner, IReadOnlyList<DrivenRunnerEntry>? runners, DrivenNetwork? network,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? imageVariants, IReadOnlyList<string>? images)
    {
        var problems = new List<string>();
        if (runner is not null && runners is not null)
            problems.Add("name the runner in either runner or runners, not both");
        if (runners is { Count: 0 })
            problems.Add("runners must list at least one runner (leave it out for none)");
        problems.AddRange(DrivenRunnerEntry.Problems(runners, network?.McpForward, network?.ModelUrl, network?.OtlpUrl));
        foreach (var (reference, variants) in imageVariants ?? new Dictionary<string, IReadOnlyDictionary<string, string>>())
            problems.AddRange(VariantProblems(reference, variants, images));
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"driven: {string.Join("; ", problems)}", "Fix driven.runners and driven.image_variants in the profile; docs/guide/driven.md lists the keys.");
    }

    private static IEnumerable<string> VariantProblems(string reference, IReadOnlyDictionary<string, string> variants, IReadOnlyList<string>? images)
    {
        var label = $"image_variants.{reference}";
        if (!ImageReference().IsMatch(reference))
            yield return $"{label}: the key must be pinned by digest (name@sha256:<64 hex>)";
        else if (images?.Contains(reference, StringComparer.Ordinal) != true)
            yield return $"{label}: the key must also be listed in driven.images, which is what the server starts";
        if (variants.Count == 0)
            yield return $"{label}: name at least one platform";
        foreach (var (platform, image) in variants)
        {
            if (!DrivenRunnerEntry.Platforms.Contains(platform))
                yield return $"{label}: platform '{platform}' must be one of {string.Join(", ", DrivenRunnerEntry.Platforms)}";
            if (!ImageReference().IsMatch(image))
                yield return $"{label}.{platform}: must be pinned by digest (name@sha256:<64 hex>)";
        }
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._/-]*(:[0-9]+)?(/[a-z0-9._-]+)*@sha256:[0-9a-f]{64}$")]
    private static partial Regex ImageReference();
}
