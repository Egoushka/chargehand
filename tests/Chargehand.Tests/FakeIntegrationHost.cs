using Chargehand.Modules.Forge.Api;

namespace Chargehand.Tests;

/// <summary>A pull request on a pretend GitHub: the snapshot a test sets (or a script computes) plus a record of what the integrator did to it.</summary>
internal sealed class FakeIntegrationHost : IIntegrationHost
{
    public List<string> Calls { get; } = [];

    public List<string> Comments { get; } = [];

    public PrSnapshot Current { get; set; } = Snapshot();

    /// <summary>Computes the snapshot for each read, from the host's own record of what was done; null: <see cref="Current"/>.</summary>
    public Func<FakeIntegrationHost, PrSnapshot>? Script { get; set; }

    public int BehindBy { get; set; }

    public bool UpdateMovesHead { get; set; } = true;

    public Exception? UpdateFails { get; set; }

    public Exception? AutoMergeRefused { get; set; }

    public Exception? ReadFails { get; set; }

    public string StatusAfterReady { get; set; } = "BLOCKED";

    public Dictionary<long, string> Logs { get; } = [];

    /// <summary>What the base branch's rules say; not in <see cref="Calls"/>, so the order of the calls that change the pull request reads as before.</summary>
    public ChecksGate Gate { get; set; } = ChecksGate.Required;

    public Task<ChecksGate> ChecksGateAsync(RemoteRepository repository, string branch, CancellationToken ct) => Task.FromResult(Gate);

    /// <summary>What the base branch's own head reports; null: no required checks are failing there.</summary>
    public IReadOnlyList<CheckStatus> BaseChecks { get; set; } = [];

    public Exception? BaseChecksFail { get; set; }

    public Task<IReadOnlyList<CheckStatus>> BaseChecksAsync(RemoteRepository repository, string branch, CancellationToken ct)
    {
        Calls.Add($"base-checks {branch}");
        return BaseChecksFail is not null ? throw BaseChecksFail : Task.FromResult(BaseChecks);
    }

    public static PrSnapshot Snapshot(string head = "aaaaaaa1", bool draft = true, string status = "DRAFT", bool autoMerge = false, bool merged = false, IReadOnlyList<CheckStatus>? checks = null) =>
        new("PR_node", 7, head, "main", draft, merged, false, status, autoMerge, checks ?? [], []);

    public Task<PrSnapshot> GetAsync(RemoteRepository repository, int number, CancellationToken ct)
    {
        Calls.Add("get");
        if (ReadFails is not null)
            throw ReadFails;
        var snapshot = Script?.Invoke(this) ?? Current;
        return Task.FromResult(snapshot with { Comments = [.. snapshot.Comments, .. Comments] });
    }

    public Task<int> BehindByAsync(RemoteRepository repository, string baseBranch, string branch, CancellationToken ct)
    {
        Calls.Add($"behind {baseBranch} {branch}");
        return Task.FromResult(BehindBy);
    }

    public Task UpdateBranchAsync(RemoteRepository repository, int number, string expectedHeadSha, CancellationToken ct)
    {
        Calls.Add($"update-branch {expectedHeadSha}");
        if (UpdateFails is not null)
            throw UpdateFails;
        if (UpdateMovesHead)
            Current = Current with { HeadSha = "bbbbbbb2" };
        BehindBy = 0;
        return Task.CompletedTask;
    }

    public Task MarkReadyAsync(RemoteRepository repository, string nodeId, CancellationToken ct)
    {
        Calls.Add("ready");
        Current = Current with { IsDraft = false, MergeStatus = StatusAfterReady };
        return Task.CompletedTask;
    }

    public Task EnableAutoMergeAsync(RemoteRepository repository, string nodeId, string headSha, CancellationToken ct)
    {
        Calls.Add($"auto-merge {headSha}");
        if (AutoMergeRefused is not null)
            throw AutoMergeRefused;
        Current = Current with { AutoMerge = true };
        return Task.CompletedTask;
    }

    public Task MergeNowAsync(RemoteRepository repository, int number, string headSha, CancellationToken ct)
    {
        Calls.Add($"merge {headSha}");
        Current = Current with { Merged = true };
        return Task.CompletedTask;
    }

    public Task CommentAsync(RemoteRepository repository, int number, string body, CancellationToken ct)
    {
        Calls.Add("comment");
        Comments.Add(body);
        return Task.CompletedTask;
    }

    /// <summary>The job ids each re-run was asked for; a re-run turns the failed checks pending.</summary>
    public List<IReadOnlyList<long>> Reruns { get; } = [];

    public Exception? RerunFails { get; set; }

    public Task<int> RerunFailedJobsAsync(RemoteRepository repository, IReadOnlyList<long> jobIds, CancellationToken ct)
    {
        Calls.Add("rerun");
        if (RerunFails is not null)
            throw RerunFails;
        Reruns.Add(jobIds);
        Current = Current with { Checks = [.. Current.Checks.Select(c => jobIds.Contains(c.JobId ?? -1) ? c with { Outcome = CheckOutcome.Pending } : c)] };
        return Task.FromResult(jobIds.Count);
    }

    public Task<string> JobLogTailAsync(RemoteRepository repository, long jobId, int maxChars, CancellationToken ct)
    {
        Calls.Add($"log {jobId}");
        return Logs.TryGetValue(jobId, out var log) ? Task.FromResult(log) : throw new IntegrationException("no log");
    }
}
