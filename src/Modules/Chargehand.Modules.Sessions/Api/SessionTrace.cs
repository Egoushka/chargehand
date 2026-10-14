using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>What a handoff takes from the session's own stream (ADR 0045): its recent tool calls without their outputs, the last test run, whether each step of the <c>change</c>
/// skill last worked, and the todo list it kept. Pure: it reads events and nothing else; the caller reads it after the stream has ended.</summary>
public sealed partial class SessionTrace
{
    /// <summary>How many of the session's latest tool calls the handoff keeps.</summary>
    public const int MaxCalls = 60;

    private const int MaxArguments = 8;
    private const int MaxArgumentLength = 200;
    private static readonly string[] StepNames = ["research", "write", "test", "review"];

    private readonly HashSet<string> _seen = [];
    private readonly Dictionary<string, string> _pending = [];
    private readonly List<ToolCall> _calls = [];
    private readonly Dictionary<string, bool> _passes = [];
    private string? _testId;
    private List<TodoItem> _todos = [];

    public IReadOnlyList<ToolCall> Calls => _calls;

    /// <summary>The latest test command (the one <see cref="Adherence"/> recognises) with its result; <c>IsError</c> is null while no result came back, so the session was stopped inside it.</summary>
    public TestRun? LastTest { get; private set; }

    /// <summary>The <c>change</c> skill's steps. A step passes when its latest call returned without an error: the driver sets it from the stream, never from what the model says.
    /// A step with no call has not passed.</summary>
    public IReadOnlyList<StepState> Steps => [.. StepNames.Select(n => new StepState(n, _passes.GetValueOrDefault(n)))];

    /// <summary>The session's own todo list as of its last <c>TodoWrite</c>; empty if it kept none.</summary>
    public IReadOnlyList<TodoItem> Todos => _todos;

    public void OnEvent(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type) || !e.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;
        foreach (var block in content.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("type", out _)))
            switch (type.GetString(), block.GetProperty("type").GetString())
            {
                case ("assistant", "tool_use"):
                    OnCall(block);
                    break;
                case ("user", "tool_result"):
                    OnResult(block);
                    break;
            }
    }

    private void OnCall(JsonElement block)
    {
        var id = block.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        // A tool call can appear in more than one partial message of a turn.
        if (id is not null && !_seen.Add(id))
            return;
        var name = block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        var input = block.TryGetProperty("input", out var inp) ? inp : default;
        _calls.Add(new ToolCall(name, Arguments(input)));
        if (_calls.Count > MaxCalls)
            _calls.RemoveAt(0);
        if (name == "TodoWrite")
            OnTodos(input);
        if (id is null || Adherence.Step(block) is not { } step)
            return;
        _pending[id] = step;
        if (step == "test")
            OnTest(id, input);
    }

    private void OnTodos(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array)
            _todos = [.. todos.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object && t.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                .Select(t => new TodoItem(t.GetProperty("content").GetString()!, t.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "pending"))];
    }

    private void OnTest(string id, JsonElement input)
    {
        _testId = id;
        LastTest = new TestRun(input.ValueKind == JsonValueKind.Object && input.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "", null, null);
    }

    private void OnResult(JsonElement block)
    {
        if (!block.TryGetProperty("tool_use_id", out var id) || id.ValueKind != JsonValueKind.String || !_pending.Remove(id.GetString()!, out var step))
            return;
        var failed = block.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
        _passes[step] = !failed;
        if (step == "test" && id.GetString() == _testId && LastTest is { } test)
            LastTest = test with { IsError = failed, ExitCode = failed ? ExitCodeOf(block) : 0 };
    }

    /// <summary>Claude Code's Bash tool starts a failing result with <c>Exit code N</c>; the output after it is not kept.</summary>
    private static int? ExitCodeOf(JsonElement result)
    {
        var text = new StringBuilder();
        if (result.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
                text.Append(content.GetString());
            else if (content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String))
                    text.Append(part.GetProperty("text").GetString());
        }
        return ExitCode().Match(text.ToString()) is { Success: true } m && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    private static Dictionary<string, string> Arguments(JsonElement input)
    {
        var arguments = new Dictionary<string, string>();
        if (input.ValueKind == JsonValueKind.Object)
            foreach (var p in input.EnumerateObject().Take(MaxArguments))
                arguments[p.Name] = Cut(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());
        else if (input.ValueKind != JsonValueKind.Undefined)
            arguments["value"] = Cut(input.GetRawText());
        return arguments;
    }

    private static string Cut(string s) => s.Length <= MaxArgumentLength ? s : s[..MaxArgumentLength] + "…";

    [GeneratedRegex(@"^\s*Exit code (\d+)")]
    private static partial Regex ExitCode();
}
