using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Kernel;

namespace Chargehand.Modules.Forge.Api;

public sealed class IntegrationException(string message) : Exception(message);

public enum CheckOutcome { Pending, Passed, Failed, Other }

/// <param name="Required">Whether the repository's rules require this check for this pull request (GitHub's <c>isRequired</c>).</param>
/// <param name="JobId">The Actions job id, which is the check run's id; null for a commit status.</param>
public sealed record CheckStatus(string Name, bool Required, CheckOutcome Outcome, long? JobId);

/// <param name="NodeId">The host's own handle for the pull request, which the ready and auto-merge calls take: GraphQL's id on GitHub, the project path and iid (<c>group/project!7</c>) on GitLab.</param>
/// <param name="MergeStatus">GitHub's <c>mergeStateStatus</c>: CLEAN, BEHIND, BLOCKED, DIRTY, DRAFT, HAS_HOOKS, UNSTABLE or UNKNOWN. A forge that names its states otherwise maps them onto these.</param>
/// <param name="Comments">The bodies of the latest comments, for the markers chargehand leaves.</param>
public sealed record PrSnapshot(string NodeId, int Number, string HeadSha, string BaseBranch, bool IsDraft, bool Merged, bool Closed, string MergeStatus, bool AutoMerge,
    IReadOnlyList<CheckStatus> Checks, IReadOnlyList<string> Comments);

/// <summary>What the integrator does on a forge (ADR 0046), each call as the GitHub App (on GitLab, with the forge token). Every merge is pinned to a head commit and goes through the repository's rules:
/// there is no call here that bypasses one. It is a different interface from <see cref="IPullRequests"/>, which still opens drafts and nothing else; <see cref="IForge"/> joins them.</summary>
public interface IIntegrationHost : IBranchRules, ICiHistory
{
    /// <summary>The forge's name in the words chargehand writes about it.</summary>
    string Name => "GitHub";

    /// <summary>A forge that cannot read its branch rules says so, and the integrator then asks instead of merging.</summary>
    Task<ChecksGate> IBranchRules.ChecksGateAsync(RemoteRepository repository, string branch, CancellationToken ct) => Task.FromResult(ChecksGate.Unknown);

    Task<IReadOnlyList<CiJob>> ICiHistory.RecentJobsAsync(RemoteRepository repository, DateTimeOffset since, CancellationToken ct) =>
        throw new IntegrationException($"{Name} cannot read CI history from here");

    Task<PrSnapshot> GetAsync(RemoteRepository repository, int number, CancellationToken ct);

    /// <summary>How many commits of <paramref name="baseBranch"/> the branch lacks.</summary>
    Task<int> BehindByAsync(RemoteRepository repository, string baseBranch, string branch, CancellationToken ct);

    /// <summary>The checks on a branch's own head, by name (CHARGEHAND-669): whether a required check failing on a pull request also fails on its base, so the pull request did not cause it.
    /// <see cref="CheckStatus.Required"/> is meaningless here (a branch, not a pull request, has no required checks of its own) and is always true. A forge that cannot read this throws.</summary>
    Task<IReadOnlyList<CheckStatus>> BaseChecksAsync(RemoteRepository repository, string branch, CancellationToken ct) =>
        throw new IntegrationException($"{Name} cannot read a branch's checks from here");

    Task UpdateBranchAsync(RemoteRepository repository, int number, string expectedHeadSha, CancellationToken ct);

    /// <param name="repository">The pull request's repository: the App is installed per owner, so the call needs to know whose installation to act as.</param>
    Task MarkReadyAsync(RemoteRepository repository, string nodeId, CancellationToken ct);

    /// <summary>Auto-merge with squash: GitHub merges once the required checks and reviews are met, for this head commit.</summary>
    Task EnableAutoMergeAsync(RemoteRepository repository, string nodeId, string headSha, CancellationToken ct);

    /// <summary>A squash merge now, for a pull request GitHub already reports mergeable; refused if the head moved.</summary>
    Task MergeNowAsync(RemoteRepository repository, int number, string headSha, CancellationToken ct);

    Task CommentAsync(RemoteRepository repository, int number, string body, CancellationToken ct);

