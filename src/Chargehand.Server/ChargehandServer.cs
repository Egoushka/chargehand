using System.Globalization;
using System.Net;
using System.Net.ServerSentEvents;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
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
public sealed record ServerSettings(int Port, string ApiKey, string PresetsDirectory, string Listen = "127.0.0.1",
    IReadOnlyList<string>? AllowedHosts = null);

/// <summary>
/// The v1 HTTP interface and MCP server (ADR 0014, ADR 0018), on 127.0.0.1 unless the profile opens it (ADR 0024): POST /v1/runs, GET /v1/runs/{id},
/// GET /v1/runs/{id}/events (server-sent events), and the MCP tool "orchestrate" at /v1/mcp.
/// </summary>
public static partial class ChargehandServer
{
    public const int MaxRequestBytes = 1_000_000;

    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    public const int MaxWaitSeconds = 60;

    /// <summary>Sent to MCP clients on initialize so they know when to call chargehand without loading the tool first.</summary>
    public const string Instructions =
        "chargehand runs a request on coding-agent workers (OpenCode or Claude Code, whichever runtime this server's profile "
        + "configures) and returns result/v1: an answer whose claims each carry evidence (file at a commit, diff, session "
        + "message or caller input), a confidence, and open questions for claims that did not resolve. Workers are read-only. "
        + "Call the orchestrate tool for questions about a codebase that need reading several files or a second agent's "
        + "independent answer; pass request/v1 with text, and context.repository (path, commit) to pin the checkout. "
        + "Set context.preset (default, cheap, thorough, strict, draft) or context.budget_usd to bound cost. "
        + "Set context.interactive false to get status needs_input instead of questions. Runs can take minutes; clients "
        + "that support MCP tasks get the run as a task to poll.";

    public static WebApplication Create(ServerSettings settings, Orchestrator orchestrator, IRunLog log)
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
        builder.Services.AddSingleton(sp => new RunService(orchestrator, settings.PresetsDirectory,
            sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        builder.Services.AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "chargehand", Version = typeof(Orchestrator).Assembly.GetName().Version?.ToString() ?? "0" };
                o.ServerInstructions = Instructions;
            })
            // Hybrid: 2026-07-28 clients run stateless (tasks, input_required results); initialize clients get a session.
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
            .WithTools([OrchestrateTool.Create()])
            .WithTasks(new InMemoryMcpTaskStore());

        var app = builder.Build();
        app.UseHostFiltering();
        var expected = Encoding.UTF8.GetBytes($"Bearer {settings.ApiKey}");
        app.Use(async (ctx, next) =>
        {
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString()), expected))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(ctx);
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
            if (runs.Start(request) is not { } run)
                return Json(new { errors = new[] { $"{RunService.MaxUnfinished} runs are unfinished; retry later" } }, StatusCodes.Status429TooManyRequests);
            ctx.Response.Headers.Location = $"/v1/runs/{run.Id}";
            await Task.WhenAny(run.Done, Task.Delay(Wait(ctx.Request.Headers["Prefer"]), ctx.RequestAborted));
            return Reply(run);
        });

        app.MapGet("/v1/runs/{id}", async (string id, HttpContext ctx, RunService runs) =>
        {
            if (runs.Find(id) is { } run)
                return Reply(run);
            var status = await Stored(log, id, ctx.RequestAborted);
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
            if (runs.Find(id) is { } run)
                return (IResult)TypedResults.ServerSentEvents(Sse(run.Events(ctx.RequestAborted)));
            // Finished, lost, or running in another process: one event from the log.
            return await Stored(log, id, ctx.RequestAborted) is { } status ? TypedResults.ServerSentEvents(Sse(One(status))) : HttpResults.NotFound();
        });

        app.MapMcp("/v1/mcp");
        return app;
    }

    /// <summary>200 with result/v1 once finished; 202 with the latest run-status/v1 before; 410 if the run died.</summary>
    private static IResult Reply(RunHandle run) =>
        run.Done.IsCompletedSuccessfully ? Json(run.Done.Result, StatusCodes.Status200OK)
        : run.Done.IsFaulted ? Json(run.Latest, StatusCodes.Status410Gone)
        : Json(run.Latest, StatusCodes.Status202Accepted);

    /// <summary>A run this process does not hold, from the run log: finished (with result), running elsewhere, or lost.</summary>
    private static async Task<RunStatus?> Stored(IRunLog log, string id, CancellationToken ct)
    {
        var entry = await log.ReadAsync(id, ct);
        if (entry.Run is { } done)
            return RunStatus.Of(id, RunStatus.StateOf(done.Result.Status), RunEventKind.RunFinished) with { Result = done.Result };
        if (entry.Start is not { } start)
            return null;
        return start.Pid != Environment.ProcessId && start.OwnerAlive()
            ? RunStatus.Of(id, RunState.Running) with { Detail = $"running in process {start.Pid}" }
            : RunStatus.Of(id, RunState.Lost, RunEventKind.RunFinished) with { Detail = "the process that ran it ended before it finished" };
    }

    /// <summary>RFC 7240 "Prefer: wait=N" in seconds, at most <see cref="MaxWaitSeconds"/>.</summary>
    internal static TimeSpan Wait(string? prefer) =>
        PreferWait().Match(prefer ?? "") is { Success: true } m ? TimeSpan.FromSeconds(Math.Min(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), MaxWaitSeconds)) : DefaultWait;

    private static IResult Json(object value, int status) => HttpResults.Json(value, ContractJson.Options, statusCode: status);

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
