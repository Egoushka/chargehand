using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Forge.Api;

/// <summary>Which code host a remote is on, when its host name does not say (ADR 0047's forge point). Unset, a remote on <c>gitlab.com</c> or a host whose first label is <c>gitlab</c> is GitLab and any other
/// host is GitHub, as before.</summary>
/// <param name="Kind"><c>github</c> or <c>gitlab</c>, for every remote of the profile.</param>
/// <param name="BaseUrl">The API root: <c>https://host/api/v4</c> for GitLab (the instance root <c>https://host</c> is accepted), <c>https://host/api/v3</c> for GitHub Enterprise. Unset: derived from the remote's host (GitLab) or api.github.com.</param>
/// <param name="TokenSecret">GitLab only: the secret item for the API token (<c>PRIVATE-TOKEN</c>, scope <c>api</c>). Unset: <c>driven.push_secret</c>, which then needs <c>api</c> and <c>write_repository</c>.</param>
public sealed record DrivenForge(string Kind, string? BaseUrl = null, string? TokenSecret = null)
{
    /// <summary>Throws an invalid_request error naming the key when a value cannot work.</summary>
    public void Validate()
    {
        var problems = new List<string>();
        if (Kind is not ("github" or "gitlab")) problems.Add("kind must be github or gitlab");
        if (BaseUrl is not null && !(Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0))
            problems.Add("base_url must be an https URL without credentials, query or fragment");
        if (TokenSecret is { } secret && string.IsNullOrWhiteSpace(secret)) problems.Add("token_secret must name a secret item");
        if (TokenSecret is not null && Kind == "github") problems.Add("token_secret is for gitlab; GitHub uses driven.push_secret and the App");
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"driven.forge: {string.Join("; ", problems)}", "Fix driven.forge in the profile; docs/guide/driven.md lists the keys.");
    }
}
