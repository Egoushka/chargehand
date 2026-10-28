using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AgentClientProtocol;

/// <summary>A JSON-RPC failure to send back to the agent for a call it made.</summary>
internal sealed class RpcFailure(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>What the connection hands the calls the agent makes.</summary>
internal interface IRpcHandler
{
    ValueTask OnNotificationAsync(string method, JsonElement args, CancellationToken ct);

    /// <summary>Returns the result as JSON, or throws <see cref="RpcFailure"/>.</summary>
    ValueTask<byte[]> OnRequestAsync(string method, JsonElement args, CancellationToken ct);
}

/// <summary>JSON-RPC 2.0 over newline-delimited JSON, both ways. One reader loop; calls from the agent that may wait on a person run on their own task.</summary>
internal sealed class JsonRpcConnection : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    private readonly Stream _fromAgent;
    private readonly Stream _toAgent;
    private readonly IRpcHandler _handler;
    private readonly Action<AcpWireDirection, string>? _tap;
    private readonly Action<Exception>? _onError;
    private readonly Func<CancellationToken, Task<Exception>> _describeClosure;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, (string Method, TaskCompletionSource<JsonElement> Reply)> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<Exception> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private Task? _reader;

    public JsonRpcConnection(
        Stream fromAgent,
        Stream toAgent,
        IRpcHandler handler,
        Action<AcpWireDirection, string>? tap,
        Action<Exception>? onError,
        Func<CancellationToken, Task<Exception>> describeClosure)
    {
        _fromAgent = fromAgent;
        _toAgent = toAgent;
        _handler = handler;
        _tap = tap;
        _onError = onError;
        _describeClosure = describeClosure;
    }

    /// <summary>Completes with the reason when the connection ends, whether the agent closed it or it was disposed.</summary>
    public Task<Exception> Closed => _closed.Task;

    public void Start() => _reader = Task.Run(ReadLoopAsync);

    public async Task<TResult> RequestAsync<TParams, TResult>(
        string method, TParams value, JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken ct)
    {
        var reply = await RequestRawAsync(method, writer => System.Text.Json.JsonSerializer.Serialize(writer, value, paramsInfo), ct).ConfigureAwait(false);
        return reply.Deserialize(resultInfo) ?? throw new AcpConnectionException($"{method} returned null");
    }

    public Task NotifyAsync<TParams>(string method, TParams value, JsonTypeInfo<TParams> paramsInfo, CancellationToken ct) =>
        SendAsync(Frame(null, method, writer => System.Text.Json.JsonSerializer.Serialize(writer, value, paramsInfo)), ct);

    private async Task<JsonElement> RequestRawAsync(string method, Action<Utf8JsonWriter> writeParams, CancellationToken ct)
    {
        ThrowIfClosed();
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (method, reply);
        try
        {
            if (_closed.Task.IsCompleted)
                ThrowIfClosed();
            await SendAsync(Frame(id, method, writeParams), ct).ConfigureAwait(false);
            return await reply.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private static byte[] Frame(long? id, string method, Action<Utf8JsonWriter> writeParams)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            if (id is { } number)
                writer.WriteNumber("id", number);
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            writeParams(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private async Task SendAsync(byte[] json, CancellationToken ct)
    {
        ThrowIfClosed();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            _tap?.Invoke(AcpWireDirection.ToAgent, Utf8.GetString(json));
            await _toAgent.WriteAsync(json, ct).ConfigureAwait(false);
            await _toAgent.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
            await _toAgent.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            throw _closed.Task.IsCompletedSuccessfully ? _closed.Task.Result : new AcpConnectionException($"writing to the agent failed: {e.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed.Task.IsCompletedSuccessfully)
            throw _closed.Task.Result;
    }

    private async Task ReadLoopAsync()
    {
        Exception end;
        try
        {
            using var reader = new StreamReader(_fromAgent, Utf8, false, 1 << 16, leaveOpen: true);
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0)
                    await DispatchAsync(line).ConfigureAwait(false);
            }

            end = await _describeClosure(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            end = new AcpConnectionException("the connection was disposed");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            end = await _describeClosure(CancellationToken.None).ConfigureAwait(false);
        }

        Close(end);
    }

    private void Close(Exception reason)
    {
        _closed.TrySetResult(reason);
        foreach (var (_, (_, reply)) in _pending)
            reply.TrySetException(reason);
    }

    private async Task DispatchAsync(string line)
    {
        _tap?.Invoke(AcpWireDirection.FromAgent, line);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return; // A banner or log line on standard output: the tap has it, the protocol does not.
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            var hasId = root.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null;
            if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
            {
                var name = method.GetString()!;
                var args = root.TryGetProperty("params", out var p) ? p.Clone() : EmptyObject;
                if (hasId)
                {
                    var callId = id.Clone(); // the document is disposed when this method returns
                    _ = Task.Run(() => AnswerAsync(name, callId, args));
                }
                else
                {
                    await NotifyHandlerAsync(name, args).ConfigureAwait(false);
                }
            }
            else if (hasId)
            {
                Complete(id, root);
            }
        }
    }

    private async Task NotifyHandlerAsync(string method, JsonElement args)
    {
        try
        {
            await _handler.OnNotificationAsync(method, args, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _onError?.Invoke(e);
        }
    }

    private void Complete(JsonElement id, JsonElement root)
    {
        if (!id.TryGetInt64(out var number) || !_pending.TryGetValue(number, out var call))
            return;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : -32603;
            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";
            call.Reply.TrySetException(new AcpRequestException(call.Method, code, message, error.TryGetProperty("data", out var d) ? d.Clone() : null));
        }
        else
        {
            call.Reply.TrySetResult(root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object ? result.Clone() : EmptyObject);
        }
    }

    private async Task AnswerAsync(string method, JsonElement id, JsonElement args)
    {
        byte[] reply;
        try
        {
            reply = Respond(id, "result", await _handler.OnRequestAsync(method, args, _stop.Token).ConfigureAwait(false));
        }
        catch (RpcFailure f)
        {
            reply = Respond(id, "error", ErrorBody(f.Code, f.Message));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            _onError?.Invoke(e);
            reply = Respond(id, "error", ErrorBody(-32603, e.Message));
        }

        try
        {
            await SendAsync(reply, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AcpConnectionException or OperationCanceledException or ObjectDisposedException)
        {
            // The agent is gone; there is nobody to answer.
        }
    }

    private static byte[] ErrorBody(int code, string message)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Respond(JsonElement id, string member, byte[] body)
    {
        var buffer = new ArrayBufferWriter<byte>(body.Length + 48);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName(member);
            writer.WriteRawValue(body);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        Close(new AcpConnectionException("the connection was disposed"));
        try
        {
            await _toAgent.DisposeAsync().ConfigureAwait(false);
        }
        // agent-checks: allow empty-catch -- the agent may have closed its input already; disposing is the point
        catch (IOException)
        {
        }

        if (_reader is not null)
            await Task.WhenAny(_reader, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        _stop.Dispose();
    }
}
