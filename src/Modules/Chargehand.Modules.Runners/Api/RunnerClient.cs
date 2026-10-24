using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Runners.Api;

/// <summary>An <see cref="IContainerEngine"/> over the runner's HTTP API (ADR 0039), for a server that must not hold the container engine's
/// socket. The <see cref="HttpClient"/> carries the base address and the bearer key. A call that gets no answer within <see cref="ControlTimeout"/> (a transfer: <see cref="TransferTimeout"/>) is a runner
/// that did not answer, not a cancel, so give the client an infinite <see cref="HttpClient.Timeout"/> when files of repository size travel through it (ADR 0058).</summary>
/// <param name="maxDownloadBytes">The most a file read out of an output volume may be (the session's bundle); more is refused before it is copied, as the Docker CLI engine refuses it.</param>
public sealed class RunnerClient(HttpClient http, long maxDownloadBytes = DockerCliEngine.OutFileCap)
    : IContainerEngine, IWorkspaceEngine, IBundleWorkspaceEngine, IOutVolumeEngine, IOutTailEngine, IServiceEngine, IHostSource
{
    public static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(100);

    public static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(30);

    public async Task<string> StartAsync(ContainerSpec spec, CancellationToken ct)
    {
        var body = new RunnerStart(spec.RunId, spec.ImageDigest, spec.WorkVolume, spec.OutVolume, spec.Network, new Dictionary<string, string>(spec.Env),
            spec.MemoryMb, spec.Cpus, spec.Pids, [.. spec.Command], spec.Caches is { Count: > 0 } caches ? [.. caches] : null,
            spec.Home, spec.Install ? true : null);
        return (await Send(HttpMethod.Post, "/start", Json(body, RunnerJsonContext.Default.RunnerStart), RunnerJsonContext.Default.IdReply, ct)).Id;
    }

    public async Task<string> StartServiceAsync(ServiceSpec spec, CancellationToken ct) =>
        (await Send(HttpMethod.Post, "/service", Json(new RunnerService(spec.BatchId, spec.Name, spec.Image, spec.Network, spec.Port, new Dictionary<string, string>(spec.Env),
            [.. spec.Tmpfs], spec.MemoryMb), RunnerJsonContext.Default.RunnerService), RunnerJsonContext.Default.IdReply, ct)).Id;

    public Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct) =>
        Send(HttpMethod.Post, "/workspace", Json(new RunnerWorkspace(spec.RunId, spec.Image, spec.SourcePath, spec.WorkVolume, spec.Branch, spec.Commit), RunnerJsonContext.Default.RunnerWorkspace), ct);

    public Task PrepareBundleWorkspaceAsync(BundleWorkspaceSpec spec, CancellationToken ct) =>
        Send(HttpMethod.Post, "/workspace/bundle", Json(new RunnerBundleWorkspace(spec.RunId, spec.Image, spec.WorkVolume, spec.Branch, spec.Commit), RunnerJsonContext.Default.RunnerBundleWorkspace), ct);

    public async Task WriteOutFileAsync(OutFileSpec spec, ReadOnlyMemory<byte> content, CancellationToken ct) =>
        (await SendRaw(HttpMethod.Put, OutPath(spec), new ByteArrayContent(content.ToArray()), ct)).Dispose();

    /// <summary>Sends the file as the request body with its length, so the runner can refuse it by size before reading it and passes it on as it arrives.</summary>
    public async Task WriteOutStreamAsync(OutFileSpec spec, Stream content, long length, CancellationToken ct)
    {
        using var body = new StreamContent(content, 81920);
        body.Headers.ContentLength = length;
        (await SendRaw(HttpMethod.Put, OutPath(spec), body, ct, transfer: true)).Dispose();
    }

    public async Task<bool> ReadOutFileAsync(OutFileSpec spec, Stream destination, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendRaw(HttpMethod.Get, OutPath(spec), null, ct, HttpCompletionOption.ResponseHeadersRead, transfer: true);
        }
        catch (ChargehandException e) when (e.Message.Contains("404", StringComparison.Ordinal))
        {
            return false;
        }
        using (response)
        {
            if (response.Content.Headers.ContentLength is { } length && length > maxDownloadBytes)
                throw TooLarge();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TransferTimeout);
            var buffer = new byte[81920];
            long total = 0;
            try
            {
                int n;
                while ((n = await body.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += n;
                    if (total > maxDownloadBytes)
                        throw TooLarge();
                    await destination.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            catch (IOException e)
            {
                throw new RunnerUnreachableException($"the runner stopped sending {spec.Name}: {e.Message}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RunnerUnreachableException($"the runner did not finish sending {spec.Name} in time");
            }
        }
        return true;

        ChargehandException TooLarge() => new(ErrorCode.ContainerUnavailable, $"a file in the output volume is larger than {maxDownloadBytes / (1024 * 1024)} MiB",
            "The session wrote more than chargehand will copy out; nothing from it is used.");
    }

    /// <summary>The end of a session's transcript (ADR 0059): at most <paramref name="maxBytes"/>, read by the runner with <c>tail -c</c>. A runner that predates this refuses the name (403) or the parameter
    /// and reads as an error the caller treats as "not available".</summary>
    public async Task<bool> ReadOutTailAsync(OutFileSpec spec, int maxBytes, Stream destination, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendRaw(HttpMethod.Get, $"{OutPath(spec)}&tail={maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}", null, ct);
        }
        catch (ChargehandException e) when (e.Message.Contains("404", StringComparison.Ordinal))
        {
            return false;
        }
        using (response)
        {
            if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
                throw new ChargehandException(ErrorCode.ContainerUnavailable, $"the runner sent {length} bytes of a tail of {maxBytes}", "Update the runner.");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > maxBytes)
                throw new ChargehandException(ErrorCode.ContainerUnavailable, $"the runner sent {bytes.Length} bytes of a tail of {maxBytes}", "Update the runner.");
            await destination.WriteAsync(bytes, ct);
        }
        return true;
    }

    private static string OutPath(OutFileSpec spec) =>
        $"/out/{Uri.EscapeDataString(spec.RunId)}/{Uri.EscapeDataString(spec.Name)}?image={Uri.EscapeDataString(spec.Image)}";

    public Task SignalAsync(string id, string signal, CancellationToken ct) => Send(HttpMethod.Post, "/signal", Json(new RunnerSignal(id, signal), RunnerJsonContext.Default.RunnerSignal), ct);

    public async Task<ContainerState> InspectAsync(string id, CancellationToken ct)
    {
        var reply = await Send(HttpMethod.Get, $"/inspect/{Uri.EscapeDataString(id)}", null, RunnerJsonContext.Default.RunnerInspect, ct);
        return new ContainerState(Enum.Parse<ContainerStatus>(reply.Status, ignoreCase: true), reply.ExitCode, reply.OomKilled, reply.MemoryPeakMb);
    }

    /// <summary>A task cleans up by the container's fixed name even when it never started one, and the runner answers 404 for a name it does not own or that
    /// is gone. That is not a failure: it must not replace the error that came first (<c>docker rm -f</c> is silent about a missing container too).</summary>
    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        try
        {
            await Send(HttpMethod.Post, $"/remove/{Uri.EscapeDataString(id)}", Json(new RunnerEmpty(), RunnerJsonContext.Default.RunnerEmpty), ct);
        }
        catch (ChargehandException e) when (e.Message.Contains("404", StringComparison.Ordinal))
        {
        }
    }

    public Task KillAllAsync(CancellationToken ct) => Send(HttpMethod.Post, "/kill-all", Json(new RunnerEmpty(), RunnerJsonContext.Default.RunnerEmpty), ct);

    public async Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) =>
        (await Send(HttpMethod.Get, $"/logs/{Uri.EscapeDataString(id)}?bytes={bytes}", null, RunnerJsonContext.Default.LogsReply, ct)).Logs;

    public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) => Send(HttpMethod.Post, "/volumes", Json(new RunnerVolume(name, runId), RunnerJsonContext.Default.RunnerVolume), ct);

    public Task RemoveVolumeAsync(string name, CancellationToken ct) => Send(HttpMethod.Delete, $"/volumes/{Uri.EscapeDataString(name)}", null, ct);

    public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) => Send(HttpMethod.Post, "/networks", Json(new RunnerNetwork(name, batchId), RunnerJsonContext.Default.RunnerNetwork), ct);

    public Task RemoveNetworkAsync(string name, CancellationToken ct) => Send(HttpMethod.Delete, $"/networks/{Uri.EscapeDataString(name)}", null, ct);

    public async Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) =>
        (await Send(HttpMethod.Post, "/egress", Json(new RunnerEgress(spec.BatchId, spec.Network, [.. spec.Allow], spec.Forwards is null ? null : [.. spec.Forwards],
            spec.Gateway is { } g ? new RunnerModelGateway(g.Credential, g.TokenKey, g.Host, g.Port) : null), RunnerJsonContext.Default.RunnerEgress), RunnerJsonContext.Default.IdReply, ct)).Id;

    public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) => Send(HttpMethod.Post, "/connect", Json(new RunnerConnect(container, network), RunnerJsonContext.Default.RunnerConnect), ct);

    public async Task<bool> OwnsAsync(string id, CancellationToken ct)
    {
        try
        {
            await Send(HttpMethod.Get, $"/inspect/{Uri.EscapeDataString(id)}", null, RunnerJsonContext.Default.RunnerInspect, ct);
            return true;
        }
        catch (ChargehandException e) when (e.Message.Contains("404", StringComparison.Ordinal))
        {
            return false;
        }
    }

    public async Task<int> CountAsync(CancellationToken ct) => (await Send(HttpMethod.Get, "/count", null, RunnerJsonContext.Default.CountReply, ct)).Count;

    public async Task<EngineResources> ListAsync(CancellationToken ct)
    {
        var reply = await Send(HttpMethod.Get, "/resources", null, RunnerJsonContext.Default.RunnerResources, ct);
        return new EngineResources(reply.Containers, reply.Networks, reply.Volumes);
    }

    /// <summary>The runner's host snapshot. A runner older than <c>GET /host</c> answers 404: null, which the scheduler reads as "unknown".</summary>
    public async Task<HostSnapshot?> ReadAsync(CancellationToken ct)
    {
        try
        {
            return await Send(HttpMethod.Get, "/host", null, RunnerJsonContext.Default.HostSnapshot, ct);
        }
        catch (ChargehandException e) when (e.Message.Contains("404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private async Task Send(HttpMethod method, string path, HttpContent? body, CancellationToken ct) => await SendRaw(method, path, body, ct);

    private async Task<T> Send<T>(HttpMethod method, string path, HttpContent? body, JsonTypeInfo<T> reply, CancellationToken ct)
    {
        using var response = await SendRaw(method, path, body, ct);
        return await response.Content.ReadFromJsonAsync(reply, ct) ?? throw Unavailable("the runner answered with an empty body", null);
    }

    private static JsonContent Json<T>(T body, JsonTypeInfo<T> info) => JsonContent.Create(body, info);

    private async Task<HttpResponseMessage> SendRaw(HttpMethod method, string path, HttpContent? body, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, bool transfer = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(transfer ? TransferTimeout : ControlTimeout);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = body;
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, completion, timeout.Token);
        }
        catch (HttpRequestException e)
        {
            throw new RunnerUnreachableException($"the runner could not be reached: {e.Message}");
        }
        // A timeout (the client's, or ours) is a cancellation to .NET; read as one it would stop a session whose runner merely did not answer.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RunnerUnreachableException($"the runner did not answer {method} {path} in time");
        }
        if (response.IsSuccessStatusCode)
            return response;
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        string? message = null;
        try { message = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString(); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
        response.Dispose();
        throw Unavailable($"the runner refused {method} {path}: {(int)response.StatusCode} {message ?? (text.Length > 200 ? text[..200] : text)}", (int)response.StatusCode);
    }

    private static ChargehandException Unavailable(string message, int? status)
    {
        // 429 is the runner at its container limit: another runner may take the task, or this one later.
        if (status is 429 or 502 or 503 or 504)
            return new RunnerUnreachableException(message);
        return new ChargehandException(ErrorCode.ContainerUnavailable, message, status is 401 or 403
            ? "Check the runner's key and its image allowlist (driven.runner in the profile)."
            : "Check that the runner service is running and reachable, then retry.");
    }
}
