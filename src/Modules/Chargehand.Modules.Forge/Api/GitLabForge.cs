using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Forge.Api;

/// <summary>GitLab over REST API v4, with one token (<c>PRIVATE-TOKEN</c>) for everything (ADR 0046, ADR 0047). A pull request is a merge request: a draft is a title that starts with <c>Draft:</c>,
/// auto-merge is <c>merge_when_pipeline_succeeds</c> pinned to the head commit, and "the required checks" are the jobs of the head pipeline that are not allowed to fail, because GitLab's gate is
/// "pipelines must succeed" and it names no individual required check. Every call is bound to the project's path, URL-encoded; the token is redacted from every message.</summary>
public sealed partial class GitLabForge(HttpClient http, string token) : IForge
{
    private const int PageSize = 100;
    private const int CommentCount = 50;

    public string Name => "GitLab";

    /// <summary>The API root of an instance: <c>https://host/api/v4/</c>, from the remote's host or from <paramref name="baseUrl"/> (the instance root or the API root, as the profile gives it).</summary>
    public static Uri ApiRoot(string host, string? baseUrl)
    {
        var root = (baseUrl ?? $"https://{host}").TrimEnd('/');
        return new Uri(root.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase) ? $"{root}/" : $"{root}/api/v4/");
    }

    public async Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = Request(HttpMethod.Post, $"{ProjectPath(repo)}/merge_requests",
                new MergeRequestDraft(head, @base, DraftPrefix().IsMatch(title) ? title : $"Draft: {title}", body));
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new PullRequestException(Redact($"the merge request could not be opened: {e.Message}"));
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            // A handover repeated after a restart finds the merge request it opened before: return that one.
            if (response.StatusCode == HttpStatusCode.Conflict && text.Contains("already exists", StringComparison.OrdinalIgnoreCase) && await FindByHeadAsync(repo, head, ct) is { } existing)
                return existing with { Reused = true };
            if (!response.IsSuccessStatusCode)
                throw new PullRequestException(Redact($"GitLab answered {(int)response.StatusCode}: {Message(text)}"));
            try
            {
                using var doc = JsonDocument.Parse(text);
                return new PullRequestRef(doc.RootElement.GetProperty("web_url").GetString()!, doc.RootElement.GetProperty("iid").GetInt32());
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new PullRequestException("GitLab answered with a body that is not a merge request");
            }
        }
    }

    /// <summary>Sets the title and description; a request that is a draft stays one, because the title is what makes it so.</summary>
    public async Task EditAsync(RemoteRepository repo, int number, string title, string body, CancellationToken ct)
    {
        try
        {
            using var current = JsonDocument.Parse(await RestAsync(HttpMethod.Get, MergeRequestPath(repo, number), null, "the merge request's title", ct));
            var isDraft = current.RootElement.TryGetProperty("title", out var t) && DraftPrefix().IsMatch(t.GetString() ?? "");
            await RestAsync(HttpMethod.Put, MergeRequestPath(repo, number), new TitleAndDescription(isDraft && !DraftPrefix().IsMatch(title) ? $"Draft: {title}" : title, body), "the edit", ct);
        }
        catch (IntegrationException e)
        {
            throw new PullRequestException(e.Message);
        }
        catch (JsonException)
        {
            throw new PullRequestException("GitLab answered with a body that is not a merge request");
        }
    }

    public async Task<PrSnapshot> GetAsync(RemoteRepository repository, int number, CancellationToken ct)
    {
        using var mr = JsonDocument.Parse(await RestAsync(HttpMethod.Get, MergeRequestPath(repository, number), null, "the merge request's state", ct));
        try
        {
            var root = mr.RootElement;
            var state = root.GetProperty("state").GetString();
            var sha = root.GetProperty("sha").GetString() ?? "";
            var checks = new List<CheckStatus>();
            // A pipeline that belongs to an older head says nothing about this one; a fresh head has none until GitLab creates it.
            if (root.TryGetProperty("head_pipeline", out var pipeline) && pipeline.ValueKind == JsonValueKind.Object && pipeline.GetProperty("sha").GetString() == sha)
                checks.AddRange(await PipelineChecksAsync(repository, pipeline.GetProperty("id").GetInt64(), pipeline.GetProperty("status").GetString(), ct));
            using var notes = JsonDocument.Parse(await RestAsync(HttpMethod.Get,
                $"{MergeRequestPath(repository, number)}/notes?order_by=created_at&sort=desc&per_page={CommentCount.ToString(CultureInfo.InvariantCulture)}", null, "the merge request's comments", ct));
            var autoMerge = (root.TryGetProperty("merge_when_pipeline_succeeds", out var mwps) && mwps.ValueKind == JsonValueKind.True)
                || (root.TryGetProperty("auto_merge_enabled", out var am) && am.ValueKind == JsonValueKind.True);
            return new PrSnapshot($"{repository.Owner}/{repository.Name}!{number.ToString(CultureInfo.InvariantCulture)}", root.GetProperty("iid").GetInt32(), sha, root.GetProperty("target_branch").GetString()!,
                root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True, state == "merged", state == "closed",
                MergeStatus(root.TryGetProperty("detailed_merge_status", out var detailed) ? detailed.GetString() : null), autoMerge, checks,
                [.. notes.RootElement.EnumerateArray().Reverse().Select(c => c.GetProperty("body").GetString() ?? "")]);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or JsonException or FormatException)
        {
            throw new IntegrationException("GitLab answered with a body that is not a merge request's state");
        }
    }

    public async Task<int> BehindByAsync(RemoteRepository repository, string baseBranch, string branch, CancellationToken ct)
    {
        // The commits of the base that the branch does not have.
        var text = await RestAsync(HttpMethod.Get, $"{ProjectPath(repository)}/repository/compare?from={Uri.EscapeDataString(branch)}&to={Uri.EscapeDataString(baseBranch)}", null, "the comparison with the base branch", ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("commits", out var commits) ? commits.GetArrayLength() : 0;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new IntegrationException("GitLab answered with a body that is not a comparison");
        }
    }

    /// <summary>GitLab rebases the branch onto its target; it takes no expected head, so the head is checked first. A rebase rewrites the branch's commits.</summary>
    public async Task UpdateBranchAsync(RemoteRepository repository, int number, string expectedHeadSha, CancellationToken ct)
    {
        using var mr = JsonDocument.Parse(await RestAsync(HttpMethod.Get, MergeRequestPath(repository, number), null, "the merge request's head", ct));
        if (mr.RootElement.TryGetProperty("sha", out var sha) && sha.GetString() != expectedHeadSha)
            throw new IntegrationException("the merge request's head moved, so the branch was not rebased");
        await RestAsync(HttpMethod.Put, $"{MergeRequestPath(repository, number)}/rebase", new NoFields(), "the rebase", ct);
    }

    /// <summary>The handle names the project and the number; <paramref name="repository"/> is not needed here (one token serves every project).</summary>
    public async Task MarkReadyAsync(RemoteRepository repository, string nodeId, CancellationToken ct)
    {
        var (project, number) = Handle(nodeId);
        using var mr = JsonDocument.Parse(await RestAsync(HttpMethod.Get, MergeRequestPath(project, number), null, "the merge request's title", ct));
        var title = mr.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        if (DraftPrefix().IsMatch(title))
            await RestAsync(HttpMethod.Put, MergeRequestPath(project, number), new TitleOnly(DraftPrefix().Replace(title, "")), "marking the merge request ready", ct);
    }

    public async Task EnableAutoMergeAsync(RemoteRepository repository, string nodeId, string headSha, CancellationToken ct)
    {
        var (project, number) = Handle(nodeId);
        await RestAsync(HttpMethod.Put, $"{MergeRequestPath(project, number)}/merge", new AutoMergeRequest(MergeWhenPipelineSucceeds: true, headSha, Squash: true), "enabling auto-merge", ct);
    }

    public async Task MergeNowAsync(RemoteRepository repository, int number, string headSha, CancellationToken ct) =>
        await RestAsync(HttpMethod.Put, $"{MergeRequestPath(repository, number)}/merge", new GitLabMerge(headSha, Squash: true), "the merge", ct);

    public async Task CommentAsync(RemoteRepository repository, int number, string body, CancellationToken ct) =>
        await RestAsync(HttpMethod.Post, $"{MergeRequestPath(repository, number)}/notes", new CommentBody(body), "the comment", ct);

    public async Task<string> JobLogTailAsync(RemoteRepository repository, long jobId, int maxChars, CancellationToken ct)
    {
        var text = await RestAsync(HttpMethod.Get, $"{ProjectPath(repository)}/jobs/{jobId.ToString(CultureInfo.InvariantCulture)}/trace", null, "the job log", ct);
        return GitHubIntegrationHost.TailOf(Section().Replace(text, ""), maxChars);
    }

    /// <summary>The jobs of the pipeline, each required unless GitLab lets it fail. A pipeline with no job (a configuration error ends it so) stands for itself.</summary>
    private async Task<IEnumerable<CheckStatus>> PipelineChecksAsync(RemoteRepository repository, long pipelineId, string? pipelineStatus, CancellationToken ct)
    {
        using var jobs = JsonDocument.Parse(await RestAsync(HttpMethod.Get,
            $"{ProjectPath(repository)}/pipelines/{pipelineId.ToString(CultureInfo.InvariantCulture)}/jobs?per_page={PageSize.ToString(CultureInfo.InvariantCulture)}", null, "the pipeline's jobs", ct));
        var list = jobs.RootElement.EnumerateArray().Select(j => new CheckStatus(j.GetProperty("name").GetString() ?? "",
            !(j.TryGetProperty("allow_failure", out var allowed) && allowed.ValueKind == JsonValueKind.True), Classify(j.GetProperty("status").GetString()), j.GetProperty("id").GetInt64())).ToList();
        return list.Count > 0 ? list : [new CheckStatus("pipeline", true, Classify(pipelineStatus), null)];
    }

    private static CheckOutcome Classify(string? status) =>
        status switch
        {
            "success" or "skipped" => CheckOutcome.Passed,
            "failed" => CheckOutcome.Failed,
            "created" or "pending" or "running" or "preparing" or "waiting_for_resource" or "scheduled" => CheckOutcome.Pending,
            _ => CheckOutcome.Other,
        };

    /// <summary>GitLab's <c>detailed_merge_status</c> in the words the integrator reads (GitHub's <c>mergeStateStatus</c>): what is waiting on a pipeline, an approval or a discussion is BLOCKED, and the
    /// integrator then asks for auto-merge.</summary>
    private static string MergeStatus(string? detailed) =>
        detailed switch
        {
            "mergeable" => "CLEAN",
            "conflict" => "DIRTY",
            "need_rebase" => "BEHIND",
            "draft_status" => "DRAFT",
            null or "checking" or "unchecked" or "preparing" or "approvals_syncing" => "UNKNOWN",
            _ => "BLOCKED",
        };

    public async Task<PullRequestRef?> FindByHeadAsync(RemoteRepository repo, string head, CancellationToken ct)
    {
        try
        {
            using var request = Request(HttpMethod.Get, $"{ProjectPath(repo)}/merge_requests?source_branch={Uri.EscapeDataString(head)}&state=opened", null);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var first = doc.RootElement.EnumerateArray().FirstOrDefault();
            return first.ValueKind == JsonValueKind.Object ? new PullRequestRef(first.GetProperty("web_url").GetString()!, first.GetProperty("iid").GetInt32()) : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ProjectPath(RemoteRepository repository) => $"projects/{Uri.EscapeDataString($"{repository.Owner}/{repository.Name}")}";

    private static string MergeRequestPath(RemoteRepository repository, int number) => $"{ProjectPath(repository)}/merge_requests/{number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The project and iid of a handle <see cref="GetAsync"/> made (<c>group/sub/project!7</c>).</summary>
    private static (RemoteRepository Repository, int Number) Handle(string nodeId)
    {
        var bang = nodeId.LastIndexOf('!');
        var slash = nodeId.LastIndexOf('/', Math.Max(bang, 0));
        if (bang < 0 || slash < 0 || !int.TryParse(nodeId[(bang + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new IntegrationException("the merge request's handle is not a GitLab project path and number");
        return (new RemoteRepository(nodeId[..slash], nodeId[(slash + 1)..bang], ForgeKind.GitLab), number);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), ForgeJsonContext.Default), Encoding.UTF8, "application/json");
        request.Headers.Add("PRIVATE-TOKEN", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("chargehand", "1"));
        return request;
    }

    private async Task<string> RestAsync(HttpMethod method, string path, object? body, string what, CancellationToken ct)
    {
        try
        {
            using var request = Request(method, path, body);
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new IntegrationException(Redact($"GitLab answered {(int)response.StatusCode} to {what}: {Message(text)}"));
            return text;
        }
        catch (HttpRequestException e)
        {
            throw new IntegrationException(Redact($"{what} could not be reached: {e.Message}"));
        }
    }

    private string Redact(string text) => token.Length >= 4 ? text.Replace(token, "[redacted]", StringComparison.Ordinal) : text;

    /// <summary>GitLab's error text: <c>message</c> as a string, a list or an object, or <c>error</c>.</summary>
    private static string Message(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var found = doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.TryGetProperty("message", out var m) ? m : doc.RootElement.TryGetProperty("error", out var e) ? e : default
                : default;
            var message = found.ValueKind switch { JsonValueKind.String => found.GetString(), JsonValueKind.Undefined => null, _ => found.GetRawText() };
            return (message ?? "no message").Length > 300 ? message![..300] : message ?? "no message";
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }

    /// <summary>The title prefixes GitLab reads as a draft: <c>Draft:</c>, <c>[Draft]</c> and <c>(Draft)</c>, any case, with the space after.</summary>
    [GeneratedRegex(@"^(\s*(\[draft\]|\(draft\)|draft:))+\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DraftPrefix();

    /// <summary>The collapsible-section markers GitLab's runner puts in a job log.</summary>
    [GeneratedRegex(@"section_(start|end):\d+:\S*\r?")]
    private static partial Regex Section();
}
