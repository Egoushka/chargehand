using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Containers;
using Chargehand.Contracts;

namespace Chargehand.Runner;

/// <summary>An <see cref="IContainerEngine"/> over the runner's HTTP API (ADR 0039), for a server that must not hold the container engine's
/// socket. The <see cref="HttpClient"/> carries the base address and the bearer key.</summary>
public sealed class RunnerClient(HttpClient http) : IContainerEngine, IWorkspaceEngine, IOutVolumeEngine
{
    public async Task<string> StartAsync(ContainerSpec spec, CancellationToken ct)
    {
        var body = new RunnerStart(spec.RunId, spec.ImageDigest, spec.WorkVolume, spec.OutVolume, spec.Network, new Dictionary<string, string>(spec.Env),
            spec.MemoryMb, spec.Cpus, spec.Pids, [.. spec.Command]);
        return (await Send<IdReply>(HttpMethod.Post, "/start", body, ct)).Id;
    }

    public Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct) =>
        Send(HttpMethod.Post, "/workspace", new RunnerWorkspace(spec.RunId, spec.Image, spec.SourcePath, spec.WorkVolume, spec.Branch, spec.Commit), ct);

    public Task WriteOutFileAsync(OutFileSpec spec, ReadOnlyMemory<byte> content, CancellationToken ct) =>
        SendRaw(HttpMethod.Put, OutPath(spec), new ByteArrayContent(content.ToArray()), ct);

    public async Task<bool> ReadOutFileAsync(OutFileSpec spec, Stream destination, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendRaw(HttpMethod.Get, OutPath(spec), null, ct, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (ChargehandException e) when (e.Message.Contains("404", StringComparison.Ordinal))
        {
            return false;
        }
        using (response)
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await body.CopyToAsync(destination, ct);
        }
        return true;
    }

    private static string OutPath(OutFileSpec spec) =>
        $"/out/{Uri.EscapeDataString(spec.RunId)}/{Uri.EscapeDataString(spec.Name)}?image={Uri.EscapeDataString(spec.Image)}";

    public Task SignalAsync(string id, string signal, CancellationToken ct) => Send(HttpMethod.Post, "/signal", new RunnerSignal(id, signal), ct);

    public async Task<ContainerState> InspectAsync(string id, CancellationToken ct)
    {
        var reply = await Send<RunnerInspect>(HttpMethod.Get, $"/inspect/{Uri.EscapeDataString(id)}", null, ct);
        return new ContainerState(Enum.Parse<ContainerStatus>(reply.Status, ignoreCase: true), reply.ExitCode, reply.OomKilled);
    }

    /// <summary>A task cleans up by the container's fixed name even when it never started one, and the runner answers 404 for a name it does not own or that
    /// is gone. That is not a failure: it must not replace the error that came first (<c>docker rm -f</c> is silent about a missing container too).</summary>
    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        try
        {
            await Send(HttpMethod.Post, $"/remove/{Uri.EscapeDataString(id)}", new { }, ct);
        }
        catch (ChargehandException e) when (e.Message.Contains("404", StringComparison.Ordinal))
        {
        }
    }

    public Task KillAllAsync(CancellationToken ct) => Send(HttpMethod.Post, "/kill-all", new { }, ct);

    public async Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) =>
        (await Send<LogsReply>(HttpMethod.Get, $"/logs/{Uri.EscapeDataString(id)}?bytes={bytes}", null, ct)).Logs;

    public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) => Send(HttpMethod.Post, "/volumes", new RunnerVolume(name, runId), ct);

    public Task RemoveVolumeAsync(string name, CancellationToken ct) => Send(HttpMethod.Delete, $"/volumes/{Uri.EscapeDataString(name)}", null, ct);

    public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) => Send(HttpMethod.Post, "/networks", new RunnerNetwork(name, batchId), ct);

    public Task RemoveNetworkAsync(string name, CancellationToken ct) => Send(HttpMethod.Delete, $"/networks/{Uri.EscapeDataString(name)}", null, ct);

    public async Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) =>
        (await Send<IdReply>(HttpMethod.Post, "/egress", new RunnerEgress(spec.BatchId, spec.Network, [.. spec.Allow], spec.Forwards is null ? null : [.. spec.Forwards],
            spec.Gateway is { } g ? new RunnerModelGateway(g.Credential, g.TokenKey, g.Host, g.Port) : null), ct)).Id;

    public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) => Send(HttpMethod.Post, "/connect", new RunnerConnect(container, network), ct);

    public async Task<bool> OwnsAsync(string id, CancellationToken ct)
    {
        try
        {
            await Send<RunnerInspect>(HttpMethod.Get, $"/inspect/{Uri.EscapeDataString(id)}", null, ct);
            return true;
        }
        catch (ChargehandException e) when (e.Message.Contains("404", StringComparison.Ordinal))
        {
            return false;
        }
    }

    public async Task<int> CountAsync(CancellationToken ct) => (await Send<CountReply>(HttpMethod.Get, "/count", null, ct)).Count;

    private sealed record IdReply(string Id);

    private sealed record LogsReply(string Logs);

    private sealed record CountReply(int Count);

    private async Task Send(HttpMethod method, string path, object? body, CancellationToken ct) => await SendRaw(method, path, body, ct);

    private async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRaw(method, path, body, ct);
        return await response.Content.ReadFromJsonAsync<T>(RunnerJson.Options, ct) ?? throw Unavailable("the runner answered with an empty body", null);
    }

    private async Task<HttpResponseMessage> SendRaw(HttpMethod method, string path, object? body, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is HttpContent raw)
            request.Content = raw;
        else if (body is not null)
            request.Content = JsonContent.Create(body, options: RunnerJson.Options);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException e)
        {
            throw Unavailable($"the runner could not be reached: {e.Message}", null);
        }
        if (response.IsSuccessStatusCode)
            return response;
        var text = await response.Content.ReadAsStringAsync(ct);
        string? message = null;
        try { message = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString(); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
        response.Dispose();
        throw Unavailable($"the runner refused {method} {path}: {(int)response.StatusCode} {message ?? (text.Length > 200 ? text[..200] : text)}", (int)response.StatusCode);
    }

    private static ChargehandException Unavailable(string message, int? status) =>
        new(ErrorCode.ContainerUnavailable, message, status is 401 or 403
            ? "Check the runner's key and its image allowlist (driven.runner in the profile)."
            : "Check that the runner service is running and reachable, then retry.");
}
