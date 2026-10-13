using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Containers;
using Chargehand.Contracts;
using Chargehand.Egress;
using Chargehand.Results;
using Chargehand.RunLog;
using Chargehand.Signing;
using Chargehand.Verification;
using Chargehand.Workspace;

namespace Chargehand.Driven;

/// <summary>What a batch needs from the outside world; the CLI builds it from the profile when driven sessions are on, a test supplies fakes.</summary>
/// <param name="Secret">Resolves a secret item by name (the profile's sources).</param>
/// <param name="Host">Where admission reads the host's free memory and load (the runner, or this machine); null: tasks start by count only.</param>
/// <param name="HandoverFor">The handover of a batch; null: <see cref="Handover"/> with a <see cref="ContainerVerifier"/> on <see cref="Engine"/> and <see cref="PullRequestsFor"/>.</param>
/// <param name="Integration">What the integrator does on GitHub as the App (ADR 0046); null: nothing is integrated on GitHub even if the profile has a <c>driven.integrator</c> block.</param>
/// <param name="ForgeFor">The forge of a remote that is not on GitHub, given the API token (<c>driven.forge.token_secret</c>, else the push credential); null: such a batch is refused.</param>
/// <param name="SupportCheckFor">The support check of a batch's claims, given the account that started it so the check's model calls use that account's credential (ADR 0053); null: no check.</param>
/// <param name="Planner">Plans a batch before it runs (ADR 0049); null: a batch that asks for a plan reports it skipped.</param>
/// <param name="Stacker">Makes the starting branch of a task that depends on others (ADR 0049); null: <see cref="GitStackSeeder"/>, with git on this host.</param>
/// <param name="Retro">Writes the retro after a batch when <c>driven.learning.retro</c> is on (ADR 0045); null: such a batch reports the retro skipped.</param>
/// <param name="RetroSink">Gets each written retro besides the batch result (ADR 0045); null: nowhere. No tracker write path exists yet, so none ships.</param>
/// <param name="Brancher">Fetches the pushed branch a review round continues (ADR 0051); null: <see cref="GitBranchSeeder"/>, with git on this host.</param>
public sealed record DrivenServices(IContainerEngine Engine, IWorkspaceEngine Workspace, ISessionVolumes Volumes, Func<PushCredential, IPullRequests> PullRequestsFor, ITaskSource? TaskSource,
    Func<RepositoryRef, string, CancellationToken, Task<DrivenCheckout>> Checkout, IEvidenceResolver Resolver, Func<string, string> Secret,
    Func<string?, Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>>?>? SupportCheckFor = null, Func<ContainerVerifierSettings, PushCredential, IBranchHandover>? HandoverFor = null,
    TimeSpan? Poll = null, IHostSource? Host = null, IIntegrationHost? Integration = null, Func<RemoteRepository, string, IForge>? ForgeFor = null, IBatchPlanner? Planner = null, IStackSeeder? Stacker = null,
    IBatchRetro? Retro = null, IRetroSink? RetroSink = null, IBranchSeeder? Brancher = null);

/// <summary>A request with a <c>driven</c> block, from start to the batch's result (ADR 0039): tasks resolved, the batch network up, tasks run under the batch's limits by
/// <see cref="TaskRunner"/>, the network down, and one <c>result/v1</c> that lists the tasks. Every refusal (driven sessions off, no push credential, no image) is a failed
/// result with a code, recorded like any run. How the model credential reaches a session, recorded on the batch result: a subscription token goes only to the batch's egress container,
/// which exchanges a per-task token for it (<c>token_exchange</c>); a key an operator's gateway issued is in the session's environment (<c>gateway_key</c>); a plain API key too
/// (<c>environment</c>).</summary>
/// <param name="learning">Where each finished task's record is written and pull request outcomes are read back (ADR 0045); null: nothing is recorded.</param>
public sealed class DrivenRun(Profile profile, IRunLog log, string rootDirectory, DrivenServices? services, string? stateDirectory = null, ILearningStore? learning = null) : IDisposable
{
    /// <summary>The learning records this server keeps, for <c>GET /v1/learn</c>; null when none are kept.</summary>
    public ILearningStore? Learning => learning;

    /// <summary>How often <see cref="RefreshPrOutcomesAsync"/> should run.</summary>
    public TimeSpan PrCheckEvery => TimeSpan.FromMinutes((profile.Driven?.Learning ?? new DrivenLearning()).PrCheckMinutes);