    /// <summary>The end of a CI job's log, at most <paramref name="maxChars"/> characters, ANSI codes and timestamps removed.</summary>
    Task<string> JobLogTailAsync(RemoteRepository repository, long jobId, int maxChars, CancellationToken ct);

    /// <summary>Runs the failed jobs again (CHARGEHAND-214): the workflow runs that the given jobs belong to, failed jobs only. A forge without this says so.</summary>
    /// <returns>How many workflow runs were re-run.</returns>
    Task<int> RerunFailedJobsAsync(RemoteRepository repository, IReadOnlyList<long> jobIds, CancellationToken ct) =>
        throw new IntegrationException($"{Name} cannot re-run checks from here");
}

/// <summary>The GitHub implementation: REST for the branch update, merge, comment and logs, GraphQL for what REST cannot do (ready for review, auto-merge) and for which checks are required.
/// The token comes from <see cref="IInstallationTokens"/> and is redacted from every message.</summary>
public sealed partial class GitHubIntegrationHost(HttpClient http, IInstallationTokens tokens) : IIntegrationHost
{
    private const string StateQuery = """
        query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){
          id number state isDraft merged headRefOid baseRefName mergeStateStatus autoMergeRequest{enabledAt}
          comments(last:50){nodes{body}}
          commits(last:1){nodes{commit{statusCheckRollup{contexts(first:100){nodes{
            __typename
            ... on CheckRun{name status conclusion databaseId isRequired(pullRequestNumber:$number)}
            ... on StatusContext{context state isRequired(pullRequestNumber:$number)}
          }}}}}}
        }}}
        """;

    public async Task<PrSnapshot> GetAsync(RemoteRepository repository, int number, CancellationToken ct)
    {
        using var data = await GraphQlAsync(repository, "the pull request's state", StateQuery, new PullRequestVariables(repository.Owner, repository.Name, number), ct);
        try
        {
            var pr = data.RootElement.GetProperty("repository").GetProperty("pullRequest");
            var state = pr.GetProperty("state").GetString();
            var checks = new List<CheckStatus>();
            foreach (var commit in pr.GetProperty("commits").GetProperty("nodes").EnumerateArray())
            {
                if (commit.GetProperty("commit").GetProperty("statusCheckRollup") is not { ValueKind: JsonValueKind.Object } rollup)
                    continue;
                foreach (var node in rollup.GetProperty("contexts").GetProperty("nodes").EnumerateArray())
                    checks.Add(node.GetProperty("__typename").GetString() == "CheckRun"
                        ? new CheckStatus(node.GetProperty("name").GetString() ?? "", node.GetProperty("isRequired").GetBoolean(),
                            Classify(node.GetProperty("status").GetString(), node.GetProperty("conclusion").GetString()), node.GetProperty("databaseId").GetInt64())
                        : new CheckStatus(node.GetProperty("context").GetString() ?? "", node.GetProperty("isRequired").GetBoolean(), ClassifyStatus(node.GetProperty("state").GetString()), null));
            }
            return new PrSnapshot(pr.GetProperty("id").GetString()!, pr.GetProperty("number").GetInt32(), pr.GetProperty("headRefOid").GetString()!, pr.GetProperty("baseRefName").GetString()!,
                pr.GetProperty("isDraft").GetBoolean(), pr.GetProperty("merged").GetBoolean(), state == "CLOSED", pr.GetProperty("mergeStateStatus").GetString() ?? "UNKNOWN",
                pr.GetProperty("autoMergeRequest").ValueKind == JsonValueKind.Object, checks,
                [.. pr.GetProperty("comments").GetProperty("nodes").EnumerateArray().Select(c => c.GetProperty("body").GetString() ?? "")]);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or JsonException or FormatException)
        {
            throw new IntegrationException("GitHub answered with a body that is not a pull request's state");
        }
    }

    public async Task<ChecksGate> ChecksGateAsync(RemoteRepository repository, string branch, CancellationToken ct)
    {
        try
        {
            return await GitHubBranchRules.ReadAsync(http, await TokenAsync(repository, ct), repository, branch, ct);
        }
        catch (IntegrationException)
        {
            return ChecksGate.Unknown;
        }
    }

