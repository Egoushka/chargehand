using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0045: a task that ended with a question, or with work kept, is continued by a task of a later batch (<c>continue_from</c>, <c>answers</c>) on the same branch, from the kept bundle.
/// The sessions are scripted by the fake output volume; nothing starts a container.</summary>
public class DrivenAnswersTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string First = "run-batch-q";
    private const string Second = "run-batch-a";

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest Request(DrivenTask task) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([task], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static RunRequest Ask(string goal = "Add a retry") => Request(new DrivenTask("t1", Goal: goal));

    private static RunRequest Answer(params string[] answers) =>
        Request(new DrivenTask("t1", Answers: answers.Length == 0 ? null : answers, ContinueFrom: new ContinueFrom(First, "t1")));

    private static SessionOutcome Asked(int commits) =>
        new(SessionStatus.NeedsInput, "", 5, 1000, 200, 0, null, 0, ["Which database?", "Keep the old API?"], commits > 0, Commits: commits);

    private static SessionOutcome Done(int commits = 4) =>
        new(SessionStatus.Completed, "", 30, 1000, 200, 4000, null, 0, [], true, Commits: commits);

    private static SessionOutcome Stopped(int commits = 3) =>
        new(SessionStatus.Stalled, "no_progress", 60, 1000, 200, 4000, null, 0, [], true, Commits: commits);

    /// <summary>The first batch's sessions ask (after <paramref name="commits"/> commits); any session whose prompt carries the answers, or says the earlier one was stopped, finishes.</summary>
    private static Func<string, string, SessionOutcome?> Script(int commits = 2, Func<int, SessionOutcome>? first = null) =>
        (_, goal) => goal.Contains("The answers from the person", StringComparison.Ordinal) || goal.Contains("was stopped before it finished", StringComparison.Ordinal)
            ? Done()
            : (first ?? Asked)(commits);

    private static JsonElement Row(ResultContract result) =>
        JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks").EnumerateArray().Single();

    private static async Task<(string Run, JsonElement Kept)> KeptWork(DrivenRunTests.Rig rig, ResultContract batch)
    {
        var run = Row(batch).GetProperty("run_id").GetString()!;
        var artifact = (await rig.Log.ReadAsync(run, default)).Run!.Result.Artifacts.Single(a => a.Kind == "kept-work");
        return (run, JsonDocument.Parse(artifact.Content!).RootElement);
    }

    [Fact]
    public async Task A_question_is_a_run_event_with_the_questions_and_the_task_keeps_its_work_for_an_answer()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: Script());
        List<RunStatus> events = [];
        var result = await rig.Run.RunAsync(Ask(), First, Token, default, events.Add);

        var question = Assert.Single(events, e => e.Event == RunEventKind.Question);
        Assert.Equal("t1", question.TaskId);
        Assert.Equal("task t1: the session asked:\n- Which database?\n- Keep the old API?", question.Detail);
        Assert.Equal("needs_input", Row(result).GetProperty("status").GetString());
        var (run, kept) = await KeptWork(rig, result);
        var started = rig.Engine.Started.Single().RunId;
        Assert.Equal(($"chargehand/{started}", started, 2, Commit), (kept.GetProperty("branch").GetString(), kept.GetProperty("bundle_run").GetString(), kept.GetProperty("commits").GetInt32(), kept.GetProperty("base").GetString()));
        Assert.Equal(started, run);
        // The output volume is not removed with the work volume; it holds the bundle the answer starts from.
        Assert.DoesNotContain($"volume-rm chargehand-out-{started}", rig.Engine.Calls);
    }

    [Fact]
    public async Task A_question_with_no_commits_is_an_event_too_and_keeps_no_work()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: Script(commits: 0));
        List<RunStatus> events = [];
        var result = await rig.Run.RunAsync(Ask(), First, Token, default, events.Add);

        Assert.Single(events, e => e.Event == RunEventKind.Question);
        Assert.DoesNotContain((await rig.Log.ReadAsync(Row(result).GetProperty("run_id").GetString()!, default)).Run!.Result.Artifacts, a => a.Kind == "kept-work");
    }

    [Fact]
    public async Task A_task_continued_with_answers_starts_from_the_kept_bundle_on_the_same_branch_and_its_goal_carries_the_questions_and_answers()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: Script());
        rig.Volumes.Progress = (_, _) => "Done: the retry loop. Next: pick the database.";
        await rig.Run.RunAsync(Ask(), First, Token, default);
        var firstRun = rig.Engine.Started[0].RunId;

        var result = await rig.Run.RunAsync(Answer("Postgres", "Yes, keep it"), Second, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var secondRun = rig.Engine.Started[1].RunId;
        Assert.Equal(secondRun, Assert.Single(rig.Volumes.Seeds).RunId);
        Assert.EndsWith(Path.Combine("out", firstRun, "chargehand.bundle"), rig.Volumes.Seeds[0].Path);
        var task = rig.Volumes.Tasks[1];
        Assert.Equal($"chargehand/{firstRun}", task.Branch);
        Assert.StartsWith("Add a retry\n\nAn earlier session of this task stopped to ask:\n- Which database?\n- Keep the old API?\n\nThe answers from the person who sent the task:\n- Postgres\n- Yes, keep it\n\n", task.Goal);
        Assert.Contains($"2 commit(s) past {Commit}", task.Goal);
        Assert.Contains($"git diff {Commit}..HEAD", task.Goal);
        Assert.EndsWith("The handoff the earlier session left:\n\nDone: the retry loop. Next: pick the database.", task.Goal);
        // The pull request comes from the new session, on the earlier branch; its title is the original goal's.
        var handover = Assert.Single(rig.Handover.Inputs, i => i.RunId == secondRun);
        Assert.Equal(($"chargehand/{firstRun}", "Add a retry", false), (handover.Branch, handover.Title, handover.Partial));
        Assert.Contains(rig.Engine.Calls, c => c == $"prepare {secondRun} chargehand/{firstRun} {Commit}");
        // The record of the new task carries the goal with the questions and answers, so a task that asks again continues from all of it.
        var record = await rig.Log.ReadAsync(Row(result).GetProperty("run_id").GetString()!, default);
        Assert.Contains("- Postgres", record.Start!.Request.Text);
        Assert.DoesNotContain("The handoff", record.Start.Request.Text);
    }

    [Fact]
    public async Task A_question_with_no_commits_continues_from_the_base_commit_on_a_branch_of_its_own()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: Script(commits: 0));
        await rig.Run.RunAsync(Ask(), First, Token, default);
        var firstRun = rig.Engine.Started[0].RunId;

        var result = await rig.Run.RunAsync(Answer("Postgres"), Second, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(rig.Volumes.Seeds);
        var secondRun = rig.Engine.Started[1].RunId;
        Assert.Equal($"chargehand/{secondRun}", rig.Volumes.Tasks[1].Branch);
        Assert.NotEqual($"chargehand/{firstRun}", rig.Volumes.Tasks[1].Branch);
        Assert.Contains("It made no commits: start from the base commit.", rig.Volumes.Tasks[1].Goal);
    }

    [Fact]
    public async Task A_partial_task_continues_without_answers_from_the_bundle_it_kept()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: Script(first: _ => Stopped()));
        var partial = await rig.Run.RunAsync(Ask(), First, Token, default);
        Assert.Equal("failed", Row(partial).GetProperty("status").GetString());
        var (_, kept) = await KeptWork(rig, partial);

        var result = await rig.Run.RunAsync(Answer(), Second, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.EndsWith(Path.Combine("out", kept.GetProperty("bundle_run").GetString()!, "chargehand.bundle"), Assert.Single(rig.Volumes.Seeds).Path);
        var goal = rig.Volumes.Tasks[^1].Goal;
        Assert.StartsWith("Add a retry\n\nAn earlier session of this task was stopped before it finished.\n\nIts work so far is on this branch, 3 commit(s)", goal);
        Assert.DoesNotContain("The answers", goal);
        Assert.Equal(kept.GetProperty("branch").GetString(), rig.Volumes.Tasks[^1].Branch);
    }

    [Fact]
    public async Task A_continued_task_that_adds_no_commit_and_is_stopped_does_not_continue_again()
    {
        // The new session ends on the turn limit with only the commits it was seeded with: a further session would repeat it.
        using var rig = new DrivenRunTests.Rig(outcomes: (_, goal) => goal.Contains("The answers from the person", StringComparison.Ordinal) ? new SessionOutcome(SessionStatus.Stalled, "turn_limit", 80, 1000, 200, 4000, null, 0, [], true, Commits: 2) : Asked(2));
        await rig.Run.RunAsync(Ask(), First, Token, default);

        await rig.Run.RunAsync(Answer("Postgres"), Second, Token, default);

        Assert.Equal(2, rig.Engine.Started.Count);
        // The work it was seeded with is handed over as a partial pull request, as any stop is.
        Assert.True(rig.Handover.Inputs[^1].Partial);
    }

    [Fact]
    public async Task A_second_question_after_an_answer_is_continued_with_the_first_round_still_in_the_goal()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, goal) =>
            goal.Contains("- MySQL", StringComparison.Ordinal) ? Done() : Asked(2));
        await rig.Run.RunAsync(Ask(), First, Token, default);
        await rig.Run.RunAsync(Answer("Postgres"), Second, Token, default);   // asks again

        await rig.Run.RunAsync(Request(new DrivenTask("t1", Answers: ["MySQL"], ContinueFrom: new ContinueFrom(Second, "t1"))), "run-batch-b", Token, default);

        var goal = rig.Volumes.Tasks[^1].Goal;
        Assert.StartsWith("Add a retry\n\nAn earlier session of this task stopped to ask:", goal);
        Assert.True(goal.IndexOf("- Postgres", StringComparison.Ordinal) is > 0 && goal.IndexOf("- Postgres", StringComparison.Ordinal) < goal.IndexOf("- MySQL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_continuation_whose_bundle_is_gone_fails_the_task_with_a_clear_error_and_starts_no_session()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: Script());
        await rig.Run.RunAsync(Ask(), First, Token, default);
        rig.Volumes.NoOutput.Add(rig.Engine.Started[0].RunId);

        var result = await rig.Run.RunAsync(Answer("Postgres"), Second, Token, default);

        Assert.Single(rig.Engine.Started);
        var row = Row(result);
        Assert.Equal(("failed", "invalid_request"), (row.GetProperty("status").GetString(), row.GetProperty("error_code").GetString()));
        Assert.Contains("is gone", row.GetProperty("detail").GetString());
        Assert.Contains("7 days", row.GetProperty("detail").GetString());
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "no-such-run", "is not a finished driven batch in the run log" },
        { "no-such-task", "has no such task" },
        { "failed", "ended failed and kept no work" },
        { "no-answers", "ended needs_input: give answers" },
        { "moved", "started at commit" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_reference_that_cannot_be_continued_is_refused_before_anything_starts(string case_, string message)
    {
        using var rig = new DrivenRunTests.Rig(outcomes: Script(first: _ => case_ == "failed" ? new SessionOutcome(SessionStatus.Failed, "session_error", 0, 0, 0, 0, null, null, [], false) : Asked(2)));
        var earlier = await rig.Run.RunAsync(Ask(), First, Token, default);
        if (case_ == "moved")
        {
            var (run, kept) = await KeptWork(rig, earlier);
            var record = (await rig.Log.ReadAsync(run, default)).Run!;
            await rig.Log.AppendAsync(record with
            {
                Result = record.Result with
                {
                    Artifacts = [.. record.Result.Artifacts.Where(a => a.Kind != "kept-work"), new Artifact("kept-work", "application/json", "x", Content: kept.GetRawText().Replace(Commit, "0000000", StringComparison.Ordinal))]
                }
            }, default);
        }
        var started = rig.Engine.Started.Count;
        var from = case_ switch { "no-such-run" => new ContinueFrom("run-nothing", "t1"), "no-such-task" => new ContinueFrom(First, "t9"), _ => new ContinueFrom(First, "t1") };
        var answers = case_ == "no-answers" ? null : (IReadOnlyList<string>)["Postgres"];

        var result = await rig.Run.RunAsync(Request(new DrivenTask("t1", Answers: answers, ContinueFrom: from)), Second, Token, default);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Contains(message, result.Error.Message);
        Assert.Equal(started, rig.Engine.Started.Count);
        Assert.Empty(rig.Volumes.Seeds);
    }

    [Fact]
    public async Task Answers_without_continue_from_are_refused_with_the_rule_text()
    {
        using var rig = new DrivenRunTests.Rig();
        var result = await rig.Run.RunAsync(Request(new DrivenTask("t1", Goal: "g", Answers: ["Postgres"])), Second, Token, default);

        Assert.Equal(ErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Contains("task 't1': answers need continue_from", result.Error.Message);
        Assert.Empty(rig.Engine.Started);
    }

    [Fact]
    public async Task The_sweep_keeps_the_output_volumes_of_a_task_with_kept_work_for_a_week_and_then_removes_them()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: Script());
        var result = await rig.Run.RunAsync(Ask(), First, Token, default);
        var (run, kept) = await KeptWork(rig, result);
        rig.Engine.Resources = new([], [], [$"chargehand-out-{run}", $"chargehand-work-{run}", "chargehand-out-run-x"]);

        var removed = await rig.Run.SweepOrphansAsync([], default);

        Assert.Equal([$"chargehand-work-{run}", "chargehand-out-run-x"], removed);
        Assert.Equal(run, kept.GetProperty("bundle_run").GetString());

        var record = (await rig.Log.ReadAsync(run, default)).Run!;
        await rig.Log.AppendAsync(record with { Finished = DateTimeOffset.UtcNow - ContinuationSource.KeptFor - TimeSpan.FromHours(1) }, default);
        Assert.Equal([$"chargehand-out-{run}", $"chargehand-work-{run}", "chargehand-out-run-x"], await rig.Run.SweepOrphansAsync([], default));
    }

    [Fact]
    public async Task The_sweep_also_keeps_the_volume_of_an_earlier_session_that_holds_the_bundle()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: Script());
        var result = await rig.Run.RunAsync(Ask(), First, Token, default);
        var (run, _) = await KeptWork(rig, result);
        // The task's last session left no bundle of its own; an earlier one did.
        var record = (await rig.Log.ReadAsync(run, default)).Run!;
        var work = record.Result.Artifacts.Single(a => a.Kind == "kept-work");
        await rig.Log.AppendAsync(record with
        {
            Result = record.Result with { Artifacts = [.. record.Result.Artifacts.Where(a => a != work), work with { Content = work.Content!.Replace(run, "run-earlier", StringComparison.Ordinal) }] }
        }, default);
        rig.Engine.Resources = new([], [], [$"chargehand-out-{run}", "chargehand-out-run-earlier", "chargehand-out-run-x"]);

        Assert.Equal(["chargehand-out-run-x"], await rig.Run.SweepOrphansAsync([], default));
    }

    [Fact]
    public async Task A_restart_before_the_continued_tasks_container_started_starts_it_again_under_its_run_id_from_the_kept_bundle()
    {
        using var rig = new DrivenRunTests.Rig(durable: true, outcomes: Script());
        await rig.Run.RunAsync(Ask(), First, Token, default);
        var firstRun = rig.Engine.Started[0].RunId;
        using var stop = new CancellationTokenSource();
        // The server stops as the answering task's container is being started: its journal entry names the run, but no container.
        rig.Engine.OnStart = n => n == 2 ? Stopping(stop) : null;
        await rig.Run.RunAsync(Answer("Postgres"), Second, Token, stop.Token, null, () => false);
        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));
        var entry = state.Tasks.Single();
        Assert.Equal($"chargehand/{firstRun}", entry.Branch);
        Assert.Equal(firstRun, entry.From!.Kept!.BundleRun);
        Assert.Contains("- Postgres", entry.Goal);
        Assert.Equal(firstRun, entry.Best!.RunId);
        var lost = rig.Engine.Started[1].RunId;
        rig.Engine.Gone.Add($"chargehand-{lost}");
        rig.Engine.OnStart = null;

        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        // The start runs again under the same run id (ADR 0061); the seed it wrote before the cut is not written again.
        Assert.Equal([firstRun, lost, lost], rig.Engine.Started.Select(s => s.RunId));
        var seed = Assert.Single(rig.Volumes.Seeds);
        Assert.Equal(lost, seed.RunId);
        Assert.EndsWith(Path.Combine("out", firstRun, "chargehand.bundle"), seed.Path);
        Assert.Equal($"chargehand/{firstRun}", rig.Volumes.Tasks[^1].Branch);
        Assert.Contains("- Postgres", rig.Volumes.Tasks[^1].Goal);
        Assert.Equal($"chargehand/{firstRun}", rig.Handover.Inputs[^1].Branch);
    }

    private static OperationCanceledException Stopping(CancellationTokenSource stop)
    {
        stop.Cancel();
        return new OperationCanceledException(stop.Token);
    }
}
