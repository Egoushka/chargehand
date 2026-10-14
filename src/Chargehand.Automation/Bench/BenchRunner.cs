using System.Globalization;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Bench;

/// <param name="Environment">What the profile says about the server and its images; the benchmark reads it and changes nothing.</param>
/// <param name="Only">Task ids to run; null: all.</param>
/// <param name="CheckPins">In a dry run, ask each upstream whether it serves the pinned commit (needs the network).</param>
/// <param name="RemoteTemplate">Where the server may push a task's branch, one URL per repository: <c>{name}</c> is the repository's name in the set. A fork you own, or a <c>file://</c> path.</param>
/// <param name="MaxTokens">Input plus output tokens per batch (one batch per repository, one more for the question tasks); a real run needs this, <paramref name="MaxUsd"/> or both.</param>
public sealed record BenchOptions(
    string OutDirectory,
    BenchEnvironment Environment,
    IReadOnlySet<string>? Only = null,
    bool DryRun = false,
    bool Baseline = false,
    bool CheckPins = false,
    string? RemoteTemplate = null,
    long? MaxTokens = null,
    decimal? MaxUsd = null,
    int? MaxParallel = null);

/// <param name="ImageFor">The session image for a repository, by digest; null when the profile has none for it.</param>
/// <param name="Allow">The hosts a session's container may reach: the driven preset's and the profile's.</param>
/// <param name="GovernorMode"><c>observe</c>, <c>enforce</c> or <c>off</c>; null when the profile names none (the server's default is observe).</param>
public sealed record BenchEnvironment(Func<BenchRepository, string?> ImageFor, IReadOnlyList<string> Allow, string? GovernorMode, TimeSpan AcceptanceTimeout)
{
    public static BenchEnvironment Empty { get; } = new(_ => null, [], null, TimeSpan.FromMinutes(15));
}

/// <summary>Runs a task set (ADR 0052): checks it, or runs it on a server and measures it. A real run clones each repository at its pin, sends one batch per repository through the
/// server's ordinary API, answers the question tasks in a second batch, runs every acceptance check in a fresh container on the task's branch, and writes <c>results.json</c> and
/// <c>report.md</c>. Nothing here calls a model: the server's sessions do, and only in a real run.</summary>
public sealed class BenchRunner(IBenchWorkspace workspace, IBenchServer? server, IAcceptanceRunner? acceptance, TextWriter output, TimeProvider? time = null)
{
    public const string ResultsFile = "results.json";
    public const string ReportFile = "report.md";

    /// <summary>The tokens a past driven task used, input plus output, on small repositories (docs/guide/driven.md: 74,206 over 12 sessions, 101,000 over 10): what the plan line
    /// prints as a reference. Larger repositories and L tasks use more.</summary>
    public const int ReferenceTokensLow = 6_000, ReferenceTokensHigh = 10_000;

    /// <summary>The driven preset's per-task token cap (<c>presets/driven.yaml</c>, <c>max_tokens</c>): a batch is only given a task it could afford at this size.</summary>
    public const long PerTaskTokenCap = 2_000_000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <returns>0 done (a run's pass rate is the report's business, not the exit code), 1 a check failed or the run could not finish, 2 a usage problem.</returns>
    public async Task<int> RunAsync(TaskSet set, BenchOptions options, CancellationToken ct)
    {
        var selected = Select(set, options.Only, out var problem);
        if (problem is not null)
        {
            await output.WriteLineAsync(problem);
            return 2;
        }
        var repositories = set.Repositories.Where(r => selected.Any(t => t.Repository == r.Name)).ToList();
        await PrintPlanAsync(set, selected, repositories, options);
        if (options.DryRun)
            return await DryRunAsync(set, selected, repositories, options, ct);
        if (options.Baseline)
            return await BaselineAsync(set, selected, repositories, options, ct);
        if (options.RemoteTemplate is null)
        {
            await output.WriteLineAsync("a real run needs --remote <url template>: where the server may push the branches, one repository each, e.g. https://github.com/<you>/{name}.git (your forks) or file:///path/{name}.git.");
            return 2;
        }
        if (options.MaxTokens is null && options.MaxUsd is null)
        {
            await output.WriteLineAsync("a real run needs a cap per batch: --max-tokens <n> (input plus output tokens), --max-usd <n>, or both. Nothing was started.");
            return 2;
        }
        if (server is null || acceptance is null)
            throw new InvalidOperationException("a real run needs a server and an acceptance runner");
        return await ExecuteAsync(set, selected, repositories, options, ct);
    }

