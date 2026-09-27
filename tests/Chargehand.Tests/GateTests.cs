using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Evals;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>Prompt CI's scoring and gate (ADR 0019).</summary>
public class GateTests
{
    private static readonly EvalCell Cell = new("cheap/worker", "d", "worker", "cheap", ["prompts/core/worker.md"], 0.10, 0.15);

    /// <summary>Paired items: base quality, the change's offset per item, and each arm's cost.</summary>
    private static List<Pair> Pairs(double[] offsets, double baseQuality = 0.8, decimal baseUsd = 0.01m, decimal changeUsd = 0.01m) =>
        [.. offsets.Select((o, i) => new Pair($"i{i}", baseQuality, Math.Clamp(baseQuality + o, 0, 1), baseUsd, changeUsd))];

    [Fact]
    public void Noise_around_no_change_passes()
    {
        var v = Gate.Decide(Cell, Pairs([0.05, -0.05, 0.1, -0.1, 0, 0.05, -0.05, 0]), null);
        Assert.False(v.Blocked, v.Reason);
    }

    [Fact]
    public void A_clear_quality_drop_blocks()
    {
        var v = Gate.Decide(Cell, Pairs([-0.3, -0.25, -0.35, -0.3, -0.2, -0.4, -0.3, -0.25]), null);
        Assert.True(v.Blocked);
        Assert.StartsWith("quality -0.294", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drop_that_noise_explains_passes()
    {
        // Mean -0.14, beyond T, but the items disagree too much for the drop to be more than chance.
        var v = Gate.Decide(Cell, Pairs([-0.6, 0.3, -0.6, 0.3, -0.6, 0.3, -0.3, 0.1], baseQuality: 0.6), null);
        Assert.True(v.QualityDelta < -Cell.QualityTolerance);
        Assert.False(v.Blocked, v.Reason);
    }

    [Fact]
    public void A_drop_inside_the_tolerance_passes()
    {
        var v = Gate.Decide(Cell, Pairs([-0.05, -0.05, -0.05, -0.05, -0.05, -0.05, -0.05, -0.05]), null);
        Assert.False(v.Blocked, v.Reason);
    }

    [Fact]
    public void A_cost_rise_beyond_the_tolerance_blocks()
    {
        var pairs = Pairs(new double[8]).Select((p, i) => p with { ChangeUsd = 0.015m + i * 0.0001m }).ToList();
        var v = Gate.Decide(Cell, pairs, null);
        Assert.True(v.Blocked);
        Assert.StartsWith("cost +", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declared_trade_passes_only_when_it_is_delivered()
    {
        var cheaper = Pairs([-0.12, -0.1, -0.14, -0.12, -0.1, -0.12, -0.14, -0.12], changeUsd: 0.006m);
        var trade = new Trade(-0.15, -0.25);
        Assert.True(Gate.Decide(Cell, cheaper, null).Blocked);
        Assert.False(Gate.Decide(Cell, cheaper, trade).Blocked);
        var notCheaper = cheaper.Select(p => p with { ChangeUsd = 0.009m }).ToList();
        Assert.StartsWith("trade not delivered: cost", Gate.Decide(Cell, notCheaper, trade).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Fewer_items_than_the_cell_needs_block()
    {
        Assert.True(Gate.Decide(Cell, Pairs([0, 0, 0, 0, 0]), null).Blocked);
    }

    [Theory]
    [InlineData("Faster.\n\nprompt-ci: trade quality>=-0.15 cost<=-25%\n", -0.15, -0.25)]
    [InlineData("PROMPT-CI: trade quality >= -0.05 cost <= -10%", -0.05, -0.10)]
    public void The_trade_line_is_read_from_the_pull_request_body(string body, double quality, double cost)
    {
        var trade = Gate.ParseTrade(body)!;
        Assert.Equal(quality, trade.Quality, 6);
        Assert.Equal(cost, trade.Cost, 6);
    }

    [Fact]
    public void A_body_without_a_trade_line_declares_none() => Assert.Null(Gate.ParseTrade("Fixes a typo."));

    [Fact]
    public void Changed_files_map_to_cells_and_the_rest_is_uncovered()
    {
        var cells = EvalCell.Load(Repo.Path("evals", "cells.json"));
        var (affected, uncovered) = EvalRunner.Affected(cells, ["prompts/preset/cheap.md", "prompts/preset/default.md", "src/Chargehand/Orchestrator.cs"]);
        Assert.Equal(["cheap/worker"], affected.Select(c => c.Name));
        Assert.Equal(["prompts/preset/default.md"], uncovered);
    }

    [Fact]
    public void Shipped_cells_and_example_items_load()
    {
        var cells = EvalCell.Load(Repo.Path("evals", "cells.json"));
        Assert.Equal(["cheap/worker", "draft/draft", "intake"], cells.Select(c => c.Name));
        foreach (var file in cells.SelectMany(c => c.Files))
            Assert.True(File.Exists(Repo.Path(file)), file);
        foreach (var line in File.ReadAllLines(Repo.Path("evals", "example.jsonl")))
        {
            var item = JsonSerializer.Deserialize<EvalItem>(line, ContractJson.Options)!;
            Assert.Empty(ContractSchemas.Validate(ContractSchemas.Request, JsonSerializer.SerializeToElement(item.Request, ContractJson.Options)));
            Assert.All(item.Request.CallerBlocks ?? [], b => Assert.Equal(PromptBlock.Hash(b.Text), b.Sha256));
        }
    }

    private static ResultContract Result(IReadOnlyList<Claim> claims, IReadOnlyList<Evidence> evidence, IReadOnlyList<string> questions, IReadOnlyList<Artifact>? artifacts = null) =>
        new("result/v1", "t", "n", new string('0', 32), new PromptChain([], new AsSent("2.0.16", "build", "p/m", "2026-09-26")), ResultStatus.Completed, "s",
            claims, evidence, artifacts ?? [], questions, 0.8, new Usage(0, 0, 0, 0, 0.01m));

    [Fact]
    public void A_worker_scores_half_on_resolved_claims_and_half_on_reference_recall()
    {
        var r = Result([new Claim("a", ["e1"], 0.9), new Claim("b", ["e1"], 0.9)], [new Evidence("e1", EvidenceKind.File, "src/a.cs:1-3")],
            ["[s1] Unverified: c (e2: gone)"]);
        var scores = Scoring.Worker(r, ["src/a.cs", "src/b.cs"]);
        Assert.Equal(2.0 / 3, scores["evidence_resolved"], 6);
        Assert.Equal(0.5, scores["reference_recall"], 6);
        Assert.Equal((2.0 / 3 + 0.5) / 2, scores["quality"], 6);
        Assert.Equal(0, Scoring.Worker(r with { Status = ResultStatus.Failed }, ["src/a.cs"])["quality"]);
    }

    [Fact]
    public void A_worker_answer_thinner_than_the_reference_scales_its_grounding_down()
    {
        // Two kept claims; the one moved to open questions as unverified does not count.
        var r = Result([new Claim("a", ["e1"], 0.9), new Claim("b", ["e1"], 0.9)], [new Evidence("e1", EvidenceKind.File, "src/a.cs:1-3")],
            ["[s1] Unverified: c (e2: gone)"]);
        var scores = Scoring.Worker(r, ["src/a.cs"], referenceClaims: 4);
        Assert.Equal(0.5, scores["completeness"], 6);
        Assert.Equal((2.0 / 3 + 1) / 2 * 0.5, scores["quality"], 6);
        Assert.Equal(1, Scoring.Worker(r, ["src/a.cs"], referenceClaims: 2)["completeness"], 6);
        Assert.Equal(1, Scoring.Worker(r, ["src/a.cs"], referenceClaims: 1)["completeness"], 6); // no bonus above the reference
        Assert.Equal(1, Scoring.Worker(r, ["src/a.cs"])["completeness"], 6); // no reference: grounding alone
    }

    [Fact]
    public void Seeded_worker_items_take_the_run_claim_count_and_each_subtask_its_own()
    {
        var spec = JsonSerializer.Deserialize<TaskSpec>(ScriptedRuntime.Spec("split",
            """{"subtasks":[{"id":"a","goal":"A","read_only":true},{"id":"b","goal":"B","read_only":true}]}"""), ContractJson.Options)!;
        var result = Result([new Claim("x", ["a.e1"], 0.9), new Claim("y", ["a.e1"], 0.9), new Claim("z", ["b.e1"], 0.9)],
            [new Evidence("a.e1", EvidenceKind.File, "src/a.cs:1"), new Evidence("b.e1", EvidenceKind.File, "src/b.cs:2")], []);
        var run = new RunRecord("run-1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "cheap", "split", spec, result);

        var items = EvalRunner.Seed(Cell, [new RunEntry(null, run, [])]).ToList();

        Assert.Equal(["run-1", "run-1.a", "run-1.b"], items.Select(i => i.Id));
        Assert.Equal(new int?[] { 3, 2, 1 }, items.Select(i => i.Expected.ReferenceClaims));
        Assert.Equal(["src/a.cs"], items[1].Expected.ReferenceFiles!);
    }

    [Fact]
    public void A_draft_scores_bounds_evidence_required_inputs_and_banned_phrases()
    {
        var r = Result([new Claim("v1 is out", ["e1"], 0.9)], [new Evidence("e1", EvidenceKind.Input, "rel-v1")], [],
            [new Artifact("draft", "text/markdown", new string('a', 64), Content: "A revolutionary release.")]);
        var scores = Scoring.Draft(r, new DraftExpectation(400, ["rel-v1", "bench"], ["revolutionary"]));
        Assert.Equal((1 + 1 + 0.5 + 0) / 4.0, scores["quality"], 6);
        Assert.Equal(0, Scoring.Draft(r with { Artifacts = [] }, new DraftExpectation(400))["quality"]);
    }

    [Fact]
    public void Intake_scores_the_action_it_chose()
    {
        var spec = JsonSerializer.Deserialize<TaskSpec>(ScriptedRuntime.Spec("ask", """{"questions":["Which?"]}"""), ContractJson.Options)!;
        Assert.Equal(1, Scoring.Intake(spec, "ask")["quality"]);
        Assert.Equal(0, Scoring.Intake(spec, "answer")["quality"]);
        Assert.Equal(1, Scoring.Intake(null, "needs_input")["quality"]);
    }

    [Fact]
    public async Task An_intake_item_whose_preset_is_new_in_the_change_runs_under_the_change_alone()
    {
        using var dir = new TempDir();
        // The base predates the draft preset: intake's prompt and cheap only.
        dir.Write("base/prompts/intake/task-spec.md", File.ReadAllText(Repo.Path("prompts", "intake", "task-spec.md")));
        dir.Write("base/presets/cheap.yaml", File.ReadAllText(Repo.Path("presets", "cheap.yaml")));
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var runner = new EvalRunner(_ => throw new InvalidOperationException("intake items run no orchestrator"), runtime, "p/small",
            new JsonlRunLog(System.IO.Path.Combine(dir.Path, "log.jsonl")), null, () => { }, TextWriter.Null);
        EvalItem Item(string id, RequestContext context) => new(id, new RunRequest("request/v1", "Do it.", context), new EvalExpected(Action: "answer"));
        var intake = new EvalCell("intake", "d", "intake", null, ["prompts/intake/task-spec.md"], 0.10, 0.15, MinItems: 1);

        var v = await runner.RunAsync(intake, [Item("cheap", new RequestContext(false, "cheap", Repository: new RepositoryRef("/r", "c"))), Item("draft", new RequestContext(false, "draft"))],
            System.IO.Path.Combine(dir.Path, "base"), Repo.Root, "t", null, CancellationToken.None);

        Assert.Equal((1, false), (v.Items, v.Blocked));
        Assert.Equal(3, runtime.IntakePrompts.Count); // cheap under both arms, draft under the change only
    }

    [Theory]
    [InlineData("OpenCode 503 ServiceUnavailableError: Rate limit exceeded for api_key: k. Limit type: max_parallel_requests.", true)]
    [InlineData("API Error: 429 {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\"}}", true)]
    [InlineData("OpenCode 400 : Model unavailable", false)]
    [InlineData("checkout is at 1a2b3c, request pins 4290abc", false)]
    public void Rate_limits_are_told_apart_from_other_failures(string message, bool limited) =>
        Assert.Equal(limited, EvalRunner.RateLimited(message));

    [Fact]
    public async Task A_rate_limited_arm_runs_again_and_is_scored_on_the_retry()
    {
        using var dir = new TempDir();
        var runtime = new ScriptedRuntime(Runs.DraftReply);
        runtime.GenerateFailures.Enqueue(new InvalidOperationException("OpenCode 503 ServiceUnavailableError: Rate limit exceeded"));
        var log = new JsonlRunLog(System.IO.Path.Combine(dir.Path, "log.jsonl"));
        var runner = new EvalRunner(_ => Runs.Orchestrator(runtime, dir.Path, log), runtime, "p/small", log, null, () => { }, TextWriter.Null);
        var draft = new EvalCell("draft/draft", "d", "draft", "draft", ["prompts/preset/draft.md"], 0.10, 0.15, MinItems: 1);
        var item = new EvalItem("note", Runs.DraftRequest(), new EvalExpected(Draft: new DraftExpectation(1200, ["rel-v1"])));
        var retries = EvalRunner.RateLimitRetries;
        EvalRunner.RateLimitRetries = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
        try
        {
            var v = await runner.RunAsync(draft, [item], Repo.Root, Repo.Root, "t", null, CancellationToken.None);

            Assert.Equal(1, v.Items);
            Assert.Equal(0, v.QualityDelta); // both arms scored on a real draft, not the failed attempt
            Assert.Equal(2, runtime.Created.Count);

            for (var i = 0; i < 4; i++)
                runtime.GenerateFailures.Enqueue(new InvalidOperationException("OpenCode 503 ServiceUnavailableError: Rate limit exceeded"));
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(draft, [item], Repo.Root, Repo.Root, "t", null, CancellationToken.None));
            Assert.Contains("still rate limited after 4 attempts", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            EvalRunner.RateLimitRetries = retries;
        }
    }

    [Fact]
    public async Task A_worker_arm_that_fails_before_any_model_call_stops_the_gate()
    {
        using var dir = new TempDir();
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var log = new JsonlRunLog(System.IO.Path.Combine(dir.Path, "log.jsonl"));
        var runner = new EvalRunner(_ => Runs.Orchestrator(runtime, dir.Path, log), runtime, "p/small", log, null, () => { }, TextWriter.Null);
        // The request pins a commit the repository does not have: the orchestrator refuses before a session starts.
        var item = new EvalItem("readme", Runs.CheapRequest(Runs.GitRepo(dir.Path) with { Commit = new string('0', 40) }),
            new EvalExpected(ReferenceFiles: ["README.md"]));

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Cell, [item], Repo.Root, Repo.Root, "t", null, CancellationToken.None));

        Assert.Contains("readme (t-base): failed before any model call: commit 0000000000000000000000000000000000000000 is not in", e.Message, StringComparison.Ordinal);
        Assert.Empty(runtime.Created);
    }
}
