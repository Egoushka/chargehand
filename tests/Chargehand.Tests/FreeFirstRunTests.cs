using System.Net;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.OpenCode;

namespace Chargehand.Tests;

/// <summary>Spec 2026-11-25, F6 and F7: a run tries the free models of its class, falls back on a failure and says which model ran.</summary>
public class FreeFirstRunTests
{
    private const string List = """
        [[model]]
        id = "zen/alpha-free"
        tier = "private"
        tasks = { mechanical = "good", ask = "ok" }

        [[model]]
        id = "zen/beta-free"
        tier = "private"
        tasks = { mechanical = "ok" }

        [[model]]
        id = "zen/open-free"
        tier = "public"
        tasks = { mechanical = "ok" }
        """;

    private sealed record Rig(Orchestrator Orchestrator, ScriptedRuntime Paid, ScriptedRuntime Free, RunRequest Request, JsonlRunLog Log);

    private static Rig Build(TempDir root, string list = List, string preset = "cheap", string? repoUnder = null)
    {
        var listFile = Path.Combine(root.Path, "free-models.toml");
        File.WriteAllText(listFile, list);
        var repo = Runs.GitRepo(repoUnder is null ? root.Path : Directory.CreateDirectory(Path.Combine(root.Path, repoUnder)).FullName);
        var profile = Runs.Profile(root.Path) with { FreeFirst = new FreeFirstSettings(listFile, null, ["zen"]) };
        var paid = new ScriptedRuntime(Runs.WorkerReply);
        var free = new ScriptedRuntime(Runs.WorkerReply);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var gate = new FreeFirstGate(profile, profile.FreeFirst!, new PublicFreePoolTests.FakeVisibility(_ => RepositoryVisibility.Private), free);
        var orchestrator = new Orchestrator(profile, paid, "2.0.16", Repo.Root, log, new Dictionary<string, int>(), freeFirst: gate);
        var request = Runs.CheapRequest(repo);
        return new(orchestrator, paid, free, request with { Context = request.Context with { Preset = preset } }, log);
    }

    private static async Task<RunRecord> Record(Rig rig) => (await rig.Log.ReadAllAsync(CancellationToken.None)).Runs.Single();

    private static OpenCodeException RateLimit() => new(HttpStatusCode.ServiceUnavailable, "ServiceUnavailableError", "Rate limit exceeded");

    [Fact]
    public async Task A_small_slot_run_on_a_private_repository_runs_on_the_best_private_free_model()
    {
        using var root = new TempDir();
        var rig = Build(root);

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(rig.Paid.Created);
        Assert.Equal(("zen", "alpha-free"), (rig.Free.Created.Single().Model!.ProviderId, rig.Free.Created.Single().Model!.ModelId));
        var record = await Record(rig);
        var attempt = Assert.Single(record.Attempts!);
        Assert.Equal(new RunAttempt("zen/alpha-free", "ScriptedRuntime", true, "completed"), attempt);
        Assert.Contains("mechanical/private", record.FreeFirst, StringComparison.Ordinal);
        Assert.DoesNotContain("zen/open-free", record.FreeFirst, StringComparison.Ordinal);   // a public-tier model is not offered for a repository GitHub does not call public
    }

    [Fact]
    public async Task A_rate_limit_moves_to_the_next_free_model_then_to_the_paid_slot()
    {
        using var root = new TempDir();
        var rig = Build(root);
        rig.Free.GenerateFailures.Enqueue(RateLimit());
        rig.Free.GenerateFailures.Enqueue(RateLimit());

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Single(rig.Paid.Created);
        Assert.Equal("p/small", $"{rig.Paid.Created.Single().Model!.ProviderId}/{rig.Paid.Created.Single().Model!.ModelId}");
        Assert.Equal(
            [new RunAttempt("zen/alpha-free", "ScriptedRuntime", true, "failed:rate_limited"), new RunAttempt("zen/beta-free", "ScriptedRuntime", true, "failed:rate_limited"), new("p/small", "ScriptedRuntime", false, "completed")],
            (await Record(rig)).Attempts!.Select(a => a with { Detail = null }));
    }

    [Fact]
    public async Task One_rate_limit_leaves_the_second_free_model_to_finish_the_run()
    {
        using var root = new TempDir();
        var rig = Build(root);
        rig.Free.GenerateFailures.Enqueue(RateLimit());

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(rig.Paid.Created);
        Assert.Equal(["failed:rate_limited", "completed"], (await Record(rig)).Attempts!.Select(a => a.Outcome));
        Assert.Contains("Rate limit", (await Record(rig)).Attempts![0].Detail, StringComparison.Ordinal);
        Assert.Null((await Record(rig)).Attempts![1].Detail);
        Assert.Equal(["zen/alpha-free", "zen/beta-free"], (await Record(rig)).Attempts!.Select(a => a.Model));
    }