    public Task<IReadOnlyList<CiJob>> RecentJobsAsync(RemoteRepository repository, DateTimeOffset since, CancellationToken ct) =>
        GitHubCiHistory.ReadAsync(path => RestAsync(repository, HttpMethod.Get, path, null, "the CI history (the App needs the Actions permission to read)", ct), RepoPath(repository), since, ct);

    public async Task<int> BehindByAsync(RemoteRepository repository, string baseBranch, string branch, CancellationToken ct)
    {
        var path = $"{RepoPath(repository)}/compare/{Uri.EscapeDataString(baseBranch)}...{Uri.EscapeDataString(branch)}";
        var text = await RestAsync(repository, HttpMethod.Get, path, null, "the comparison with the base branch", ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("behind_by", out var behind) ? behind.GetInt32() : 0;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new IntegrationException("GitHub answered with a body that is not a comparison");
        }
    }

    private const string RefChecksQuery = """
        query($owner:String!,$name:String!,$qualified:String!){repository(owner:$owner,name:$name){ref(qualifiedName:$qualified){target{... on Commit{statusCheckRollup{contexts(first:100){nodes{
          __typename
          ... on CheckRun{name status conclusion databaseId}
          ... on StatusContext{context state}
        }}}}}}}}
        """;

    public async Task<IReadOnlyList<CheckStatus>> BaseChecksAsync(RemoteRepository repository, string branch, CancellationToken ct)
    {
        using var data = await GraphQlAsync(repository, "the base branch's checks", RefChecksQuery, new RefVariables(repository.Owner, repository.Name, $"refs/heads/{branch}"), ct);
        try
        {
            var @ref = data.RootElement.GetProperty("repository").GetProperty("ref");
            if (@ref.ValueKind != JsonValueKind.Object || @ref.GetProperty("target").GetProperty("statusCheckRollup") is not { ValueKind: JsonValueKind.Object } rollup)
                return [];
            var checks = new List<CheckStatus>();
            foreach (var node in rollup.GetProperty("contexts").GetProperty("nodes").EnumerateArray())
                checks.Add(node.GetProperty("__typename").GetString() == "CheckRun"
                    ? new CheckStatus(node.GetProperty("name").GetString() ?? "", true, Classify(node.GetProperty("status").GetString(), node.GetProperty("conclusion").GetString()), node.GetProperty("databaseId").GetInt64())
                    : new CheckStatus(node.GetProperty("context").GetString() ?? "", true, ClassifyStatus(node.GetProperty("state").GetString()), null));
            return checks;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or JsonException or FormatException)
        {
            throw new IntegrationException("GitHub answered with a body that is not a branch's checks");
        }
    }

