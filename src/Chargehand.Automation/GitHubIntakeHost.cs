using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Chargehand.Driven;
using Chargehand.Modules.Forge.Api;

namespace Chargehand.Automation;

/// <summary>The GitHub implementation of <see cref="IIntakeHost"/>: REST only. The token comes from <see cref="IInstallationTokens"/> and is redacted from every message. A list is read up to
/// <see cref="MaxPages"/> pages of 100.</summary>
public sealed class GitHubIntakeHost(HttpClient http, IInstallationTokens tokens) : IIntakeHost
{
    public const int MaxPages = 10;

    public async Task<IReadOnlyList<IntakeIssue>> LabeledAsync(string repository, string label, CancellationToken ct) =>
        [.. (await PagesAsync(repository, $"{RepoPath(repository)}/issues?state=open&labels={Uri.EscapeDataString(label)}", "the labelled issues", ct)).Select(Issue)];

    public async Task<LabelEvent?> LastLabelEventAsync(string repository, int number, string label, CancellationToken ct)
    {
        LabelEvent? last = null;
        foreach (var e in await PagesAsync(repository, $"{RepoPath(repository)}/issues/{number.ToString(CultureInfo.InvariantCulture)}/events", "the issue's events", ct))
            if (Text(e, "event") == "labeled" && e.TryGetProperty("label", out var l) && string.Equals(Text(l, "name"), label, StringComparison.OrdinalIgnoreCase))
                last = new LabelEvent(Long(e, "id"), Login(e, "actor"));
        return last;
    }

    public async Task<IntakeIssue> GetAsync(string repository, int number, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/issues/{number.ToString(CultureInfo.InvariantCulture)}", null, "the issue", ct));
        return Issue(doc.RootElement);
    }

    public async Task<IReadOnlyList<IntakeComment>> CommentsSinceAsync(string repository, DateTimeOffset since, CancellationToken ct) =>
        [.. (await PagesAsync(repository, $"{RepoPath(repository)}/issues/comments?sort=created&direction=asc&since={Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}",
            "the comments", ct)).Select(c => Parse(() => new IntakeComment(Long(c, "id"), IssueNumber(c), Login(c, "user"), Text(c, "body") ?? "", Time(c, "updated_at"))))];

