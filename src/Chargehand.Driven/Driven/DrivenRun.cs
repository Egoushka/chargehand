using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using Chargehand.RunLog;
using Chargehand.Runtimes.ClaudeCode.Api;
using Chargehand.Signing;
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
/// <param name="Runners">The runners of a profile that names them (ADR 0058); each task is placed on one by <see cref="RunnerPool"/>. Null: <see cref="Engine"/>, <see cref="Workspace"/>, <see cref="Volumes"/> and <see cref="Host"/> are the one runner.</param>
/// <param name="AppToken">A GitHub App installation token for a GitHub repository (the integrator's App, ADR 0046), asked for at each push and pull request call; null: no App, so a batch needs
/// <c>driven.push_secret</c>. <c>driven.push_secret</c> wins when it is set.</param>
/// <param name="OutageGrace">How long a task waits for a runner that stops answering during its session; <see cref="TaskRunner.DefaultOutageGrace"/> when null.</param>
public sealed record DrivenServices(IContainerEngine Engine, IWorkspaceEngine Workspace, ISessionVolumes Volumes, Func<PushCredential, IPullRequests> PullRequestsFor, ITaskSource? TaskSource,
    Func<RepositoryRef, string, CancellationToken, Task<DrivenCheckout>> Checkout, IEvidenceResolver Resolver, Func<string, string> Secret,
    Func<string?, Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>>?>? SupportCheckFor = null, Func<ContainerVerifierSettings, PushCredential, IBranchHandover>? HandoverFor = null,
    TimeSpan? Poll = null, IHostSource? Host = null, IIntegrationHost? Integration = null, Func<RemoteRepository, string, IForge>? ForgeFor = null, IBatchPlanner? Planner = null, IStackSeeder? Stacker = null,
    IBatchRetro? Retro = null, IRetroSink? RetroSink = null, IBranchSeeder? Brancher = null, IReadOnlyList<RunnerSite>? Runners = null, TimeSpan? OutageGrace = null,
    Func<RemoteRepository, CancellationToken, Task<string>>? AppToken = null);

/// <summary>A request with a <c>driven</c> block, from start to the batch's result (ADR 0039): tasks resolved, the batch network up, tasks run under the batch's limits by
/// <see cref="TaskRunner"/>, the network down, and one <c>result/v1</c> that lists the tasks. Every refusal (driven sessions off, no push credential, no image) is a failed
/// result with a code, recorded like any run. How the model credential reaches a session, recorded on the batch result: a subscription token goes only to the batch's egress container,
/// which exchanges a per-task token for it (<c>token_exchange</c>); a key an operator's gateway issued is in the session's environment (<c>gateway_key</c>); a plain API key too
/// (<c>environment</c>).</summary>
/// <param name="learning">Where each finished task's record is written and pull request outcomes are read back (ADR 0045); null: nothing is recorded.</param>
/// <param name="records">Where a batch's events and its tasks' sessions, governor decisions, checks and CI fix attempts are kept for the console (CHARGEHAND-214); null: they are not.</param>
public sealed class DrivenRun(Profile profile, IRunLog log, string rootDirectory, DrivenServices? services, string? stateDirectory = null, ILearningStore? learning = null, BatchRecords? records = null) : IDisposable
{
    /// <summary>The learning records this server keeps, for <c>GET /v1/learn</c>; null when none are kept.</summary>
    public ILearningStore? Learning => learning;

    /// <summary>The batch records this server keeps for the console; null when none are kept.</summary>
    public BatchRecords? Records => records;

    /// <summary>How often <see cref="RefreshPrOutcomesAsync"/> should run.</summary>
    public TimeSpan PrCheckEvery => TimeSpan.FromMinutes((profile.Driven?.Learning ?? new DrivenLearning()).PrCheckMinutes);

    public const string CredentialDelivery = "environment";

    /// <summary>The real credential is only in the batch's egress container; a session holds a per-task token worth nothing outside its run and network.</summary>
    public const string TokenExchangeDelivery = "token_exchange";

    /// <summary>The containers hold a key the gateway issued for driven sessions (a budget, revocable), not the real credential.</summary>
    public const string GatewayKeyDelivery = "gateway_key";

    /// <summary>The batches' lifecycle, their live view and the journal of their side effects (ADR 0061); the profile's wiring is <see cref="RunBatchAsync"/>.</summary>
    private readonly BatchLifecycle _lifecycle = new(log, stateDirectory, records);

