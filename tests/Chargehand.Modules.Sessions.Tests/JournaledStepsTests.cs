using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Sessions.Tests;

/// <summary>ADR 0061, first slice: the workspace helper and the writes into an output volume are recorded steps. "Killed" is a call cancelled while its helper runs, with the daemon unreachable so
/// the engine's own cleanup cannot remove the helper, and the journal closed; "restarted" is a journal opened again on the same file and the same (fake) daemon.</summary>
public sealed class JournaledStepsTests : IDisposable
{
    private const string Batch = "batch-1";
    private const string Image = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static readonly WorkspaceSpec Workspace = new("run1", Image, "/srv/checkouts/r", "chargehand-work-run1", "chargehand/run1", "abc1234");

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Stale => Path.Combine(_dir.Path, "stale");

    private string Hang => Path.Combine(_dir.Path, "hang");

    private string Down => Path.Combine(_dir.Path, "down");

    private string Log => Path.Combine(_dir.Path, "calls.log");

    private string State => Path.Combine(_dir.Path, "batches");

    /// <summary>A <c>docker</c> that keeps one helper container under the run's name: <c>run</c> makes it first and removes it when it ends (<c>--rm</c>), so a call killed in between leaves it
    /// created. Hang: the helper does not end. Down: the daemon refuses <c>rm</c>.</summary>
    private DockerCliEngine Daemon(string helper)
    {
        var conflict = $"docker: Error response from daemon: Conflict. The container name \"/chargehand-{helper}-run1\" is already in use by container \"124189f9\". You have to remove (or rename) that container to be able to reuse that name.";
        var script = _dir.Write("docker", $$"""
            #!/bin/sh
            if [ "$1" = run ]; then echo run >> '{{Log}}'; else printf '%s\n' "$*" >> '{{Log}}'; fi
            case "$1" in
              run)
                if [ -e '{{Stale}}' ]; then echo '{{conflict}}' >&2; exit 125; fi
                : > '{{Stale}}'
                if [ -e '{{Hang}}' ]; then sleep 30; fi
                cat > /dev/null
                rm -f '{{Stale}}';;
              inspect) [ -e '{{Stale}}' ] && echo run1;;
              rm) if [ -e '{{Down}}' ]; then echo 'Cannot connect to the Docker daemon' >&2; exit 1; fi; rm -f '{{Stale}}';;
            esac
            """);
        return new DockerCliEngine("/bin/sh", [script], retryEvery: TimeSpan.Zero);
    }

    private string[] Calls() => File.Exists(Log) ? File.ReadAllLines(Log) : [];

    private void Kill()
    {
        File.WriteAllText(Hang, "");
        File.WriteAllText(Down, "");
    }

    private void Recover()
    {
        File.Delete(Hang);
        File.Delete(Down);
    }

    private static CancellationTokenSource KilledAfterHalfASecond() => new(TimeSpan.FromMilliseconds(500));

    /// <summary>Cancelled once the hanging helper exists under the run's name, however long the calls before it take on a loaded machine.</summary>
    private CancellationTokenSource KilledOnceTheHelperIsMade()
    {
        var cut = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = Task.Run(async () =>
        {
            using var watch = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (!File.Exists(Stale) && await watch.WaitForNextTickAsync(cut.Token))
            {
            }
            await cut.CancelAsync();
        });
        return cut;
    }

    [Fact]
    public async Task A_workspace_cut_short_between_intent_and_result_is_cleared_and_prepared_once_after_a_restart()
    {
        var engine = Daemon("prep");
        Kill();
        using (var journal = await DrivenJournal.OpenAsync(State))
        {
            using var cut = KilledOnceTheHelperIsMade();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new JournaledWorkspaceEngine(engine, engine, journal, Batch).PrepareWorkspaceAsync(Workspace, cut.Token));
            Assert.Equal([StepKey.Prep(Batch, "run1")], await journal.UnfinishedAsync());
        }
        Assert.True(File.Exists(Stale), "the kill leaves the helper created under the run's name");
        Recover();
        var before = Calls().Length;

        using (var journal = await DrivenJournal.OpenAsync(State))
        {
            await new JournaledWorkspaceEngine(engine, engine, journal, Batch).PrepareWorkspaceAsync(Workspace, default);
            Assert.Empty(await journal.UnfinishedAsync());
        }

        Assert.False(File.Exists(Stale), "the engine removed the stopped helper before it ran the new one");
        var resumed = Calls()[before..];
        // The part-filled volume goes first; then the helper meets its own leftover, and the engine removes that (without -f: a running helper is another call) and runs it again.
        Assert.Equal("volume rm -f chargehand-work-run1", resumed[0]);
        Assert.Equal(["volume", "volume", "run", "inspect", "rm", "run"], resumed.Select(c => c.Split(' ')[0]));
        Assert.Contains("rm chargehand-prep-run1", resumed);

