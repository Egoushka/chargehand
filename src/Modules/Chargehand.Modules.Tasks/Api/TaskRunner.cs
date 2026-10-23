using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Core;

namespace Chargehand.Modules.Tasks.Api;

/// <summary>What a session's run token is for: one repository at one commit, within the task's caps, until it expires.</summary>
public sealed record TaskGrant(string RunId, string RepositoryPath, string Commit, decimal? MaxUsd, long? MaxTokens, DateTimeOffset Expires);

public delegate string TaskTokenMinter(TaskGrant grant);

/// <param name="Repository">The request's repository; its commit is what a session's run token is limited to.</param>
/// <param name="SourcePath">chargehand's own checkout of that commit: the workspace's read-only source and the handover's scratch clone source.</param>
/// <param name="ModelEnvironment">The real model credential by environment name. It is the session container's environment only when <paramref name="GatewayKey"/> is null (the fallback
/// delivery, recorded as such on the batch result); always the list of values the handover scans for.</param>
/// <param name="GatewayKey">Set, the egress container exchanges run tokens (ADR 0039, decision 9): a session gets a token minted with this key and the model endpoint's address, and no real credential.</param>
/// <param name="McpUrl">The chargehand server's MCP address on the batch network; null: the session gets no research or review calls.</param>
/// <param name="ModelBaseUrl">The session's <c>ANTHROPIC_BASE_URL</c> when it reaches the model through a gateway; null: Anthropic directly.</param>
/// <param name="CallbackForwards">Some address the session uses is a forward on the egress container (plain HTTP), which must bypass the proxy.</param>
/// <param name="Otlp">Where the session's Claude Code exports its own logs and metrics; null: it exports nothing.</param>
/// <param name="Prices">The profile's price table, for the session span's cost.</param>
/// <param name="UsageOnSpans">The profile's <c>telemetry.usage_on_spans</c> (ADR 0021).</param>
/// <param name="Governor">The profile's <c>driven.governor</c> (ADR 0045); null: no governor in the sessions.</param>
/// <param name="Integration">The profile's <c>driven.integrator</c> (ADR 0046): marks a finished task's pull request ready, merges it by policy and fixes failing CI; null: the draft is left alone.</param>
/// <param name="Shared">The batch's cache volumes and test services and the repository facts for the session's environment note (ADR 0048); null: the note has the image's facts only.</param>
/// <param name="Actor">The account that started the batch (ADR 0050); on the task's records and result.</param>
/// <param name="LocalOnly">The repository is local-only (ADR 0069): the hand-over, the integrator and the CI fixer each refuse to push or call the forge.</param>
/// <param name="Mise">ADR 0078: set when the profile says <c>driven.environment: "mise"</c>; an install helper runs before each session; null: none, as before.</param>
/// <param name="Remote">The remote as the forge names it, for the learning record's pull request (ADR 0045); null: the remote is not one a forge can be asked about.</param>
public sealed record TaskRunnerSettings(string BatchId, RunRequest Request, string Image, string SourcePath, string BaseCommit, string RemoteUrl, string BaseBranch,
    BatchNetworkInfo Network, string? McpUrl, DrivenPreset Preset, TaskLimits Limits, bool Priced, IReadOnlyDictionary<string, string> ModelEnvironment, PushCredential Push,
    string ScratchRoot, string Model, string ClaudeVersion, bool AllowLocalRemote = false, string? ModelBaseUrl = null, bool CallbackForwards = false, DrivenOtlpRoute? Otlp = null,
    IReadOnlyDictionary<string, ModelPrice>? Prices = null, bool UsageOnSpans = false, string? GatewayKey = null, DrivenGovernor? Governor = null, Integrator? Integration = null, SharedServicesPlan? Shared = null, string? Actor = null, RemoteRepository? Remote = null, bool LocalOnly = false, MiseSetup? Mise = null);

/// <summary>What the install helper of ADR 0078 needs besides the batch's: the egress proxy's image, which its own network is given, and the network that egress joins to reach the toolchain hosts.</summary>
public sealed record MiseSetup(string EgressImage, string Outside = BatchNetwork.DefaultOutside);

