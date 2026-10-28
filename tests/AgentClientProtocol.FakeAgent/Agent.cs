using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace AgentClientProtocol.FakeAgent;

/// <summary>The fake agent's side of the protocol. A prompt's first word picks the scenario:
/// <c>say</c> (three chunks), <c>gate</c> (holds the turn until <c>session/set_mode</c> asks for mode <c>release</c>), <c>slow</c> (waits for cancel), <c>ask KIND</c> (permission), <c>tool</c>, <c>usage</c>,
/// <c>die</c>, <c>fs PATH</c>, <c>caps</c>, <c>unknown</c>, <c>noise</c>, <c>terminal</c>.</summary>
internal sealed class Agent(Wire wire, int protocolVersion)
{
    private readonly ConcurrentDictionary<string, bool> _active = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _cancels = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
    private JsonNode? _clientCapabilities;
    private int _sessions;
    private string _model = "fast";

    public async Task HandleAsync(string method, JsonNode? id, JsonObject args)
    {
        switch (method)
        {
            case "initialize":
                _clientCapabilities = args["clientCapabilities"]?.DeepClone();
                wire.Reply(id, new JsonObject
                {
                    ["protocolVersion"] = protocolVersion,
                    ["agentCapabilities"] = new JsonObject { ["loadSession"] = true, ["promptCapabilities"] = new JsonObject { ["image"] = true } },
                    ["agentInfo"] = new JsonObject { ["name"] = "fake-agent", ["version"] = "1.0.0" },
                    ["authMethods"] = new JsonArray(new JsonObject { ["id"] = "fake-key", ["name"] = "Fake key" }),
                });
                break;
            case "authenticate":
                wire.Reply(id, new JsonObject());
                break;
            case "session/new":
                wire.Reply(id, NewSession($"sess-{Interlocked.Increment(ref _sessions)}"));
                break;
            case "session/load":
                Replay(args["sessionId"]!.GetValue<string>());
                wire.Reply(id, new JsonObject { ["modes"] = Modes(), ["configOptions"] = ConfigOptions() });
                break;
            case "session/set_mode":
                SetMode(id, args);
                break;
            case "session/set_model":
                wire.Reply(id, new JsonObject());
                break;
            case "session/set_config_option":
                _model = args["value"]!.GetValue<string>();
                wire.Reply(id, new JsonObject { ["configOptions"] = ConfigOptions() });
                break;
            case "session/cancel":
                Cancel(args["sessionId"]!.GetValue<string>());
                break;
            case "session/prompt":
                await PromptAsync(id, args);
                break;
            default:
                if (id is not null)
                    wire.Fail(id, -32601, $"method not found: {method}");
                break;
        }
    }

    private void SetMode(JsonNode? id, JsonObject args)
    {
        var mode = args["modeId"]!.GetValue<string>();
        if (mode == "bad")
        {
            wire.Fail(id, -32602, "unknown mode");
            return;
        }

        if (mode == "release")
            Gate(args["sessionId"]!.GetValue<string>()).TrySetResult();
        wire.Reply(id, new JsonObject());
    }

    private JsonObject NewSession(string sessionId) => new()
    {
        ["sessionId"] = sessionId,
        ["modes"] = Modes(),
        ["models"] = new JsonObject
        {
            ["currentModelId"] = "fast",
            ["availableModels"] = new JsonArray(new JsonObject { ["modelId"] = "fast", ["name"] = "Fast" }, new JsonObject { ["modelId"] = "deep", ["name"] = "Deep" }),
        },
        ["configOptions"] = ConfigOptions(),
    };

    private static JsonObject Modes() => new()
    {
        ["currentModeId"] = "default",
        ["availableModes"] = new JsonArray(new JsonObject { ["id"] = "default", ["name"] = "Default" }, new JsonObject { ["id"] = "plan", ["name"] = "Plan" }),
    };

    private JsonArray ConfigOptions() => new(new JsonObject
    {
        ["id"] = "model",
        ["name"] = "Model",
        ["type"] = "select",
        ["currentValue"] = _model,
        ["options"] = new JsonArray(new JsonObject { ["value"] = "fast", ["name"] = "Fast" }, new JsonObject { ["value"] = "deep", ["name"] = "Deep" }),
    });

    private void Replay(string sessionId)
    {
        Update(sessionId, "user_message_chunk", new JsonObject { ["content"] = Text("earlier question") });
        Update(sessionId, "agent_message_chunk", new JsonObject { ["content"] = Text("earlier answer") });
    }

    private void Cancel(string sessionId)
    {
        if (_cancels.TryGetValue(sessionId, out var cancel))
            cancel.TrySetResult();
    }

    private async Task PromptAsync(JsonNode? id, JsonObject args)
    {
        var sessionId = args["sessionId"]!.GetValue<string>();
        var text = args["prompt"]!.AsArray()[0]!["text"]!.GetValue<string>();
        if (!_active.TryAdd(sessionId, true))
        {
            // Two prompts at once on one session: a client that queues never causes this.
            Chunk(sessionId, "OVERLAP");
            wire.Reply(id, new JsonObject { ["stopReason"] = "end_turn" });
            return;
        }

        var cancel = _cancels[sessionId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var result = await Scenarios.RunAsync(this, sessionId, text, cancel.Task);
            wire.Reply(id, result);
        }
        finally
        {
            _active.TryRemove(sessionId, out _);
        }
    }

    public Task GateAsync(string sessionId) => Gate(sessionId).Task;

    private TaskCompletionSource Gate(string sessionId) => _gates.GetOrAdd(sessionId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public Wire Wire => wire;

    public JsonNode? ClientCapabilities => _clientCapabilities;

    public static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    public void Chunk(string sessionId, string text) => Update(sessionId, "agent_message_chunk", new JsonObject { ["content"] = Text(text) });

    public void Update(string sessionId, string kind, JsonObject fields)
    {
        fields["sessionUpdate"] = kind;
        wire.Notify("session/update", new JsonObject { ["sessionId"] = sessionId, ["update"] = fields });
    }
}