        var done = Calls().Length;
        using (var journal = await DrivenJournal.OpenAsync(State))
            await new JournaledWorkspaceEngine(engine, engine, journal, Batch).PrepareWorkspaceAsync(Workspace, default);
        Assert.Equal(done, Calls().Length);
    }

    [Fact]
    public async Task A_write_cut_short_between_intent_and_result_leaves_no_helper_after_a_restart_and_a_finished_one_is_not_repeated()
    {
        var engine = Daemon("xfer");
        var task = new SessionTask("run1", "Fix the parser", "chargehand/run1", 40, 30, 600, 500_000);
        ISessionVolumes Volumes(DrivenJournal journal) => new JournaledSessionVolumes(new EngineSessionVolumes(engine, Image), journal, Batch);
        Kill();
        using (var journal = await DrivenJournal.OpenAsync(State))
        {
            using var cut = KilledOnceTheHelperIsMade();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Volumes(journal).WriteTaskAsync("run1", task, cut.Token));
            Assert.Equal([StepKey.Transfer(Batch, "run1", "task")], await journal.UnfinishedAsync());
        }
        Assert.True(File.Exists(Stale));
        Recover();
        var before = Calls().Length;

        using (var journal = await DrivenJournal.OpenAsync(State))
        {
            await Volumes(journal).WriteTaskAsync("run1", task, default);
            Assert.Empty(await journal.UnfinishedAsync());
        }

        Assert.False(File.Exists(Stale));
        Assert.Equal(["run", "inspect", "rm", "run"], Calls()[before..].Select(c => c.Split(' ')[0]));
        Assert.Contains("rm chargehand-xfer-run1", Calls()[before..]);

        var done = Calls().Length;
        using (var journal = await DrivenJournal.OpenAsync(State))
            await Volumes(journal).WriteTaskAsync("run1", task, default);
        Assert.Equal(done, Calls().Length);
    }

    [Fact]
    public async Task The_task_file_and_the_seed_are_separate_steps_and_reads_always_go_to_the_engine()
    {
        var engine = Daemon("xfer");
        var bundle = _dir.Write("seed.bundle", "bundle");
        var task = new SessionTask("run1", "Fix the parser", "chargehand/run1", 40, 30, 600, 500_000);
        using var journal = await DrivenJournal.OpenAsync(State);
        var volumes = new JournaledSessionVolumes(new EngineSessionVolumes(engine, Image), journal, Batch);

        await volumes.WriteTaskAsync("run1", task, default);
        await volumes.WriteSeedAsync("run1", bundle, default);
        await volumes.WriteTaskAsync("run1", task, default);
        await volumes.WriteSeedAsync("run1", bundle, default);
        Assert.Equal(2, Calls().Count(c => c == "run"));

        // A read lands in a directory this process owns, which a restart loses: it is no step.
        await volumes.ReadUsageAsync("run1", default);
        await volumes.ReadUsageAsync("run1", default);
        Assert.Equal(4, Calls().Count(c => c == "run"));
    }

    [Fact]
    public async Task A_prep_that_failed_is_prepared_again_and_each_attempt_clears_the_volume_first()
    {
        var engine = Daemon("prep");
        using var journal = await DrivenJournal.OpenAsync(State);
        var failing = new Failing();
        var workspace = new JournaledWorkspaceEngine(failing, engine, journal, Batch);

        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.PrepareWorkspaceAsync(Workspace, default));
        Assert.Empty(await journal.UnfinishedAsync());
        failing.Fail = false;
        await workspace.PrepareWorkspaceAsync(Workspace, default);

        Assert.Equal(2, failing.Calls);
        Assert.Equal(["volume rm -f chargehand-work-run1", "volume rm -f chargehand-work-run1"], Calls());
    }

    [Fact]
    public async Task The_verifications_workspace_is_prepared_again_for_each_verification_of_a_run_and_one_cut_short_runs_again_resumed()
    {
        var inner = new Failing { Fail = false };
        using (var journal = await DrivenJournal.OpenAsync(State))
        {
            var workspace = new JournaledVerifierWorkspace(inner, journal, Batch);
            await workspace.PrepareWorkspaceAsync(Workspace, default);
            await workspace.PrepareWorkspaceAsync(Workspace, default);
            Assert.Equal(2, inner.Calls);

            inner.Hang = true;
            using var cut = KilledAfterHalfASecond();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.PrepareWorkspaceAsync(Workspace, cut.Token));
            Assert.Equal([StepKey.VerifyPrep(Batch, "run1")], await journal.UnfinishedAsync());
        }

        inner.Hang = false;
        using (var journal = await DrivenJournal.OpenAsync(State))
        {
            await new JournaledVerifierWorkspace(inner, journal, Batch).PrepareWorkspaceAsync(Workspace, default);
            Assert.Empty(await journal.UnfinishedAsync());
            // The session's own workspace is another key: it is not recorded by a verification.
            var sessionPrep = 0;
            await journal.RunAsync(StepKey.Prep(Batch, "run1"), (_, _) =>
            {
                sessionPrep++;
                return Task.CompletedTask;
            });
            Assert.Equal(1, sessionPrep);
        }
        Assert.Equal(4, inner.Calls);
    }

    private sealed class Failing : IWorkspaceEngine
    {
        public bool Fail { get; set; } = true;

        /// <summary>The prep does not end until it is cancelled.</summary>
        public bool Hang { get; set; }

        public int Calls { get; private set; }

        public async Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct)
        {
            Calls++;
            if (Hang)
                await Task.Delay(Timeout.Infinite, ct);
            if (Fail)
                throw new InvalidOperationException("the engine blinked");
        }
    }
}