    [Fact]
    public async Task A_free_server_that_is_down_falls_back_like_a_rate_limit()
    {
        using var root = new TempDir();
        var rig = Build(root, "[[model]]\nid = \"zen/alpha-free\"\ntier = \"private\"\ntasks = { mechanical = \"good\" }\n");
        rig.Free.GenerateFailures.Enqueue(new ChargehandException(ErrorCode.RuntimeUnavailable, "down", "start it"));

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Single(rig.Paid.Created);
        Assert.Equal("failed:runtime_unavailable", (await Record(rig)).Attempts![0].Outcome);
    }

    [Fact]
    public async Task No_graded_model_means_the_paid_slot_only_and_the_record_says_why()
    {
        using var root = new TempDir();
        var rig = Build(root, "[[model]]\nid = \"zen/alpha-free\"\ntier = \"private\"\n");

        await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Empty(rig.Free.Created);
        Assert.Single(rig.Paid.Created);
        var record = await Record(rig);
        Assert.False(record.Attempts!.Single().Free);
        Assert.StartsWith("none (mechanical)", record.FreeFirst, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_list_means_the_paid_slot()
    {
        using var root = new TempDir();
        var rig = Build(root);
        File.Delete(Path.Combine(root.Path, "free-models.toml"));

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(rig.Free.Created);
        Assert.Single(rig.Paid.Created);
    }

    [Fact]
    public async Task A_repository_under_a_work_directory_never_reaches_a_free_model()
    {
        using var root = new TempDir();
        var rig = Build(root, repoUnder: "work");

        var result = await rig.Orchestrator.RunAsync(rig.Request, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(rig.Free.Created);
        Assert.Single(rig.Paid.Created);
    }

    [Fact]
    public async Task A_run_that_is_not_free_first_is_recorded_without_attempts()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var orchestrator = new Orchestrator(Runs.Profile(root.Path), new ScriptedRuntime(Runs.WorkerReply), "2.0.16", Repo.Root, log, new Dictionary<string, int>());

        await orchestrator.RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        var record = (await log.ReadAllAsync(CancellationToken.None)).Runs.Single();
        Assert.Null(record.Attempts);
        Assert.Null(record.FreeFirst);
    }

    [Fact]
    public void Only_a_failed_result_gives_way_and_a_cancel_or_a_cost_cap_stays()
    {
        ResultContract Failed(ErrorCode code) => Runs.Completed() with { Status = ResultStatus.Failed, Error = new ResultError(code, "x", false) };
        Assert.True(FreeFirstGate.FallsThrough(Failed(ErrorCode.RateLimited)));
        Assert.True(FreeFirstGate.FallsThrough(Failed(ErrorCode.InvalidResult)));
        Assert.True(FreeFirstGate.FallsThrough(Failed(ErrorCode.ProviderUnavailable)));
        Assert.False(FreeFirstGate.FallsThrough(Failed(ErrorCode.Cancelled)));
        Assert.False(FreeFirstGate.FallsThrough(Failed(ErrorCode.CostCapReached)));
        Assert.False(FreeFirstGate.FallsThrough(Failed(ErrorCode.CheckoutInvalid)));   // every model would meet it again
        Assert.False(FreeFirstGate.FallsThrough(Failed(ErrorCode.InvalidRequest)));
        Assert.False(FreeFirstGate.FallsThrough(Failed(ErrorCode.Internal) with { Artifacts = [new Artifact("a", "text/plain", "x")] }));   // a run that produced something is never repeated
        Assert.False(FreeFirstGate.FallsThrough(Runs.Completed()));
    }

    [Theory]
    [InlineData("cheap", "provider/small-model", FreeTask.Mechanical)]
    [InlineData("prior-art", "provider/small-model", FreeTask.Mechanical)]
    [InlineData("draft", "provider/small-model", FreeTask.Mechanical)]
    [InlineData("review", "provider/worker-model", FreeTask.Review)]
    [InlineData("design", "provider/worker-model", FreeTask.Plan)]
    [InlineData("split", "provider/worker-model", FreeTask.Plan)]
    [InlineData("default", "provider/worker-model", FreeTask.Ask)]
    [InlineData("thorough", "provider/worker-model", FreeTask.Ask)]
    [InlineData("strict", "provider/worker-model", FreeTask.Ask)]
    [InlineData("incident", "provider/worker-model", FreeTask.Ask)]
    [InlineData("release-notes", "provider/worker-model", FreeTask.Ask)]
    [InlineData("code", "provider/worker-model", FreeTask.Edit)]
    [InlineData("driven", "provider/worker-model", FreeTask.Ask)]   // its session kind does not write: a driven run goes through the driven path, never through a free model
    [InlineData("public-free", "provider/free-model", null)]
    public void Every_shipped_preset_maps_to_the_class_the_spec_names(string name, string slot, FreeTask? expected)
    {
        var preset = Preset.Load(Path.Combine(Repo.Root, "presets"), name);
        var (_, kind) = Orchestrator.AnswerKind(preset);
        Assert.Equal(slot, kind.Model);
        Assert.Equal(expected, FreeTaskClass.For(name, kind.Model, kind.Writes));
        Assert.Equal(14, Directory.GetFiles(Path.Combine(Repo.Root, "presets"), "*.yaml").Length);   // a new preset needs a row here
    }
}
