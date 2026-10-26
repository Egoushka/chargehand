using System.Globalization;
using System.Net;
using System.Net.ServerSentEvents;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.RunLog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Extensions.Tasks;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Chargehand.Server;

/// <param name="Port">0 picks a free one.</param>
/// <param name="ApiKey">Required as a bearer token on every route, even on loopback: any local process can reach the port.</param>
/// <param name="PresetsDirectory">Where presets live, to refuse an unknown preset before a run starts.</param>
/// <param name="Listen">Address to bind; loopback unless the server runs on a private network (ADR 0024).</param>
/// <param name="AllowedHosts">Host names accepted besides localhost and 127.0.0.1; required when Listen is not loopback.</param>
/// <param name="MaxConcurrentRuns">Runs the server executes at once (a driven batch counts as one); the rest wait.</param>
/// <param name="MaxSessionCalls">Research and review runs driven sessions may have executing at once, across all sessions.</param>
/// <param name="StateDirectory">Where the run-token key is kept so tokens outlive a restart (ADR 0043); null: a fresh key each start.</param>
/// <param name="Accounts">The accounts besides the owner that the profile declares (ADR 0050).</param>
/// <param name="AccountKeysFile">The file that holds their keys, as salted hashes; null: no account key opens anything.</param>
/// <param name="Clock">The time the accounts' day and month windows are cut by; null: the system's.</param>
/// <param name="Ui">Serve the operator console at /console/ (ADR 0056); /ui/ redirects to it (ADR 0072).</param>
/// <param name="Profile">The profile, for the console's read routes (CHARGEHAND-209): the owner's <c>system</c> and <c>repositories</c> views and intake's settings. Null: those views are empty.</param>
/// <param name="SecretPresent">Whether a secret item resolves, for an account's credential state; null: it is not known.</param>
/// <param name="IntakeStatePath">Intake's state file (<c>intake/github.json</c>) for <c>GET /v1/intake</c>; null: none.</param>
/// <param name="Access">Sign-in by Cloudflare Access identity (ADR 0071); null: keys only.</param>
/// <param name="AccessCerts">Reads the team's signing keys; null: an HTTPS GET of <see cref="CloudflareAccessSettings.CertsUrl"/>.</param>
/// <param name="ProxySignIn">Sign-in by the email a trusted single-sign-on proxy forwards (ADR 0071); null: keys only.</param>
/// <param name="ProxyResolver">Resolves the proxies' names; null: DNS.</param>
/// <param name="Probes">What <c>?deep=1</c> on <c>/v1/health</c> checks besides the intake model (CHARGEHAND-316): the runners, the GitHub App and the push token the profile configures; null: none.</param>
/// <param name="InboxDismissalsPath">The file of the inbox's dismissals (<c>inbox/dismissed.json</c>, ADR 0064); null: nothing can be dismissed.</param>
public sealed record ServerSettings(int Port, string ApiKey, string PresetsDirectory, string Listen = "127.0.0.1",
    IReadOnlyList<string>? AllowedHosts = null, int MaxConcurrentRuns = 1, int MaxSessionCalls = 2, string? StateDirectory = null,
    IReadOnlyList<AccountSettings>? Accounts = null, string? AccountKeysFile = null, TimeProvider? Clock = null, bool Ui = false,
    Profile? Profile = null, Func<string, bool>? SecretPresent = null, string? IntakeStatePath = null, string? InboxDismissalsPath = null, TimeSpan? McpWait = null,
    CloudflareAccessSettings? Access = null, Func<CancellationToken, Task<string>>? AccessCerts = null,
    ProxySignInSettings? ProxySignIn = null, Func<string, CancellationToken, Task<System.Net.IPAddress[]>>? ProxyResolver = null,
    IReadOnlyList<DependencyProbe>? Probes = null);

/// <summary>
/// The v1 HTTP interface and MCP server (ADR 0014, ADR 0018), on 127.0.0.1 unless the profile opens it (ADR 0024): POST /v1/runs, GET /v1/runs/{id},
/// GET /v1/runs/{id}/events (server-sent events), and the MCP tool "orchestrate" at /v1/mcp; the same tool over stdio (ADR 0027).
/// </summary>
public static partial class ChargehandServer
{
    public const int MaxRequestBytes = 1_000_000;

    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    /// <summary>How long an MCP call over HTTP waits before it answers with the run id (ADR 0065): under the 60 s after which Claude Code's client gives up.</summary>
    public static readonly TimeSpan DefaultMcpWait = TimeSpan.FromSeconds(50);

