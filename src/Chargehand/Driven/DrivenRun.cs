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

namespace Chargehand.Driven;

/// <summary>chargehand's own checkout of the request's commit and where it came from.</summary>
public sealed record DrivenCheckout(string Directory, string Commit, string RemoteUrl, string BaseBranch);

/// <summary>What a batch needs from the outside world; the CLI builds it from the profile when driven sessions are on, a test supplies fakes.</summary>
/// <param name="Secret">Resolves a secret item by name (the profile's sources).</param>
/// <param name="Host">Where admission reads the host's free memory and load (the runner, or this machine); null: tasks start by count only.</param>
/// <param name="HandoverFor">The handover of a batch; null: <see cref="Handover"/> with a <see cref="ContainerVerifier"/> on <see cref="Engine"/> and <see cref="PullRequestsFor"/>.</param>
/// <param name="Integration">What the integrator does on GitHub as the App (ADR 0046); null: nothing is integrated even if the profile has a <c>driven.integrator</c> block.</param>
public sealed record DrivenServices(IContainerEngine Engine, IWorkspaceEngine Workspace, ISessionVolumes Volumes, Func<PushCredential, IPullRequests> PullRequestsFor, ITaskSource? TaskSource,
    Func<RepositoryRef, string, CancellationToken, Task<DrivenCheckout>> Checkout, IEvidenceResolver Resolver, Func<string, string> Secret,
    Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>>? SupportCheck = null, Func<ContainerVerifierSettings, PushCredential, IBranchHandover>? HandoverFor = null,
    TimeSpan? Poll = null, IHostSource? Host = null, IIntegrationHost? Integration = null);

/// <summary>A request with a <c>driven</c> block, from start to the batch's result (ADR 0039): tasks resolved, the batch network up, tasks run under the batch's limits by
/// <see cref="TaskRunner"/>, the network down, and one <c>result/v1</c> that lists the tasks. Every refusal (driven sessions off, no push credential, no image) is a failed
/// result with a code, recorded like any run. How the model credential reaches a session, recorded on the batch result: a subscription token goes only to the batch's egress container,
/// which exchanges a per-task token for it (<c>token_exchange</c>); a key an operator's gateway issued is in the session's environment (<c>gateway_key</c>); a plain API key too
/// (<c>environment</c>).</summary>
public sealed class DrivenRun(Profile profile, IRunLog log, string rootDirectory, DrivenServices? services, string? stateDirectory = null) : IDisposable
{
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
    public async Task<ResultContract> RunAsync(RunRequest request, string runId, TaskTokenMinter mint, CancellationToken ct, Action<RunStatus>? progress = null,
        Func<bool>? cancelledByCaller = null, BatchState? resume = null)
    {
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
        await log.AppendAsync(new StartRecord(runId, started, traceId, request, Environment.ProcessId, BootId: ProcessBoot.Id), ct);
        progress?.Invoke(RunStatus.Of(runId, RunState.Running, RunEventKind.Started));

        ResultContract result;
        System.Security.Cryptography.ECDsa? key = null;
        try
        {
            key = ResultSigner.Load(profile.Signing, Environment.GetEnvironmentVariable);
            result = await RunBatchAsync(request, runId, started, traceId, mint, key is null ? null : r => ResultSignature.Sign(r, key), progress, run, resume, Detaching, () => cancelledByCaller?.Invoke() == true, ct);
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
        if (key is not null)
        {
            result = ResultSignature.Sign(result, key);
            key.Dispose();
        }
        run?.SetTag("chargehand.contract.status", JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString()));
        run?.SetTag("chargehand.error.code", result.Error is { } failure ? JsonNamingPolicy.SnakeCaseLower.ConvertName(failure.Code.ToString()) : null);
        if (result.Status != ResultStatus.Completed)
            run?.SetStatus(ActivityStatusCode.Error, result.Error?.Message);
        await log.AppendAsync(new RunRecord(runId, started, DateTimeOffset.UtcNow, request.Context.Preset, null, null, result), cancelledByCaller?.Invoke() == true ? CancellationToken.None : ct);
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
        await Sweep(listed.Volumes, name => IdAfter(name, "chargehand-work-") ?? IdAfter(name, "chargehand-out-"), name => services.Engine.RemoveVolumeAsync(name, ct));
        return removed;
    }

    private static string? IdAfter(string name, string prefix) => name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : null;

