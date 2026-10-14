using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Config;

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
        problems.AddRange(DrivenRunnerEntry.Problems(Runners, Network?.McpForward, Network?.ModelUrl, Network?.OtlpUrl));
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

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._/-]*(:[0-9]+)?(/[a-z0-9._-]+)*@sha256:[0-9a-f]{64}$")]
    private static partial Regex ImageReference();
}
