using System.Text;
using System.Text.Json;
using AgentClientProtocol;

// Client mode (default): start this binary as an agent, say "ping", answer its permission request, expect "pong".
// Agent mode (--agent): the smallest agent that exercises initialize, a session, streamed chunks and a permission round trip.
if (args is ["--agent"])
{
    return RunAgent();
}

var text = new StringBuilder();
await using var client = AcpClient.Launch(
    new AcpLaunchOptions { FileName = Environment.ProcessPath!, Arguments = ["--agent"] },
    new AcpClientOptions
    {
        OnSessionUpdate = (n, _) =>
        {
            if (n.Update is AgentMessageChunk { Content.Text: { } chunk })
                text.Append(chunk);
            return ValueTask.CompletedTask;
        },
        OnRequestPermission = (r, _) => ValueTask.FromResult(RequestPermissionOutcome.Selected(r.Options[0].OptionId)),
    });
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var init = await client.InitializeAsync(deadline.Token);
var session = await client.NewSessionAsync(Directory.GetCurrentDirectory(), ct: deadline.Token);
var result = await session.PromptAsync("ping", deadline.Token);
if (init.ProtocolVersion != 1 || result.StopReason != AcpStopReason.EndTurn || text.ToString() != "pong:allow")
{
    Console.Error.WriteLine($"smoke failed: version {init.ProtocolVersion}, stop {result.StopReason}, text '{text}'");
    return 1;
}

Console.WriteLine("ok acp client under Native AOT");
return 0;

static int RunAgent()
{
    var stdout = Console.OpenStandardOutput();
    void Send(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            body(w);
            w.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        stdout.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
        stdout.Flush();
    }

    void Reply(long id, Action<Utf8JsonWriter> result) => Send(w =>
    {
        w.WriteNumber("id", id);
        w.WritePropertyName("result");
        w.WriteStartObject();
        result(w);
        w.WriteEndObject();
    });

    void Chunk(string t) => Send(w =>
    {
        w.WriteString("method", "session/update");
        w.WriteStartObject("params");
        w.WriteString("sessionId", "s1");
        w.WriteStartObject("update");
        w.WriteString("sessionUpdate", "agent_message_chunk");
        w.WriteStartObject("content");
        w.WriteString("type", "text");
        w.WriteString("text", t);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndObject();
    });

    using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    while (input.ReadLine() is { } line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("method", out var m) || !root.TryGetProperty("id", out var idElement))
            continue;
        var id = idElement.GetInt64();
        switch (m.GetString())
        {
            case "initialize":
                Reply(id, w => w.WriteNumber("protocolVersion", 1));
                break;
            case "session/new":
                Reply(id, w => w.WriteString("sessionId", "s1"));
                break;
            case "session/prompt":
                Chunk("po");
                Chunk("ng");
                Send(w =>
                {
                    w.WriteNumber("id", 900);
                    w.WriteString("method", "session/request_permission");
                    w.WriteStartObject("params");
                    w.WriteString("sessionId", "s1");
                    w.WriteStartObject("toolCall");
                    w.WriteString("toolCallId", "t1");
                    w.WriteEndObject();
                    w.WriteStartArray("options");
                    w.WriteStartObject();
                    w.WriteString("optionId", "allow");
                    w.WriteString("name", "Allow");
                    w.WriteString("kind", "allow_once");
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteEndObject();
                });
                var answer = input.ReadLine()!;
                using (var answerDoc = JsonDocument.Parse(answer))
                    Chunk(":" + answerDoc.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
                Reply(id, w => w.WriteString("stopReason", "end_turn"));
                break;
        }
    }

    return 0;
}
