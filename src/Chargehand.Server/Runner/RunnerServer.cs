using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;
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
/// <param name="Options">How the runner looks after the machine it runs on (ADR 0058); null: it does not.</param>
public sealed record RunnerSettings(int Port, string ApiKey, RunnerPolicy Policy, string Listen = "127.0.0.1", IReadOnlyList<string>? AllowedHosts = null, RunnerOptions? Options = null);

/// <summary>What a runner on a laptop adds (ADR 0058).</summary>
/// <param name="KeepAwake">Hold the machine awake (<c>caffeinate</c>) while a session container runs. macOS only.</param>
/// <param name="Drain">When <c>GET /host</c> says <c>draining</c>.</param>
/// <param name="HostSource"><c>proc</c>: this machine's <c>/proc</c>; <c>engine</c>: ask the engine, for a machine whose engine is a virtual machine; <c>auto</c>: <c>proc</c> on Linux, <c>engine</c> elsewhere.</param>
public sealed record RunnerOptions(bool KeepAwake = false, DrainRules? Drain = null, string HostSource = "auto");

/// <summary>The runner (ADR 0039): the one process that holds the container engine's socket, in front of it a narrow HTTP API. Each route is one
/// engine call with its inputs checked by <see cref="RunnerPolicy"/> and <see cref="ContainerTemplate"/>; a caller names a run, a listed image
/// and numbers under ceilings, never a flag, a mount, a network mode or a capability. It touches only containers, volumes and networks that
/// carry chargehand's label or prefix, so it is safe on an engine that other stacks share.</summary>
public static class RunnerServer
{
    private const long MaxSeedBytes = 128L * 1024 * 1024;

