using System.Text.RegularExpressions;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>A caller of the server besides the owner (ADR 0050). It sees and cancels only its own runs. Every cap left out is no cap; a window is a UTC calendar day or month.</summary>
/// <param name="Name">Lowercase letters, digits and hyphens; <c>owner</c> is the implicit account of <c>api_key_secret</c> and cannot be declared.</param>
/// <param name="MaxUsdPerDay">Dollars the account's runs may cost in a UTC day, by the results' usage (priced runs only; a subscription run counts tokens).</param>
/// <param name="MaxConcurrentRuns">Runs of this account that are unfinished at once (queued or running).</param>
/// <param name="Driven">Whether the account may start a batch (a request with a <c>driven</c> block): a batch pushes branches and opens pull requests with the server's credential.</param>
/// <param name="Repositories">Directories the account's runs may name as <c>context.repository.path</c>, itself or below; null: any the server allows.</param>
/// <param name="Model">The account's own model credential (ADR 0053); its read-only runs and its batches use it instead of the server's.</param>
/// <param name="AllowServerCredential">An account with no <paramref name="Model"/> may spend the server's model credential. Default false: it is refused instead.</param>
public sealed record AccountSettings(string Name, decimal? MaxUsdPerDay = null, decimal? MaxUsdPerMonth = null, long? MaxTokensPerDay = null, long? MaxTokensPerMonth = null,
    int? MaxConcurrentRuns = null, bool Driven = false, IReadOnlyList<string>? Repositories = null, AccountModel? Model = null, bool AllowServerCredential = false)
{
    public const string Owner = "owner";

    public static bool ValidName(string name) => Regex.IsMatch(name, "^[a-z][a-z0-9-]{0,31}$");

    /// <summary>The problems of the profile's <c>http.accounts</c>: each account's own, named by <c>accounts.&lt;name&gt;</c>, and a name used twice. Empty when the list is valid.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<AccountSettings> accounts) =>
    [
        .. accounts.SelectMany(account => account.Problems().Select(p => $"accounts.{account.Name}: {p}")),
        .. accounts.GroupBy(a => a.Name).Where(g => g.Count() > 1).Select(g => $"accounts: name '{g.Key}' is used more than once"),
    ];

    public IEnumerable<string> Problems()
    {
        if (!ValidName(Name)) yield return "name must be lowercase letters, digits and hyphens, starting with a letter, at most 32 characters";
        if (Name == Owner) yield return "owner is the account of api_key_secret; pick another name";
        if (MaxUsdPerDay is <= 0) yield return "max_usd_per_day must be above 0";
        if (MaxUsdPerMonth is <= 0) yield return "max_usd_per_month must be above 0";
        if (MaxTokensPerDay is <= 0) yield return "max_tokens_per_day must be above 0";
        if (MaxTokensPerMonth is <= 0) yield return "max_tokens_per_month must be above 0";
        if (MaxConcurrentRuns is < 1) yield return "max_concurrent_runs must be at least 1";
        if (Repositories?.Any(r => !Path.IsPathRooted(r)) == true) yield return "repositories must be absolute paths";
        foreach (var problem in Model?.Problems() ?? [])
            yield return $"model: {problem}";
    }
}

/// <summary>An account's own model credential (ADR 0053), named by secret item: an Anthropic API key, or the key a gateway issued with that gateway's <paramref name="BaseUrl"/>.
/// The server resolves it when the account's first run needs it; no key is ever in the profile.</summary>
/// <param name="ApiKeySecret">The secret item holding the API key (or the gateway's key).</param>
/// <param name="BaseUrl">An Anthropic-compatible gateway's base URL; null: Anthropic's own API.</param>
/// <param name="OauthTokenSecret">Never allowed: a subscription token is the owner's plan, and the profile refuses it here with that reason instead of ignoring it.</param>
public sealed record AccountModel(string? ApiKeySecret = null, string? BaseUrl = null, string? OauthTokenSecret = null)
{
    public IEnumerable<string> Problems()
    {
        if (OauthTokenSecret is not null) yield return "oauth_token_secret is not allowed for an account: a subscription token is the owner's plan, cannot be priced or capped in dollars, and lending it out spends the owner's limits; use api_key_secret";
        if (string.IsNullOrWhiteSpace(ApiKeySecret)) yield return "api_key_secret is required";
        if (BaseUrl is null)
            yield break;
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            yield return "base_url must be an absolute http or https URL";
        else if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            yield return "base_url must be the gateway's base path only, with no credentials, query or fragment";
    }
}
