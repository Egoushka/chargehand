namespace Chargehand.Modules.Accounts.Api;

/// <summary>The profile's <c>public_free</c> block (ADR 0093): the free-model pool for runs on public repositories. The key is a gateway virtual key named by secret item, never written here; the
/// models are the pool preset's placeholders resolved by the profile's <c>models</c>.</summary>
/// <param name="ApiKeySecret">The secret item holding the gateway's <c>public-free</c> virtual key.</param>
/// <param name="BaseUrl">The gateway's Anthropic-compatible base URL.</param>
/// <param name="Preset">The preset a pool run uses.</param>
/// <param name="Replaces">The presets whose runs on a qualifying repository move to <paramref name="Preset"/>; none: nothing moves by itself, only a request naming the pool preset uses it.</param>
/// <param name="DenyPathSegments">A checkout with one of these as a directory name never qualifies (default <c>work</c>).</param>
/// <param name="IntakeModel">The model a pool run's intake and support check use, as <c>provider/model</c> of the gateway; none: the runtime's own default. The profile's <c>intake_model</c> is the owner's usual one and is never sent to the pool.</param>
/// <param name="GithubTokenSecret">The secret item holding a GitHub token for the visibility lookup; none: the lookup is unauthenticated, which only ever sees public repositories.</param>
public sealed record PublicFreeSettings(string? ApiKeySecret = null, string? BaseUrl = null, string Preset = "public-free", IReadOnlyList<string>? Replaces = null,
    IReadOnlyList<string>? DenyPathSegments = null, string? IntakeModel = null, string? GithubTokenSecret = null)
{
    public static readonly IReadOnlyList<string> DefaultDeniedSegments = ["work"];

    /// <summary>The header the gateway reads its virtual key from (read from its config, not tested live: ADR 0093).</summary>
    public const string KeyHeader = "x-bf-vk";

    /// <summary>The header line <c>x-bf-vk: &lt;key&gt;</c> for the CLI's custom-headers variable. Throws <see cref="InvalidOperationException"/> for a key that is not one token, whose message never holds the key.</summary>
    public static string KeyHeaderLine(string key) =>
        key.Length == 0 || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            ? throw new InvalidOperationException("the public_free key must be a single token without whitespace")
            : $"{KeyHeader}: {key}";

    public IReadOnlyList<string> Denied => DenyPathSegments is { Count: > 0 } ? DenyPathSegments : DefaultDeniedSegments;

    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(ApiKeySecret))
            yield return "api_key_secret is required: the secret item holding the gateway's public-free virtual key";
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            yield return "base_url must be an absolute http or https URL";
        else if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            yield return "base_url must be the gateway's base path only, with no credentials, query or fragment";
        if (string.IsNullOrWhiteSpace(Preset) || Preset.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            yield return "preset must be a preset name";
        if (Replaces?.Contains(Preset, StringComparer.Ordinal) == true)
            yield return "replaces must not list the pool preset itself";
    }
}