    public const int MaxWaitSeconds = 60;

    /// <summary>Sent to MCP clients on initialize so they know when to call chargehand without loading the tool first.</summary>
    public const string Instructions =
        "chargehand answers codebase questions with read-only coding-agent workers and returns result/v1: claims with "
        + "evidence, a confidence, and open questions. Call the orchestrate tool when a question needs several files read or "
        + "a second agent's independent answer. Pin the checkout with context.repository (path, commit): the path "
        + "may be owner/name or a GitHub URL, and the repositories tool lists what this server reads. Bound cost with "
        + "context.preset (default, cheap, thorough, strict, draft) or context.budget_usd. Runs can take minutes: a call that "
        + "returns a run id instead of result/v1 is still running, and the result tool with that run_id reads it.";

    public static WebApplication Create(ServerSettings settings, Orchestrator orchestrator, IRunLog log, Chargehand.Driven.DrivenRun? driven = null, Chargehand.Automation.GitHubIntake? intake = null,
        Func<Chargehand.Automation.IMorningSource, Chargehand.Automation.Scheduler>? scheduler = null,
        Chargehand.Automation.PhoneAnswers? phone = null)
    {
        var address = IPAddress.Parse(settings.Listen);
        IReadOnlyList<string> hosts = settings.AllowedHosts ?? [];
        // Beyond loopback the Host check is the only guard against DNS rebinding, so it must name the server.
        if (!IPAddress.IsLoopback(address) && hosts.Count == 0)
            throw new InvalidOperationException($"http.listen {settings.Listen} is not loopback; set http.allowed_hosts (ADR 0024)");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(address, settings.Port);
            k.Limits.MaxRequestBodySize = MaxRequestBytes;
        });
        // A page in the owner's browser could reach the port through DNS rebinding; only a known Host is accepted.
        builder.Services.AddHostFiltering(o => o.AllowedHosts = ["localhost", "127.0.0.1", .. hosts]);
        // OrchestrateTool reads the MCP call's Prefer header.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(new McpHttp(settings.McpWait ?? DefaultMcpWait, log));
        // The owner hears of a paused or resumed model credential where scheduled results go (automation.schedules.notify_url), else on the phone (automation.notify.ntfy_url).
        var notify = CredentialAlert.Notify(settings.Profile?.Automation);
        AddOrchestrate(builder.Services, orchestrator, settings.PresetsDirectory, driven, settings.MaxConcurrentRuns, settings.MaxSessionCalls, settings.StateDirectory, log, settings.Clock,
                settings.Accounts, notify)
            // Hybrid: 2026-07-28 clients run stateless (tasks, input_required results); initialize clients get a session.
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);

        // Before the listener opens: interrupted batches are taken up and leftovers swept, so no new batch can be mistaken for one.
        builder.Services.AddHostedService(sp => new BatchResumer(sp.GetRequiredService<RunService>(), log));
        builder.Services.AddHostedService(sp => new CredentialProbe(sp.GetRequiredService<RunService>(),
            (actor, ct) => orchestrator.ProbeAsync(CredentialProbe.Timeout, ct, actor), settings.Clock));
        // ADR 0076: automated starts are charged to and admitted as automation.account when the profile names one.
        var automationAccount = settings.Profile?.Automation?.Account is { } charged ? settings.Accounts?.FirstOrDefault(a => a.Name == charged) : null;
        // ADR 0051: only when the profile has automation.github. After the resumer, so a restart's own batches are counted before intake starts new ones.
        if (intake is not null)
            builder.Services.AddHostedService(sp => new IntakeService(intake, sp.GetRequiredService<RunService>(), automationAccount));
        // ADR 0068: only when the profile has automation.schedules. The morning report reads the same run log and inbox the console does.
        if (scheduler is not null)
            builder.Services.AddHostedService(sp =>
            {
                var runs = sp.GetRequiredService<RunService>();
                var dismissals = settings.InboxDismissalsPath is { } path ? new InboxDismissals(path) : null;
                var source = new ServerMorningSource(() => new ConsoleContext(settings, runs, log, driven, new SecretPresence(settings.SecretPresent), dismissals));
                return new ScheduleService(scheduler(source), runs, automationAccount);
            });
        // ADR 0077: only when the profile has automation.notify. It reads the same inbox the console does.
        if (phone is not null)
            builder.Services.AddHostedService(sp =>
            {
                var runs = sp.GetRequiredService<RunService>();
                var dismissals = settings.InboxDismissalsPath is { } path ? new InboxDismissals(path) : null;
                return new AnswerService(phone.Notifier(new ServerQuestionSource(() => new ConsoleContext(settings, runs, log, driven, new SecretPresence(settings.SecretPresent), dismissals)), settings.Clock));
            });
        // ADR 0045: reads back what became of the pull requests, only where there are learning records.
        if (driven?.Learning is not null)
            builder.Services.AddHostedService(_ => new PrOutcomeRefresher(driven));

        var app = builder.Build();
        app.UseHostFiltering();
        var expected = Encoding.UTF8.GetBytes($"Bearer {settings.ApiKey}");
        var accounts = settings.Accounts is { Count: > 0 } declared && settings.AccountKeysFile is { } keysFile ? new AccountDirectory(declared, new AccountKeyStore(keysFile), new AccountKeyUse(AccountKeyUse.FileFor(keysFile), settings.Clock)) : null;
        var access = settings.Access is { } accessSettings ? new CloudflareAccessVerifier(accessSettings, settings.AccessCerts ?? Certs(accessSettings), settings.Clock) : null;
        var proxies = settings.ProxySignIn is { } proxy ? new TrustedProxies(proxy.Proxies, settings.ProxyResolver, settings.Clock) : null;
        if (access is not null)
            app.Lifetime.ApplicationStopped.Register(access.Dispose);
        app.Use(async (ctx, next) =>
        {
            // The console's files (and the redirect from /ui/) are public and carry no data; the page calls the routes below with a key (ADR 0056).
            if (settings.Ui && WebConsole.Serves(ctx.Request.Path))
            {
                await next(ctx);
                return;
            }
            // An approvals server's answer carries no key: its signature is checked by the route (ADR 0077).
            if (phone?.Settings.Approvals is not null && HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path == ConsoleAnswers.Path)
            {
                await next(ctx);
                return;
            }
            var header = ctx.Request.Headers.Authorization.ToString();
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), expected))
            {
                Identify(ctx, Principal.Owner);
                await next(ctx);
                return;
            }
            // An account's key (ADR 0050) opens every route an owner's does, for its own runs.
            if (header.StartsWith("Bearer ", StringComparison.Ordinal) && accounts?.Authenticate(header["Bearer ".Length..]) is { } account)
            {
                Identify(ctx, account);
                await next(ctx);
                return;
            }
            // A driven session's run-scoped token opens three things and nothing else: POST /v1/runs, the status of its own run and of the runs it started, and the MCP tool (ADR 0039).
            if (header.StartsWith("Bearer ", StringComparison.Ordinal)
                && ctx.RequestServices.GetRequiredService<RunService>().Tokens.Validate(header["Bearer ".Length..], DateTimeOffset.UtcNow) is { } claims)
            {
                var path = ctx.Request.Path.Value ?? "";
                var open = (ctx.Request.Method == "POST" && path == "/v1/runs")
                    || (ctx.Request.Method == "GET" && Regex.IsMatch(path, "^/v1/runs/[^/]+$"))
                    || path == "/v1/mcp" || path.StartsWith("/v1/mcp/", StringComparison.Ordinal);
                if (!open)
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                ctx.Items[RunScope.ItemKey] = claims;
                Identify(ctx, new Principal(claims.Actor ?? AccountSettings.Owner));
                await next(ctx);
                return;
            }
            // No key at all: the identity Cloudflare Access checked (ADR 0071), for the console's own calls only.
            if (header.Length == 0 && access is not null && await AccessPrincipal(ctx, access, settings) is { } identity)
            {
                Identify(ctx, identity);
                await next(ctx);
                return;
            }
            if (header.Length == 0 && proxies is not null && await ProxyPrincipal(ctx, proxies, settings) is { } forwarded)
            {
                Identify(ctx, forwarded);
                await next(ctx);
                return;
            }
            // The console's first call, with no key: "no identity" is an answer, not an error, so the browser logs nothing.
            ctx.Response.StatusCode = ctx.Request.Path == IdentityPath ? StatusCodes.Status204NoContent : StatusCodes.Status401Unauthorized;
        });

        app.MapPost("/v1/runs", async (HttpContext ctx, RunService runs) =>
        {
            RunRequest? request;
            IReadOnlyList<string> errors;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                (request, errors) = runs.Validate(doc.RootElement);
            }
            catch (JsonException e)
            {
                (request, errors) = (null, [$"not JSON: {e.Message}"]);
            }
            if (request is null)
                return Json(new { errors }, StatusCodes.Status400BadRequest);
            if (runs.Halted)
                return Json(new { errors = new[] { RunService.HaltedMessage } }, StatusCodes.Status503ServiceUnavailable);
            string? parent = null;
            var who = PrincipalHttp.Of(ctx);
            if (ctx.Items[RunScope.ItemKey] is RunTokenClaims scope)
            {
                var applied = await RunScope.ApplyAsync(scope, request, runs.Ledger, ctx.RequestAborted);
                if (applied.Refusal is { } refusal)
                    return Json(Problem(refusal), StatusCodes.Status403Forbidden);
                (request, parent) = (applied.Request!, scope.RunId);
            }
            else
            {
                var admitted = await runs.AdmitAsync(who, request, ctx.RequestAborted);
                if (admitted.Refusal is { } refusal)
                    return Refused(ctx, refusal);
                request = admitted.Request;
            }
            if (runs.Start(request, parent, parent, who?.Name) is not { } run)
                return Json(new { errors = new[] { $"{RunService.MaxUnfinished} runs are unfinished; retry later" } }, StatusCodes.Status429TooManyRequests);
            ctx.Response.Headers.Location = $"/v1/runs/{run.Id}";
            await Task.WhenAny(run.Done, Task.Delay(Wait(ctx.Request.Headers["Prefer"]), ctx.RequestAborted));
            return Reply(run);
        });

        if (settings.Ui)
        {
            WebConsole.Map(app);
        }

        ConsoleRoutes.Map(app, settings, log, driven);
        if (phone?.Settings.Approvals is not null)
        {
            var dismissals = settings.InboxDismissalsPath is { } path ? new InboxDismissals(path) : null;
            ConsoleAnswers.Map(app, ctx => new ConsoleContext(settings, ctx.RequestServices.GetRequiredService<RunService>(), log, driven, new SecretPresence(settings.SecretPresent), dismissals), phone);
        }
        ConsoleSources.Map(app, settings, intake);

        foreach (var path in new[] { "/v1/accounts/me", IdentityPath })
            app.MapGet(path, async (HttpContext ctx, RunService runs) =>
                Json(await AccountStanding.ReadAsync(PrincipalHttp.Of(ctx)!, runs, log, ctx.RequestAborted), StatusCodes.Status200OK));

        app.MapGet("/v1/runs", async (HttpContext ctx) =>
        {
            var q = ctx.Request.Query;
            RunState? status = null;
            if (q.TryGetValue("status", out var st))
            {
                var parsed = Enum.GetValues<RunState>().Where(v => JsonNamingPolicy.SnakeCaseLower.ConvertName(v.ToString()) == st.ToString()).Select(v => (RunState?)v).FirstOrDefault();
                if (parsed is null)
                    return Json(new { errors = new[] { $"status must be one of {string.Join(", ", Enum.GetNames<RunState>().Select(JsonNamingPolicy.SnakeCaseLower.ConvertName))}" } }, StatusCodes.Status400BadRequest);
                status = parsed;
            }
            DateTimeOffset? since = null;
            if (q.TryGetValue("since", out var sinceText))
            {
                if (!DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
                    return Json(Problem("since must be an ISO 8601 time"), StatusCodes.Status400BadRequest);
                since = when;
            }
            var limit = 50;
            if (q.TryGetValue("limit", out var limitText) && (!int.TryParse(limitText, out limit) || limit is < 1 or > 500))
                return Json(Problem("limit must be 1 to 500"), StatusCodes.Status400BadRequest);
            string? kind = q.TryGetValue("kind", out var kindText) ? kindText.ToString() : null;
            if (kind is not (null or "single" or "batch"))
                return Json(Problem("kind must be single or batch"), StatusCodes.Status400BadRequest);
            var who = PrincipalHttp.Of(ctx);
            // An account's list is its own runs whatever it asks; only the owner may pick an actor.
            var actor = who is { IsOwner: false } ? who.Name : q.TryGetValue("actor", out var actorText) ? actorText.ToString() : null;
            var query = new RunListQuery(status, since, limit, actor, kind, q.TryGetValue("repository", out var repository) ? repository.ToString() : null, q.TryGetValue("run_id", out var runId) ? runId.ToString() : null);
            return Json(new { runs = await log.ListAsync(query, ctx.RequestAborted) }, StatusCodes.Status200OK);
        });

        // What finished driven tasks taught (ADR 0045), the table of `chargehand learn report` as JSON; benchmark tasks only with bench=include.
        app.MapGet("/v1/learn", async (HttpContext ctx) =>
        {
            DateTimeOffset? since = null;
            if (ctx.Request.Query.TryGetValue("since", out var sinceText) && (since = Chargehand.Modules.Oversight.Api.LearningReport.ParseSince(sinceText.ToString(), DateTimeOffset.UtcNow)) is null)
                return Json(Problem("since must be a number of days such as 30d, or an ISO 8601 time"), StatusCodes.Status400BadRequest);
            // An account sees the tasks of its own batches (ADR 0050); the owner sees all.
            var who = PrincipalHttp.Of(ctx);
            var includeBench = ctx.Request.Query.TryGetValue("bench", out var bench) && bench == "include";
            var entries = driven?.Learning is { } store ? (await store.ReadAsync(ctx.RequestAborted, includeBench)).Where(e => Principal.Sees(who, e.Record.Actor)).ToList() : [];
            return Json(Chargehand.Modules.Oversight.Api.LearningReport.Build(entries, since), StatusCodes.Status200OK);
        });

        app.MapPost("/v1/runs/{id}/cancel", async (string id, HttpContext ctx, RunService runs) =>
        {
            var who = PrincipalHttp.Of(ctx);
            if (runs.Cancel(id, who))
                return Json(new { run_id = id, status = "cancelling" }, StatusCodes.Status202Accepted);
            return await Stored(log, id, ctx.RequestAborted) is not { } stored || !Principal.Sees(who, stored.Actor)
                ? HttpResults.NotFound()
                : Json(Problem("this server does not hold that run: it has finished, or another process runs it"), StatusCodes.Status409Conflict);
        });

        // The kill switch stops every account's runs, so it is the owner's alone (ADR 0050).
        app.MapPost("/v1/halt", (HttpContext ctx, RunService runs) =>
            OwnerOnly(ctx) ?? Json(new { halted = true, cancelled = runs.Halt() }, StatusCodes.Status200OK));

        app.MapPost("/v1/resume", (HttpContext ctx, RunService runs) =>
        {
            if (OwnerOnly(ctx) is { } refused)
                return refused;
            runs.Resume();
            return Json(new { halted = false }, StatusCodes.Status200OK);
        });

        // Liveness for any key; ?deep=1 (owner) also calls the intake model and probes what the profile configures, which is what a canary needs: the process can be up while every run fails.
        app.MapGet("/v1/health", async (HttpContext ctx, RunService runs) =>
        {
            var version = typeof(Orchestrator).Assembly.GetName().Version?.ToString() ?? "0";
            if (ctx.Request.Query["deep"] != "1")
                return Json(new HealthReport("ok", version, runs.Halted, null), StatusCodes.Status200OK);
            if (OwnerOnly(ctx) is { } refused)
                return refused;
            // The model call takes its 60 s while the configured dependencies are asked at once, each within its own bound.
            var model = orchestrator.ProbeAsync(TimeSpan.FromSeconds(60), ctx.RequestAborted);
            var checks = DependencyHealth.RunAsync(settings.Probes ?? [], DependencyHealth.Timeout, ctx.RequestAborted);
            var probe = await model;
            var dependencies = await checks;
            var ok = probe.Ok && dependencies.All(c => c.Ok);
            return Json(new HealthReport(ok ? "ok" : "failing", version, runs.Halted, probe, dependencies.Count > 0 ? dependencies : null), ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/v1/runs/{id}", async (string id, HttpContext ctx, RunService runs) =>
        {
            var who = PrincipalHttp.Of(ctx);
            var scope = ctx.Items[RunScope.ItemKey] as RunTokenClaims;
            if (runs.Find(id) is { } run)
                return Principal.Sees(who, run.Actor) && RunScope.Reads(scope, id, run.ParentRunId) ? Reply(run) : HttpResults.NotFound();
            var status = await Stored(log, id, ctx.RequestAborted, scope);
            if (status is not null && !Principal.Sees(who, status.Actor))
                status = null;
            return (IResult)(status switch
            {
                null => HttpResults.NotFound(),
                { Result: { } result } => Json(result, StatusCodes.Status200OK),
                { Status: RunState.Lost } => Json(status, StatusCodes.Status410Gone),
                _ => Json(status, StatusCodes.Status202Accepted),
            });
        });

        app.MapGet("/v1/runs/{id}/events", async (string id, HttpContext ctx, RunService runs) =>
        {
            var who = PrincipalHttp.Of(ctx);
            var run = runs.Find(id);
            if (run is not null && !Principal.Sees(who, run.Actor))
                return HttpResults.NotFound();
            if (run is { IsBatch: true } && driven is { JournalsEvents: true })
                return TypedResults.ServerSentEvents(BatchEventStream.Of(run, (after, ct) => driven.JournaledEventsAsync(id, after, ct), BatchEventStream.After(ctx.Request), ctx.RequestAborted));
            if (run is not null)
                return TypedResults.ServerSentEvents(Sse(run.Events(ctx.RequestAborted)));
            // Finished, lost, or running in another process: one event from the log.
            return await Stored(log, id, ctx.RequestAborted) is { } status && Principal.Sees(who, status.Actor) ? TypedResults.ServerSentEvents(Sse(One(status))) : HttpResults.NotFound();
        });

        app.MapMcp("/v1/mcp");
        return app;
    }

    /// <summary>
    /// `chargehand mcp` (ADR 0027): the same tool over stdio, one session, no listener and so no key. Protocol traffic
    /// owns <paramref name="output"/> (the process's stdout), so every log line goes to stderr.
    /// </summary>
    public static IHost CreateStdio(string presetsDirectory, Orchestrator orchestrator, Stream input, Stream output)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new());
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace).SetMinimumLevel(LogLevel.Warning);
        AddOrchestrate(builder.Services, orchestrator, presetsDirectory).WithStreamServerTransport(input, output);
        return builder.Build();
    }

    /// <summary>What both hosts share: the run service, the tool, the tasks store and the instructions.</summary>
    private static IMcpServerBuilder AddOrchestrate(IServiceCollection services, Orchestrator orchestrator, string presetsDirectory, Chargehand.Driven.DrivenRun? driven = null,
        int maxConcurrentRuns = 1, int maxSessionCalls = 2, string? stateDirectory = null, IRunLog? log = null, TimeProvider? clock = null,
        IReadOnlyList<AccountSettings>? accounts = null, Func<string, string, CancellationToken, Task>? notify = null)
    {
        services.AddSingleton(sp => new RunService(orchestrator, presetsDirectory,
            sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping, driven, maxConcurrentRuns, maxSessionCalls, stateDirectory, log, clock, accounts, notify));
        return services.AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "chargehand", Version = typeof(Orchestrator).Assembly.GetName().Version?.ToString() ?? "0" };
                o.ServerInstructions = Instructions;
            })
            .WithTools([OrchestrateTool.Create(), ResultTool.Create(), RepositoriesTool.Create()])
            .WithTasks(new InMemoryMcpTaskStore());
    }

    /// <summary>200 with result/v1 once finished; 202 with the latest run-status/v1 before; 410 if the run died.</summary>
    private static IResult Reply(RunHandle run) =>
        run.Done.IsCompletedSuccessfully ? Json(run.Done.Result, StatusCodes.Status200OK)
        : run.Done.IsFaulted ? Json(run.Latest, StatusCodes.Status410Gone)
        : Json(run.Latest, StatusCodes.Status202Accepted);

    /// <summary>A run this process does not hold, from the run log: finished (with result), running elsewhere, or lost. Null too when a token (<paramref name="scope"/>) may not read it.</summary>
    internal static async Task<RunStatus?> Stored(IRunLog log, string id, CancellationToken ct, RunTokenClaims? scope = null)
    {
        var entry = await log.ReadAsync(id, ct);
        if (!RunScope.Reads(scope, id, entry.Start?.ParentRunId))
            return null;
        if (entry.Run is { } done)
            return RunStatus.Of(id, RunStatus.StateOf(done.Result.Status), RunEventKind.RunFinished) with { Result = done.Result, Actor = done.Actor ?? entry.Start?.Actor };
        if (entry.Start is not { } start)
            return null;
        return start.OwnerAlive()
            ? RunStatus.Of(id, RunState.Running) with { Detail = $"running in process {start.Pid}", Actor = start.Actor }
            : RunStatus.Of(id, RunState.Lost, RunEventKind.RunFinished) with { Detail = "the process that ran it ended before it finished", Actor = start.Actor };
    }

    /// <summary>RFC 7240 "Prefer: wait=N" in seconds, at most <see cref="MaxWaitSeconds"/>.</summary>
    internal static TimeSpan Wait(string? prefer) =>
        PreferWait().Match(prefer ?? "") is { Success: true } m ? TimeSpan.FromSeconds(Math.Min(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), MaxWaitSeconds)) : DefaultWait;

    internal static object Problem(string message) => new { errors = new[] { message } };

    /// <summary>The refusal as the response: its status, the code and the window in the body, and when the window resets as Retry-After.</summary>
    private static IResult Refused(HttpContext ctx, AccountRefusal refusal)
    {
        if (refusal.ResetsAt is { } resets)
            ctx.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((resets - ctx.RequestServices.GetRequiredService<RunService>().Now).TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return Json(refusal.Body(), refusal.Status);
    }

    /// <summary>Records who the request is. The MCP transport ties a session to the user it started with, so an account cannot use another's session id.</summary>
    private static void Identify(HttpContext ctx, Principal who)
    {
        ctx.Items[PrincipalHttp.ItemKey] = who;
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, who.Name)], "chargehand"));
    }

    internal static IResult? OwnerOnly(HttpContext ctx) =>
        PrincipalHttp.Of(ctx) is { IsOwner: false } ? Json(Problem("only the owner's key may do that"), StatusCodes.Status403Forbidden) : null;

    internal static IResult Json(object value, int status) => HttpResults.Json(value, ContractJson.Options, statusCode: status);

    private static async IAsyncEnumerable<SseItem<string>> Sse(IAsyncEnumerable<RunStatus> events)
    {
        await foreach (var e in events)
            yield return new SseItem<string>(JsonSerializer.Serialize(e, ContractJson.Options), JsonNamingPolicy.SnakeCaseLower.ConvertName((e.Event ?? RunEventKind.Started).ToString()));
    }

    private static async IAsyncEnumerable<RunStatus> One(RunStatus status)
    {
        await Task.CompletedTask;
        yield return status;
    }

    [GeneratedRegex(@"\bwait=(\d{1,4})\b")]
    private static partial Regex PreferWait();
}

/// <summary>What the HTTP host adds for its MCP tools; absent over stdio. <paramref name="Wait"/>: how long a call without <c>Prefer: wait</c> waits for a run.</summary>
public sealed record McpHttp(TimeSpan Wait, IRunLog Log);

/// <summary><c>GET /v1/health</c>: <paramref name="Probe"/> and <paramref name="Checks"/> (what the profile configures besides the model, none when it configures nothing) only with <c>?deep=1</c>.</summary>
public sealed record HealthReport(string Status, string Version, bool Halted, ModelProbe? Probe, IReadOnlyList<DependencyCheck>? Checks = null);
