using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Oversight.Tests;

/// <summary>Issue #452 end to end, against fakes: once a task's pull request reads back <see cref="PrOutcomes.MergedEdited"/>, the tracker fetches the commits after chargehand's own
/// last push, classifies them, and proposes once the same category recurs. Nothing here reaches a real forge.</summary>
public sealed class HumanEditTrackerTests : IDisposable
{
    private static readonly RemoteRepository Remote = new("owner", "repo", ForgeKind.GitHub, null);

    private readonly string _dir = Directory.CreateTempSubdirectory("human-edit-tracker-").FullName;
    private readonly ManualTimeProvider _clock = new();
    private DateTimeOffset Now => _clock.GetUtcNow();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JsonlLearningStore Learning() => new(Path.Combine(_dir, "learning.jsonl"));

    private LearningRecord Record(string runId, string prUrl, int number, string pushedHead) =>
        new("batch", "task-" + runId, runId, Now, "owner/repo", "M", "default", new LearningBudget(null, new BudgetSource("none")),
            new LearningSpend(null, false, 0, 0, 0, 0, 0, 0), LearningOutcome.Completed, null, null, [], false,
            new LearningPr(prUrl, number, Remote, pushedHead));

    private sealed class FakeCommitHost : IIntegrationHost
    {
        public Dictionary<(int Number, string AfterSha), IReadOnlyList<PrCommit>> Commits { get; } = [];

        public Task<IReadOnlyList<PrCommit>> CommitsAfterAsync(RemoteRepository repository, int number, string afterSha, CancellationToken ct) =>
            Task.FromResult(Commits.TryGetValue((number, afterSha), out var commits) ? commits : throw new IntegrationException("no fixture for this pull request"));

        public Task<PrSnapshot> GetAsync(RemoteRepository repository, int number, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> BehindByAsync(RemoteRepository repository, string baseBranch, string branch, CancellationToken ct) => throw new NotSupportedException();

        public Task UpdateBranchAsync(RemoteRepository repository, int number, string expectedHeadSha, CancellationToken ct) => throw new NotSupportedException();

        public Task MarkReadyAsync(RemoteRepository repository, string nodeId, CancellationToken ct) => throw new NotSupportedException();

        public Task EnableAutoMergeAsync(RemoteRepository repository, string nodeId, string headSha, CancellationToken ct) => throw new NotSupportedException();

        public Task MergeNowAsync(RemoteRepository repository, int number, string headSha, CancellationToken ct) => throw new NotSupportedException();

        public Task CommentAsync(RemoteRepository repository, int number, string body, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> JobLogTailAsync(RemoteRepository repository, long jobId, int maxChars, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_merged_edited_pull_requests_commits_are_fetched_classified_and_recorded()
    {
        var learning = Learning();
        await learning.AppendAsync(Record("r1", "https://example.test/pull/1", 1, "pushed1"), CancellationToken.None);
        await learning.AppendAsync(new PrOutcomeRecord("r1", Now, PrOutcomes.MergedEdited, "merged1"), CancellationToken.None);

        var host = new FakeCommitHost();
        host.Commits[(1, "pushed1")] = [new PrCommit("sha1", "style: fix lint", ["src/Foo.cs"])];

        var edits = new JsonlHumanEditStore(Path.Combine(_dir, "human-edits.jsonl"));
        var proposals = new JsonlProposalSink(Path.Combine(_dir, "proposals.jsonl"));
        var tracker = new HumanEditTracker(learning, edits, proposals, _ => host, _clock);

        var written = await tracker.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, written);
        var recorded = await edits.ReadAsync(CancellationToken.None);
        Assert.Equal([new HumanEditCommit(Now, "r1", "https://example.test/pull/1", "sha1", HumanEditCategory.Lint, "style: fix lint")], recorded);
        Assert.Empty(await proposals.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_second_pull_requests_same_category_commit_writes_a_proposal()
    {
        var learning = Learning();
        await learning.AppendAsync(Record("r1", "https://example.test/pull/1", 1, "pushed1"), CancellationToken.None);
        await learning.AppendAsync(new PrOutcomeRecord("r1", Now, PrOutcomes.MergedEdited, "merged1"), CancellationToken.None);
        await learning.AppendAsync(Record("r2", "https://example.test/pull/2", 2, "pushed2"), CancellationToken.None);
        await learning.AppendAsync(new PrOutcomeRecord("r2", Now, PrOutcomes.MergedEdited, "merged2"), CancellationToken.None);

        var host = new FakeCommitHost();
        host.Commits[(1, "pushed1")] = [new PrCommit("sha1", "style: fix lint", ["src/Foo.cs"])];
        host.Commits[(2, "pushed2")] = [new PrCommit("sha2", "chore: run dotnet format", ["src/Bar.cs"])];

        var edits = new JsonlHumanEditStore(Path.Combine(_dir, "human-edits.jsonl"));
        var proposals = new JsonlProposalSink(Path.Combine(_dir, "proposals.jsonl"));
        var tracker = new HumanEditTracker(learning, edits, proposals, _ => host, _clock);

        var written = await tracker.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, written);
        var writtenProposals = await proposals.ReadAsync(CancellationToken.None);
        Assert.Single(writtenProposals);
        Assert.Equal(HumanEditCategory.Lint, writtenProposals[0].Category);
        Assert.Equal(2, writtenProposals[0].Occurrences);
    }

    [Fact]
    public async Task A_pull_request_not_merged_edited_is_left_alone()
    {
        var learning = Learning();
        await learning.AppendAsync(Record("r1", "https://example.test/pull/1", 1, "pushed1"), CancellationToken.None);
        await learning.AppendAsync(new PrOutcomeRecord("r1", Now, PrOutcomes.MergedUnchanged, "pushed1"), CancellationToken.None);

        var host = new FakeCommitHost();
        var edits = new JsonlHumanEditStore(Path.Combine(_dir, "human-edits.jsonl"));
        var tracker = new HumanEditTracker(learning, edits, new JsonlProposalSink(Path.Combine(_dir, "proposals.jsonl")), _ => host, _clock);

        Assert.Equal(0, await tracker.RefreshAsync(CancellationToken.None));
        Assert.Empty(await edits.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_forge_error_is_skipped_not_thrown()
    {
        var learning = Learning();
        await learning.AppendAsync(Record("r1", "https://example.test/pull/1", 1, "pushed1"), CancellationToken.None);
        await learning.AppendAsync(new PrOutcomeRecord("r1", Now, PrOutcomes.MergedEdited, "merged1"), CancellationToken.None);

        var tracker = new HumanEditTracker(learning, new JsonlHumanEditStore(Path.Combine(_dir, "human-edits.jsonl")),
            new JsonlProposalSink(Path.Combine(_dir, "proposals.jsonl")), _ => new FakeCommitHost(), _clock);

        Assert.Equal(0, await tracker.RefreshAsync(CancellationToken.None));
    }
}