    private readonly int _maxParallelTotal = Math.Max(1, profile.Driven?.MaxParallelTotal ?? 4);

    private readonly SemaphoreSlim _global = new(Math.Max(1, profile.Driven?.MaxParallelTotal ?? 4));

    /// <summary>What an operator can do to a batch this process is running now (CHARGEHAND-214); null for any other.</summary>
    public BatchControl? ControlOf(string batchRunId) => _lifecycle.ControlOf(batchRunId);

    /// <summary>Tells a batch's events something an operator did (<see cref="BatchLifecycle.Announce"/>).</summary>
    public void Announce(string batchRunId, RunStatus e) => _lifecycle.Announce(batchRunId, e);

    /// <summary>Whether a batch's events are journaled, so <see cref="JournaledEventsAsync"/> can replay them.</summary>
    public bool JournalsEvents => _lifecycle.JournalsEvents;

    /// <summary>A batch's journaled events after sequence <paramref name="after"/> (<see cref="BatchLifecycle.JournaledEventsAsync"/>).</summary>
    public Task<IReadOnlyList<(long Sequence, RunStatus Event)>> JournaledEventsAsync(string batch, long after, CancellationToken ct) => _lifecycle.JournaledEventsAsync(batch, after, ct);

    // With several runners (ADR 0058) the pool places each task; with one host, the memory rule of ADR 0045 alone.
    private readonly IAdmission? _admission = services?.Runners is { Count: > 0 } runners
        ? new RunnerPool([.. runners.Select(r => new PoolSite(r.Config, r.Host))], profile.Driven?.Admission ?? new DrivenAdmission(), services.Poll, log: message => Console.Error.WriteLine($"chargehand: {message}"))
        : services?.Host is { } host
            ? new HostAdmission(host, profile.Driven?.Admission ?? new DrivenAdmission(), services.Poll, log: message => Console.Error.WriteLine($"chargehand: {message}"))
            : null;

    public void Dispose()
    {
        _global.Dispose();
        _lifecycle.Dispose();
        (_admission as IDisposable)?.Dispose();
    }

    /// <summary>The runners a batch may use: those the services hold, else the one engine they hold as the runner named <see cref="DrivenSettings.DefaultRunnerName"/>.</summary>
    private static IReadOnlyList<RunnerSite> SitesOf(DrivenServices s) =>
        s.Runners is { Count: > 0 } runners ? runners : [new RunnerSite(new DrivenRunnerEntry(DrivenSettings.DefaultRunnerName, "", ""), s.Engine, s.Workspace, s.Volumes, s.Host)];

    /// <summary>Whether driven sessions are set up on this server (the profile has them on and a container engine is connected).</summary>
    public bool Enabled => services is not null;

    /// <summary>The container engine (the runner, or Docker on this machine); null when driven sessions are off. The console reads its resource names and count and never changes anything through it.</summary>
    public IContainerEngine? Engine => services?.Engine;

    /// <summary>The runners a profile names (ADR 0058) and their admission state; null with one runner.</summary>
    public RunnerPool? Pool => _admission as RunnerPool;

    /// <summary>The runners a batch may use, with the engines that reach them; empty when driven sessions are off.</summary>
    public IReadOnlyList<RunnerSite> Sites => services is null ? [] : SitesOf(services);

    /// <summary>Where admission reads the host's free memory and load; null when driven sessions are off.</summary>
    public IHostSource? Host => services?.Host;

    /// <summary><c>driven.max_parallel_total</c>: tasks running at once across every batch.</summary>
    public int MaxParallelTotal => _maxParallelTotal;

    /// <summary>Tasks running now, across every batch of this process.</summary>
    public int SessionsRunning => Math.Max(0, _maxParallelTotal - _global.CurrentCount);

    /// <summary>The unfinished batches this process carries (ADR 0043): those it started, and those its predecessor left, read back by <see cref="UnfinishedAsync"/> at the start; each with its
    /// tasks as the journal folds them now.</summary>
    public IReadOnlyList<OpenBatch> LiveBatches() => _lifecycle.LiveBatches();

    /// <summary>Usage or rate limits that hold new starts now, one per batch.</summary>
    public IReadOnlyList<RateLimitPause> RateLimitPauses(DateTimeOffset now) => _lifecycle.RateLimitPauses(now);

