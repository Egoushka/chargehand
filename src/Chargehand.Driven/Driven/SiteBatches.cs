using System.Collections.Concurrent;
using System.Globalization;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using Chargehand.Runtimes.ClaudeCode.Api;
using Chargehand.Workspace;

namespace Chargehand.Driven;

/// <summary>What the profile and the services give one batch, worked out before anything is created (<see cref="DrivenRun"/>): a setting that cannot work fails the batch at its start.</summary>
/// <param name="Model">The model the preset's answer kind runs.</param>
/// <param name="Own">The account's own model credential (ADR 0053); null: the server's.</param>
/// <param name="Priced">The credential has a dollar price (an API key), so the dollar cap binds.</param>
/// <param name="Push">The credential <c>driven.push_secret</c> names; null: the GitHub App pushes (<see cref="BatchCheckout.Push"/>).</param>
/// <param name="ModelEnvironment">The one model credential variable a session gets.</param>
internal sealed record BatchSetup(DrivenServices Services, DrivenSettings Settings, ClaudeCodeSettings Claude, string Image, DrivenPreset Preset, string Model, AccountModel? Own, bool Priced,
    BatchLimits Limits, PushCredential? Push, IReadOnlyDictionary<string, string> ModelEnvironment, IReadOnlyList<RunnerSite> Sites);

/// <summary>The batch's checkout, and what acts on its remote: a forge other than GitHub (one token both opens the pull request and integrates it), and the integrator (ADR 0046).</summary>
/// <param name="Push">What pushes the branches: <c>driven.push_secret</c>, or the GitHub App's token for the remote.</param>
internal sealed record BatchCheckout(DrivenCheckout Checkout, IForge? Forge, Integrator? Integrator, PushCredential Push);

