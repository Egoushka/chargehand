using System.Diagnostics;
using System.Globalization;
using Chargehand.Config;
using Chargehand.Containers;
using Chargehand.Contracts;
using Chargehand.Results;
using Chargehand.RunLog;
using Chargehand.Signing;
using Chargehand.Verification;

namespace Chargehand.Driven;

/// <summary>chargehand's own checkout of the request's commit and where it came from.</summary>
public sealed record DrivenCheckout(string Directory, string Commit, string RemoteUrl, string BaseBranch);

/// <summary>What a batch needs from the outside world; the CLI builds it from the profile when driven sessions are on, a test supplies fakes.</summary>
/// <param name="Secret">Resolves a secret item by name (the profile's sources).</param>
/// <param name="HandoverFor">The handover of a batch; null: <see cref="Handover"/> with a <see cref="ContainerVerifier"/> on <see cref="Engine"/> and <see cref="PullRequestsFor"/>.</param>
public sealed record DrivenServices(IContainerEngine Engine, IWorkspaceEngine Workspace, ISessionVolumes Volumes, Func<PushCredential, IPullRequests> PullRequestsFor, ITaskSource? TaskSource,
    Func<RepositoryRef, string, CancellationToken, Task<DrivenCheckout>> Checkout, IEvidenceResolver Resolver, Func<string, string> Secret,
    Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>>? SupportCheck = null, Func<ContainerVerifierSettings, PushCredential, IBranchHandover>? HandoverFor = null,
    TimeSpan? Poll = null);

/// <summary>A request with a <c>driven</c> block, from start to the batch's result (ADR 0039): tasks resolved, the batch network up, tasks run under the batch's limits by
/// <see cref="TaskRunner"/>, the network down, and one <c>result/v1</c> that lists the tasks. Every refusal (driven sessions off, no push credential, no image) is a failed
/// result with a code, recorded like any run. The model credential reaches a container in its environment: the gateway exchange is not built, and the batch result says so.</summary>
public sealed class DrivenRun(Profile profile, IRunLog log, string rootDirectory, DrivenServices? services) : IDisposable
{
    public const string CredentialDelivery = "environment";

    private readonly SemaphoreSlim _global = new(Math.Max(1, profile.Driven?.MaxParallelTotal ?? 4));

    public void Dispose() => _global.Dispose();