    /// <param name="resume">A batch a restart interrupted, as <see cref="UnfinishedAsync"/> read it back: the batch carries on from it under the same run id (ADR 0043).</param>
    /// <param name="actor">The account that started the batch (ADR 0050); a resumed batch keeps the one its state saved.</param>
    public Task<ResultContract> RunAsync(RunRequest request, string runId, TaskTokenMinter mint, CancellationToken ct, Action<RunStatus>? progress = null,
        Func<bool>? cancelledByCaller = null, OpenBatch? resume = null, string? actor = null) =>
        _lifecycle.RunAsync(new BatchRunStart(request, runId, progress, cancelledByCaller, resume, actor, profile.ClaudeCode?.Version ?? "",
            () => ResultSigner.Load(profile.Signing, Environment.GetEnvironmentVariable)), c => RunBatchAsync(c, mint), ct);

    private readonly object _literalsGate = new();
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<string> Values)> _literals = [];

    /// <summary>The end of a session's transcript for the console (ADR 0059), read from the session's output volume on the runner that ran it (<paramref name="runner"/>; null: each runner in turn) and made
    /// safe to show: whole lines, long ones cut, secrets redacted by pattern and by the credentials this server holds for <paramref name="actor"/>'s batch. Nothing is kept: the transcript stays where the
    /// session wrote it, and goes no further than the response.</summary>
    /// <param name="maxBytes">Within <see cref="Transcript.MinBytes"/> and <see cref="Transcript.MaxBytes"/>.</param>
    public async Task<TranscriptRead> ReadTranscriptAsync(string sessionRunId, string? runner, int maxBytes, string? actor, CancellationToken ct)
    {
        if (services is null)
            return new TranscriptRead(null, "driven_off");
        maxBytes = Math.Clamp(maxBytes, Transcript.MinBytes, Transcript.MaxBytes);
        var sites = SitesOf(services);
        var wanted = string.IsNullOrEmpty(runner) ? sites : [.. sites.Where(s => s.Name == runner)];
        if (wanted.Count == 0)
            return new TranscriptRead(null, "runner_unknown");
        string? problem = null;
        foreach (var site in wanted)
        {
            try
            {
                if (await site.Volumes.ReadTranscriptTailAsync(sessionRunId, maxBytes, ct) is { } raw)
                    return new TranscriptRead(Transcript.Prepare(raw, maxBytes, CredentialLiterals(actor)), null);
            }
            catch (RunnerUnreachableException)
            {
                problem = "runner_unreachable";
            }
            catch (ChargehandException e)
            {
                // A runner older than this feature refuses the parameter or the name; anything else is the engine failing.
                problem = e.Message.Contains(": 403 ", StringComparison.Ordinal) || e.Message.Contains(": 501 ", StringComparison.Ordinal) ? "runner_too_old" : "runner_error";
            }
        }
        return new TranscriptRead(null, problem ?? "session_gone");
    }

    /// <summary>The values of the credentials this server holds that a session could have printed: the model's (the batch account's own, else the server's), the push and forge tokens, the runners' keys and the
    /// owner's API key. Resolved from the profile's sources and remembered for a minute, because a source can be a command.</summary>
    private IReadOnlyList<string> CredentialLiterals(string? actor)
    {
        var key = actor ?? "";
        lock (_literalsGate)
            if (_literals.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < TimeSpan.FromMinutes(1))
                return known.Values;
        List<string> values = [];
        // The header lines carry a gateway's virtual key (claude_code.custom_headers_secret): the values are model credentials.
        if (profile.ClaudeCode?.CustomHeadersSecret is { Length: > 0 } headersItem)
            values.AddRange(HeaderValues(headersItem));
        foreach (var item in CredentialItems(actor))
            if (SecretOrNull(item) is { Length: > 0 } value)
                values.Add(value);
        lock (_literalsGate)
            _literals[key] = (DateTimeOffset.UtcNow, values);
        return values;
    }

    /// <summary>The secret items of the credentials in <see cref="CredentialLiterals"/>, each once.</summary>
    private IEnumerable<string> CredentialItems(string? actor)
    {
        List<string?> items =
        [
            profile.ClaudeCode?.ApiKeySecret, profile.ClaudeCode?.OauthTokenSecret, profile.Driven?.PushSecret, profile.Driven?.Forge?.TokenSecret, profile.Http?.ApiKeySecret,
            .. profile.Driven?.ResolvedRunners().Select(r => r.ApiKeySecret) ?? [],
            .. profile.Http?.Accounts?.Where(a => a.Name == actor).Select(a => a.Model?.ApiKeySecret) ?? [],
        ];
        return items.OfType<string>().Where(i => i.Length > 0).Distinct();
    }

    private IReadOnlyList<string> HeaderValues(string item)
    {
        try
        {
            return [.. ClaudeCodeSettings.HeaderValues(services!.Secret(item))];
        }
        catch (Exception e) when (e is InvalidOperationException or ChargehandException)
        {
            return [];
        }
    }

    /// <summary>A secret's value; null when it does not resolve: such a secret is not in any transcript.</summary>
    private string? SecretOrNull(string item)
    {
        try
        {
            return services!.Secret(item);
        }
        catch (Exception e) when (e is InvalidOperationException or ChargehandException)
        {
            return null;
        }
    }

    /// <summary>The batches a restart interrupted (<see cref="BatchLifecycle.UnfinishedAsync"/>); empty when driven sessions are off, though <see cref="LiveBatches"/> still shows them.</summary>
    public Task<IReadOnlyList<OpenBatch>> UnfinishedAsync(CancellationToken ct) => _lifecycle.UnfinishedAsync(services is not null, ct);

    /// <summary>Removes chargehand's containers, networks and volumes that belong to no batch in <paramref name="resumed"/>: what a crash or a restart left behind, on every runner (ADR 0058). The engine's
    /// label keeps it to chargehand's own; the name must carry chargehand's prefix too. A runner that does not answer is skipped, and swept before its first task.</summary>
    /// <returns>The names removed.</returns>
    public async Task<IReadOnlyList<string>> SweepOrphansAsync(IEnumerable<OpenBatch> resumed, CancellationToken ct)
    {
        if (services is null)
            return [];
        var known = resumed.SelectMany(b => b.RunIds).ToHashSet(StringComparer.Ordinal);
        // A finished task's session container and work volume are no use to anyone: a kill between its saved outcome and its teardown leaves them. Its output volumes may still seed a continuation.
        var done = resumed.SelectMany(b => b.Tasks).Where(t => t.Phase is TaskPhase.HandedOver or TaskPhase.Failed).SelectMany(t => (t.Runs ?? []).Append(t.RunId)).OfType<string>();
        var busy = known.Except(done).ToHashSet(StringComparer.Ordinal);
        List<string> removed = [];
        var pool = _admission as RunnerPool;
        var sites = SitesOf(services);
        if (pool is not null)
            pool.Sweep = async (site, token) => await SweepSiteAsync(sites.First(s => s.Name == site.Name), known, busy, token);
        foreach (var site in sites)
        {
            try
            {
                removed.AddRange(await SweepSiteAsync(site, known, busy, ct));
            }
            catch (ChargehandException e) when (pool is not null)
            {
                pool.MarkUnswept(site.Name);
                Console.Error.WriteLine($"chargehand: runner '{site.Name}' was not swept at start ({e.Message}); it is swept before its first task");
            }
        }
        return removed;
    }

    /// <param name="known">Every run id of the resumed batches: an output volume of one is kept.</param>
    /// <param name="busy">Those of them whose task has not finished: their containers and work volumes are kept.</param>
    private async Task<IReadOnlyList<string>> SweepSiteAsync(RunnerSite site, HashSet<string> known, HashSet<string> busy, CancellationToken ct)
    {
        var engine = site.Engine;
        var listed = await engine.ListAsync(ct);
        List<string> removed = [];
        async Task Sweep(IEnumerable<string> names, Func<string, string?> idOf, Func<string, Task> remove)
        {
            foreach (var name in names)
            {
                if (idOf(name) is not { } id || (name.StartsWith("chargehand-out-", StringComparison.Ordinal) ? known : busy).Contains(id))
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
        await Sweep(listed.Containers, ContainerRunId, name => engine.RemoveAsync(name, ct));
        await Sweep(listed.Networks, name => IdAfter(name, "chargehand-net-"), name => engine.RemoveNetworkAsync(name, ct));
        var kept = await KeptForContinuationAsync(listed.Volumes, ct);
        // An output volume kept for a continuation is no leftover; a work volume never is one.
        await Sweep(listed.Volumes, name => IdAfter(name, "chargehand-work-") ?? (IdAfter(name, "chargehand-out-") is { } id && !kept.Contains(id) ? id : null), name => engine.RemoveVolumeAsync(name, ct));
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

    /// <summary>The profile's wiring of a batch (ADR 0039), between its start and its result, which <see cref="BatchLifecycle"/> runs: tasks resolved, refusals checked, the run-token key and the
    /// routes, the tasks planned and carried, the checkout, each runner's part, the schedule, the retro and the result.</summary>
    private async Task<ResultContract> RunBatchAsync(BatchRunContext c, TaskTokenMinter mint)
    {
        var settings = profile.Driven ?? new DrivenSettings();
        var options = new BatchOptions(settings.Enabled, settings.MaxParallel);
        // Driven sessions exist to push and open pull requests (ADR 0069): a local run, or a repository the profile keeps local, is refused before the task source or the push credential is touched.
        if (c.Request.Context.Repository is { } repository && profile.DeliveryPolicy.IsLocal(c.Request.Context.Delivery, repository.Path))
            throw DeliveryPolicy.Refused(repository.Path, "a driven batch");
        // Throws when the profile has driven sessions off, so a refused request touches nothing else.
        var (resolved, unresolved) = c.Resume is null
            ? await _lifecycle.ResolveAsync(c, options, services?.TaskSource is { } source ? source.ResolveAsync : null)
            : (BatchLifecycle.Resumed(c.Request, c.Resume.Tasks), c.Resume.Unresolved);
        var setup = SetupOf(c, settings, options);
        // One route per runner (ADR 0058): where that runner's sessions reach the server, the model and the collector, and the image for its platform. Worked out before anything is created,
        // so a setting that cannot work fails the batch at its start, whichever runner it is for.
        var routes = SiteRoute.Of(setup, setup.Priced ? null : _lifecycle.TokenKey(c.RunId));
        // Planning (ADR 0049) changes sizes, goals and the chain of tasks before they are recorded, so a restart carries the planned batch and does not plan again.
        var (ready, plan) = c.Resume is null ? await _lifecycle.PlanAsync(c, resolved, settings.Planning?.Enabled ?? false, setup.Services.Planner) : (resolved, c.Resume.Plan);
        var tasks = await _lifecycle.TasksOfAsync(c, ready, unresolved, plan);
        var checkout = await CheckoutAsync(c, setup, ready);
        c.Span?.SetTag("chargehand.credential_delivery", routes[0].Delivery);
        var steps = _lifecycle.JournalAsync() is { } journal ? await journal : null;
        var sites = new SiteBatches(setup, routes, checkout, c, new SiteBatches.SiteParts(log, mint, tasks, learning, records?.Tasks, steps, ControlOf(c.RunId)?.Sessions, profile));
        var ran = await ScheduleAsync(c, setup, ready, tasks, sites, checkout.Checkout);
        var retro = await BatchLifecycle.RetroAsync(c, settings.Learning is { Retro: true }, learning, setup.Services.Retro, setup.Services.RetroSink);
        return BatchLifecycle.Result(c, unresolved, ready, ran, routes[0].Delivery, setup.Priced, setup.Claude.Version, plan, retro);
    }

    /// <summary>What the profile and the services give the batch; throws for a refusal: driven sessions not set up, no <c>claude_code</c> block, no image, a preset with no driven block, no push
    /// credential, or no model credential.</summary>
    private BatchSetup SetupOf(BatchRunContext c, DrivenSettings settings, BatchOptions options)
    {
        CheckArm(c.Request.Driven!);
        var s = services ?? throw new ChargehandException(ErrorCode.ContainerUnavailable, "driven sessions are on, but no container engine is set up for them", "Check driven.runner in the profile, or install Docker.");
        var claude = profile.ClaudeCode ?? throw new ChargehandException(ErrorCode.InvalidRequest, "driven sessions need a claude_code block in the profile", "Add claude_code with an api_key_secret or an oauth_token_secret.");
        var image = settings.Images is { Count: > 0 } images ? images[0] : throw new ChargehandException(ErrorCode.InvalidRequest, "driven.images is empty", "List the session image by digest in driven.images.");
        var (drivenPreset, kind) = PresetOf(c.Request, settings);
        // An account runs on its own key, or on the server's only where the profile lets it (ADR 0053); an API key has a dollar price, so its cap binds.
        var own = new AccountCredentials(profile.Http?.Accounts).For(c.Actor);
        var priced = own is not null || claude.ApiKeySecret is not null;
        var perTaskUsd = Math.Min(kind.Budget.MaxUsd, c.Request.Driven!.MaxUsdTotal ?? decimal.MaxValue);
        var limits = DrivenBatch.Limits(c.Request, options, drivenPreset, perTaskUsd, priced, _global);
        // No push_secret: the GitHub App pushes, once the checkout says the remote is on GitHub (PushOf).
        var push = settings.PushSecret is { } pushSecret ? new PushCredential(Resolve(s, pushSecret)) : s.AppToken is not null ? null : throw new ChargehandException(ErrorCode.CredentialUnavailable,
            "driven.push_secret is not set and there is no GitHub App (driven.integrator), so nothing could be pushed",
            "Set driven.integrator's app_id and private_key_secret to push as the App, or name the secret item that holds a push credential in driven.push_secret.");
        var credential = own?.ApiKeySecret ?? (priced ? claude.ApiKeySecret : claude.OauthTokenSecret)
            ?? throw new ChargehandException(ErrorCode.CredentialUnavailable, "claude_code names no credential", "Set api_key_secret or oauth_token_secret.");
        var modelEnvironment = new Dictionary<string, string> { [priced ? "ANTHROPIC_API_KEY" : "CLAUDE_CODE_OAUTH_TOKEN"] = Resolve(s, credential) };
        return new BatchSetup(s, settings, claude, image, drivenPreset, kind.Model, own, priced, limits, push, modelEnvironment, SitesOf(s));
    }

    /// <summary>The bare arm pushes a branch nobody has tested, so it is for the benchmark only (ADR 0079).</summary>
    public static void CheckArm(RequestDriven driven)
    {
        if (driven.Arm is null or RequestDriven.ChargehandArm)
            return;
        if (driven.Arm != RequestDriven.BareArm)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"driven.arm '{driven.Arm}' is not an arm", "Use chargehand or bare.");
        if (driven.Bench != true)
            throw new ChargehandException(ErrorCode.InvalidRequest, "driven.arm bare is a benchmark arm and needs driven.bench", "Set bench to true, or leave arm out.");
    }

    /// <summary>The request's preset with the profile's limits over its driven block, and its answer kind.</summary>
    private (DrivenPreset Preset, NodeKind Kind) PresetOf(RunRequest request, DrivenSettings settings)
    {
        var preset = Preset.Load(Path.Combine(rootDirectory, "presets"), request.Context.Preset);
        var drivenPreset = preset.Driven ?? throw new ChargehandException(ErrorCode.InvalidRequest, $"preset '{preset.Name}' has no driven block", "Use the driven preset for a batch.");
        if (settings.Limits is { } profileLimits)
            drivenPreset = profileLimits.Apply(drivenPreset);
        return (drivenPreset, Orchestrator.AnswerKind(preset).Kind);
    }

    /// <summary>The batch's checkout and what acts on its remote. A kept bundle, and a pushed branch, are branches from the commit their task started at; fetched into a workspace at another
    /// commit they would not apply, so such a batch is refused.</summary>
    private static async Task<BatchCheckout> CheckoutAsync(BatchRunContext c, BatchSetup setup, IReadOnlyList<ResolvedTask> ready)
    {
        var (s, settings) = (setup.Services, setup.Settings);
        var checkout = await s.Checkout(c.Request.Context.Repository!, c.Request.Context.Preset, c.Ct);
        var push = await PushOfAsync(setup, checkout, c.Ct);
        var forge = ForgeOf(setup, checkout, push);
        var integrator = settings.Integrator is { } integration && (forge ?? s.Integration) is { } integrationHost
            ? new Integrator(new IntegratorPolicy(integration), integrationHost, integration.CiFix, forge: settings.Forge) : null;
        c.Span?.SetTag("chargehand.base_commit", checkout.Commit);
        if (ready.FirstOrDefault(t => (t.From?.Kept?.Base ?? t.From?.Pushed?.Base) is { } startedAt && startedAt != checkout.Commit) is { } moved)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"task '{moved.Id}' continues work that started at commit {moved.From!.Kept?.Base ?? moved.From.Pushed!.Base}, but this request's repository is at {checkout.Commit}",
                "Send the request with context.repository.commit set to the commit the earlier task started at.");
        return new BatchCheckout(checkout, forge, integrator, push);
    }

    /// <summary>The batch's push credential: <c>driven.push_secret</c> when it is set, else the GitHub App's installation token for the remote, minted at each use. One token is asked for now,
    /// so a repository the App is not installed on fails the batch before any session runs.</summary>
    private static async Task<PushCredential> PushOfAsync(BatchSetup setup, DrivenCheckout checkout, CancellationToken ct)
    {
        if (setup.Push is { } stored)
            return stored;
        var mint = setup.Services.AppToken!;
        if (RemoteRepository.Parse(checkout.RemoteUrl, setup.Settings.Forge) is not { Forge: ForgeKind.GitHub } remote)
            throw new ChargehandException(ErrorCode.CredentialUnavailable, "driven.push_secret is not set and the GitHub App pushes only to a GitHub remote",
                "Name the secret item that holds a push credential for this remote in driven.push_secret.");
        try
        {
            await mint(remote, ct);
        }
        catch (GitHubAppException e)
        {
            throw new ChargehandException(ErrorCode.CredentialUnavailable, $"driven.push_secret is not set and the GitHub App cannot push to {remote.Owner}/{remote.Name}: {e.Message}",
                "Install the App on the repository with contents and pull requests write access, or name a push credential in driven.push_secret.");
        }
        return PushCredential.Minted(token => mint(remote, token));
    }

    /// <summary>A forge other than GitHub acts with one token, both to open the pull request and to integrate it; null on GitHub.</summary>
    private static IForge? ForgeOf(BatchSetup setup, DrivenCheckout checkout, PushCredential push)
    {
        var (s, settings) = (setup.Services, setup.Settings);
        if (RemoteRepository.Parse(checkout.RemoteUrl, settings.Forge) is not { Forge: not ForgeKind.GitHub } remote)
            return null;
        return s.ForgeFor?.Invoke(remote, settings.Forge?.TokenSecret is { } tokenSecret ? Resolve(s, tokenSecret) : push.Value)
            ?? throw new ChargehandException(ErrorCode.InvalidRequest, $"the remote is a {remote.Forge} project, but this server has no support for it set up", "Check driven.forge in the profile.");
    }

    /// <summary>The batch's tasks run (<see cref="BatchLifecycle.ScheduleAsync"/>), with a runner's part made when its first task is placed there; with one runner, now, so a refusal ends the
    /// batch at its start. Each runner's part is torn down unless the server is stopping.</summary>
    private async Task<BatchOutcome> ScheduleAsync(BatchRunContext c, BatchSetup setup, IReadOnlyList<ResolvedTask> ready, TaskJournal? tasks, SiteBatches sites, DrivenCheckout checkout)
    {
        if (sites.Count == 1)
            await sites.RunnerOn(sites.First);
        try
        {
            var router = new SiteRouter(sites.RunnerOn, sites.First, setup.Settings.NamesRunners, tasks, sites.SessionsStarted, Pool is { } pool ? pool.Failed : null);
            var repository = TaskRunner.RepositoryKey(RemoteRepository.Parse(checkout.RemoteUrl, setup.Settings.Forge), c.Request.Context.Repository?.Path);
            var requestMb = await AdmissionRequestAsync(repository, setup.Preset.MemoryMb, c.Ct);
            return await _lifecycle.ScheduleAsync(c, ready, setup.Limits, tasks, router, _admission, requestMb);
        }
        finally
        {
            if (!c.Detaching())
                await sites.TeardownAsync();
        }
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

    /// <summary>What the host must have free for each task of the batch (ADR 0045, admission): the working set this repository's sessions have measured, else the profile's default, never above the
    /// container's limit. Read once per batch; without a host to ask there is no admission and nothing to read.</summary>
    private async Task<int> AdmissionRequestAsync(string repository, int limitMb, CancellationToken ct)
    {
        if (_admission is null)
            return limitMb;
        IReadOnlyList<LearningRecord> records = [];
        if (learning is not null)
        {
            try
            {
                records = [.. (await learning.ReadAsync(ct)).Select(e => e.Record)];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"chargehand: could not read the learning records for the admission request: {e.Message}");
            }
        }
        var choice = MemoryRequest.Choose(profile.Driven?.Admission ?? new DrivenAdmission(), limitMb, repository, records, DateTimeOffset.UtcNow);
        Console.Error.WriteLine($"chargehand: admission: a task asks the host for {choice.Describe()}; its container is limited to {limitMb} MiB");
        return choice.Mb;
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
