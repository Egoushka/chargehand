using Chargehand.Contracts;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Journal.Api;

namespace Chargehand.Modules.Handover.Tests;

// Inside this namespace, "Handover" is the module's namespace; the alias names the class.
using Handover = Chargehand.Modules.Handover.Api.Handover;

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

    private async Task<(HandoverOutcome Outcome, List<HandoverEvent> Events)> HandOverAsync(Host host, IBranchVerifier? verifier = null)
    {
        List<HandoverEvent> events = [];
        using var journal = await DrivenJournal.OpenAsync(State);
        var outcome = await new Handover(verifier ?? new HandoverTests.FakeVerifier(), host, steps: journal, batchId: Batch).RunAsync(_f.Input(with: i => i with { Report = events.Add }), default);
        Assert.Empty(await journal.UnfinishedAsync());
        return (outcome, events);
    }

    private async Task KilledAsync(Host host, Func<bool> reached, StepKey cutStep, IBranchVerifier? verifier = null)
    {
        using (var journal = await DrivenJournal.OpenAsync(State))
        {
            using var cut = CutWhen(reached);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Handover(verifier ?? new HandoverTests.FakeVerifier(), host, steps: journal, batchId: Batch).RunAsync(_f.Input(), cut.Token));
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
        var verifier = new HandoverTests.FakeVerifier();
        await KilledAsync(host, () => host.Created == 1, StepKey.PullRequest(Batch, "run1"), verifier);
        host.HangAfterCreate = false;

        var (outcome, events) = await HandOverAsync(host, verifier);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        // The verification finished before the cut: its recorded verdict stands, and the branch is not verified again.
        Assert.Single(verifier.Calls);
        Assert.Equal(0, outcome.Verification!.ExitCode);
        Assert.Equal(1, host.Created);
        Assert.Equal([(1, "chargehand/run1")], host.Open);
        Assert.Equal(1, outcome.PullRequest!.Number);
        Assert.Equal(outcome.PullRequest.Url, events.Single(e => e.Kind == RunEventKind.PrOpened).PrUrl);
    }

    /// <summary>Hangs in its first verification until it is cancelled; answers red, then green, after that.</summary>
    private sealed class HangingVerifier : IBranchVerifier
    {
        public int Calls { get; private set; }

        public async Task<VerificationRecord> VerifyAsync(BranchVerification v, CancellationToken ct)
        {
            Calls++;
            if (Calls == 1)
                await Task.Delay(Timeout.Infinite, ct);
            return new VerificationRecord(v.Argv, "detected", Calls == 2 ? 1 : 0, "tests ran", false, TimeSpan.FromSeconds(3), true);
        }
    }

    [Fact]
    public async Task A_verification_cut_short_runs_again_after_a_restart_and_a_red_verdict_recorded_then_stands()
    {
        var host = new Host();
        var verifier = new HangingVerifier();
        await KilledAsync(host, () => verifier.Calls == 1, StepKey.Verify(Batch, "run1"), verifier);

        var (red, _) = await HandOverAsync(host, verifier);
        var (again, _) = await HandOverAsync(host, verifier);

        Assert.Equal(HandoverStatus.NotPushed, red.Status);
        Assert.Equal(ErrorCode.VerificationFailed, red.Error);
        Assert.Equal((HandoverStatus.NotPushed, 1), (again.Status, again.Verification!.ExitCode));
        Assert.Equal(2, verifier.Calls);
        Assert.Equal("", _f.RemoteRef("refs/heads/chargehand/run1"));
        Assert.Equal(0, host.Created);
    }

    [Fact]
    public async Task A_finished_handover_run_again_neither_reaches_the_remote_nor_opens_a_second_pull_request()
    {
        var host = new Host();
        var verifier = new HandoverTests.FakeVerifier();
        var (first, _) = await HandOverAsync(host, verifier);
        // Gone: a push or a look at the remote would now fail the handover.
        Directory.Delete(_f.Remote, recursive: true);

        var (again, events) = await HandOverAsync(host, verifier);

        Assert.Equal(HandoverStatus.Pushed, again.Status);
        Assert.Single(verifier.Calls);
        // The verdict comes back as recorded (a list compares by reference, so the members are compared).
        Assert.Equal(first.Verification!.Argv, again.Verification!.Argv);
        Assert.Equal((first.Verification.ExitCode, first.Verification.Source, first.Verification.OutputTail, first.Verification.Duration),
            (again.Verification.ExitCode, again.Verification.Source, again.Verification.OutputTail, again.Verification.Duration));
        Assert.Equal((first.Commit, first.PullRequest), (again.Commit, again.PullRequest));
        Assert.Equal(1, host.Created);
        Assert.Contains(events, e => e.Kind == RunEventKind.Pushed);
    }
}
