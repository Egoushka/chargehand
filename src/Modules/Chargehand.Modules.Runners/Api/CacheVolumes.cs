using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Runners.Api;

/// <summary>The package cache volumes of ADR 0048: one named volume per ecosystem and repository, mounted read-write into that repository's sessions and verifier at
/// <c>/cache/&lt;ecosystem&gt;</c>, with the environment variable that points the package manager at it. The volume name, the mount and the variable are fixed here, so a caller
/// cannot name a path or a volume of another repository's.</summary>
public static partial class CacheVolumes
{
    public const string Prefix = "chargehand-cache-";

    public const string MountRoot = "/cache";

    /// <summary>The cache each ecosystem has, in the profile's names (<c>driven.services.caches</c>), with the variable and value that point its package manager at the mount.</summary>
    public static readonly IReadOnlyDictionary<string, (string Variable, string Value)> Ecosystems = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["nuget"] = ("NUGET_PACKAGES", $"{MountRoot}/nuget"),
        ["npm"] = ("npm_config_cache", $"{MountRoot}/npm"),
        ["pip"] = ("PIP_CACHE_DIR", $"{MountRoot}/pip"),
        ["gradle"] = ("GRADLE_USER_HOME", $"{MountRoot}/gradle"),
        ["maven"] = ("MAVEN_OPTS", $"-Dmaven.repo.local={MountRoot}/maven"),
    };

    /// <summary>The volume of <paramref name="ecosystem"/> for the repository <paramref name="repository"/> names (<c>owner/repo</c> or a remote URL; case does not matter). Twelve hex
    /// characters of its hash keep the name plain and the repository's name out of the engine's listings.</summary>
    public static string Name(string ecosystem, string repository)
    {
        if (!Ecosystems.ContainsKey(ecosystem))
            throw new ArgumentException($"no cache for '{ecosystem}'", nameof(ecosystem));
        return $"{Prefix}{ecosystem}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repository.Trim().ToLowerInvariant())))[..12].ToLowerInvariant()}";
    }

    /// <summary>The ecosystem of a cache volume name, or null when the name is not one.</summary>
    public static string? EcosystemOf(string volume) => VolumeName().Match(volume) is { Success: true } m ? m.Groups["e"].Value : null;

    /// <summary>The environment a session or verifier needs for the caches in <paramref name="volumes"/>.</summary>
    public static IReadOnlyDictionary<string, string> Environment(IEnumerable<string> volumes) =>
        volumes.Select(EcosystemOf).OfType<string>().Distinct(StringComparer.Ordinal).ToDictionary(e => Ecosystems[e].Variable, e => Ecosystems[e].Value, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="name"/> is a variable of a cache and <paramref name="value"/> is the only value it may have.</summary>
    internal static bool IsCacheVariable(string name, out string value)
    {
        foreach (var (_, (variable, fixedValue)) in Ecosystems)
            if (variable == name)
            {
                value = fixedValue;
                return true;
            }
        value = "";
        return false;
    }

    [GeneratedRegex(@"^chargehand-cache-(?<e>nuget|npm|pip|gradle|maven)-[0-9a-f]{12}$")]
    private static partial Regex VolumeName();
}