    public static IReadOnlyList<BenchTask> Select(TaskSet set, IReadOnlySet<string>? only, out string? problem)
    {
        problem = null;
        if (only is null)
            return set.Tasks;
        var unknown = only.Where(id => set.Tasks.All(t => t.Id != id)).ToList();
        if (unknown.Count > 0)
            problem = $"--only names tasks the set does not have: {string.Join(", ", unknown)}";
        return [.. set.Tasks.Where(t => only.Contains(t.Id))];
    }

    // ---- plan and dry run ----

    private async Task PrintPlanAsync(TaskSet set, IReadOnlyList<BenchTask> tasks, List<BenchRepository> repositories, BenchOptions options)
    {
        await output.WriteLineAsync($"task set {set.Name}: {tasks.Count} tasks over {repositories.Count} repositories, one batch each{(tasks.Any(t => t.Kind == BenchTask.Question) ? ", plus a second batch for the questions" : "")}");
        foreach (var repo in repositories)
        {
            var mine = tasks.Where(t => t.Repository == repo.Name).ToList();
            await output.WriteLineAsync($"  {repo.Name} ({repo.Ecosystem}) {repo.Url} @ {repo.Commit[..12]}: {string.Join(' ', mine.Select(t => $"{t.Id}[{t.Size},{t.Kind}]"))}");
        }
        var by = tasks.GroupBy(t => t.Kind).ToDictionary(g => g.Key, g => g.Count());
        await output.WriteLineAsync($"  kinds: {by.GetValueOrDefault(BenchTask.Change)} change, {by.GetValueOrDefault(BenchTask.Impossible)} impossible, {by.GetValueOrDefault(BenchTask.Question)} question");
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"  reference: past driven tasks used {ReferenceTokensLow:N0} to {ReferenceTokensHigh:N0} input plus output tokens each on small repositories (docs/guide/driven.md), so {tasks.Count * (long)ReferenceTokensLow:N0} to {tasks.Count * (long)ReferenceTokensHigh:N0} here before continuations; cache tokens ran 70 to 90 times that."));
        if (options.MaxTokens is not null || options.MaxUsd is not null)
            await output.WriteLineAsync($"  caps per batch: {(options.MaxTokens is { } t ? $"{t:N0} tokens" : "no token cap")}, {(options.MaxUsd is { } u ? $"{u.ToString("0.00", CultureInfo.InvariantCulture)} USD" : "no dollar cap")}");
        if (options.MaxTokens < PerTaskTokenCap)
            await output.WriteLineAsync($"  note: a batch cap under {PerTaskTokenCap:N0} tokens is below the driven preset's per-task cap, and the server starts no task it could not afford");
    }

    private async Task<int> DryRunAsync(TaskSet set, IReadOnlyList<BenchTask> tasks, List<BenchRepository> repositories, BenchOptions options, CancellationToken ct)
    {
        var failed = 0;
        async Task Line(bool ok, string check, string? why = null)
        {
            if (!ok)
                failed++;
            await output.WriteLineAsync($"{(ok ? "ok  " : "FAIL")} {check}{(why is null ? "" : $": {why}")}");
        }
        async Task Note(string text) => await output.WriteLineAsync($"note {text}");

        await Line(true, $"task set is valid ({tasks.Count} tasks, {repositories.Count} repositories, sha256 {set.Sha256[..Math.Min(12, set.Sha256.Length)]})");
        foreach (var repo in repositories)
        {
            if (options.CheckPins)
            {
                var pin = await workspace.CheckPinAsync(repo, ct);
                await Line(pin is null, $"{repo.Name}: upstream serves {repo.Commit[..12]}", pin);
            }
            else
                await Line(true, $"{repo.Name}: pin {repo.Commit[..12]} is a full commit id (--check-pins asks the upstream)");
            var existing = await workspace.ExistingCommitAsync(repo, ct);
            if (existing is not null)
                await Line(existing == repo.Commit, $"{repo.Name}: existing clone is at the pin", existing == repo.Commit ? null : $"it is at {(existing.Length >= 12 ? existing[..12] : "no commit")}; remove it before a run");
            var image = options.Environment.ImageFor(repo);
            if (image is null)
                await Note($"{repo.Name}: the profile names no session image for {repo.Ecosystem} (driven.images, driven.services.images)");
            var uncovered = RepositoryDetector.Uncovered(RepositoryDetector.HostsFor(repo.Ecosystem), options.Environment.Allow);
            if (uncovered.Count > 0)
                await Note($"{repo.Name}: add {string.Join(", ", uncovered)} to driven.network.allow, or its restore and tests cannot reach them");
        }
        foreach (var task in tasks)
        {
            var repo = repositories.First(r => r.Name == task.Repository);
            var argv = TaskSetLoader.AcceptanceArgv(set, task, repo);
            await Line(argv.Count > 0 && argv.All(a => a.Length > 0), $"{task.Id}: acceptance command present", task.Acceptance.Script ?? string.Join(' ', task.Acceptance.Argv ?? []));
        }
        if (options.Environment.GovernorMode is { } mode)
            await Note($"governor mode is {mode}{(mode == "observe" ? "" : ": the false-stop rate needs observe, where nothing is stopped")}");
        else
            await Note("the profile names no governor mode; the server's default is observe");
        if (options.RemoteTemplate is null)
            await Note("no --remote: a real run needs one");
        if (options.MaxTokens is null && options.MaxUsd is null)
            await Note("no --max-tokens or --max-usd: a real run needs one");
        await output.WriteLineAsync(failed == 0 ? "dry run: nothing was started." : $"dry run: {failed} checks failed; nothing was started.");
        return failed == 0 ? 0 : 1;
    }

    // ---- baseline: the checks against the pin, with no session ----

    private async Task<int> BaselineAsync(TaskSet set, IReadOnlyList<BenchTask> tasks, List<BenchRepository> repositories, BenchOptions options, CancellationToken ct)
    {
        if (acceptance is null)
            throw new InvalidOperationException("a baseline run needs an acceptance runner");
        var failed = 0;
        foreach (var repo in repositories)
        {
            var image = options.Environment.ImageFor(repo);
            if (image is null)
            {
                await output.WriteLineAsync($"FAIL {repo.Name}: the profile names no session image for {repo.Ecosystem}");
                failed++;
                continue;
            }
            try
            {
                await workspace.PrepareAsync(repo, options.RemoteTemplate?.Replace("{name}", repo.Name, StringComparison.Ordinal) ?? repo.Url, ct);
                var branch = $"chargehand/bench-baseline-{repo.Name}";
                var directory = await workspace.BaselineBranchAsync(repo, branch, ct);
                async Task<AcceptanceResult> Check(string label, IReadOnlyList<string> argv) =>
                    await acceptance.RunAsync(new AcceptanceRequest(label, directory, branch, repo.Commit, argv, options.Environment.AcceptanceTimeout, image, options.Environment.Allow), ct);

                var test = await Check($"{repo.Name}-test", repo.Test);
                failed += await Report($"{repo.Name}: the test command passes on the pin", test.Passed, test) ? 0 : 1;
                foreach (var task in tasks.Where(t => t.Repository == repo.Name))
                {
                    var result = await Check(task.Id, TaskSetLoader.AcceptanceArgv(set, task, repo));
                    // A change that is not made yet fails its check; the guard of an impossible task holds on untouched code.
                    var expectPass = task.Kind == BenchTask.Impossible;
                    failed += await Report($"{task.Id}: the {(expectPass ? "guard passes" : "acceptance fails")} on the pin", result.Passed == expectPass, result) ? 0 : 1;
                }
            }
            catch (Exception e) when (e is BenchException or ChargehandException)
            {
                await output.WriteLineAsync($"FAIL {repo.Name}: {e.Message}");
                failed++;
            }
        }
        await output.WriteLineAsync(failed == 0 ? "baseline: every check behaves as the set says; no model was called." : $"baseline: {failed} checks did not behave as the set says; no model was called.");
        return failed == 0 ? 0 : 1;
    }

    private async Task<bool> Report(string check, bool ok, AcceptanceResult result)
    {
        await output.WriteLineAsync($"{(ok ? "ok  " : "FAIL")} {check} (exit {result.ExitCode}{(result.TimedOut ? ", timed out" : "")}, {result.DurationSeconds:0}s)");
        if (!ok)
            foreach (var line in result.OutputTail.TrimEnd().Split('\n').TakeLast(12))
                await output.WriteLineAsync($"       {line.TrimEnd()}");
        return ok;
    }

    // ---- the real run ----

    private async Task<int> ExecuteAsync(TaskSet set, IReadOnlyList<BenchTask> tasks, List<BenchRepository> repositories, BenchOptions options, CancellationToken ct)
    {
        Directory.CreateDirectory(options.OutDirectory);
        var started = _time.GetUtcNow();
        List<TaskResult> done = [];
        var exit = 0;
        BenchResults Snapshot(bool complete) => new(BenchResults.SchemaId, set.Name, set.Sha256, started, complete ? _time.GetUtcNow() : null, complete,
            [.. repositories.Select(r => new RepositoryPin(r.Name, r.Ecosystem, r.Url, r.Commit))], done);
        void Write(bool complete)
        {
            var results = Snapshot(complete) with { FinishedAt = _time.GetUtcNow() };
            File.WriteAllText(Path.Combine(options.OutDirectory, ResultsFile), results.ToJson());
            File.WriteAllText(Path.Combine(options.OutDirectory, ReportFile), BenchReport.Render(results));
        }

        foreach (var repo in repositories)
        {
            var mine = tasks.Where(t => t.Repository == repo.Name).ToList();
            try
            {
                done.AddRange(await RunRepositoryAsync(set, repo, mine, options, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Write(complete: false);
                await output.WriteLineAsync($"stopped; the partial results are in {options.OutDirectory}. A batch already started keeps running on the server (chargehand cancel <run-id>).");
                return 1;
            }
            catch (Exception e) when (e is BenchException or ChargehandException or HttpRequestException or IOException)
            {
                await output.WriteLineAsync($"{repo.Name}: {e.Message}");
                done.AddRange(mine.Select(t => NotRun(repo, t, e.Message)));
                exit = 1;
            }
            Write(complete: false);
        }
        Write(complete: exit == 0);
        var results = Snapshot(exit == 0) with { FinishedAt = _time.GetUtcNow() };
        var stats = BenchStats.Compute(results);
        await output.WriteLineAsync($"pass rate {stats.Pass.Format()}; honest stops {stats.HonestStop.Format()}; {stats.Tokens:N0} tokens, {(stats.Usd is { } usd ? $"${usd.ToString("0.00", CultureInfo.InvariantCulture)} equivalent" : "no dollar figure")}.");
        await output.WriteLineAsync($"results: {Path.Combine(options.OutDirectory, ResultsFile)}; report: {Path.Combine(options.OutDirectory, ReportFile)}");
        return exit;
    }

    private static TaskResult NotRun(BenchRepository repo, BenchTask task, string why) =>
        Result(repo, task, "not_run", null, why, null, null, TaskTelemetry.Empty, null, false, false, false, null);

    private async Task<IReadOnlyList<TaskResult>> RunRepositoryAsync(TaskSet set, BenchRepository repo, List<BenchTask> tasks, BenchOptions options, CancellationToken ct)
    {
        var image = options.Environment.ImageFor(repo) ?? throw new BenchException($"the profile names no session image for {repo.Ecosystem}");
        var remote = options.RemoteTemplate!.Replace("{name}", repo.Name, StringComparison.Ordinal);
        var path = await workspace.PrepareAsync(repo, remote, ct);
        await output.WriteLineAsync($"{repo.Name}: checkout at {repo.Commit[..12]} ready; submitting {tasks.Count} tasks");

        var first = await RunBatchAsync(repo, path, [.. tasks.Select(t => new DrivenTask(t.Id, Goal: t.Goal, Size: t.Size))], options, ct);
        var rows = first.Rows.ToDictionary(r => r.Id);
        Dictionary<string, TaskTelemetry> telemetry = tasks.ToDictionary(t => t.Id, t => TaskTelemetry.Summarize(first.Events, t.Id));
        Dictionary<string, bool> answered = [];

        // A question task whose session asked is answered in a second batch that continues it.
        var asked = tasks.Where(t => t.Kind == BenchTask.Question && rows.TryGetValue(t.Id, out var r) && r.Status == "needs_input").ToList();
        if (asked.Count > 0)
        {
            await output.WriteLineAsync($"{repo.Name}: {asked.Count} tasks asked a question; answering");
            var second = await RunBatchAsync(repo, path, [.. asked.Select(t => new DrivenTask(t.Id, Size: t.Size, Answers: t.Answers, ContinueFrom: new ContinueFrom(first.RunId, t.Id)))], options, ct);
            foreach (var row in second.Rows)
            {
                rows[row.Id] = row;
                telemetry[row.Id] = TaskTelemetry.Merge(telemetry[row.Id], TaskTelemetry.Summarize(second.Events, row.Id));
                answered[row.Id] = true;
            }
        }

        List<TaskResult> results = [];
        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();
            if (!rows.TryGetValue(task.Id, out var row))
            {
                results.Add(NotRun(repo, task, first.Problem ?? "the batch result does not list the task"));
                continue;
            }
            var (usage, integrity) = await TaskDetailAsync(row, ct);
            var tally = telemetry[task.Id].WithResult(usage);
            var branch = row.Branch ?? tally.Branch;
            AcceptanceResult? checkedBranch = null;
            if (branch is not null)
                checkedBranch = await CheckAsync(set, repo, task, branch, image, options, ct);
            var asking = tally.AskedQuestion || row.Status == "needs_input";
            results.Add(Result(repo, task, row.Status, row.ErrorCode, row.Detail, branch, row.PrUrl ?? tally.PrUrl, tally, checkedBranch, asking, answered.ContainsKey(task.Id), integrity.Flagged, integrity.Reason));
            await output.WriteLineAsync($"{task.Id}: {results[^1].Outcome} ({row.Status}{(checkedBranch is null ? ", no branch to check" : checkedBranch.Passed ? ", acceptance passed" : ", acceptance failed")})");
        }
        return results;
    }

    private sealed record BatchRun(string RunId, IReadOnlyList<RunStatus> Events, IReadOnlyList<BatchRow> Rows, string? Problem);

    private async Task<BatchRun> RunBatchAsync(BenchRepository repo, string path, IReadOnlyList<DrivenTask> driven, BenchOptions options, CancellationToken ct)
    {
        // Plan false: the planner would size and split the tasks, and the set's sizes and goals are what is measured.
        var request = new RunRequest("request/v1", $"chargehand benchmark: {repo.Name}",
            new RequestContext(false, "driven", Repository: new RepositoryRef(path, repo.Commit), Verify: repo.Test),
            Driven: new RequestDriven(driven, options.MaxParallel, options.MaxTokens, options.MaxUsd, true, Plan: false));
        var runId = await server!.SubmitAsync(request, ct);
        await output.WriteLineAsync($"{repo.Name}: batch {runId} started ({driven.Count} tasks); `chargehand watch {runId}` shows it");
        var (events, result) = await server.FollowAsync(runId, e =>
        {
            if (e.Event is RunEventKind.TaskFinished or RunEventKind.Question or RunEventKind.Plan or RunEventKind.PrOpened)
                output.WriteLine($"  {e.Event.ToString()!.ToLowerInvariant()}: {e.Detail}");
        }, ct);
        var rows = BatchRows(result, out var problem);
        return new BatchRun(runId, events, rows, problem);
    }

    public sealed record BatchRow(string Id, string RunId, string Status, string? Branch, string? PrUrl, string? ErrorCode, string? Detail);

    /// <summary>The tasks of a batch result, from its <c>driven-batch</c> artifact. A batch that failed before it started has none; <paramref name="problem"/> says why.</summary>
    public static IReadOnlyList<BatchRow> BatchRows(ResultContract batch, out string? problem)
    {
        problem = null;
        var artifact = batch.Artifacts.FirstOrDefault(a => a.Kind == "driven-batch");
        if (artifact?.Content is null)
        {
            problem = batch.Error is { } e ? $"{JsonNamingPolicy.SnakeCaseLower.ConvertName(e.Code.ToString())}: {e.Message}" : batch.Summary;
            return [];
        }
        using var doc = JsonDocument.Parse(artifact.Content);
        string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return [.. doc.RootElement.GetProperty("tasks").EnumerateArray().Select(t => new BatchRow(Text(t, "id")!, Text(t, "run_id") ?? "", Text(t, "status") ?? "failed", Text(t, "branch"), Text(t, "pr_url"), Text(t, "error_code"), Text(t, "detail")))];
    }

    /// <summary>The task's own result: its usage (a lower bound the events may have missed) and chargehand's test-integrity flag, which lives in the verification artifact.</summary>
    private async Task<(Usage? Usage, (bool Flagged, string? Reason) Integrity)> TaskDetailAsync(BatchRow row, CancellationToken ct)
    {
        if (row.RunId.Length == 0 || await server!.GetResultAsync(row.RunId, ct) is not { } result)
            return (null, (false, null));
        return (result.Usage, TestIntegrity(result));
    }

    public static (bool Flagged, string? Reason) TestIntegrity(ResultContract result)
    {
        var content = result.Artifacts.FirstOrDefault(a => a.Kind == "verification")?.Content;
        if (content is null)
            return (false, null);
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.TryGetProperty("test_integrity", out var flag) && flag.ValueKind == JsonValueKind.Object
            ? (true, flag.TryGetProperty("reason", out var reason) ? reason.GetString() : null)
            : (false, null);
    }

    private async Task<AcceptanceResult?> CheckAsync(TaskSet set, BenchRepository repo, BenchTask task, string branch, string image, BenchOptions options, CancellationToken ct)
    {
        try
        {
            var directory = await workspace.FetchBranchAsync(repo, branch, ct);
            if (directory is null)
            {
                await output.WriteLineAsync($"{task.Id}: branch {branch} is not on the remote; no acceptance check");
                return null;
            }
            var timeout = task.Acceptance.TimeoutMinutes is { } m ? TimeSpan.FromMinutes(m) : options.Environment.AcceptanceTimeout;
            return await acceptance!.RunAsync(new AcceptanceRequest(task.Id, directory, branch, repo.Commit, TaskSetLoader.AcceptanceArgv(set, task, repo), timeout, image, options.Environment.Allow), ct);
        }
        catch (Exception e) when (e is BenchException or ChargehandException)
        {
            // A check that could not run is not a pass; the tail says why.
            return new AcceptanceResult(false, -1, false, 0, e.Message);
        }
    }

    private static TaskResult Result(BenchRepository repo, BenchTask task, string state, string? errorCode, string? detail, string? branch, string? prUrl, TaskTelemetry tally,
        AcceptanceResult? acceptance, bool asked, bool answered, bool flagged, string? flagReason) =>
        new(task.Id, repo.Name, repo.Ecosystem, task.Size, task.Kind, state, errorCode, detail, branch, prUrl, acceptance, asked, answered, flagged, flagReason,
            tally.UsdEstimate, tally.UsdEstimatePartial, tally.Tokens, tally.CacheRead, tally.CacheWrite, tally.Turns, tally.Continuations, tally.Governor, tally.WallSeconds,
            Outcomes.Classify(task.Kind, state, errorCode, acceptance));
}