    public const string CredentialDelivery = "environment";

    /// <summary>The real credential is only in the batch's egress container; a session holds a per-task token worth nothing outside its run and network.</summary>
    public const string TokenExchangeDelivery = "token_exchange";

    /// <summary>The containers hold a key the gateway issued for driven sessions (a budget, revocable), not the real credential.</summary>
    public const string GatewayKeyDelivery = "gateway_key";

    private readonly BatchStateStore? _store = stateDirectory is null ? null : new BatchStateStore(stateDirectory);

    private readonly SemaphoreSlim _global = new(Math.Max(1, profile.Driven?.MaxParallelTotal ?? 4));

    private readonly HostAdmission? _admission = services?.Host is { } host
        ? new HostAdmission(host, profile.Driven?.Admission ?? new DrivenAdmission(), log: message => Console.Error.WriteLine($"chargehand: {message}"))
        : null;

    public void Dispose()
    {
        _global.Dispose();
        _admission?.Dispose();
    }

    /// <param name="resume">The saved state of a batch a restart interrupted: the batch carries on from it under the same run id (ADR 0043).</param>
    /// <param name="actor">The account that started the batch (ADR 0050); a resumed batch keeps the one its state saved.</param>
    public async Task<ResultContract> RunAsync(RunRequest request, string runId, TaskTokenMinter mint, CancellationToken ct, Action<RunStatus>? progress = null,
        Func<bool>? cancelledByCaller = null, BatchState? resume = null, string? actor = null)
    {
        actor ??= resume?.Actor;
        var started = resume?.Started ?? DateTimeOffset.UtcNow;
        // A server that is stopping, as against a caller who cancels, leaves the batch for the next process to carry on; only when its state is saved.
        bool Detaching() => _store is not null && ct.IsCancellationRequested && cancelledByCaller?.Invoke() != true;
        // As for an orchestrator run: under serve the request's activity is not recorded, and a parent-based sampler would drop the batch's spans.
        if (Activity.Current is { Recorded: false })
            Activity.Current = null;
        using var run = Telemetry.Source.StartActivity(DrivenTelemetry.RunSpan);
        var traceId = resume?.TraceId ?? run?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        run?.SetTag("langfuse.trace.name", DrivenTelemetry.RunSpan);
        run?.SetTag("chargehand.run_id", runId);
        run?.SetTag("chargehand.preset", request.Context.Preset);
        run?.SetTag("chargehand.repository", request.Context.Repository?.Path);
        run?.SetTag("chargehand.driven.task_count", request.Driven?.Tasks.Count);
        var version = profile.ClaudeCode?.Version ?? "";
        var chain = new PromptChain([], new AsSent($"claude-code/{version}", "driven", "batch", started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        // A resumed batch is written again so the log names the process that runs it now.
        await log.AppendAsync(new StartRecord(runId, started, traceId, request, Environment.ProcessId, BootId: ProcessBoot.Id, Actor: actor), ct);
        progress?.Invoke(RunStatus.Of(runId, RunState.Running, RunEventKind.Started));

        ResultContract result;
        System.Security.Cryptography.ECDsa? key = null;
        try
        {
            key = ResultSigner.Load(profile.Signing, Environment.GetEnvironmentVariable);
            result = await RunBatchAsync(request, runId, started, traceId, mint, key is null ? null : r => ResultSignature.Sign(r, key), progress, run, resume, Detaching, () => cancelledByCaller?.Invoke() == true, ct, actor);
        }
        catch (Exception e) when (e is not OperationCanceledException || cancelledByCaller?.Invoke() == true)
        {
            var error = e is OperationCanceledException
                ? new ResultError(ErrorCode.Cancelled, "the run was cancelled", false, "Start the batch again if it is still wanted; nothing was pushed.")
                : ChargehandException.ErrorOf(e);
            result = new ResultContract("result/v1", runId, "driven", traceId, chain, ResultStatus.Failed, error.Message, [], [], [], [error.Message], 0, new Usage(0, 0, 0, 0, 0), error);
        }
        if (Detaching())
        {
            key?.Dispose();
            return result;
        }
        result = result with { Actor = actor };
        if (key is not null)
        {
            result = ResultSignature.Sign(result, key);
            key.Dispose();
        }
        run?.SetTag("chargehand.contract.status", JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString()));
        run?.SetTag("chargehand.error.code", result.Error is { } failure ? JsonNamingPolicy.SnakeCaseLower.ConvertName(failure.Code.ToString()) : null);
        if (result.Status != ResultStatus.Completed)
            run?.SetStatus(ActivityStatusCode.Error, result.Error?.Message);
        await log.AppendAsync(new RunRecord(runId, started, DateTimeOffset.UtcNow, request.Context.Preset, null, null, result, Actor: actor), cancelledByCaller?.Invoke() == true ? CancellationToken.None : ct);
        _store?.Delete(runId);
        progress?.Invoke(RunStatus.Of(runId, RunStatus.StateOf(result.Status), RunEventKind.RunFinished) with { Result = result });
        return result;
    }

    /// <summary>The batches a restart interrupted: saved state with no run record (ADR 0043). State whose run record exists is a leftover and is deleted.</summary>
    public async Task<IReadOnlyList<BatchState>> UnfinishedAsync(CancellationToken ct)
    {
        if (_store is null || services is null)
            return [];
        List<BatchState> open = [];
        foreach (var state in _store.List())
        {
            if ((await log.ReadAsync(state.RunId, ct)).Run is not null)
                _store.Delete(state.RunId);
            else
                open.Add(state);
        }
        return open;
    }

    /// <summary>Removes chargehand's containers, networks and volumes that belong to no batch in <paramref name="resumed"/>: what a crash or a restart left behind. The engine's
    /// label keeps it to chargehand's own; the name must carry chargehand's prefix too.</summary>
    /// <returns>The names removed.</returns>
    public async Task<IReadOnlyList<string>> SweepOrphansAsync(IEnumerable<BatchState> resumed, CancellationToken ct)
    {
        if (services is null)
            return [];
        var known = resumed.SelectMany(b => b.RunIds).ToHashSet(StringComparer.Ordinal);
        var listed = await services.Engine.ListAsync(ct);
        List<string> removed = [];
        async Task Sweep(IEnumerable<string> names, Func<string, string?> idOf, Func<string, Task> remove)
        {
            foreach (var name in names)
            {
                if (idOf(name) is not { } id || known.Contains(id))
                    continue;
                try
                {
                    await remove(name);
                    removed.Add(name);
                }
                catch (ChargehandException)
                {
                    // One resource the engine will not release must not keep the rest from being swept.
                }
            }
        }
        await Sweep(listed.Containers, ContainerRunId, name => services.Engine.RemoveAsync(name, ct));
        await Sweep(listed.Networks, name => IdAfter(name, "chargehand-net-"), name => services.Engine.RemoveNetworkAsync(name, ct));
        var kept = await KeptForContinuationAsync(listed.Volumes, ct);
        // An output volume kept for a continuation is no leftover; a work volume never is one.
        await Sweep(listed.Volumes, name => IdAfter(name, "chargehand-work-") ?? (IdAfter(name, "chargehand-out-") is { } id && !kept.Contains(id) ? id : null), name => services.Engine.RemoveVolumeAsync(name, ct));
        return removed;
    }

    /// <summary>The runs whose output volume a later task may still continue from (ADR 0045): a task that ended with kept work, in the last <see cref="ContinuationSource.KeptFor"/>, and the
    /// session that holds its bundle.</summary>
    private async Task<HashSet<string>> KeptForContinuationAsync(IEnumerable<string> volumes, CancellationToken ct)
    {
        HashSet<string> kept = [];
        foreach (var run in volumes.Select(v => IdAfter(v, "chargehand-out-")).OfType<string>())
            if ((await log.ReadAsync(run, ct)).Run is { } record && DateTimeOffset.UtcNow - record.Finished <= ContinuationSource.KeptFor && ContinuationSource.KeptWorkOf(record.Result) is { } work)
            {
                kept.Add(run);
                kept.Add(work.BundleRun);
            }
        return kept;
    }

    private static string? IdAfter(string name, string prefix) => name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : null;

    /// <summary>The batch id in <c>chargehand-svc-&lt;service&gt;-&lt;batch&gt;</c> (ADR 0048); a service name has no hyphen, so the first one after it ends the name.</summary>
    private static string? ServiceBatch(string name) => IdAfter(name, "chargehand-svc-") is { } rest && rest.IndexOf('-') is > 0 and var dash ? rest[(dash + 1)..] : null;

    private static string? ContainerRunId(string name) =>
        IdAfter(name, "chargehand-egress-") ?? ServiceBatch(name) ?? IdAfter(name, "chargehand-prep-") ?? IdAfter(name, "chargehand-xfer-") ?? IdAfter(name, "chargehand-");

    private async Task<ResultContract> RunBatchAsync(RunRequest request, string runId, DateTimeOffset started, string traceId, TaskTokenMinter mint, Func<ResultContract, ResultContract>? sign,
        Action<RunStatus>? progress, Activity? span, BatchState? resume, Func<bool> detaching, Func<bool> cancelledByCaller, CancellationToken ct, string? actor = null)
    {
        var settings = profile.Driven ?? new DrivenSettings();
        // Throws when the profile has driven sessions off, so a refused request touches nothing else.
        var (resolved, unresolved) = resume is null
            ? await DrivenBatch.PrepareAsync(request, settings, services?.TaskSource, ct, new ContinuationSource(log))
            : ([.. resume.Tasks.Select(t => Resumed(request, t))], resume.Unresolved);
        IReadOnlyList<ResolvedTask> ready = resolved;
        var s = services ?? throw new ChargehandException(ErrorCode.ContainerUnavailable, "driven sessions are on, but no container engine is set up for them", "Check driven.runner in the profile, or install Docker.");
        var claude = profile.ClaudeCode ?? throw new ChargehandException(ErrorCode.InvalidRequest, "driven sessions need a claude_code block in the profile", "Add claude_code with an api_key_secret or an oauth_token_secret.");
        var image = settings.Images is { Count: > 0 } images ? images[0] : throw new ChargehandException(ErrorCode.InvalidRequest, "driven.images is empty", "List the session image by digest in driven.images.");
        var preset = Preset.Load(Path.Combine(rootDirectory, "presets"), request.Context.Preset);
        var drivenPreset = preset.Driven ?? throw new ChargehandException(ErrorCode.InvalidRequest, $"preset '{preset.Name}' has no driven block", "Use the driven preset for a batch.");
        if (settings.Limits is { } profileLimits)
            drivenPreset = profileLimits.Apply(drivenPreset);
        var (_, kind) = Orchestrator.AnswerKind(preset);

        // An account runs on its own key, or on the server's only where the profile lets it (ADR 0053); an API key has a dollar price, so its cap binds.
        var own = new AccountCredentials(profile.Http?.Accounts).For(actor);
        var priced = own is not null || claude.ApiKeySecret is not null;
        var perTaskUsd = Math.Min(kind.Budget.MaxUsd, request.Driven!.MaxUsdTotal ?? decimal.MaxValue);
        var limits = DrivenBatch.Limits(request, settings, drivenPreset, perTaskUsd, priced, _global);
        var push = settings.PushSecret is { } pushSecret ? new PushCredential(Resolve(s, pushSecret)) : throw new ChargehandException(ErrorCode.CredentialUnavailable,
            "driven.push_secret is not set, so nothing could be pushed", "Name the secret item that holds the credential which pushes non-default branches.");
        var modelEnvironment = new Dictionary<string, string>
        {
            [priced ? "ANTHROPIC_API_KEY" : "CLAUDE_CODE_OAUTH_TOKEN"] = Resolve(s, own?.ApiKeySecret ?? (priced ? claude.ApiKeySecret : claude.OauthTokenSecret)
                ?? throw new ChargehandException(ErrorCode.CredentialUnavailable, "claude_code names no credential", "Set api_key_secret or oauth_token_secret.")),
        };

        var forward = settings.Network?.McpForward;
        var port = forward is null ? 0 : int.Parse(forward[(forward.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
        // The server's gateway is for the server's credential: an account's own key goes direct to Anthropic, or to the gateway its model block names.
        var model = own is null ? DrivenModelRoute.Parse(settings.Network?.ModelUrl, priced, forward is null ? null : port)
            : DrivenModelRoute.Parse(own.BaseUrl, priced, forward is null ? null : port, "http.accounts[].model.base_url");
        // A subscription token without a gateway of the operator's goes only to the batch's egress container, which exchanges a per-task token for it (ADR 0039,
        // decision 9). An API key, or a key a gateway issued, stays in the session's environment: the exchange is measured on the subscription only.
        var exchange = !priced && model is null ? new ModelGatewaySpec(modelEnvironment.Values.Single(), resume?.TokenKey ?? ModelTokens.NewKey()) : null;
        // Planning (ADR 0049) changes sizes, goals and the chain of tasks before they are saved, so a restart carries the planned batch and does not plan again.
        var plan = resume?.Plan;
        if (resume is null && (request.Driven!.Plan ?? settings.Planning?.Enabled ?? false))
        {
            (ready, plan) = await PlanAsync(request, ready, runId, request.Driven.Plan is not null ? "request" : "profile", s.Planner, ct);
            progress?.Invoke(RunStatus.Of(runId, RunState.Running, RunEventKind.Plan) with { Detail = plan.Describe() });
        }
        var journal = _store is null ? null : new BatchJournal(_store, resume ?? new BatchState(runId, started, traceId, request, exchange?.TokenKey,
            [.. ready.Select(t => new TaskEntry(t.Id, t.Ref, t.Goal, From: t.From, DependsOn: t.DependsOn, Size: t.Size, SplitFrom: t.SplitFrom, SizeFrom: t.SizeFrom))], unresolved, plan, actor));

        var otlp = DrivenOtlpRoute.Parse(settings.Network?.OtlpUrl,
            new[] { forward is null ? (int?)null : port, model?.ForwardPort }.OfType<int>());

        var checkout = await s.Checkout(request.Context.Repository!, request.Context.Preset, ct);
        // A forge other than GitHub acts with one token, both to open the pull request and to integrate it.
        var forge = RemoteRepository.Parse(checkout.RemoteUrl, settings.Forge) is { Forge: not ForgeKind.GitHub } remote
            ? s.ForgeFor?.Invoke(remote, settings.Forge?.TokenSecret is { } tokenSecret ? Resolve(s, tokenSecret) : push.Value)
                ?? throw new ChargehandException(ErrorCode.InvalidRequest, $"the remote is a {remote.Forge} project, but this server has no support for it set up", "Check driven.forge in the profile.")
            : null;
        var integrator = settings.Integrator is { } integration && (forge ?? s.Integration) is { } integrationHost
            ? new Integrator(new IntegratorPolicy(integration), integrationHost, integration.CiFix, forge: settings.Forge) : null;
        span?.SetTag("chargehand.base_commit", checkout.Commit);
        // A kept bundle, and a pushed branch, are branches from the commit their task started at; fetched into a workspace at another commit they would not apply.
        if (ready.FirstOrDefault(t => (t.From?.Kept?.Base ?? t.From?.Pushed?.Base) is { } startedAt && startedAt != checkout.Commit) is { } moved)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"task '{moved.Id}' continues work that started at commit {moved.From!.Kept?.Base ?? moved.From.Pushed!.Base}, but this request's repository is at {checkout.Commit}",
                "Send the request with context.repository.commit set to the commit the earlier task started at.");
        span?.SetTag("chargehand.credential_delivery", Delivery(model, exchange));
        List<string> forwards = [];
        if (forward is not null)
            forwards.Add($"{port}={forward}");
        if (model?.Forward is { } modelForward)
            forwards.Add(modelForward);
        if (otlp is not null)
            forwards.Add(otlp.Forward);
        IReadOnlyList<string> allow = [.. (drivenPreset.Allow ?? []).Concat(settings.Network?.Allow ?? []).Concat(model?.AllowHost is { } host ? new[] { host } : []).Distinct()];
        // ADR 0048: the repository's session image, cache volumes and test services. With no driven.services key the plan is only the facts for the environment note.
        var identity = SharedServicesPlan.Identity(checkout.RemoteUrl, request.Context.Repository!.Path);
        var shared = SharedServicesPlan.For(settings.Services, checkout.Directory, request.Context.Verify, identity, runId, BatchNetwork.NetworkName(runId));
        var sessionImage = SharedServicesPlan.PickImage(settings.Services, shared.Ecosystems, image);
        var batchNetwork = new BatchNetwork(s.Engine);
        var net = resume is null
            ? await batchNetwork.CreateAsync(runId, image, allow, ct, settings.Network?.Outside ?? BatchNetwork.DefaultOutside, forwards.Count == 0 ? null : forwards, exchange)
            : await batchNetwork.EnsureAsync(runId, image, allow, ct, settings.Network?.Outside ?? BatchNetwork.DefaultOutside, forwards.Count == 0 ? null : forwards, exchange);
        BatchOutcome ran;
        try
        {
            if (resume is null)
                await new BatchServices(s.Engine).StartAsync(shared.Services, ct);
            else
                await new BatchServices(s.Engine).EnsureAsync(shared.Services, ct);
            var verifierSettings = new ContainerVerifierSettings(sessionImage, checkout.Directory, checkout.Commit, net.Network, net.ProxyUrl(runId), drivenPreset.MemoryMb, drivenPreset.Cpus, drivenPreset.Pids, shared);
            var handover = s.HandoverFor?.Invoke(verifierSettings, push)
                ?? new Handover(new ContainerVerifier(s.Engine, s.Workspace, verifierSettings), forge ?? s.PullRequestsFor(push), settings.Forge);
            var task = new TaskRunnerSettings(runId, request, sessionImage, checkout.Directory, checkout.Commit, checkout.RemoteUrl, checkout.BaseBranch, net,
                forward is null ? null : $"{BatchNetworkInfo.ServiceUrl(port)}/v1/mcp", drivenPreset, limits.PerTask, priced, modelEnvironment, push,
                Path.Combine(profile.WorkerRoot, ".driven"), kind.Model, claude.Version,
                Environment.GetEnvironmentVariable("CHARGEHAND_E2E_LOCAL_REMOTE") == "1", model?.BaseUrl, forward is not null || model?.Forwarded == true || otlp is not null, otlp,
                profile.Prices, profile.Telemetry?.UsageOnSpans == true, exchange?.TokenKey, settings.Governor ?? new DrivenGovernor(), integrator, shared, actor, RemoteRepository.Parse(checkout.RemoteUrl, settings.Forge));
            var runner = new TaskRunner(s.Engine, s.Workspace, s.Volumes, handover, log, mint, task, s.Resolver, s.SupportCheckFor?.Invoke(actor), sign, s.Poll, publish: progress, journal: journal, detaching: detaching, stacker: s.Stacker, learning: learning, brancher: s.Brancher);
            ran = await new BatchScheduler(runner, admission: _admission, taskMemoryMb: drivenPreset.MemoryMb).RunAsync(ready, limits, ct, o =>
            {
                if (detaching())
                    return;
                journal?.Finish(o);
                progress?.Invoke(TaskFinished(runId, o));
            }, (id, why) => progress?.Invoke(RunStatus.Of(runId, RunState.Running, RunEventKind.SessionProgress) with { TaskId = id, Detail = $"task {id}: {why}" }),
            o => progress?.Invoke(TaskFinished(runId, o)));
        }
        finally
        {
            if (!detaching())
            {
                // A network with a container still attached cannot be removed: the services go first.
                await new BatchServices(s.Engine).RemoveAsync(shared.Services, CancellationToken.None);
                await new BatchNetwork(s.Engine).RemoveAsync(net, CancellationToken.None);
            }
        }

        var retro = detaching() || ct.IsCancellationRequested ? null : await RetroAsync(request, runId, ct);
        // In request order, a task the plan split in place: tasks that did not resolve were never started.
        List<TaskOutcome> all = [];
        foreach (var t in request.Driven.Tasks)
            all.AddRange(unresolved.FirstOrDefault(u => u.Id == t.Id) is { } none
                ? [none]
                : ready.Where(r => r.Id == t.Id || r.SplitFrom == t.Id).Select(r => ran.Tasks.First(o => o.Id == r.Id)));
        span?.SetTag("chargehand.driven.pr_urls", all.Select(o => o.PrUrl).OfType<string>().ToArray());
        span?.SetTag("chargehand.driven.tokens", all.Sum(o => o.Tokens));
        return DrivenResult.BuildBatch(runId, traceId, "", new BatchOutcome(all, ran.Action, ran.Error), Delivery(model, exchange), priced, claude.Version,
            cancelledByCaller: ct.IsCancellationRequested && cancelledByCaller(), plan, retro);
    }

    /// <summary>The retro after a batch (ADR 0045); null while <c>driven.learning.retro</c> is off. One that cannot be written is reported skipped with the reason, and never changes the batch's result.
    /// A cancel is not caught.</summary>
    private async Task<BatchRetro?> RetroAsync(RunRequest request, string runId, CancellationToken ct)
    {
        if (profile.Driven?.Learning is not { Retro: true })
            return null;
        if (learning is null || services?.Retro is not { } writer)
            return BatchRetro.Skipped("this server keeps no learning records or has no retro writer");
        var records = (await learning.ReadAsync(ct)).Where(e => e.Record.BatchId == runId).Select(e => e.Record).ToList();
        if (records.Count == 0)
            return BatchRetro.Skipped("no task of the batch left a learning record");
        BatchRetro retro;
        try
        {
            retro = await writer.WriteAsync(new RetroInput(request, runId, records), ct);
        }
        catch (Exception e) when (e is RetroException or ChargehandException or HttpRequestException or IOException)
        {
            return BatchRetro.Skipped(e.Message, records.Count);
        }
        if (services.RetroSink is { } sink)
        {
            try
            {
                await sink.PublishAsync(runId, retro, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.Error.WriteLine($"chargehand: the retro sink failed for batch {runId}: {e.Message}");
            }
        }
        return retro;
    }

    /// <summary>The forge to ask about a pull request's outcome (ADR 0045): on GitHub the App's host, on any other forge the forge token (<c>driven.forge.token_secret</c>, else the push credential). Null when
    /// that credential is not configured or does not resolve: the outcomes of that forge's pull requests are then not read.</summary>
    public IIntegrationHost? ReadHost(RemoteRepository repository)
    {
        if (services is null)
            return null;
        if (repository.Forge == ForgeKind.GitHub)
            return services.Integration;
        var settings = profile.Driven ?? new DrivenSettings();
        if (services.ForgeFor is not { } forgeFor || (settings.Forge?.TokenSecret ?? settings.PushSecret) is not { } item)
            return null;
        try
        {
            return forgeFor(repository, services.Secret(item));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>One pass of reading back what became of the pull requests of recent tasks (ADR 0045). Does nothing without learning records or a forge credential.</summary>
    /// <returns>The outcomes written.</returns>
    public Task<int> RefreshPrOutcomesAsync(CancellationToken ct) =>
        learning is null ? Task.FromResult(0) : new PrOutcomeTracker(learning, ReadHost).RefreshAsync((profile.Driven?.Learning ?? new DrivenLearning()).PrDays, ct);

    /// <summary>Asks the planner (ADR 0049) and applies what it says; a planner that is missing, fails or is unusable leaves the batch as the caller sent it and says so in the plan. A cancel is not caught.</summary>
    private static async Task<(IReadOnlyList<ResolvedTask> Tasks, BatchPlan Plan)> PlanAsync(RunRequest request, IReadOnlyList<ResolvedTask> tasks, string runId, string source, IBatchPlanner? planner, CancellationToken ct)
    {
        if (planner is null)
            return (tasks, PlanApplier.Skipped(source, "this server has no planner", null, tasks));
        try
        {
            return PlanApplier.Apply(tasks, await planner.ProposeAsync(new PlanInput(request, tasks, runId), ct), source);
        }
        catch (Exception e) when (e is PlanException or ChargehandException or HttpRequestException or IOException)
        {
            return (tasks, PlanApplier.Skipped(source, e.Message, null, tasks));
        }
    }

    private static ResolvedTask Resumed(RunRequest request, TaskEntry entry)
    {
        var asked = request.Driven?.Tasks.FirstOrDefault(t => t.Id == entry.Id);
        return new ResolvedTask(entry.Id, entry.Ref, entry.Goal, asked?.MaxTurns, asked?.MaxMinutes, entry.Size ?? asked?.Size, asked?.BudgetUsd, entry.From, entry.DependsOn ?? asked?.DependsOn, SplitFrom: entry.SplitFrom, SizeFrom: entry.SizeFrom);
    }

    private static RunStatus TaskFinished(string runId, TaskOutcome o) =>
        RunStatus.Of(runId, RunState.Running, RunEventKind.TaskFinished) with
        {
            TaskId = o.Id,
            Branch = o.Branch,
            PrUrl = o.PrUrl,
            Tokens = o.Tokens,
            Detail = $"task {o.Id}: {JsonNamingPolicy.SnakeCaseLower.ConvertName(o.State.ToString())}{(o.Detail is { } d ? $": {d}" : "")}",
        };

    private static string Delivery(DrivenModelRoute? model, ModelGatewaySpec? exchange) =>
        model is not null ? GatewayKeyDelivery : exchange is not null ? TokenExchangeDelivery : CredentialDelivery;

    private static string Resolve(DrivenServices s, string item)
    {
        try
        {
            return s.Secret(item);
        }
        catch (InvalidOperationException e)
        {
            throw new ChargehandException(ErrorCode.CredentialUnavailable, e.Message, "Check the profile's secret sources.");
        }
    }
}
