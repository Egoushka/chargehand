using System.Security.Cryptography;
using System.Text;
using Chargehand.Contracts;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>The content engine's call (ADR 0014, ADR 0018): a draft from the caller's inputs, without a repository or tools.</summary>
public class DraftTests
{
    [Fact]
    public async Task A_draft_request_without_a_repository_returns_an_inline_draft_citing_inputs()
    {
        using var root = new TempDir();
        var log = new JsonlRunLog(System.IO.Path.Combine(root.Path, "log.jsonl"));
        var runtime = new ScriptedRuntime(Runs.DraftReply);
        var events = new List<RunStatus>();

        var r = await Runs.Orchestrator(runtime, root.Path, log).RunAsync(Runs.DraftRequest(), CancellationToken.None, progress: events.Add);

        Assert.Equal(ResultStatus.Completed, r.Status);
        var draft = Assert.Single(r.Artifacts);
        Assert.Equal(("draft", "text/markdown", "I released v1."), (draft.Kind, draft.MediaType, draft.Content));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("I released v1."))), draft.Sha256);
        var evidence = Assert.Single(r.Evidence);
        Assert.Equal((EvidenceKind.Input, "rel-v1"), (evidence.Kind, evidence.Locator));
        // Without a checkout a file claim cannot resolve, so it moves to open questions.
        Assert.Contains(r.OpenQuestions, q => q.StartsWith("Unverified: It reads files.", StringComparison.Ordinal));
        Assert.Equal(["core/draft", "preset/draft", "generator/note"], r.PromptChain.Blocks.Select(b => b.Name));
        Assert.Equal(BlockSource.Caller, r.PromptChain.Blocks[2].Source);

        Assert.Equal(System.IO.Path.Combine(root.Path, ".chargehand-empty"), Assert.Single(runtime.Created).Directory);
        Assert.StartsWith("Task:" + Environment.NewLine, runtime.Prompts.First(), StringComparison.Ordinal);
        // Quoted ids: rendered as "[rel-v1]", a live model cited "[rel-v1]", which matches no input.
        Assert.Contains("- id \"rel-v1\" (signal): Released v1.", runtime.Prompts.First(), StringComparison.Ordinal);
        Assert.Contains("inputs to use: rel-v1 (signal)", runtime.IntakePrompts.Single(), StringComparison.Ordinal);
        Assert.Equal([RunEventKind.Started, RunEventKind.Intake, RunEventKind.NodeStarted, RunEventKind.NodeFinished, RunEventKind.RunFinished],
            events.Select(e => e.Event!.Value));
        Assert.Equal(r, events[^1].Result);

        var entry = await log.ReadAsync(r.TaskId, CancellationToken.None);
        Assert.Equal("Draft a note.", entry.Start!.Request.Text);
        Assert.Equal(Environment.ProcessId, entry.Start.Pid);
        Assert.Equal("answer", entry.Run!.ExecutedAction);
    }

    [Fact]
    public async Task A_preset_that_checks_out_a_repository_still_asks_for_one()
    {
        using var root = new TempDir();
        var runtime = new ScriptedRuntime(Runs.DraftReply);

        var r = await Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(System.IO.Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Runs.DraftRequest() with { Context = new RequestContext(false, "cheap") }, CancellationToken.None);

        Assert.Equal(ResultStatus.NeedsInput, r.Status);
        Assert.Contains("context.repository", r.Summary, StringComparison.Ordinal);
        Assert.Empty(runtime.IntakePrompts);
        Assert.Empty(runtime.Created);
    }

    [Fact]
    public async Task A_run_that_throws_ends_with_a_failed_result_and_a_run_record()
    {
        using var root = new TempDir();
        var log = new JsonlRunLog(System.IO.Path.Combine(root.Path, "log.jsonl"));

        var r = await Runs.Orchestrator(new ScriptedRuntime(Runs.DraftReply), root.Path, log)
            .RunAsync(Runs.DraftRequest() with { Context = new RequestContext(false, "no-such-preset") }, CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Contains("no-such-preset", r.Summary, StringComparison.Ordinal);
        Assert.Equal(ResultStatus.Failed, (await log.ReadAsync(r.TaskId, CancellationToken.None)).Run!.Result.Status);
    }
}
