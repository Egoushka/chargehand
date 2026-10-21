using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Forge.Api;

/// <summary>GitHub Actions history: the workflow runs since a date (<c>GET actions/runs</c>, one page), then the jobs of each attempt (<c>GET actions/runs/{id}/attempts/{n}/jobs</c>) only of the runs
/// that can hold a flake: a run that was re-run, or a commit whose runs of one workflow did not all end the same. Needs the App's Actions permission to read.</summary>
internal static class GitHubCiHistory
{
    public const int MaxRuns = 100;
    public const int MaxJobLists = 40;

    public static async Task<IReadOnlyList<CiJob>> ReadAsync(Func<string, Task<string>> get, string repoPath, DateTimeOffset since, CancellationToken ct)
    {
        var runs = Runs(await get($"{repoPath}/actions/runs?per_page={MaxRuns.ToString(CultureInfo.InvariantCulture)}&created=%3E%3D{since.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"));
        var candidates = runs.GroupBy(r => (r.WorkflowId, r.HeadSha))
            .Where(g => g.Any(r => r.Attempts > 1) || g.Select(r => r.Conclusion).Distinct().Count() > 1)
            .SelectMany(g => g).OrderByDescending(r => r.Id);
        List<CiJob> jobs = [];
        var lists = 0;
        foreach (var run in candidates)
            for (var attempt = 1; attempt <= run.Attempts && lists < MaxJobLists; attempt++, lists++)
            {
                ct.ThrowIfCancellationRequested();
                jobs.AddRange(Jobs(await get($"{repoPath}/actions/runs/{run.Id.ToString(CultureInfo.InvariantCulture)}/attempts/{attempt.ToString(CultureInfo.InvariantCulture)}/jobs?per_page=100"), run));
            }
        return jobs;
    }

    private sealed record Run(long Id, long WorkflowId, string Workflow, string HeadSha, string? Branch, int? PullNumber, int Attempts, string? Conclusion);

    private static List<Run> Runs(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return [.. doc.RootElement.GetProperty("workflow_runs").EnumerateArray().Where(r => r.GetProperty("status").GetString() == "completed").Select(r => new Run(
                r.GetProperty("id").GetInt64(), r.GetProperty("workflow_id").GetInt64(), r.GetProperty("name").GetString() ?? "", r.GetProperty("head_sha").GetString() ?? "",
                r.TryGetProperty("head_branch", out var b) ? b.GetString() : null,
                r.TryGetProperty("pull_requests", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0 ? p[0].GetProperty("number").GetInt32() : null,
                r.TryGetProperty("run_attempt", out var a) ? a.GetInt32() : 1, r.GetProperty("conclusion").GetString()))];
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new IntegrationException("GitHub answered with a body that is not a list of workflow runs");
        }
    }

    private static IEnumerable<CiJob> Jobs(string body, Run run)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return [.. doc.RootElement.GetProperty("jobs").EnumerateArray().Where(j => j.GetProperty("status").GetString() == "completed").Select(j => new CiJob(
                j.GetProperty("id").GetInt64(), run.Workflow, j.GetProperty("name").GetString() ?? "", j.TryGetProperty("head_sha", out var sha) ? sha.GetString() ?? run.HeadSha : run.HeadSha,
                run.Branch, run.PullNumber, Outcome(j.GetProperty("conclusion").GetString()),
                j.TryGetProperty("completed_at", out var at) && at.ValueKind == JsonValueKind.String ? at.GetDateTimeOffset() : DateTimeOffset.MinValue,
                j.TryGetProperty("html_url", out var url) ? url.GetString() : null))];
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new IntegrationException("GitHub answered with a body that is not a list of jobs");
        }
    }

    private static CheckOutcome Outcome(string? conclusion) =>
        conclusion switch { "success" => CheckOutcome.Passed, "failure" or "timed_out" => CheckOutcome.Failed, _ => CheckOutcome.Other };
}

/// <summary>GitLab CI history: the pipelines updated since a date (<c>GET pipelines</c>, newest first), then each one's jobs with the retried ones (<c>include_retried=true</c>), since a retry is a new job of the
/// same name in the same pipeline. A merge request pipeline's ref (<c>refs/merge-requests/N/head</c>) names its merge request.</summary>
internal static partial class GitLabCiHistory
{
    public const int MaxPipelines = 30;

    public static async Task<IReadOnlyList<CiJob>> ReadAsync(Func<string, Task<string>> get, string projectPath, DateTimeOffset since, CancellationToken ct)
    {
        var updated = Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        List<CiJob> jobs = [];
        foreach (var (id, sha, @ref) in Pipelines(await get($"{projectPath}/pipelines?updated_after={updated}&order_by=id&sort=desc&per_page={MaxPipelines.ToString(CultureInfo.InvariantCulture)}")))
        {
            ct.ThrowIfCancellationRequested();
            var request = MergeRequestRef().Match(@ref ?? "");
            var pull = request.Success ? int.Parse(request.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
            jobs.AddRange(Jobs(await get($"{projectPath}/pipelines/{id.ToString(CultureInfo.InvariantCulture)}/jobs?include_retried=true&per_page=100"), sha, request.Success ? null : @ref, pull));
        }
        return jobs;
    }

    private static List<(long Id, string Sha, string? Ref)> Pipelines(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return [.. doc.RootElement.EnumerateArray().Select(p => (p.GetProperty("id").GetInt64(), p.GetProperty("sha").GetString() ?? "", p.TryGetProperty("ref", out var r) ? r.GetString() : null))];
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new IntegrationException("GitLab answered with a body that is not a list of pipelines");
        }
    }

    private static IEnumerable<CiJob> Jobs(string body, string sha, string? @ref, int? pull)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return [.. doc.RootElement.EnumerateArray().Where(j => j.TryGetProperty("finished_at", out var f) && f.ValueKind == JsonValueKind.String).Select(j => new CiJob(
                j.GetProperty("id").GetInt64(), j.TryGetProperty("stage", out var stage) ? stage.GetString() ?? "" : "", j.GetProperty("name").GetString() ?? "", sha, @ref, pull,
                j.GetProperty("status").GetString() switch { "success" => CheckOutcome.Passed, "failed" => CheckOutcome.Failed, _ => CheckOutcome.Other },
                j.GetProperty("finished_at").GetDateTimeOffset(), j.TryGetProperty("web_url", out var url) ? url.GetString() : null))];
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new IntegrationException("GitLab answered with a body that is not a list of jobs");
        }
    }

    [GeneratedRegex(@"^refs/merge-requests/(\d+)/")]
    private static partial Regex MergeRequestRef();
}