    /// <param name="host">What <c>GET /host</c> reports; null: this machine's <c>/proc</c>. In a container that is the host's unless the engine hides it (lxcfs, a user namespace).</param>
    public static WebApplication Create(RunnerSettings settings, IContainerEngine engine, IHostSource? host = null)
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
            var spec = new ContainerSpec(start.RunId, start.Image, start.WorkVolume, start.OutVolume, start.Network, start.Env, start.MemoryMb, start.Cpus, start.Pids, start.Command,
                Caches: start.Caches, Home: start.Home, Install: start.Install == true);
            _ = ContainerTemplate.RunArgs(spec, "/dev/null");
            _ = ContainerTemplate.EnvFileLines(spec.Env);
            return Ok(new { id = await engine.StartAsync(spec, ct) });
        }));

        app.MapPost("/service", (HttpContext ctx, CancellationToken ct) => Guard(async () =>
        {
            var service = await Body<RunnerService>(ctx, ct);
            if (engine is not IServiceEngine starter)
                return Reply(StatusCodes.Status501NotImplemented, "this runner's engine cannot start services");
            if (policy.RefuseService(service) is { } refusal)
                return Refuse(refusal);
            if (await engine.CountAsync(ct) >= policy.MaxContainers)
                return Reply(StatusCodes.Status429TooManyRequests, $"{policy.MaxContainers} containers exist already");
            var spec = new ServiceSpec(service.BatchId, service.Name, service.Image, service.Network, service.Port, service.Env, service.Tmpfs, service.MemoryMb);
            _ = ContainerTemplate.ServiceArgs(spec, "/dev/null");
            return Ok(new { id = await starter.StartServiceAsync(spec, ct) });
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

        app.MapPost("/workspace/bundle", (HttpContext ctx, CancellationToken ct) => Guard(async () =>
        {
            var workspace = await Body<RunnerBundleWorkspace>(ctx, ct);
            if (engine is not IBundleWorkspaceEngine preparer)
                return Reply(StatusCodes.Status501NotImplemented, "this runner's engine cannot prepare workspaces from a bundle");
            if (policy.RefuseBundleWorkspace(workspace) is { } refusal)
                return Refuse(refusal);
            var spec = new BundleWorkspaceSpec(workspace.RunId, workspace.Image, RunnerPolicy.OutPrefix + workspace.RunId, workspace.WorkVolume, workspace.Branch, workspace.Commit);
            _ = ContainerTemplate.BundleWorkspaceArgs(spec); // the runner validates every value itself, whatever its engine does
            await preparer.PrepareBundleWorkspaceAsync(spec, ct);
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
            // The repository's bundle (ADR 0058) is far larger than a seed: it says its length first, is checked against the runner's limit, and goes to the helper as it arrives.
            if (name == ContainerTemplate.SourceBundleFile)
            {
                if (ctx.Request.ContentLength is not { } length)
                    return Reply(StatusCodes.Status411LengthRequired, "the bundle needs a Content-Length");
                if (policy.RefuseBundleSize(length) is { } tooLarge)
                    return Reply(StatusCodes.Status413PayloadTooLarge, tooLarge);
                if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bundleLimit)
                    bundleLimit.MaxRequestBodySize = policy.MaxBundleBytes;
                await volumes.WriteOutStreamAsync(spec, ctx.Request.Body, length, ct);
                return Ok(new { });
            }
            // A seed is a bundle, far over the 64 KiB the other calls get.
            if (name == ContainerTemplate.SeedFile && ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaxSeedBytes;
            using var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body, ct);
            await volumes.WriteOutFileAsync(spec, body.ToArray(), ct);
            return Ok(new { });
        }));

        app.MapGet("/out/{runId}/{name}", (HttpContext ctx, string runId, string name, CancellationToken ct) => Guard(async () =>
        {
            var image = ctx.Request.Query["image"].ToString();
            // A transcript is served as a tail and never whole (ADR 0059): a file of that kind without ?tail is refused, and so is ?tail on any other file.
            if (ctx.Request.Query.ContainsKey("tail") || ContainerTemplate.OutFilesTail.Contains(name, StringComparer.Ordinal))
            {
                if (!int.TryParse(ctx.Request.Query["tail"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes))
                    return Refuse("the end of a transcript is read with ?tail=<bytes>");
                if (policy.RefuseOutTail(image, name, bytes) is { } tailRefusal)
                    return Refuse(tailRefusal);
                if (engine is not IOutTailEngine tailer)
                    return Reply(StatusCodes.Status501NotImplemented, "this runner's engine cannot read the end of a file in an output volume");
                var tailSpec = new OutFileSpec(runId, image, RunnerPolicy.OutPrefix + runId, name);
                _ = ContainerTemplate.OutTailArgs(tailSpec, bytes);
                using var tail = new MemoryStream();
                return await tailer.ReadOutTailAsync(tailSpec, bytes, tail, ct)
                    ? HttpResults.Bytes(tail.ToArray(), "application/octet-stream")
                    : Reply(StatusCodes.Status404NotFound, $"no {name} in the output volume");
            }
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
            return Ok(new RunnerInspect(state.Status.ToString().ToLowerInvariant(), state.ExitCode, state.OomKilled, state.MemoryPeakMb));
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

        app.MapGet("/host", (CancellationToken ct) => Guard(async () => Ok(await (host ?? new HostProbe()).ReadAsync(ct) ?? new HostSnapshot(null, null, null, null, null, null))));

        app.MapGet("/count", (HttpContext ctx, CancellationToken ct) => Guard(async () => Ok(new { count = await engine.CountAsync(ct) })));

        app.MapGet("/resources", (HttpContext ctx, CancellationToken ct) => Guard(async () =>
        {
            var listed = await engine.ListAsync(ct);
            return Ok(new RunnerResources([.. listed.Containers], [.. listed.Networks], [.. listed.Volumes]));
        }));

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
            // The model endpoint forwards only to the model's own host: the runner, not its caller, decides where a credential may travel.
            if (egress.Gateway is { } g && g.Host != Chargehand.Modules.Gateway.Api.EgressCli.DefaultModelHost)
                return Refuse($"the model endpoint forwards to {Chargehand.Modules.Gateway.Api.EgressCli.DefaultModelHost} only");
            // The same for the fallback key (ADR 0088): only the host the runner's operator named, and it must be an acceptable one.
            if (egress.Gateway?.Fallback is { } fb && (policy.FallbackHost is not { } allowed || !string.Equals(fb.Host, allowed, StringComparison.OrdinalIgnoreCase)
                || Chargehand.Modules.Gateway.Api.ModelGateway.RefuseHost(fb.Host) is not null))
                return Refuse("the model endpoint's fallback goes only to the host the runner was started with (--fallback-host)");
            var gateway = egress.Gateway is { } m ? new ModelGatewaySpec(m.Credential, m.TokenKey, m.Host, m.Port,
                m.Fallback is { } f ? new Chargehand.Modules.Gateway.Api.GatewayFallback(f.Credential, f.Host, f.AllowanceUsd, new Chargehand.Modules.Gateway.Api.FallbackPrice(f.InputPerMtok, f.OutputPerMtok, f.CacheReadPerMtok, f.CacheWritePerMtok)) : null) : null;
            return Ok(new { id = await engine.StartEgressAsync(new EgressSpec(egress.BatchId, policy.EgressImage, egress.Network, egress.Allow, Forwards: egress.Forwards, Gateway: gateway), ct) });
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