    public async Task<IntakePull> GetPullAsync(string repository, int number, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}", null, "the pull request", ct));
        return Parse(() => new IntakePull(number, doc.RootElement.GetProperty("head").GetProperty("ref").GetString() ?? "", FullName(doc.RootElement.GetProperty("head")),
            FullName(doc.RootElement.GetProperty("base")) ?? repository, Text(doc.RootElement, "state") == "open"));
    }

    public async Task<IReadOnlyList<ReviewComment>> ReviewCommentsSinceAsync(string repository, DateTimeOffset since, CancellationToken ct) =>
        [.. (await PagesAsync(repository, $"{RepoPath(repository)}/pulls/comments?sort=created&direction=asc&since={Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}",
            "the review comments", ct)).Select(c => Parse(() => new ReviewComment(Long(c, "id"), IssueNumber(c, "pull_request_url"), Login(c, "user"), Text(c, "body") ?? "", Text(c, "path") ?? "",
            OptionalInt(c, "line") ?? OptionalInt(c, "original_line"), c.TryGetProperty("in_reply_to_id", out var reply) && reply.ValueKind == JsonValueKind.Number, Time(c, "updated_at"))))];

    public async Task<IReadOnlyList<string>> CommentBodiesAsync(string repository, int number, CancellationToken ct) =>
        [.. (await PagesAsync(repository, $"{RepoPath(repository)}/issues/{number.ToString(CultureInfo.InvariantCulture)}/comments", "the conversation", ct)).Select(c => Text(c, "body") ?? "")];

    public async Task<IReadOnlyList<ReviewablePull>> OpenPullsAsync(string repository, CancellationToken ct) =>
        [.. (await PagesAsync(repository, $"{RepoPath(repository)}/pulls?state=open", "the open pull requests", ct)).Select(p => Parse(() => new ReviewablePull(p.GetProperty("number").GetInt32(), Text(p, "title") ?? "",
            Text(p, "body") ?? "", Login(p, "user"), Text(p.GetProperty("head"), "sha") ?? "", p.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)))];

    public Task<string> PullDiffAsync(string repository, int number, CancellationToken ct) =>
        SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}", null, "the pull request's diff", DiffMedia, ct);

    public async Task<long> CommentAsync(string repository, int number, string body, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await SendAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/issues/{number.ToString(CultureInfo.InvariantCulture)}/comments", new { body }, "the comment", ct));
        return Parse(() => Long(doc.RootElement, "id"));
    }

    private async Task<IReadOnlyList<JsonElement>> PagesAsync(string repository, string path, string what, CancellationToken ct)
    {
        List<JsonElement> all = [];
        for (var page = 1; page <= MaxPages; page++)
        {
            using var doc = JsonDocument.Parse(await SendAsync(repository, HttpMethod.Get, $"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}", null, what, ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new IntakeException($"GitHub answered {what} with a body that is not a list");
            var items = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            all.AddRange(items);
            if (items.Count < 100)
                break;
        }
        return all;
    }

    private static IntakeIssue Issue(JsonElement e) => Parse(() => new IntakeIssue(e.GetProperty("number").GetInt32(), Text(e, "title") ?? "", Text(e, "body") ?? "", Login(e, "user"),
        e.TryGetProperty("pull_request", out var pr) && pr.ValueKind == JsonValueKind.Object, [.. e.GetProperty("labels").EnumerateArray().Select(l => Text(l, "name") ?? "")], Time(e, "updated_at")));

    private static string? FullName(JsonElement side) => side.TryGetProperty("repo", out var repo) && repo.ValueKind == JsonValueKind.Object ? Text(repo, "full_name") : null;

    private static int? OptionalInt(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    /// <summary>The number at the end of a comment's <c>issue_url</c> (<c>.../issues/12</c>) or <c>pull_request_url</c> (<c>.../pulls/12</c>).</summary>
    private static int IssueNumber(JsonElement comment, string member = "issue_url")
    {
        var url = Text(comment, member) ?? "";
        return int.TryParse(url[(url.LastIndexOf('/') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new IntakeException("GitHub answered a comment with no issue");
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name) => e.GetProperty(name).GetInt64();

    private static DateTimeOffset Time(JsonElement e, string name) => e.GetProperty(name).GetDateTimeOffset();

    /// <summary>A deleted account has a null user; its login is empty and so never allowed.</summary>
    private static string Login(JsonElement e, string name) => e.TryGetProperty(name, out var u) && u.ValueKind == JsonValueKind.Object ? Text(u, "login") ?? "" : "";

    private static T Parse<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or JsonException)
        {
            throw new IntakeException("GitHub answered with a body that is not what intake reads");
        }
    }

    private static (string Owner, string Name) Split(string repository)
    {
        var slash = repository.IndexOf('/', StringComparison.Ordinal);
        return (repository[..slash], repository[(slash + 1)..]);
    }

    private static string RepoPath(string repository)
    {
        var (owner, name) = Split(repository);
        return $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
    }

    private const string JsonMedia = "application/vnd.github+json";
    private const string DiffMedia = "application/vnd.github.diff";

    private Task<string> SendAsync(string repository, HttpMethod method, string path, object? body, string what, CancellationToken ct) =>
        SendAsync(repository, method, path, body, what, JsonMedia, ct);

    private async Task<string> SendAsync(string repository, HttpMethod method, string path, object? body, string what, string accept, CancellationToken ct)
    {
        string token;
        try
        {
            var (owner, name) = Split(repository);
            token = await tokens.GetAsync(owner, name, ct);
        }
        catch (GitHubAppException e)
        {
            throw new IntakeException(e.Message);
        }
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("chargehand", "1"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new IntakeException(Redact($"GitHub answered {(int)response.StatusCode} to {what}: {Message(text)}", token), (int)response.StatusCode);
            return text;
        }
        catch (HttpRequestException e)
        {
            throw new IntakeException(Redact($"{what} could not be reached: {e.Message}", token));
        }
    }

    private static string Message(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var message = Text(doc.RootElement, "message") ?? "no message";
            return message.Length > 300 ? message[..300] : message;
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }

    private static string Redact(string text, string token) => token.Length >= 4 ? text.Replace(token, "[redacted]", StringComparison.Ordinal) : text;
}
