using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chargehand.Driven;

public sealed partial record RemoteRepository(string Owner, string Name)
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

public sealed record PullRequestRef(string Url, int Number);

public sealed class PullRequestException(string message) : Exception(message);

/// <summary>Opens pull requests; it can open a draft and nothing else (ADR 0039).</summary>
public interface IPullRequests
{
    /// <exception cref="PullRequestException">The host refused, or could not be reached.</exception>
    Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct);
}

/// <summary>The one thing chargehand does on GitHub: <c>POST /repos/{owner}/{repo}/pulls</c> with <c>draft: true</c>. This class has no method that merges,
/// enables auto-merge, edits, approves or closes; <c>GitHubPullRequestsTests</c> fails if one appears.</summary>
public sealed class GitHubPullRequests(HttpClient http, string token) : IPullRequests
{
    public async Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/pulls")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { title, head, @base, body, draft = true, maintainer_can_modify = false }), Encoding.UTF8, "application/json"),
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
            throw new PullRequestException(Redact($"the pull request could not be opened: {e.Message}"));
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new PullRequestException(Redact($"GitHub answered {(int)response.StatusCode}: {Message(text)}"));
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

    private string Redact(string text) => token.Length >= 4 ? text.Replace(token, "[redacted]", StringComparison.Ordinal) : text;

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
