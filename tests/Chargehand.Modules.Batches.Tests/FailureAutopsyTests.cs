using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Batches.Tests;

/// <summary>ADR 0045: the failure autopsy of one task attempt, with a fake orchestrator. The autopsy step in a batch is tested in the old suite's <c>LearningTests</c>.</summary>
public class FailureAutopsyTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string BatchId = "run-batch-l";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static RunRequest Request() =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "one")], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static LearningRecord Record(string outcome = LearningOutcome.Failed, string? stopReason = "turn_limit", bool checkpoint = false) =>
        new(BatchId, "task-r1", "r1", Now.AddHours(-1), "o/r", "M", "caller", new LearningBudget(4m, new BudgetSource("flat")),
            new LearningSpend(2m, false, 10, 5, 0, 0, 3, 0), outcome, stopReason, "observe",
            checkpoint ? [new GovernorCount("spend", "checkpoint", 1, 0)] : [], false);

    private static ResultContract Answer(ResultStatus status, string summary) =>
        new("result/v1", "run-autopsy", "answer", "trace", new PromptChain([], new AsSent("2.0.16", "build", "m", "2026-10-13")), status, summary, [], [], [], [], 0.9, new Usage(0, 0, 0, 0, 0),
            status == ResultStatus.Failed ? new ResultError(ErrorCode.CheckoutInvalid, "the checkout is invalid", false) : null);

    [Fact]
    public async Task The_model_autopsy_asks_a_read_only_run_with_the_record_and_no_goal_and_reads_the_first_line_as_the_class()
    {
        List<(RunRequest Request, string RunId, string Parent)> asked = [];
        var autopsy = new ModelAutopsy((request, runId, parent, ct) =>
        {
            asked.Add((request, runId, parent));
            return Task.FromResult(Answer(ResultStatus.Completed, "turn_limit_no_commits\nThe session hit its turn limit without a commit."));
        }, "cheap");
        var record = Record(checkpoint: true);

        var answer = await autopsy.WriteAsync(new AutopsyInput(Request(), record.RunId, record), default);

        var (request, runId, parent) = Assert.Single(asked);
        Assert.Equal(("cheap", record.RunId), (request.Context.Preset, parent));
        Assert.NotEqual(record.RunId, runId);
        Assert.Equal("/srv/repo", request.Context.Repository!.Path);
        Assert.Contains("\"outcome\":\"failed\"", request.Text);
        Assert.Contains("\"stop_reason\":\"turn_limit\"", request.Text);
        Assert.Contains("\"governor\":[{\"signal\":\"spend\",\"action\":\"checkpoint\",\"count\":1,\"acted\":0}]", request.Text);
        Assert.Contains("do not read the repository and do not change any file", request.Text);
        Assert.Equal((AutopsyClass.TurnLimitNoCommits, "The session hit its turn limit without a commit."), (answer.Class, answer.Detail));
    }

    [Fact]
    public async Task A_word_outside_the_list_is_kept_as_other_and_a_one_line_answer_has_no_detail()
    {
        var autopsy = new ModelAutopsy((_, _, _, _) => Task.FromResult(Answer(ResultStatus.Completed, "mystery_cause")), "cheap");

        var answer = await autopsy.WriteAsync(new AutopsyInput(Request(), "r1", Record()), default);

        Assert.Equal((AutopsyClass.Other, null), (answer.Class, answer.Detail));
    }

    [Theory]
    [InlineData(ResultStatus.Failed, "something")]
    [InlineData(ResultStatus.Completed, "   ")]
    public async Task The_model_autopsy_refuses_a_run_that_did_not_complete_or_printed_nothing(ResultStatus status, string summary)
    {
        var autopsy = new ModelAutopsy((_, _, _, _) => Task.FromResult(Answer(status, summary)), "cheap");

        await Assert.ThrowsAsync<AutopsyException>(() => autopsy.WriteAsync(new AutopsyInput(Request(), "r1", Record()), default));
    }
}