    private static string? ContainerRunId(string name) =>
        IdAfter(name, "chargehand-egress-") ?? IdAfter(name, "chargehand-prep-") ?? IdAfter(name, "chargehand-xfer-") ?? IdAfter(name, "chargehand-");

    private async Task<ResultContract> RunBatchAsync(RunRequest request, string runId, DateTimeOffset started, string traceId, TaskTokenMinter mint, Func<ResultContract, ResultContract>? sign,
        Action<RunStatus>? progress, Activity? span, BatchState? resume, Func<bool> detaching, Func<bool> cancelledByCaller, CancellationToken ct)
    {
        var settings = profile.Driven ?? new DrivenSettings();
        // Throws when the profile has driven sessions off, so a refused request touches nothing else.
        var (ready, unresolved) = resume is null
            ? await DrivenBatch.PrepareAsync(request, settings, services?.TaskSource, ct)
            : ([.. resume.Tasks.Select(t => Resumed(request, t))], resume.Unresolved);
        var s = services ?? throw new ChargehandException(ErrorCode.ContainerUnavailable, "driven sessions are on, but no container engine is set up for them", "Check driven.runner in the profile, or install Docker.");
        var claude = profile.ClaudeCode ?? throw new ChargehandException(ErrorCode.InvalidRequest, "driven sessions need a claude_code block in the profile", "Add claude_code with an api_key_secret or an oauth_token_secret.");
        var image = settings.Images is { Count: > 0 } images ? images[0] : throw new ChargehandException(ErrorCode.InvalidRequest, "driven.images is empty", "List the session image by digest in driven.images.");
        var preset = Preset.Load(Path.Combine(rootDirectory, "presets"), request.Context.Preset);
        var drivenPreset = preset.Driven ?? throw new ChargehandException(ErrorCode.InvalidRequest, $"preset '{preset.Name}' has no driven block", "Use the driven preset for a batch.");
        if (settings.Limits is { } profileLimits)
            drivenPreset = profileLimits.Apply(drivenPreset);
        var (_, kind) = Orchestrator.AnswerKind(preset);

        var priced = claude.ApiKeySecret is not null;
        var perTaskUsd = Math.Min(kind.Budget.MaxUsd, request.Driven!.MaxUsdTotal ?? decimal.MaxValue);
        var limits = DrivenBatch.Limits(request, settings, drivenPreset, perTaskUsd, priced, _global);
        var push = settings.PushSecret is { } pushSecret ? new PushCredential(Resolve(s, pushSecret)) : throw new ChargehandException(ErrorCode.CredentialUnavailable,
            "driven.push_secret is not set, so nothing could be pushed", "Name the secret item that holds the credential which pushes non-default branches.");
        var integrator = settings.Integrator is { } integration && s.Integration is { } github ? new Integrator(new IntegratorPolicy(integration), github, integration.CiFix) : null;
        var modelEnvironment = new Dictionary<string, string>
        {
            [priced ? "ANTHROPIC_API_KEY" : "CLAUDE_CODE_OAUTH_TOKEN"] = Resolve(s, (priced ? claude.ApiKeySecret : claude.OauthTokenSecret)
                ?? throw new ChargehandException(ErrorCode.CredentialUnavailable, "claude_code names no credential", "Set api_key_secret or oauth_token_secret.")),
        };

        var forward = settings.Network?.McpForward;
        var port = forward is null ? 0 : int.Parse(forward[(forward.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
        var model = DrivenModelRoute.Parse(settings.Network?.ModelUrl, priced, forward is null ? null : port);
        // A subscription token without a gateway of the operator's goes only to the batch's egress container, which exchanges a per-task token for it (ADR 0039,
        // decision 9). An API key, or a key a gateway issued, stays in the session's environment: the exchange is measured on the subscription only.
        var exchange = !priced && model is null ? new ModelGatewaySpec(modelEnvironment.Values.Single(), resume?.TokenKey ?? ModelTokens.NewKey()) : null;
        var journal = _store is null ? null : new BatchJournal(_store, resume ?? new BatchState(runId, started, traceId, request, exchange?.TokenKey,
            [.. ready.Select(t => new TaskEntry(t.Id, t.Ref, t.Goal))], unresolved));

        var otlp = DrivenOtlpRoute.Parse(settings.Network?.OtlpUrl,
            new[] { forward is null ? (int?)null : port, model?.ForwardPort }.OfType<int>());

        var checkout = await s.Checkout(request.Context.Repository!, request.Context.Preset, ct);
        span?.SetTag("chargehand.base_commit", checkout.Commit);
        span?.SetTag("chargehand.credential_delivery", Delivery(model, exchange));
        List<string> forwards = [];
        if (forward is not null)
            forwards.Add($"{port}={forward}");
        if (model?.Forward is { } modelForward)
            forwards.Add(modelForward);
        if (otlp is not null)
            forwards.Add(otlp.Forward);
        IReadOnlyList<string> allow = [.. (drivenPreset.Allow ?? []).Concat(settings.Network?.Allow ?? []).Concat(model?.AllowHost is { } host ? new[] { host } : []).Distinct()];
        var batchNetwork = new BatchNetwork(s.Engine);
        var net = resume is null
            ? await batchNetwork.CreateAsync(runId, image, allow, ct, settings.Network?.Outside ?? BatchNetwork.DefaultOutside, forwards.Count == 0 ? null : forwards, exchange)
            : await batchNetwork.EnsureAsync(runId, image, allow, ct, settings.Network?.Outside ?? BatchNetwork.DefaultOutside, forwards.Count == 0 ? null : forwards, exchange);
        BatchOutcome ran;
        try
        {
            var verifierSettings = new ContainerVerifierSettings(image, checkout.Directory, checkout.Commit, net.Network, net.ProxyUrl(runId), drivenPreset.MemoryMb, drivenPreset.Cpus, drivenPreset.Pids);
            var handover = s.HandoverFor?.Invoke(verifierSettings, push)
                ?? new Handover(new ContainerVerifier(s.Engine, s.Workspace, verifierSettings), s.PullRequestsFor(push));
            var task = new TaskRunnerSettings(runId, request, image, checkout.Directory, checkout.Commit, checkout.RemoteUrl, checkout.BaseBranch, net,
                forward is null ? null : $"{BatchNetworkInfo.ServiceUrl(port)}/v1/mcp", drivenPreset, limits.PerTask, priced, modelEnvironment, push,
                Path.Combine(profile.WorkerRoot, ".driven"), kind.Model, claude.Version,
                Environment.GetEnvironmentVariable("CHARGEHAND_E2E_LOCAL_REMOTE") == "1", model?.BaseUrl, forward is not null || model?.Forwarded == true || otlp is not null, otlp,
                profile.Prices, profile.Telemetry?.UsageOnSpans == true, exchange?.TokenKey, settings.Governor ?? new DrivenGovernor(), integrator);
            var runner = new TaskRunner(s.Engine, s.Workspace, s.Volumes, handover, log, mint, task, s.Resolver, s.SupportCheck, sign, s.Poll, publish: progress, journal: journal, detaching: detaching);
            ran = await new BatchScheduler(runner, admission: _admission, taskMemoryMb: drivenPreset.MemoryMb).RunAsync(ready, limits, ct, o =>
            {
                if (detaching())
                    return;
                journal?.Finish(o);
                progress?.Invoke(TaskFinished(runId, o));
            }, (id, why) => progress?.Invoke(RunStatus.Of(runId, RunState.Running, RunEventKind.SessionProgress) with { TaskId = id, Detail = $"task {id}: {why}" }));
        }
        finally
        {
            if (!detaching())
                await new BatchNetwork(s.Engine).RemoveAsync(net, CancellationToken.None);
        }

        // In request order: tasks that did not resolve were never started.
        var all = request.Driven.Tasks.Select(t => unresolved.FirstOrDefault(u => u.Id == t.Id) ?? ran.Tasks.First(o => o.Id == t.Id)).ToList();
        span?.SetTag("chargehand.driven.pr_urls", all.Select(o => o.PrUrl).OfType<string>().ToArray());
        span?.SetTag("chargehand.driven.tokens", all.Sum(o => o.Tokens));
        return DrivenResult.BuildBatch(runId, traceId, "", new BatchOutcome(all, ran.Action, ran.Error), Delivery(model, exchange), priced, claude.Version,
            cancelledByCaller: ct.IsCancellationRequested && cancelledByCaller());
    }

    private static ResolvedTask Resumed(RunRequest request, TaskEntry entry)
    {
        var asked = request.Driven?.Tasks.FirstOrDefault(t => t.Id == entry.Id);
        return new ResolvedTask(entry.Id, entry.Ref, entry.Goal, asked?.MaxTurns, asked?.MaxMinutes, asked?.Size, asked?.BudgetUsd);
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
