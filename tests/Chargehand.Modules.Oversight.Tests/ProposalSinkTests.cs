using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Oversight.Tests;

/// <summary>Issue #452's proposal sink: a proposal written goes nowhere but the file (never a push, a merge or a call to a tracker), and reads back the same.</summary>
public sealed class ProposalSinkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("proposals-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_proposal_written_reads_back_the_same()
    {
        var sink = new JsonlProposalSink(Path.Combine(_dir, "proposals.jsonl"));
        var proposal = new ProposalRecord(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero), HumanEditCategory.Lint, 2, "Run the formatter before pushing",
            "2 merged pull requests needed a human lint fix.", ["https://example.test/o/r/pull/1", "https://example.test/o/r/pull/2"]);

        await sink.WriteAsync(proposal, CancellationToken.None);
        var read = await sink.ReadAsync(CancellationToken.None);

        Assert.Equal([proposal], read);
    }

    [Fact]
    public async Task Writing_touches_no_other_file()
    {
        var path = Path.Combine(_dir, "proposals.jsonl");
        var sink = new JsonlProposalSink(path);
        await sink.WriteAsync(new ProposalRecord(DateTimeOffset.UnixEpoch, HumanEditCategory.CiConfig, 2, "t", "b", []), CancellationToken.None);

        Assert.Equal([Path.GetFileName(path)], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Reading_an_absent_file_is_empty() => Assert.Empty(await new JsonlProposalSink(Path.Combine(_dir, "none.jsonl")).ReadAsync(CancellationToken.None));
}

/// <summary>Issue #452's classified-commit store: appended once per sha, read back oldest first.</summary>
public sealed class HumanEditStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("human-edits-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Commits_read_back_oldest_first()
    {
        var store = new JsonlHumanEditStore(Path.Combine(_dir, "human-edits.jsonl"));
        var first = new HumanEditCommit(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), "run-1", "https://example.test/pull/1", "sha1", HumanEditCategory.Lint, "fix lint");
        var second = new HumanEditCommit(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), "run-2", "https://example.test/pull/2", "sha2", HumanEditCategory.CiConfig, "fix ci");

        await store.AppendAsync(second, CancellationToken.None);
        await store.AppendAsync(first, CancellationToken.None);

        Assert.Equal([first, second], await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_sha_already_held_is_not_held_twice()
    {
        var store = new JsonlHumanEditStore(Path.Combine(_dir, "human-edits.jsonl"));
        var commit = new HumanEditCommit(DateTimeOffset.UnixEpoch, "run-1", "https://example.test/pull/1", "sha1", HumanEditCategory.Lint, "fix lint");

        await store.AppendAsync(commit, CancellationToken.None);
        await store.AppendAsync(commit, CancellationToken.None);

        Assert.Equal([commit], await store.ReadAsync(CancellationToken.None));
    }
}
