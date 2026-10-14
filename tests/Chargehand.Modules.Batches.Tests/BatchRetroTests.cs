using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Batches.Tests;

/// <summary>ADR 0045: the model retro after a batch, with a fake orchestrator. The retro step in a batch is tested in the old suite's <c>LearningTests</c>.</summary>
public class BatchRetroTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string BatchId = "run-batch-l";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static RunRequest Request(params DrivenTask[] tasks) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([.. tasks], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static RunRequest One(DrivenTask? task = null) => Request(task ?? new DrivenTask("t1", Goal: "one"));

    internal static LearningRecord Record(string run = "r1", string repository = "o/r", string size = "M", decimal? usd = 1m, string outcome = LearningOutcome.Completed, bool checkpoint = false,
        DateTimeOffset? at = null, LearningPr? pr = null) =>
        new(BatchId, $"task-{run}", run, at ?? Now.AddHours(-1), repository, size, "caller", new LearningBudget(4m, new BudgetSource("flat")),
            new LearningSpend(usd, false, 10, 5, 0, 0, 3, 0), outcome, outcome == LearningOutcome.Completed ? "completed" : "turn_limit", "observe",
            checkpoint ? [new GovernorCount("spend", "checkpoint", 1, 0)] : [], false, pr);

    private static ResultContract Answer(ResultStatus status, string summary) =>
        new("result/v1", "run-retro", "answer", "trace", new PromptChain([], new AsSent("2.0.16", "build", "m", "2026-10-13")), status, summary, [], [], [], [], 0.9, new Usage(0, 0, 0, 0, 0),
            status == ResultStatus.Failed ? new ResultError(ErrorCode.CheckoutInvalid, "the checkout is invalid", false) : null);

    [Fact]
    public async Task The_model_retro_asks_a_read_only_run_with_the_records_and_no_goal_and_cuts_a_long_answer()
    {
        List<(RunRequest Request, string RunId, string Parent)> asked = [];
        var retro = new ModelRetro((request, runId, parent, ct) =>
        {
            asked.Add((request, runId, parent));
            return Task.FromResult(Answer(ResultStatus.Completed, new string('x', ModelRetro.MaxTextLength + 50)));
        }, "cheap");
        var records = new[] { Record("r1", usd: 2m, outcome: LearningOutcome.Partial, checkpoint: true) };

        var written = await retro.WriteAsync(new RetroInput(One(), BatchId, records), default);

        var (request, runId, parent) = Assert.Single(asked);
        Assert.Equal(("cheap", BatchId, runId), (request.Context.Preset, parent, written.RunId));
        Assert.Equal("/srv/repo", request.Context.Repository!.Path);
        Assert.Contains("\"id\":\"task-r1\"", request.Text);
        Assert.Contains("\"outcome\":\"partial\"", request.Text);
        Assert.Contains("\"governor\":[{\"signal\":\"spend\",\"action\":\"checkpoint\",\"count\":1,\"acted\":0}]", request.Text);
        Assert.Contains("do not read the repository and do not change any file", request.Text);
        Assert.EndsWith(" [cut]", written.Text);
        Assert.Equal(ModelRetro.MaxTextLength + " [cut]".Length, written.Text!.Length);
    }

    [Theory]
    [InlineData(ResultStatus.Failed, "something")]
    [InlineData(ResultStatus.Completed, "   ")]
    public async Task The_model_retro_refuses_a_run_that_did_not_complete_or_printed_nothing(ResultStatus status, string summary)
    {
        var retro = new ModelRetro((_, _, _, _) => Task.FromResult(Answer(status, summary)), "cheap");

        await Assert.ThrowsAsync<RetroException>(() => retro.WriteAsync(new RetroInput(One(), BatchId, [Record()]), default));
    }
}
