using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentClientProtocol.FakeAgent;

/// <summary>Newline-delimited JSON-RPC out of the fake agent, and the replies to the requests it makes of the client.</summary>
internal sealed class Wire(Stream output)
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _replies = new();
    private long _nextId = 1000;

    public void Write(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        lock (_gate)
        {
            output.Write(bytes);
            output.Flush();
        }
    }

    /// <summary>Raw text on standard output that is not JSON: what a chatty CLI does.</summary>
    public void WriteNoise(string text)
    {
        lock (_gate)
        {
            output.Write(Encoding.UTF8.GetBytes(text + "\n"));
            output.Flush();
        }
    }

    public void Reply(JsonNode? id, JsonNode result) => Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

    public void Fail(JsonNode? id, int code, string message) =>
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    public void Notify(string method, JsonObject args) => Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = args });

    /// <summary>Calls the client and waits for its answer (the result, or the whole message when it is an error).</summary>
    public async Task<JsonObject> CallAsync(string method, JsonObject args)
    {
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _replies[id] = reply;
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = args });
        return await reply.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void Complete(long id, JsonObject message)
    {
        if (_replies.TryRemove(id, out var reply))
            reply.TrySetResult(message);
    }
}