    public async Task<ResultContract> RunAsync(RunRequest request, string runId, TaskTokenMinter mint, CancellationToken ct, Action<RunStatus>? progress = null,
        Func<bool>? cancelledByCaller = null)
    {
        var started = DateTimeOffset.UtcNow;
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        var version = profile.ClaudeCode?.Version ?? "";
        var chain = new PromptChain([], new AsSent($"claude-code/{version}", "driven", "batch", started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        await log.AppendAsync(new StartRecord(runId, started, traceId, request, Environment.ProcessId), ct);
        progress?.Invoke(RunStatus.Of(runId, RunState.Running, RunEventKind.Started));

        ResultContract result;
        System.Security.Cryptography.ECDsa? key = null;
        try
        {
            key = ResultSigner.Load(profile.Signing, Environment.GetEnvironmentVariable);
            result = await RunBatchAsync(request, runId, traceId, mint, key is null ? null : r => ResultSignature.Sign(r, key), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || cancelledByCaller?.Invoke() == true)
        {
            var error = e is OperationCanceledException
                ? new ResultError(ErrorCode.Cancelled, "the run was cancelled", false, "Start the batch again if it is still wanted; nothing was pushed.")
                : ChargehandException.ErrorOf(e);
            result = new ResultContract("result/v1", runId, "driven", traceId, chain, ResultStatus.Failed, error.Message, [], [], [], [error.Message], 0, new Usage(0, 0, 0, 0, 0), error);
        }
        if (key is not null)
        {
            result = ResultSignature.Sign(result, key);
            key.Dispose();
        }
        await log.AppendAsync(new RunRecord(runId, started, DateTimeOffset.UtcNow, request.Context.Preset, null, null, result), cancelledByCaller?.Invoke() == true ? CancellationToken.None : ct);
        progress?.Invoke(RunStatus.Of(runId, RunStatus.StateOf(result.Status), RunEventKind.RunFinished) with { Result = result });
        return result;
    }

    private async Task<ResultContract> RunBatchAsync(RunRequest request, string runId, string traceId, TaskTokenMinter mint, Func<ResultContract, ResultContract>? sign, CancellationToken ct)
    {
        var settings = profile.Driven ?? new DrivenSettings();
        // Throws when the profile has driven sessions off, so a refused request touches nothing else.
        var (ready, unresolved) = await DrivenBatch.PrepareAsync(request, settings, services?.TaskSource, ct);
        var s = services ?? throw new ChargehandException(ErrorCode.ContainerUnavailable, "driven sessions are on, but no container engine is set up for them", "Check driven.runner in the profile, or install Docker.");
        var claude = profile.ClaudeCode ?? throw new ChargehandException(ErrorCode.InvalidRequest, "driven sessions need a claude_code block in the profile", "Add claude_code with an api_key_secret or an oauth_token_secret.");
        var image = settings.Images is { Count: > 0 } images ? images[0] : throw new ChargehandException(ErrorCode.InvalidRequest, "driven.images is empty", "List the session image by digest in driven.images.");
        var preset = Preset.Load(Path.Combine(rootDirectory, "presets"), request.Context.Preset);
        var drivenPreset = preset.Driven ?? throw new ChargehandException(ErrorCode.InvalidRequest, $"preset '{preset.Name}' has no driven block", "Use the driven preset for a batch.");
        var (_, kind) = Orchestrator.AnswerKind(preset);

        var priced = claude.ApiKeySecret is not null;
        var perTaskUsd = Math.Min(kind.Budget.MaxUsd, request.Driven!.MaxUsdTotal ?? decimal.MaxValue);
        var limits = DrivenBatch.Limits(request, settings, drivenPreset, perTaskUsd, priced, _global);
        var push = settings.PushSecret is { } pushSecret ? new PushCredential(Resolve(s, pushSecret)) : throw new ChargehandException(ErrorCode.CredentialUnavailable,
            "driven.push_secret is not set, so nothing could be pushed", "Name the secret item that holds the credential which pushes non-default branches.");
        var modelEnvironment = new Dictionary<string, string>
        {
            [priced ? "ANTHROPIC_API_KEY" : "CLAUDE_CODE_OAUTH_TOKEN"] = Resolve(s, (priced ? claude.ApiKeySecret : claude.OauthTokenSecret)
                ?? throw new ChargehandException(ErrorCode.CredentialUnavailable, "claude_code names no credential", "Set api_key_secret or oauth_token_secret.")),
        };

        var checkout = await s.Checkout(request.Context.Repository!, request.Context.Preset, ct);
        var forward = settings.Network?.McpForward;
        var port = forward is null ? 0 : int.Parse(forward[(forward.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
        var net = await new BatchNetwork(s.Engine).CreateAsync(runId, image, [.. (drivenPreset.Allow ?? []).Concat(settings.Network?.Allow ?? []).Distinct()], ct,
            settings.Network?.Outside ?? BatchNetwork.DefaultOutside, forward is null ? null : [$"{port}={forward}"]);
        BatchOutcome ran;
        try
        {
            var verifierSettings = new ContainerVerifierSettings(image, checkout.Directory, checkout.Commit, net.Network, net.ProxyUrl(runId), drivenPreset.MemoryMb, drivenPreset.Cpus, drivenPreset.Pids);
            var handover = s.HandoverFor?.Invoke(verifierSettings, push)
                ?? new Handover(new ContainerVerifier(s.Engine, s.Workspace, verifierSettings), s.PullRequestsFor(push));
            var task = new TaskRunnerSettings(runId, request, image, checkout.Directory, checkout.Commit, checkout.RemoteUrl, checkout.BaseBranch, net,
                forward is null ? null : $"{net.ServiceUrl(port)}/v1/mcp", drivenPreset, limits.PerTask, priced, modelEnvironment, push,
                Path.Combine(profile.WorkerRoot, ".driven"), kind.Model, claude.Version);
            var runner = new TaskRunner(s.Engine, s.Workspace, s.Volumes, handover, log, mint, task, s.Resolver, s.SupportCheck, sign, s.Poll);
            ran = await new BatchScheduler(runner).RunAsync(ready, limits, ct);
        }
        finally
        {
            await new BatchNetwork(s.Engine).RemoveAsync(net, CancellationToken.None);
        }

        // In request order: tasks that did not resolve were never started.
        var all = request.Driven.Tasks.Select(t => unresolved.FirstOrDefault(u => u.Id == t.Id) ?? ran.Tasks.First(o => o.Id == t.Id)).ToList();
        return DrivenResult.BuildBatch(runId, traceId, "", new BatchOutcome(all, ran.Action), CredentialDelivery, priced, claude.Version);
    }

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
