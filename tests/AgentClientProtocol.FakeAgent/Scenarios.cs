using System.Text.Json.Nodes;

namespace AgentClientProtocol.FakeAgent;

/// <summary>What the fake agent does for the first word of a prompt.</summary>
internal static class Scenarios
{
    /// <summary>A scenario that ends the prompt with a JSON-RPC error, as a provider that rejects the key does.</summary>
    public sealed class Failure(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    public static async Task<JsonObject> RunAsync(Agent agent, string session, string prompt, Task cancelled)
    {
        var words = prompt.Split(' ', 2);
        var rest = words.Length > 1 ? words[1] : "";
        switch (words[0])
        {
            case "say":
                foreach (var part in new[] { rest[..(rest.Length / 3)], rest[(rest.Length / 3)..(2 * rest.Length / 3)], rest[(2 * rest.Length / 3)..] })
                    agent.Chunk(session, part);
                return End("end_turn");
            case "gate":
                agent.Chunk(session, "started");
                await agent.GateAsync(session);
                agent.Chunk(session, "released");
                return End("end_turn");
            case "slow":
                agent.Chunk(session, "working");
                await cancelled;
                return End("cancelled");
            case "ask":
                return await AskAsync(agent, session, rest);
            case "tool":
                Tool(agent, session);
                return End("end_turn");
            case "usage":
                agent.Update(session, "usage_update", new JsonObject { ["used"] = 100, ["size"] = 1000, ["cost"] = new JsonObject { ["amount"] = 0.01, ["currency"] = "USD" } });
                var done = End("end_turn");
                done["usage"] = new JsonObject { ["inputTokens"] = 10, ["outputTokens"] = 5, ["totalTokens"] = 15 };
                return done;
            case "die":
                agent.Chunk(session, "dying");
                await Console.Error.WriteLineAsync("boom: the fake agent crashed");
                Environment.Exit(3);
                return End("end_turn");
            case "fs":
                return await ReadFileAsync(agent, session, rest);
            case "caps":
                agent.Chunk(session, agent.ClientCapabilities?.ToJsonString() ?? "null");
                return End("end_turn");
            case "unknown":
                agent.Update(session, "future_thing", new JsonObject { ["x"] = 1 });
                agent.Chunk(session, "after");
                return End("end_turn");
            case "noise":
                agent.Wire.WriteNoise("Loaded cached credentials.");
                agent.Chunk(session, "ok");
                return End("end_turn");
            case "terminal":
                return await TerminalAsync(agent, session);
            case "authfail":
                throw new Failure(-32000, "authentication_error: invalid x-api-key");
            default:
                agent.Chunk(session, $"unknown command: {words[0]}");
                return End("refusal");
        }
    }

    private static JsonObject End(string stopReason) => new() { ["stopReason"] = stopReason };

    private static void Tool(Agent agent, string session)
    {
        agent.Update(session, "tool_call", new JsonObject
        {
            ["toolCallId"] = "t1",
            ["title"] = "Read a.cs",
            ["kind"] = "read",
            ["status"] = "in_progress",
            ["locations"] = new JsonArray(new JsonObject { ["path"] = "src/a.cs", ["line"] = 12 }),
            ["rawInput"] = new JsonObject { ["path"] = "src/a.cs" },
        });
        agent.Update(session, "tool_call_update", new JsonObject
        {
            ["toolCallId"] = "t1",
            ["status"] = "completed",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "content", ["content"] = Agent.Text("class A {}") }),
        });
    }

    private static int _asks = 1;

    /// <summary>Each ask is a new tool call (t2, t3, ...), as a real agent's are.</summary>
    private static async Task<JsonObject> AskAsync(Agent agent, string session, string kind)
    {
        var reply = await agent.Wire.CallAsync("session/request_permission", new JsonObject
        {
            ["sessionId"] = session,
            ["toolCall"] = new JsonObject { ["toolCallId"] = $"t{Interlocked.Increment(ref _asks)}", ["title"] = "Do it", ["kind"] = kind, ["status"] = "pending", ["rawInput"] = new JsonObject { ["command"] = "rm -rf x" } },
            ["options"] = new JsonArray(
                new JsonObject { ["optionId"] = "allow-once", ["name"] = "Allow", ["kind"] = "allow_once" },
                new JsonObject { ["optionId"] = "reject", ["name"] = "Reject", ["kind"] = "reject_once" }),
        });
        var outcome = reply["result"]?["outcome"];
        agent.Chunk(session, $"permission:{outcome?["outcome"]}:{outcome?["optionId"]}");
        return End("end_turn");
    }

    private static async Task<JsonObject> ReadFileAsync(Agent agent, string session, string path)
    {
        var reply = await agent.Wire.CallAsync("fs/read_text_file", new JsonObject { ["sessionId"] = session, ["path"] = path });
        agent.Chunk(session, reply["error"] is { } error ? $"error:{error["code"]}" : $"file:{reply["result"]!["content"]}");
        return End("end_turn");
    }

    private static async Task<JsonObject> TerminalAsync(Agent agent, string session)
    {
        var created = await agent.Wire.CallAsync("terminal/create", new JsonObject { ["sessionId"] = session, ["command"] = "echo", ["args"] = new JsonArray("hi") });
        if (created["error"] is { } error)
        {
            agent.Chunk(session, $"error:{error["code"]}");
            return End("end_turn");
        }

        var terminalId = created["result"]!["terminalId"]!.GetValue<string>();
        var output = await agent.Wire.CallAsync("terminal/output", new JsonObject { ["sessionId"] = session, ["terminalId"] = terminalId });
        agent.Chunk(session, $"terminal:{output["result"]!["output"]}");
        await agent.Wire.CallAsync("terminal/release", new JsonObject { ["sessionId"] = session, ["terminalId"] = terminalId });
        return End("end_turn");
    }
}
