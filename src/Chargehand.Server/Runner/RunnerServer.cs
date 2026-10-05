using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Containers;
using Chargehand.Runner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Chargehand.Server;

/// <param name="Port">0 picks a free one.</param>
/// <param name="ApiKey">Required as a bearer token on every route.</param>
/// <param name="Listen">Loopback unless the runner sits on a private network; then <paramref name="AllowedHosts"/> must name it.</param>
public sealed record RunnerSettings(int Port, string ApiKey, RunnerPolicy Policy, string Listen = "127.0.0.1", IReadOnlyList<string>? AllowedHosts = null);

/// <summary>The runner (ADR 0039): the one process that holds the container engine's socket, in front of it a narrow HTTP API. Each route is one
/// engine call with its inputs checked by <see cref="RunnerPolicy"/> and <see cref="ContainerTemplate"/>; a caller names a run, a listed image
/// and numbers under ceilings, never a flag, a mount, a network mode or a capability. It touches only containers, volumes and networks that
/// carry chargehand's label or prefix, so it is safe on an engine that other stacks share.</summary>
public static class RunnerServer
{
    public static WebApplication Create(RunnerSettings settings, IContainerEngine engine)
    {
        var address = IPAddress.Parse(settings.Listen);
        IReadOnlyList<string> hosts = settings.AllowedHosts ?? [];
        if (!IPAddress.IsLoopback(address) && hosts.Count == 0)
            throw new InvalidOperationException($"the runner's listen address {settings.Listen} is not loopback; set allowed_hosts (ADR 0024)");
        var policy = settings.Policy;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(address, settings.Port);
            k.Limits.MaxRequestBodySize = 64 * 1024;
        });
        builder.Services.AddHostFiltering(o => o.AllowedHosts = ["localhost", "127.0.0.1", .. hosts]);
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

        app.MapPost("/start", (HttpContext ctx, CancellationToken ct) => Guard(async () =>
        {
            var start = await Body<RunnerStart>(ctx, ct);
            if (policy.RefuseStart(start) is { } refusal)
                return Refuse(refusal);
            if (await engine.CountAsync(ct) >= policy.MaxContainers)
                return Reply(StatusCodes.Status429TooManyRequests, $"{policy.MaxContainers} session containers exist already");
            var spec = new ContainerSpec(start.RunId, start.Image, start.WorkVolume, start.OutVolume, start.Network, start.Env, start.MemoryMb, start.Cpus, start.Pids, start.Command);
            _ = ContainerTemplate.RunArgs(spec, "/dev/null");
            _ = ContainerTemplate.EnvFileLines(spec.Env);
            return Ok(new { id = await engine.StartAsync(spec, ct) });
        }));

        app.MapPost("/workspace", (HttpContext ctx, CancellationToken ct) => Guard(async () =>
        {
            var workspace = await Body<RunnerWorkspace>(ctx, ct);
            if (engine is not IWorkspaceEngine preparer)
                return Reply(StatusCodes.Status501NotImplemented, "this runner's engine cannot prepare workspaces");
            if (policy.RefuseWorkspace(workspace) is { } refusal)
                return Refuse(refusal);
            var spec = new WorkspaceSpec(workspace.RunId, workspace.Image, workspace.SourcePath, workspace.WorkVolume, workspace.Branch, workspace.Commit);
            _ = ContainerTemplate.WorkspaceArgs(spec); // the runner validates every value itself, whatever its engine does
            await preparer.PrepareWorkspaceAsync(spec, ct);
            return Ok(new { });
        }));

        app.MapPut("/out/{runId}/{name}", (HttpContext ctx, string runId, string name, CancellationToken ct) => Guard(async () =>
        {
            var image = ctx.Request.Query["image"].ToString();
            if (engine is not IOutVolumeEngine volumes)
                return Reply(StatusCodes.Status501NotImplemented, "this runner's engine cannot write into an output volume");
            if (policy.RefuseOutFile(image, name, write: true) is { } refusal)
                return Refuse(refusal);
            var spec = new OutFileSpec(runId, image, RunnerPolicy.OutPrefix + runId, name);
            _ = ContainerTemplate.OutFileArgs(spec, write: true);
            using var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body, ct);
            await volumes.WriteOutFileAsync(spec, body.ToArray(), ct);
            return Ok(new { });
        }));

        app.MapGet("/out/{runId}/{name}", (HttpContext ctx, string runId, string name, CancellationToken ct) => Guard(async () =>
        {
            var image = ctx.Request.Query["image"].ToString();
            if (engine is not IOutVolumeEngine volumes)
                return Reply(StatusCodes.Status501NotImplemented, "this runner's engine cannot read an output volume");
            if (policy.RefuseOutFile(image, name, write: false) is { } refusal)
                return Refuse(refusal);
            var spec = new OutFileSpec(runId, image, RunnerPolicy.OutPrefix + runId, name);
            _ = ContainerTemplate.OutFileArgs(spec, write: false);
            // A bundle can be large: it goes through a temporary file that is deleted when the response ends, not through memory.
            var file = new FileStream(Path.GetTempFileName(), new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.ReadWrite, Options = FileOptions.DeleteOnClose });
            try
            {
                if (!await volumes.ReadOutFileAsync(spec, file, ct))
                {
                    await file.DisposeAsync();
                    return Reply(StatusCodes.Status404NotFound, $"no {name} in the output volume");
                }
                file.Position = 0;
                return HttpResults.Stream(file, "application/octet-stream");
            }
            catch
            {
                await file.DisposeAsync();
                throw;
            }
        }));

        app.MapPost("/signal", (HttpContext ctx, CancellationToken ct) => Guard(async () =>
        {
            var signal = await Body<RunnerSignal>(ctx, ct);
            if (!await engine.OwnsAsync(signal.Id, ct))
                return NotOurs();
            await engine.SignalAsync(signal.Id, signal.Signal, ct);
            return Ok(new { });
        }));

        app.MapGet("/inspect/{id}", (HttpContext ctx, string id, CancellationToken ct) => Guard(async () =>
        {
            if (!await engine.OwnsAsync(id, ct))
                return NotOurs();
            var state = await engine.InspectAsync(id, ct);
            return Ok(new RunnerInspect(state.Status.ToString().ToLowerInvariant(), state.ExitCode, state.OomKilled));
        }));

        app.MapGet("/logs/{id}", (HttpContext ctx, string id, CancellationToken ct) => Guard(async () =>
        {
            if (!await engine.OwnsAsync(id, ct))
                return NotOurs();
            var bytes = int.TryParse(ctx.Request.Query["bytes"], out var b) ? Math.Clamp(b, 1, 65_536) : 8192;
            return Ok(new { logs = await engine.LogsTailAsync(id, bytes, ct) });
        }));

        app.MapPost("/remove/{id}", (HttpContext ctx, string id, CancellationToken ct) => Guard(async () =>
        {
            if (!await engine.OwnsAsync(id, ct))
                return NotOurs();
            await engine.RemoveAsync(id, ct);
            return Ok(new { });
        }));

        app.MapPost("/kill-all", (CancellationToken ct) => Guard(async () =>
        {
            await engine.KillAllAsync(ct);
            return Ok(new { });
        }));

        app.MapGet("/count", (HttpContext ctx, CancellationToken ct) => Guard(async () => Ok(new { count = await engine.CountAsync(ct) })));

        app.MapPost("/volumes", (HttpContext ctx, CancellationToken ct) => Guard(async () =>
        {
            var volume = await Body<RunnerVolume>(ctx, ct);
            if (RunnerPolicy.RefuseVolume(volume.Name) is { } refusal)
                return Refuse(refusal);
            await engine.CreateVolumeAsync(volume.Name, volume.RunId, ct);
            return Ok(new { });
        }));

        app.MapDelete("/volumes/{name}", (HttpContext ctx, string name, CancellationToken ct) => Guard(async () =>
        {
            if (RunnerPolicy.RefuseVolume(name) is { } refusal)
                return Refuse(refusal);
            await engine.RemoveVolumeAsync(name, ct);
            return Ok(new { });
        }));

        app.MapPost("/networks", (HttpContext ctx, CancellationToken ct) => Guard(async () =>
        {
            var network = await Body<RunnerNetwork>(ctx, ct);
            if (RunnerPolicy.RefuseNetwork(network.Name) is { } refusal)
                return Refuse(refusal);
            await engine.CreateNetworkAsync(network.Name, network.BatchId, ct);
            return Ok(new { });
        }));

        app.MapDelete("/networks/{name}", (HttpContext ctx, string name, CancellationToken ct) => Guard(async () =>
        {
            if (RunnerPolicy.RefuseNetwork(name) is { } refusal)
                return Refuse(refusal);
            await engine.RemoveNetworkAsync(name, ct);
            return Ok(new { });
        }));

        app.MapPost("/egress", (HttpContext ctx, CancellationToken ct) => Guard(async () =>
        {
            var egress = await Body<RunnerEgress>(ctx, ct);
            if (RunnerPolicy.RefuseNetwork(egress.Network) is { } refusal)
                return Refuse(refusal);
            if (policy.RefuseForwards(egress.Forwards) is { } forwardRefusal)
                return Refuse(forwardRefusal);
            return Ok(new { id = await engine.StartEgressAsync(new EgressSpec(egress.BatchId, policy.EgressImage, egress.Network, egress.Allow, Forwards: egress.Forwards), ct) });
        }));

        app.MapPost("/connect", (HttpContext ctx, CancellationToken ct) => Guard(async () =>
        {
            var connect = await Body<RunnerConnect>(ctx, ct);
            if (policy.RefuseConnect(connect.Network) is { } refusal)
                return Refuse(refusal);
            if (!await engine.OwnsAsync(connect.Container, ct))
                return NotOurs();
            await engine.ConnectNetworkAsync(connect.Container, connect.Network, ct);
            return Ok(new { });
        }));
        return app;
    }

    private static async Task<T> Body<T>(HttpContext ctx, CancellationToken ct) where T : class =>
        await ctx.Request.ReadFromJsonAsync<T>(RunnerJson.Options, ct) ?? throw new JsonException("empty body");

    private static async Task<IResult> Guard(Func<Task<IResult>> handler)
    {
        try
        {
            return await handler();
        }
        catch (Exception e) when (e is JsonException or ArgumentException or BadHttpRequestException or InvalidOperationException)
        {
            return Reply(StatusCodes.Status400BadRequest, e.Message);
        }
        catch (ChargehandException e)
        {
            return Reply(StatusCodes.Status503ServiceUnavailable, e.Message);
        }
    }

    private static IResult Ok(object body) => HttpResults.Json(body, RunnerJson.Options);

    private static IResult Refuse(string reason) => Reply(StatusCodes.Status403Forbidden, reason);

    private static IResult NotOurs() => Reply(StatusCodes.Status404NotFound, "no such session container");

    private static IResult Reply(int status, string error) => HttpResults.Json(new { error }, RunnerJson.Options, statusCode: status);
}