/// <summary>Runs one task of a batch to its end (ADR 0039): a workspace at the pinned commit, a session container on the batch network, then the handover (chargehand's own
/// verification, the scan, the push, a draft pull request) and the task's own <c>result/v1</c> in the run log. A cancel signals the session and ends the task; it never reaches
/// the handover, so nothing is pushed. <paramref name="publish"/> gets the task's steps as events of the batch run: <c>container_started</c>, <c>session_progress</c> when the
/// session's tally changes, and the handover's <c>verify_finished</c>, <c>pushed</c> and <c>pr_opened</c>. With <paramref name="tasks"/> each change of the task's entry is an event in the journal (ADR 0061), and a task its
/// events show as begun is taken up where it stood after a server restart (ADR 0043). <paramref name="detaching"/> says the server is stopping rather than the run being cancelled: the session container is then left running for the next process.
/// A task with <see cref="ResolvedTask.Upstream"/> (ADR 0049) starts from the branches of the tasks it depends on: <paramref name="stacker"/> makes one branch of them, named for this task, and its bundle seeds
/// the first session. With a <paramref name="learning"/> store, a task that ends writes what it taught (ADR 0045). With <paramref name="records"/>, each session, its governor log, the pull request's checks and the CI fix
/// attempts are kept for the console (CHARGEHAND-214), and <paramref name="stops"/> holds how an operator stops the session the task waits on. <paramref name="results"/> (P4) builds, checks and signs the task's result. With <paramref name="steps"/>, each session start is a recorded step (ADR 0061), and a start a restart cut short is taken up under its own run id.</summary>
public sealed partial class TaskRunner(IContainerEngine engine, IWorkspaceEngine workspace, ISessionVolumes volumes, IBranchHandover handover, IRunLog log, TaskTokenMinter mint,
    TaskRunnerSettings settings, ITaskResults results,
    TimeSpan? poll = null, TimeSpan? stopGrace = null, Action<RunStatus>? publish = null, TaskJournal? tasks = null, Func<bool>? detaching = null, IStackSeeder? stacker = null, ILearningStore? learning = null, IBranchSeeder? brancher = null,
    Func<string?, ISessionVolumes>? volumesOf = null, TimeSpan? outageGrace = null, DrivenJournal? steps = null, TaskRecordStore? records = null, SessionStops? stops = null) : ITaskRunner
{
    private static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultUsagePoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WallClockSlack = TimeSpan.FromMinutes(5);

    /// <summary>How long an ended task's output volume (its bundle and handoff) is kept for a continuation: the sweep at a server start leaves it alone until then.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    /// <summary>How long a task waits for a runner that stops answering during a session (ADR 0058): a laptop that sleeps, a path that flaps. Past it the task fails and the session is swept later.</summary>
    public static readonly TimeSpan DefaultOutageGrace = TimeSpan.FromMinutes(10);

    /// <summary>The most memory (MiB) any session container of a task has used, by task id; tasks of a batch run at once on one runner instance.</summary>
    private readonly ConcurrentDictionary<string, int> _memoryPeaks = new();

    private sealed record BestRun(string RunId, string OutDirectory, int Commits);


    /// <summary>One task's run: what the phases of <see cref="RunAsync"/> share, from the resume decision to the cleanup.</summary>
    private sealed class TaskRun
    {
        public TaskEntry? Resumed { get; init; }

        public bool Attach { get; init; }

        public required string RunId { get; set; }

        public required string Branch { get; init; }

        public DateTimeOffset Started { get; init; }

        public int MaxTurns { get; init; }

        public int MaxMinutes { get; init; }

        /// <summary>Sessions after the first (ADR 0044).</summary>
        public int Continuation { get; set; }

        /// <summary>The earlier sessions' usage added up.</summary>
        public SessionOutcome? Prior { get; set; }

        /// <summary>Each session's run id, the first one's first (the branch is named after it).</summary>
        public required List<string> Runs { get; init; }

        public BestRun? Best { get; set; }

        public Dictionary<string, string?> Containers { get; } = [];

        public required string OutDirectory { get; set; }

        public string? Seed { get; set; }

        public string Goal { get; set; } = "";

        public string StartDetail { get; set; } = "";

        public string? StartFailure { get; set; }

        public StackBase? Stack { get; set; }

        public BudgetChoice? Budget { get; set; }

        public List<GovernorDecision> Decisions { get; } = [];

        public string? LastHead { get; set; }

        public string StackDirectory { get; set; } = "";

        public bool Ended { get; set; }

        /// <summary>The session that has begun and has no record yet: written when its outcome is read, or, if it is cut short, when the task ends.</summary>
        public (string RunId, DateTimeOffset Started, string Role)? Open { get; set; }

        public Activity? Span { get; set; }

        public string TraceId { get; set; } = "";

        public string Container { get; set; } = "";

        public DateTimeOffset SessionStarted { get; set; }

        public SessionOutcome Session { get; set; } = null!;

        public string? ReportJson { get; set; }

        public SessionOutcome Total { get; set; } = null!;
    }

    public async Task<TaskOutcome> RunAsync(ResolvedTask task, IProgress<TaskUsage> progress, CancellationToken ct)
    {
        var entry = tasks?.Entry(task.Id);
        if (TaskResume.Decide(entry, new ResumeFacts()) is TakeUp { Kind: ResumeKind.Done, Outcome: { } done })
            return done;
        var run = await PrepareAsync(task, entry, ct);
        // A child of the batch's span: the task's result carries the batch's trace id, so it resolves in Langfuse.
        using var span = Telemetry.Source.StartActivity(DrivenTelemetry.TaskSpan);
        run.Span = span;
        run.TraceId = TraceOf(task, run.RunId, span);
        try
        {
            await SeedAsync(task, run, ct);
            await FirstSessionAsync(task, run, ct);
            while (true)
            {
                if (!await EndSessionAsync(task, run, progress, ct))
                    break;
                if (!await ContinueAsync(task, run, ct))
                    break;
            }
            return await FinishAsync(task, run, progress, ct);
        }
        catch (Exception e)
        {
            Failed(task, run, e);
            throw;
        }
        finally
        {
            await CleanUpAsync(task, run);
        }
    }

    private static string TraceOf(ResolvedTask task, string runId, Activity? span)
    {
        span?.SetTag("chargehand.run_id", runId);
        span?.SetTag("chargehand.task.id", task.Id);
        span?.SetTag("chargehand.task.ref", task.Ref);
        return span?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
    }

    private void Failed(ResolvedTask task, TaskRun run, Exception e)
    {
        var span = run.Span;
        if (run.Open is { } cut && !Detaching)
            RecordCutShort(task, cut, e);
        span?.SetTag("chargehand.task.state", e is OperationCanceledException ? "cancelled" : "failed");
        span?.SetTag("chargehand.error.code", e is OperationCanceledException ? "cancelled" : JsonNamingPolicy.SnakeCaseLower.ConvertName(ChargehandException.ErrorOf(e).Code.ToString()));
        span?.SetStatus(ActivityStatusCode.Error);
    }

    private async Task CleanUpAsync(ResolvedTask task, TaskRun run)
    {
        DeleteQuietly(run.StackDirectory);
        foreach (var up in task.Upstream ?? [])
            DeleteQuietly(Path.Combine(settings.ScratchRoot, "out", up.RunId));
        // A server that is stopping leaves an unfinished task as it is: the next process takes it up (ADR 0043).
        if (run.Ended || !Detaching)
        {
            if (task.From is { } continued)
                foreach (var earlierRun in new[] { continued.LastRun, continued.Kept?.BundleRun }.OfType<string>())
                    DeleteQuietly(Path.Combine(settings.ScratchRoot, "out", earlierRun));
            // A start cancelled in flight can leave the container docker already made; the name is fixed, so remove it by name.
            run.Containers.TryAdd(run.RunId, null);
            foreach (var (runId, name) in run.Containers)
            {
                await RemoveSessionAsync(runId, name, CancellationToken.None);
                DeleteQuietly(Path.Combine(settings.ScratchRoot, "out", runId));
                DeleteQuietly(Path.Combine(settings.ScratchRoot, runId));
                // The last session's output volume and the one the handover read stay, as they always have; an earlier session's is no use once a later one has its bundle.
                if (runId != run.RunId && runId != run.Best?.RunId)
                    await engine.RemoveVolumeAsync(RunnerNames.Out(runId), CancellationToken.None);
            }
        }
    }

    private bool Detaching => detaching?.Invoke() == true;

    /// <summary>A transition of the task's entry (<see cref="TaskMachine"/>), appended to the batch's task flow; nothing without one.</summary>
    private Task RecordAsync(string taskId, TaskEvent e) => tasks?.RecordAsync(taskId, e) ?? Task.CompletedTask;

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is swept with the worker root; it must not turn a finished task into a failure.
        }
    }
}
