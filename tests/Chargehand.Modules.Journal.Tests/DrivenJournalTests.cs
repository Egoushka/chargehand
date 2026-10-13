using System.Text.Json.Serialization;
using Chargehand.Modules.Journal.Api;

namespace Chargehand.Modules.Journal.Tests;

public sealed record Branch(string Name, string Commit);

[JsonSerializable(typeof(Branch))]
internal sealed partial class TestJson : JsonSerializerContext;

public sealed class DrivenJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Directory.CreateTempSubdirectory("chargehand-journal-").FullName, "batches");

    private static readonly StepKey Key = StepKey.Prep("batch-1", "run-1");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_directory)!, recursive: true);

    [Fact]
    public void Keys_are_built_from_saved_ids_only()
    {
        Assert.Equal(new StepKey("b", "prep/r"), StepKey.Prep("b", "r"));
        Assert.Equal(new StepKey("b", "xfer/r/task"), StepKey.Transfer("b", "r", "task"));
        Assert.Equal(new StepKey("b", "session/t1/r"), StepKey.Session("b", "t1", "r"));
    }

    [Fact]
    public async Task The_file_sits_in_the_state_directory_which_is_made_when_missing()
    {
        await using (await DrivenJournal.OpenAsync(_directory))
        {
        }

        Assert.True(File.Exists(Path.Combine(_directory, DrivenJournal.FileName)));
    }

    [Fact]
    public async Task A_completed_step_does_not_run_again_not_even_after_a_restart()
    {
        var runs = 0;
        Task Step(StepAttempt attempt, CancellationToken ct)
        {
            runs++;
            return Task.CompletedTask;
        }

        await using (var first = await DrivenJournal.OpenAsync(_directory))
            await first.RunAsync(Key, Step);
        await using (var second = await DrivenJournal.OpenAsync(_directory))
            await second.RunAsync(Key, Step);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_result_comes_back_as_recorded_through_the_given_type_info()
    {
        var runs = 0;
        Task<Branch> Step(StepAttempt attempt, CancellationToken ct)
        {
            runs++;
            return Task.FromResult(new Branch("chargehand/run-1", "abc123"));
        }

        var handover = new StepKey("batch-1", "handover/task-1");
        await using (var first = await DrivenJournal.OpenAsync(_directory))
            Assert.Equal(new Branch("chargehand/run-1", "abc123"), await first.RunAsync(handover, Step, TestJson.Default.Branch));
        await using (var second = await DrivenJournal.OpenAsync(_directory))
            Assert.Equal(new Branch("chargehand/run-1", "abc123"), await second.RunAsync(handover, Step, TestJson.Default.Branch));

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_step_cut_short_is_listed_and_runs_again_resumed_after_a_restart()
    {
        using var cut = new CancellationTokenSource();
        await using (var first = await DrivenJournal.OpenAsync(_directory))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(Key, async (_, ct) =>
            {
                await cut.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }, cut.Token));
            Assert.Equal([Key], await first.UnfinishedAsync());
        }

        List<StepAttempt> attempts = [];
        await using (var second = await DrivenJournal.OpenAsync(_directory))
        {
            Assert.Equal([Key], await second.UnfinishedAsync("batch-1"));
            await second.RunAsync(Key, (attempt, _) =>
            {
                attempts.Add(attempt);
                return Task.CompletedTask;
            });
            Assert.Empty(await second.UnfinishedAsync());
        }

        Assert.Equal([new StepAttempt(2, Resumed: true)], attempts);
    }

    [Fact]
    public async Task A_failed_step_is_rethrown_and_its_next_run_is_a_new_attempt_not_a_resumed_one()
    {
        await using var journal = await DrivenJournal.OpenAsync(_directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RunAsync(Key, (_, _) => throw new InvalidOperationException("docker is down")));
        Assert.Empty(await journal.UnfinishedAsync());

        StepAttempt? seen = null;
        await journal.RunAsync(Key, (attempt, _) =>
        {
            seen = attempt;
            return Task.CompletedTask;
        });

        Assert.Equal(new StepAttempt(2, Resumed: false), seen);
        Assert.True(seen!.Value.Repeated);
    }

    [Fact]
    public async Task Forgetting_a_batch_removes_its_steps_and_no_other_batch_s()
    {
        var other = StepKey.Prep("batch-2", "run-2");
        var runs = 0;
        Task Step(StepAttempt attempt, CancellationToken ct)
        {
            runs++;
            return Task.CompletedTask;
        }

        await using var journal = await DrivenJournal.OpenAsync(_directory);
        await journal.RunAsync(Key, Step);
        await journal.RunAsync(other, Step);
        await journal.ForgetAsync("batch-1");
        await journal.RunAsync(Key, Step);
        await journal.RunAsync(other, Step);

        Assert.Equal(3, runs);
    }
}
