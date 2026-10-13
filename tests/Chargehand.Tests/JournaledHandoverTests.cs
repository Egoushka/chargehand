using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Journal.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0061, handover slice: the push and the pull request are recorded steps keyed by the handed-over run. "Killed" is a handover cancelled while a hook of the remote or the host's
/// answer hangs, with the journal closed; "restarted" is the same handover run again on a journal opened on the same file. Whatever the cut, the task ends with one branch and one pull request.</summary>
public sealed class JournaledHandoverTests : IDisposable
{
    private const string Batch = "batch-1";

    private readonly HandoverTests.Fixture _f = new();

    public void Dispose() => _f.Dispose();

    private string State => Path.Combine(_f.Dir.Path, "batches");

    private string Reached => Path.Combine(_f.Dir.Path, "reached");

    private string HangFlag => Path.Combine(_f.Dir.Path, "hang");

    /// <summary>A host that keeps the pull requests it opened, one per call (it does not refuse a second one for the same branch); with <see cref="HangAfterCreate"/> it opens one and never answers.</summary>
    private sealed class Host : IPullRequests
    {
        public List<(int Number, string Head)> Open { get; } = [];

        public int Created { get; private set; }

        public bool HangAfterCreate { get; set; }

        public async Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct)
        {
            Created++;
            Open.Add((Open.Count + 1, head));
            if (HangAfterCreate)
                await Task.Delay(Timeout.Infinite, ct);
            return Ref(Open[^1].Number);
        }

        public Task EditAsync(RemoteRepository repo, int number, string title, string body, CancellationToken ct) => Task.CompletedTask;

        public Task<PullRequestRef?> FindByHeadAsync(RemoteRepository repo, string head, CancellationToken ct) =>
            Task.FromResult(Open.Where(p => p.Head == head).Select(p => Ref(p.Number)).FirstOrDefault());

        private static PullRequestRef Ref(int number) => new($"https://example.test/o/r/pull/{number}", number);
    }

    /// <summary>A remote hook that marks it was reached and, while the hang flag exists, does not end. In <c>pre-receive</c> the branch is not updated yet; in <c>post-receive</c> it is.</summary>
    private void HangIn(string hook)
    {
        var path = Path.Combine(_f.Remote, "hooks", hook);
        File.WriteAllText(path, $"#!/bin/sh\ncat > /dev/null\n: > '{Reached}'\nwhile [ -e '{HangFlag}' ]; do sleep 0.1; done\nexit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(HangFlag, "");
    }

    private static CancellationTokenSource CutWhen(Func<bool> reached)
    {
        var cut = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = Task.Run(async () =>
        {
            while (!reached())
                await Task.Delay(20);
            await cut.CancelAsync();
        });
        return cut;
    }

    private async Task<(HandoverOutcome Outcome, List<HandoverEvent> Events)> HandOverAsync(Host host)
    {
        List<HandoverEvent> events = [];
        using var journal = await DrivenJournal.OpenAsync(State);
        var outcome = await new Handover(new HandoverTests.FakeVerifier(), host, steps: journal, batchId: Batch).RunAsync(_f.Input(with: i => i with { Report = events.Add }), default);
        Assert.Empty(await journal.UnfinishedAsync());
        return (outcome, events);
    }

    private async Task KilledAsync(Host host, Func<bool> reached, StepKey cutStep)
    {
        using (var journal = await DrivenJournal.OpenAsync(State))
        {
            using var cut = CutWhen(reached);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Handover(new HandoverTests.FakeVerifier(), host, steps: journal, batchId: Batch).RunAsync(_f.Input(), cut.Token));
            Assert.Equal([cutStep], await journal.UnfinishedAsync());
        }
        File.Delete(HangFlag);
    }

    [Fact]
    public async Task A_push_cut_after_the_remote_took_the_branch_is_adopted_after_a_restart_and_one_pull_request_is_opened()
    {
        var host = new Host();
        HangIn("post-receive");
        await KilledAsync(host, () => File.Exists(Reached), StepKey.Push(Batch, "run1"));
        var onRemote = _f.RemoteRef("refs/heads/chargehand/run1");
        Assert.NotEqual("", onRemote);
        Assert.Equal(0, host.Created);

        var (outcome, events) = await HandOverAsync(host);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Equal(onRemote, outcome.Commit);
        // The remote already held the commit: it is taken as pushed, not pushed again.
        Assert.Equal("chargehand/run1 was already on the remote", events.Single(e => e.Kind == RunEventKind.Pushed).Detail);
        Assert.Equal([(1, "chargehand/run1")], host.Open);
    }

    [Fact]
    public async Task A_push_cut_before_the_remote_took_the_branch_is_pushed_after_a_restart()
    {
        var host = new Host();
        HangIn("pre-receive");
        await KilledAsync(host, () => File.Exists(Reached), StepKey.Push(Batch, "run1"));
        Assert.Equal("", _f.RemoteRef("refs/heads/chargehand/run1"));

        var (outcome, events) = await HandOverAsync(host);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Equal(outcome.Commit, _f.RemoteRef("refs/heads/chargehand/run1"));
        Assert.Equal("pushed chargehand/run1", events.Single(e => e.Kind == RunEventKind.Pushed).Detail);
        Assert.Single(host.Open);
    }

    [Fact]
    public async Task A_pull_request_cut_after_the_host_opened_it_is_found_by_its_branch_after_a_restart_and_not_opened_twice()
    {
        var host = new Host { HangAfterCreate = true };
        await KilledAsync(host, () => host.Created == 1, StepKey.PullRequest(Batch, "run1"));
        host.HangAfterCreate = false;

        var (outcome, events) = await HandOverAsync(host);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Equal(1, host.Created);
        Assert.Equal([(1, "chargehand/run1")], host.Open);
        Assert.Equal(1, outcome.PullRequest!.Number);
        Assert.Equal(outcome.PullRequest.Url, events.Single(e => e.Kind == RunEventKind.PrOpened).PrUrl);
    }

    [Fact]
    public async Task A_finished_handover_run_again_neither_reaches_the_remote_nor_opens_a_second_pull_request()
    {
        var host = new Host();
        var (first, _) = await HandOverAsync(host);
        // Gone: a push or a look at the remote would now fail the handover.
        Directory.Delete(_f.Remote, recursive: true);

        var (again, events) = await HandOverAsync(host);

        Assert.Equal(HandoverStatus.Pushed, again.Status);
        Assert.Equal((first.Commit, first.PullRequest), (again.Commit, again.PullRequest));
        Assert.Equal(1, host.Created);
        Assert.Contains(events, e => e.Kind == RunEventKind.Pushed);
    }
}