/// <summary>What one runner needs of the batch's settings (ADR 0058): where its sessions reach the server, the model gateway and the collector (the profile's <c>network</c>, with the runner's
/// <c>forwards</c> over it), the forwards its egress container carries, the hosts it allows, and the run-token exchange.</summary>
internal sealed record SiteRoute(RunnerSite Site, string? Forward, int Port, DrivenModelRoute? Model, DrivenOtlpRoute? Otlp, IReadOnlyList<string> Forwards, IReadOnlyList<string> Allow, string Outside,
    ModelGatewaySpec? Exchange)
{
    /// <summary>How the model credential reaches this runner's sessions, as the batch result names it.</summary>
    public string Delivery => Model is not null ? DrivenRun.GatewayKeyDelivery : Exchange is not null ? DrivenRun.TokenExchangeDelivery : DrivenRun.CredentialDelivery;

    /// <summary>One route per runner of <paramref name="setup"/>; <paramref name="tokenKey"/> is the batch's run-token key, null with a priced credential.</summary>
    public static IReadOnlyList<SiteRoute> Of(BatchSetup setup, string? tokenKey) =>
        [.. setup.Sites.Select(site => Of(site, setup.Settings, setup.Own, setup.Priced, tokenKey, setup.Preset, setup.ModelEnvironment.Values.Single()))];

    private static SiteRoute Of(RunnerSite site, DrivenSettings settings, AccountModel? own, bool priced, string? tokenKey, DrivenPreset drivenPreset, string credential)
    {
        var network = settings.Network;
        var over = site.Config.Forwards;
        var forward = over?.Mcp ?? network?.McpForward;
        var port = PortOf(forward);
        var model = ModelOf(site, network, over, own, priced, forward is null ? null : port);
        // A subscription token without a gateway of the operator's goes only to the batch's egress container, which exchanges a per-task token for it (ADR 0039,
        // decision 9). An API key, or a key a gateway issued, stays in the session's environment: the exchange is measured on the subscription only.
        var exchange = !priced && model is null ? new ModelGatewaySpec(credential, tokenKey!) : null;
        var otlp = OtlpOf(over, network, forward is null ? null : port, model);
        return new SiteRoute(site, forward, port, model, otlp, ForwardsOf(forward, port, model, otlp), AllowOf(drivenPreset, network, model), over?.Outside ?? network?.Outside ?? BatchNetwork.DefaultOutside,
            exchange);
    }

    /// <summary>The collector's route, on a port neither the server's forward nor the model gateway's takes.</summary>
    private static DrivenOtlpRoute? OtlpOf(DrivenRunnerForwards? over, DrivenNetwork? network, int? port, DrivenModelRoute? model) =>
        DrivenOtlpRoute.Parse(over?.OtlpUrl ?? network?.OtlpUrl, new[] { port, model?.ForwardPort }.OfType<int>());

    private static int PortOf(string? forward) => forward is null ? 0 : int.Parse(forward[(forward.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);

    /// <summary>The hosts the egress container lets sessions reach: the preset's, the profile's and the model gateway's.</summary>
    private static IReadOnlyList<string> AllowOf(DrivenPreset drivenPreset, DrivenNetwork? network, DrivenModelRoute? model) =>
        [.. (drivenPreset.Allow ?? []).Concat(network?.Allow ?? []).Concat(model?.AllowHost is { } host ? new[] { host } : []).Distinct()];

    /// <summary>What the egress container forwards: the server's MCP port, the model gateway and the collector, those that are set.</summary>
    private static List<string> ForwardsOf(string? forward, int port, DrivenModelRoute? model, DrivenOtlpRoute? otlp)
    {
        List<string> forwards = [];
        if (forward is not null)
            forwards.Add($"{port}={forward}");
        if (model?.Forward is { } modelForward)
            forwards.Add(modelForward);
        if (otlp is not null)
            forwards.Add(otlp.Forward);
        return forwards;
    }

    /// <summary>The server's gateway is for the server's credential: an account's own key goes direct to Anthropic, or to the gateway its model block names.</summary>
    private static DrivenModelRoute? ModelOf(RunnerSite site, DrivenNetwork? network, DrivenRunnerForwards? over, AccountModel? own, bool priced, int? port)
    {
        if (own is not null)
            return DrivenModelRoute.Parse(own.BaseUrl, priced, port, "http.accounts[].model.base_url");
        var where = over?.ModelUrl is not null ? $"driven.runners.{site.Name}.forwards.model_url" : "driven.network.model_url";
        return DrivenModelRoute.Parse(over?.ModelUrl ?? network?.ModelUrl, priced, port, where);
    }
}

/// <summary>A batch's part on each runner (ADR 0058): its network, egress container, test services and task runner, made when the first task is placed there, and torn down at the end. The
/// session start, the workspace helpers, the writes into an output volume and the handover's verification, push and pull request are recorded steps in the batch's flow (ADR 0061).</summary>
internal sealed class SiteBatches
{
    private readonly BatchSetup _setup;
    private readonly IReadOnlyList<SiteRoute> _routes;
    private readonly BatchCheckout _checkout;
    private readonly BatchRunContext _c;
    private readonly SiteParts _parts;
    private readonly SharedServicesPlan _shared;
    private readonly string _sessionImage;
    private readonly ConcurrentDictionary<string, Lazy<Task<SiteBatch>>> _made = new(StringComparer.Ordinal);

    /// <summary>What a task runner takes from the server beside the batch's settings.</summary>
    /// <param name="Approved">The hosts the owner approved for the batch's repository (ADR 0078, decision 4), added to every runner's allowlist.</param>
    internal sealed record SiteParts(IRunLog Log, TaskTokenMinter Mint, TaskJournal? Tasks, ILearningStore? Learning, TaskRecordStore? Records, DrivenJournal? Steps, SessionStops? Stops, Profile Profile,
        IReadOnlyList<string>? Approved = null);

    /// <summary>A runner's part of a batch once it exists: its network and the task runner that uses it.</summary>
    private sealed record SiteBatch(RunnerSite Site, BatchNetworkInfo Network, TaskRunner Runner);

    public SiteBatches(BatchSetup setup, IReadOnlyList<SiteRoute> routes, BatchCheckout checkout, BatchRunContext c, SiteParts parts)
    {
        (_setup, _routes, _checkout, _c, _parts) = (setup, routes, checkout, c, parts);
        // ADR 0048: the repository's session image, cache volumes and test services. With no driven.services key the plan is only the facts for the environment note.
        var repository = c.Request.Context.Repository!;
        var identity = SharedServicesPlan.Identity(checkout.Checkout.RemoteUrl, repository.Path);
        // The profile's command for the repository stands in for a request that names none, for the verifier and every follow-up alike.
        if (setup.Settings.Services?.VerifyFor(c.Request.Context.Verify, identity) is { } verify && verify != c.Request.Context.Verify)
            _c = c = c with { Request = c.Request with { Context = c.Request.Context with { Verify = verify } } };
        _shared = SharedServicesPlan.For(setup.Settings.Services, checkout.Checkout.Directory, c.Request.Context.Verify, identity, c.RunId, BatchNetwork.NetworkName(c.RunId),
            setup.Settings.UsesMise);
        _sessionImage = setup.Settings.UsesMise ? setup.Image : SharedServicesPlan.PickImage(setup.Settings.Services, _shared.Ecosystems, setup.Image);
    }

    /// <summary>The tasks whose session container has started, for the router's replace rule.</summary>
    public ConcurrentDictionary<string, byte> SessionsStarted { get; } = new(StringComparer.Ordinal);

    /// <summary>The runner a task goes to when none is named.</summary>
    public string First => _routes[0].Site.Name;

    public int Count => _routes.Count;

    /// <summary>The task runner on runner <paramref name="name"/>, its part of the batch made on first use; one that fails to be made is not kept, so the next task placed there tries again.</summary>
    public async Task<TaskRunner> RunnerOn(string name)
    {
        var route = _routes.FirstOrDefault(r => r.Site.Name == name) ?? throw new ChargehandException(ErrorCode.InvalidRequest,
            $"runner '{name}' is not in the profile any more, but the batch has a task on it", "Add the runner back to driven.runners, or send the task again.");
        var lazy = _made.GetOrAdd(name, _ => new Lazy<Task<SiteBatch>>(() => BuildAsync(route)));
        try
        {
            return (await lazy.Value).Runner;
        }
        catch
        {
            _made.TryRemove(new KeyValuePair<string, Lazy<Task<SiteBatch>>>(name, lazy));
            throw;
        }
    }

    /// <summary>Each runner's part removed: a network with a container still attached cannot be removed, so the services go first. Returns the hosts the sessions were refused, read from the
    /// egress container's log before it goes (ADR 0078, decision 4).</summary>
    public async Task<IReadOnlyList<EgressRefusal>> TeardownAsync()
    {
        List<EgressRefusal> refused = [];
        foreach (var lazy in _made.Values.Where(l => l.IsValueCreated && l.Value.IsCompletedSuccessfully))
        {
            var made = lazy.Value.Result;
            refused.AddRange(await RefusedAsync(made));
            await new BatchServices(made.Site.Engine).RemoveAsync(_shared.Services, CancellationToken.None);
            await new BatchNetwork(made.Site.Engine).RemoveAsync(made.Network, CancellationToken.None);
        }
        return refused;
    }

    /// <summary>About 16,000 lines of the proxy's log: a batch's package installs connect hundreds of times, and a refusal early in the batch must not fall out of the tail.</summary>
    internal const int EgressLogBytes = 1024 * 1024;

    /// <summary>A log that cannot be read loses a hint, never the teardown.</summary>
    private static async Task<IReadOnlyList<EgressRefusal>> RefusedAsync(SiteBatch made)
    {
        try
        {
            return EgressRefusals.Parse(await made.Site.Engine.LogsTailAsync(made.Network.EgressContainer, EgressLogBytes, CancellationToken.None));
        }
        catch (Exception e) when (e is ChargehandException or HttpRequestException or IOException or ArgumentException)
        {
            return [];
        }
    }

    private async Task<SiteBatch> BuildAsync(SiteRoute route)
    {
        var site = route.Site;
        var net = await NetworkAsync(route);
        var siteImage = _setup.Settings.ImageFor(_sessionImage, site.Config.Platform);
        var task = SettingsOf(route, net, siteImage);
        var s = _setup.Services;
        var steps = _parts.Steps;
        var runner = new TaskRunner(site.Engine, steps is null ? site.Workspace : new JournaledWorkspaceEngine(site.Workspace, site.Engine, steps, _c.RunId),
            steps is null ? site.Volumes : new JournaledSessionVolumes(site.Volumes, steps, _c.RunId), HandoverOf(site, net, siteImage), _parts.Log, _parts.Mint, task,
            new DrivenTaskResults(s.Resolver, s.SupportCheckFor?.Invoke(_c.Actor), _c.Sign), s.Poll, publish: Stamp(site.Name), tasks: _parts.Tasks, detaching: _c.Detaching, stacker: s.Stacker,
            learning: _parts.Learning, brancher: s.Brancher, volumesOf: VolumesOf, outageGrace: s.OutageGrace, steps: steps, records: _parts.Records, stops: _parts.Stops);
        return new SiteBatch(site, net, runner);
    }

    /// <summary>The runner's batch network with its egress container, and the batch's test services on it; made on a first start, adopted or made again on a resumed one.</summary>
    private async Task<BatchNetworkInfo> NetworkAsync(SiteRoute route)
    {
        var engine = route.Site.Engine;
        var egressImage = _setup.Settings.ImageFor(_setup.Image, route.Site.Config.Platform);
        var batchNetwork = new BatchNetwork(engine);
        var forwards = route.Forwards.Count == 0 ? null : route.Forwards;
        IReadOnlyList<string> allow = [.. route.Allow.Concat(_parts.Approved ?? []).Distinct(StringComparer.Ordinal)];
        var net = _c.Resume is null
            ? await batchNetwork.CreateAsync(_c.RunId, egressImage, allow, _c.Ct, route.Outside, forwards, route.Exchange)
            : await batchNetwork.EnsureAsync(_c.RunId, egressImage, allow, _c.Ct, route.Outside, forwards, route.Exchange);
        try
        {
            if (_c.Resume is null)
                await new BatchServices(engine).StartAsync(_shared.Services, _c.Ct);
            else
                await new BatchServices(engine).EnsureAsync(_shared.Services, _c.Ct);
        }
        catch
        {
            // A network with a container still attached cannot be removed: the services were removed by their own failure.
            await new BatchNetwork(engine).RemoveAsync(net, CancellationToken.None);
            throw;
        }
        return net;
    }

    private IBranchHandover HandoverOf(RunnerSite site, BatchNetworkInfo net, string siteImage)
    {
        var (s, preset, checkout, steps) = (_setup.Services, _setup.Preset, _checkout.Checkout, _parts.Steps);
        var verifier = new ContainerVerifierSettings(siteImage, checkout.Directory, checkout.Commit, net.Network, net.ProxyUrl(_c.RunId), preset.MemoryMb, preset.Cpus, preset.Pids, _shared);
        return s.HandoverFor?.Invoke(verifier, _checkout.Push)
            ?? new Handover(new ContainerVerifier(site.Engine, steps is null ? site.Workspace : new JournaledVerifierWorkspace(site.Workspace, steps, _c.RunId), verifier),
                _checkout.Forge ?? s.PullRequestsFor(_checkout.Push), _setup.Settings.Forge, steps, _c.RunId);
    }

    private TaskRunnerSettings SettingsOf(SiteRoute route, BatchNetworkInfo net, string siteImage)
    {
        var (settings, checkout, profile) = (_setup.Settings, _checkout.Checkout, _parts.Profile);
        return new TaskRunnerSettings(_c.RunId, _c.Request, siteImage, checkout.Directory, checkout.Commit, checkout.RemoteUrl, checkout.BaseBranch, net,
            route.Forward is null ? null : $"{BatchNetworkInfo.ServiceUrl(route.Port)}/v1/mcp", _setup.Preset, _setup.Limits.PerTask, _setup.Priced, _setup.ModelEnvironment, _checkout.Push,
            Path.Combine(profile.WorkerRoot, ".driven"), _setup.Model, _setup.Claude.Version,
            Environment.GetEnvironmentVariable("CHARGEHAND_E2E_LOCAL_REMOTE") == "1", route.Model?.BaseUrl, route.Forward is not null || route.Model?.Forwarded == true || route.Otlp is not null, route.Otlp,
            profile.Prices, profile.Telemetry?.UsageOnSpans == true, route.Exchange?.TokenKey, settings.Governor ?? new DrivenGovernor(), _checkout.Integrator, _shared, _c.Actor,
            RemoteRepository.Parse(checkout.RemoteUrl, settings.Forge), profile.DeliveryPolicy.Forced(_c.Request.Context.Repository!.Path),
            settings.UsesMise ? new MiseSetup(_setup.Settings.ImageFor(_setup.Image, route.Site.Config.Platform), route.Outside) : null);
    }

    /// <summary>A runner's events: a started session is noted for the router, and when the profile names its runners each event says which one.</summary>
    private Action<RunStatus> Stamp(string runner) => e =>
    {
        if (e is { Event: RunEventKind.ContainerStarted, TaskId: { } id })
            SessionsStarted.TryAdd(id, 0);
        _c.Progress?.Invoke(_setup.Settings.NamesRunners ? Named(e, runner) : e);
    };

    private static RunStatus Named(RunStatus e, string runner) =>
        e with { Runner = runner, Detail = e.Event == RunEventKind.ContainerStarted && e.Detail is { } detail ? $"{detail} on runner '{runner}'" : e.Detail };

    private ISessionVolumes VolumesOf(string? name) => (_setup.Sites.FirstOrDefault(x => x.Name == name) ?? _setup.Sites[0]).Volumes;
}
