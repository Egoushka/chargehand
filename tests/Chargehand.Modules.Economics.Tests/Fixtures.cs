using Chargehand.Contracts;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Economics.Tests;

/// <summary>Run-log rows shaped like the real log: a start and a run record per run, the pull request as an artifact.</summary>
internal static class Fixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static (StartRecord Start, RunRecord Run) Run(string id, string preset, string repository, decimal? usd, int daysAgo, IReadOnlyList<Artifact>? artifacts = null,
        IReadOnlyList<CallerInput>? inputs = null, string? parent = null)
    {
        var finished = Now.AddDays(-daysAgo);
        var request = new RunRequest("request/v1", "goal", new RequestContext(false, preset, Repository: new RepositoryRef(repository, "abc123")), inputs);
        var result = new ResultContract("result/v1", id, "n", new string('0', 32), new PromptChain([], new AsSent("2", "build", "p/m", "2026-10-01")), ResultStatus.Completed,
            "done", [], [], artifacts ?? [], [], 0.9, new Usage(100, 10, 0, 0, usd));
        return (new StartRecord(id, finished.AddMinutes(-5), new string('0', 32), request, 1, parent),
            new RunRecord(id, finished.AddMinutes(-5), finished, preset, null, null, result));
    }

    public static Artifact PullRequest(string url) => new("pull-request", "application/json", new string('0', 64), Content: $$"""{"url":"{{url}}","number":1,"draft":true}""");

    public static Artifact Batch(params (string? PrUrl, decimal? Usd)[] rows) => new("driven-batch", "application/json", new string('0', 64),
        Content: "{\"tasks\":[" + string.Join(',', rows.Select((r, i) => $"{{\"id\":\"t{i}\",\"status\":\"completed\"" + (r.PrUrl is null ? "" : $",\"pr_url\":\"{r.PrUrl}\"") + (r.Usd is null ? "" : $",\"usd\":{r.Usd}") + "}")) + "]}");

    public static RunLogData Log(params (StartRecord Start, RunRecord Run)[] runs) => new([.. runs.Select(r => r.Start)], [.. runs.Select(r => r.Run)], [], []);

    public static string Pull(string repository, int number) => $"https://github.com/{repository}/pull/{number}";
}

/// <summary>A pretend GitHub: the state of each pull request by number, and the reads made. Everything else the integrator does is out of scope here.</summary>
internal sealed class FakeGitHub : IIntegrationHost
{
    public Dictionary<int, (bool Merged, bool Closed)> Pulls { get; } = [];

    public List<string> Reads { get; } = [];

    public Exception? Down { get; set; }

    public Task<PrSnapshot> GetAsync(RemoteRepository repository, int number, CancellationToken ct)
    {
        Reads.Add($"{repository.Owner}/{repository.Name}#{number}");
        if (Down is not null)
            throw Down;
        var (merged, closed) = Pulls.TryGetValue(number, out var state) ? state : throw new IntegrationException("not found");
        return Task.FromResult(new PrSnapshot("node", number, "aaaaaaa1", "main", false, merged, closed, "CLEAN", false, [], []));
    }

    public Task<int> BehindByAsync(RemoteRepository repository, string baseBranch, string branch, CancellationToken ct) => throw new NotSupportedException();

    public Task UpdateBranchAsync(RemoteRepository repository, int number, string expectedHeadSha, CancellationToken ct) => throw new NotSupportedException();

    public Task MarkReadyAsync(RemoteRepository repository, string nodeId, CancellationToken ct) => throw new NotSupportedException();

    public Task EnableAutoMergeAsync(RemoteRepository repository, string nodeId, string headSha, CancellationToken ct) => throw new NotSupportedException();

    public Task MergeNowAsync(RemoteRepository repository, int number, string headSha, CancellationToken ct) => throw new NotSupportedException();

    public Task CommentAsync(RemoteRepository repository, int number, string body, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> JobLogTailAsync(RemoteRepository repository, long jobId, int maxChars, CancellationToken ct) => throw new NotSupportedException();
}
