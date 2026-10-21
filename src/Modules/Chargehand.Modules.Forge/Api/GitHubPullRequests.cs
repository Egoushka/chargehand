using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Forge.Api;

/// <param name="Owner">The owner on GitHub; on GitLab the namespace, which can be nested (<c>group/subgroup</c>).</param>
/// <param name="Forge">The code host of the remote; <see cref="Parse(string)"/> only reads GitHub's shape, <see cref="Parse(string, DrivenForge?)"/> decides.</param>
/// <param name="Host">The remote's host name; set by <see cref="Parse(string, DrivenForge?)"/> for a forge other than GitHub.</param>
public sealed partial record RemoteRepository(string Owner, string Name, ForgeKind Forge = ForgeKind.GitHub, string? Host = null)
{
    /// <summary>The owner and repository of an https or ssh remote URL (<c>https://host/o/r(.git)</c>, <c>git@host:o/r(.git)</c>, <c>ssh://git@host/o/r(.git)</c>);
    /// null for anything else, including a local path.</summary>
    public static RemoteRepository? Parse(string url)
    {
        var match = Https().Match(url);
        if (!match.Success)
            match = Scp().Match(url);
        return match.Success ? new RemoteRepository(match.Groups["o"].Value, match.Groups["r"].Value) : null;
    }

    [GeneratedRegex(@"^(https|ssh)://([^@/]+@)?[^/@:]+(:\d+)?/(?<o>[A-Za-z0-9_.-]+)/(?<r>[A-Za-z0-9_.-]+?)(\.git)?/?$")]
    private static partial Regex Https();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+@[^/@:]+:(?<o>[A-Za-z0-9_.-]+)/(?<r>[A-Za-z0-9_.-]+?)(\.git)?$")]
    private static partial Regex Scp();
}

/// <param name="Reused">The host already had an open request for the branch (a handover repeated, or a task continued on its branch), so none was opened.</param>
public sealed record PullRequestRef(string Url, int Number, bool Reused = false);

public sealed class PullRequestException(string message) : Exception(message);

/// <summary>Opens pull requests or merge requests, as a draft, edits the title and body of one it opened (ADR 0039), and finds an open one by its branch. It cannot merge, approve, close or change whether a request is a draft.</summary>
public interface IPullRequests
{
    /// <exception cref="PullRequestException">The host refused, or could not be reached.</exception>
    Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct);

    /// <exception cref="PullRequestException">The host refused, or could not be reached.</exception>
    Task EditAsync(RemoteRepository repo, int number, string title, string body, CancellationToken ct);

    /// <summary>The open request whose head is <paramref name="head"/>; null when there is none or the host did not answer.</summary>
    Task<PullRequestRef?> FindByHeadAsync(RemoteRepository repo, string head, CancellationToken ct);
}

/// <summary>What chargehand does on GitHub with the push credential: <c>POST /repos/{owner}/{repo}/pulls</c> with <c>draft: true</c>, and <c>PATCH</c> of the title and body of that pull request, and a <c>GET</c> of the open one for a branch
/// and of the base branch's rules. This class has no
/// method that merges, enables auto-merge, approves or closes; <c>GitHubPullRequestsTests</c> fails if one appears. The token is asked of <paramref name="tokenSource"/> at each call, so a GitHub App
/// installation token is minted fresh when the one before has lapsed.</summary>
public sealed class GitHubPullRequests(HttpClient http, Func<CancellationToken, Task<string>> tokenSource) : IPullRequests, IBranchRules
{
    public GitHubPullRequests(HttpClient http, string token)
        : this(http, _ => Task.FromResult(token))
    {
    }

    public async Task<ChecksGate> ChecksGateAsync(RemoteRepository repository, string branch, CancellationToken ct)
    {
        try
        {
            return await GitHubBranchRules.ReadAsync(http, await tokenSource(ct), repository, branch, ct);
        }
        catch (GitHubAppException)
        {
            return ChecksGate.Unknown;
        }
    }

    public async Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct)
    {
        var token = await TokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/pulls")
        {
            Content = new StringContent(JsonSerializer.Serialize(new DraftPullRequest(title, head, @base, body, Draft: true, MaintainerCanModify: false), ForgeJsonContext.Default.DraftPullRequest), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("chargehand", "1"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new PullRequestException(Redact($"the pull request could not be opened: {e.Message}", token));
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            // A handover repeated after a restart finds the pull request it opened before: return that one.
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity && text.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                && await FindByHeadAsync(repo, head, ct) is { } existing)
                return existing with { Reused = true };
            if (!response.IsSuccessStatusCode)
                throw new PullRequestException(Redact($"GitHub answered {(int)response.StatusCode}: {Message(text)}", token));
            try
            {
                using var doc = JsonDocument.Parse(text);
                return new PullRequestRef(doc.RootElement.GetProperty("html_url").GetString()!, doc.RootElement.GetProperty("number").GetInt32());
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new PullRequestException("GitHub answered with a body that is not a pull request");
            }
        }
    }

    public async Task EditAsync(RemoteRepository repo, int number, string title, string body, CancellationToken ct)
    {
        var token = await TokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/pulls/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new TitleAndBody(title, body), ForgeJsonContext.Default.TitleAndBody), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("chargehand", "1"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new PullRequestException(Redact($"GitHub answered {(int)response.StatusCode}: {Message(await response.Content.ReadAsStringAsync(ct))}", token));
        }
        catch (HttpRequestException e)
        {
            throw new PullRequestException(Redact($"the pull request could not be edited: {e.Message}", token));
        }
    }

    public async Task<PullRequestRef?> FindByHeadAsync(RemoteRepository repo, string head, CancellationToken ct)
    {
        string token;
        try
        {
            token = await tokenSource(ct);
        }
        catch (GitHubAppException)
        {
            return null;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/pulls?head={Uri.EscapeDataString($"{repo.Owner}:{head}")}&state=open");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("chargehand", "1"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var first = doc.RootElement.EnumerateArray().FirstOrDefault();
            return first.ValueKind == JsonValueKind.Object ? new PullRequestRef(first.GetProperty("html_url").GetString()!, first.GetProperty("number").GetInt32()) : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <exception cref="PullRequestException">The App's token could not be had; the message carries no token.</exception>
    private async Task<string> TokenAsync(CancellationToken ct)
    {
        try
        {
            return await tokenSource(ct);
        }
        catch (GitHubAppException e)
        {
            throw new PullRequestException($"no credential for the pull request: {e.Message}");
        }
    }

    private static string Redact(string text, string token) => token.Length >= 4 ? text.Replace(token, "[redacted]", StringComparison.Ordinal) : text;

    private static string Message(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            return (message ?? "no message").Length > 300 ? message![..300] : message ?? "no message";
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }
}