    public async Task UpdateBranchAsync(RemoteRepository repository, int number, string expectedHeadSha, CancellationToken ct) =>
        await RestAsync(repository, HttpMethod.Put, $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/update-branch", new BranchUpdate(expectedHeadSha), "the branch update", ct);

    public async Task MarkReadyAsync(RemoteRepository repository, string nodeId, CancellationToken ct)
    {
        using var _ = await GraphQlAsync(repository, "marking the pull request ready", "mutation($id:ID!){markPullRequestReadyForReview(input:{pullRequestId:$id}){pullRequest{isDraft}}}", new NodeVariables(nodeId), ct);
    }

    public async Task EnableAutoMergeAsync(RemoteRepository repository, string nodeId, string headSha, CancellationToken ct)
    {
        // The squash commit keeps the run trailers only if they are in the message GitHub is given (ADR 0074).
        var body = await TrailersAsync(async () => RunMessages(await GraphQlAsync(repository, "reading the pull request's commits", CommitsQuery, new NodeVariables(nodeId), ct)), ct);
        using var _ = await GraphQlAsync(repository, "enabling auto-merge", body is null ? AutoMergeMutation : AutoMergeMutationWithBody, new AutoMergeVariables(nodeId, headSha, body), ct);
    }

    public async Task MergeNowAsync(RemoteRepository repository, int number, string headSha, CancellationToken ct)
    {
        var message = await TrailersAsync(async () => RunMessages(await RestAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/commits?per_page=100", null, "the pull request's commits", ct)), ct);
        await RestAsync(repository, HttpMethod.Put, $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/merge", new SquashMerge("squash", headSha, message), "the merge", ct);
    }

    private const string CommitsQuery = "query($id:ID!){node(id:$id){... on PullRequest{commits(first:100){nodes{commit{message}}}}}}";

    private const string AutoMergeMutation = "mutation($id:ID!,$oid:GitObjectID){enablePullRequestAutoMerge(input:{pullRequestId:$id,mergeMethod:SQUASH,expectedHeadOid:$oid}){pullRequest{number}}}";

    private const string AutoMergeMutationWithBody = "mutation($id:ID!,$oid:GitObjectID,$body:String){enablePullRequestAutoMerge(input:{pullRequestId:$id,mergeMethod:SQUASH,expectedHeadOid:$oid,commitBody:$body}){pullRequest{number}}}";

    /// <summary>The run trailers of the pull request's commits; null when there are none or the commits could not be read: the merge then goes ahead with GitHub's own message.</summary>
    private static async Task<string?> TrailersAsync(Func<Task<IEnumerable<string>>> messages, CancellationToken ct)
    {
        try
        {
            return Provenance.Collect(await messages());
        }
        catch (Exception e) when (e is IntegrationException or JsonException or KeyNotFoundException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The commit messages in a REST list of a pull request's commits (<c>commit.message</c>), or in the GraphQL answer of <see cref="CommitsQuery"/>.</summary>
    private static IEnumerable<string> RunMessages(string commits)
    {
        using var doc = JsonDocument.Parse(commits);
        return [.. doc.RootElement.EnumerateArray().Select(c => c.GetProperty("commit").GetProperty("message").GetString() ?? "")];
    }

    private static IEnumerable<string> RunMessages(JsonDocument data) =>
        [.. data.RootElement.GetProperty("node").GetProperty("commits").GetProperty("nodes").EnumerateArray().Select(c => c.GetProperty("commit").GetProperty("message").GetString() ?? "")];

    public async Task CommentAsync(RemoteRepository repository, int number, string body, CancellationToken ct) =>
        await RestAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/issues/{number.ToString(CultureInfo.InvariantCulture)}/comments", new CommentBody(body), "the comment", ct);

    /// <summary>Each job's workflow run (<c>GET actions/jobs/{id}</c>), then <c>rerun-failed-jobs</c> once per run. Needs the App's Actions permission to write; without it GitHub answers 403 and the message says so.</summary>
    public async Task<int> RerunFailedJobsAsync(RemoteRepository repository, IReadOnlyList<long> jobIds, CancellationToken ct)
    {
        HashSet<long> runs = [];
        foreach (var job in jobIds.Distinct())
        {
            var text = await RestAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/actions/jobs/{job.ToString(CultureInfo.InvariantCulture)}", null, "the job", ct);
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("run_id", out var run) && run.TryGetInt64(out var id))
                    runs.Add(id);
            }
            catch (JsonException)
            {
                throw new IntegrationException("GitHub answered with a body that is not a job");
            }
        }
        foreach (var run in runs)
            await RestAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/actions/runs/{run.ToString(CultureInfo.InvariantCulture)}/rerun-failed-jobs", null, "re-running the failed jobs (the App needs the Actions permission to write)", ct);
        return runs.Count;
    }

    public async Task<string> JobLogTailAsync(RemoteRepository repository, long jobId, int maxChars, CancellationToken ct)
    {
        var token = await TokenAsync(repository, ct);
        using var first = Request(HttpMethod.Get, $"{RepoPath(repository)}/actions/jobs/{jobId.ToString(CultureInfo.InvariantCulture)}/logs", null, token);
        var (status, location, text) = await SendAsync(first, "the job log", token, ct);
        // The log is a redirect to a signed address on another host; that request carries no credential.
        if (status is HttpStatusCode.Found or HttpStatusCode.Moved or HttpStatusCode.TemporaryRedirect or HttpStatusCode.SeeOther && location is not null)
        {
            using var second = new HttpRequestMessage(HttpMethod.Get, location);
            second.Headers.UserAgent.Add(new ProductInfoHeaderValue("chargehand", "1"));
            (status, _, text) = await SendAsync(second, "the job log", token, ct);
        }
        if (status != HttpStatusCode.OK)
            throw new IntegrationException($"GitHub answered {(int)status} to the job log request");
        return TailOf(text, maxChars);
    }

    /// <summary>The last <paramref name="maxChars"/> characters of a job log, starting at a line, without colour codes and timestamps.</summary>
    public static string TailOf(string log, int maxChars)
    {
        var lines = log.Split('\n').Select(l => Timestamp().Replace(Ansi().Replace(l.TrimEnd('\r'), ""), "")).ToList();
        var text = string.Join('\n', lines).Trim();
        if (text.Length <= maxChars)
            return text;
        var cut = text[^maxChars..];
        var newline = cut.IndexOf('\n', StringComparison.Ordinal);
        return newline >= 0 ? cut[(newline + 1)..] : cut;
    }

    private static CheckOutcome Classify(string? status, string? conclusion) =>
        status != "COMPLETED" ? CheckOutcome.Pending
        : conclusion is "SUCCESS" or "NEUTRAL" or "SKIPPED" ? CheckOutcome.Passed
        : conclusion is "FAILURE" or "TIMED_OUT" or "STARTUP_FAILURE" ? CheckOutcome.Failed
        : CheckOutcome.Other;

    private static CheckOutcome ClassifyStatus(string? state) =>
        state switch { "SUCCESS" => CheckOutcome.Passed, "FAILURE" or "ERROR" => CheckOutcome.Failed, "PENDING" or "EXPECTED" => CheckOutcome.Pending, _ => CheckOutcome.Other };

    private static string RepoPath(RemoteRepository repository) => $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}";

    private async Task<string> TokenAsync(RemoteRepository repository, CancellationToken ct)
    {
        try
        {
            return await tokens.GetAsync(repository.Owner, repository.Name, ct);
        }
        catch (GitHubAppException e)
        {
            throw new IntegrationException(e.Message);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body, string token)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), ForgeJsonContext.Default), Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("chargehand", "1"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private async Task<(HttpStatusCode Status, Uri? Location, string Body)> SendAsync(HttpRequestMessage request, string what, string token, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct);
            return (response.StatusCode, response.Headers.Location is { } l && !l.IsAbsoluteUri ? new Uri(http.BaseAddress!, l) : response.Headers.Location, await response.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException e)
        {
            throw new IntegrationException(Redact($"{what} could not be reached: {e.Message}", token));
        }
    }

    private async Task<string> RestAsync(RemoteRepository repository, HttpMethod method, string path, object? body, string what, CancellationToken ct)
    {
        var token = await TokenAsync(repository, ct);
        using var request = Request(method, path, body, token);
        var (status, _, text) = await SendAsync(request, what, token, ct);
        if ((int)status is < 200 or >= 300)
            throw new IntegrationException(Redact($"GitHub answered {(int)status} to {what}: {GitHubApi.Message(text)}", token));
        return text;
    }

    /// <summary>The <c>data</c> member of a GraphQL answer; its first error is an <see cref="IntegrationException"/>.</summary>
    private async Task<JsonDocument> GraphQlAsync(RemoteRepository repository, string what, string query, object variables, CancellationToken ct)
    {
        var token = await TokenAsync(repository, ct);
        using var request = Request(HttpMethod.Post, "graphql", new GraphQlRequest(query, variables), token);
        var (status, _, text) = await SendAsync(request, what, token, ct);
        if (status != HttpStatusCode.OK)
            throw new IntegrationException(Redact($"GitHub answered {(int)status} to {what}: {GitHubApi.Message(text)}", token));
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var message = errors[0].TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new IntegrationException(Redact($"GitHub refused {what}: {Cut(message ?? "no message", 300)}", token));
            }
            return JsonDocument.Parse(doc.RootElement.GetProperty("data").GetRawText());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new IntegrationException($"GitHub answered {what} with a body that is not GraphQL data");
        }
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..max] : text;

    private static string Redact(string text, string token) => token.Length >= 4 ? text.Replace(token, "[redacted]", StringComparison.Ordinal) : text;

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT[\d:.]+Z ?")]
    private static partial Regex Timestamp();
}
