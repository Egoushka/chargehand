using System.Text;
using System.Text.Json;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0045: a stopped session's handoff is built from facts, with no model call. <see cref="SessionTrace"/> takes what it needs from the stream, <see cref="HandoffBuilder"/>
/// renders the two files and keeps secrets out of them.</summary>
public class HandoffTests
{
    private const string Canary = "canary-secret-value-123";
    private static readonly string AwsKey = "AKIA" + "IOSFODNN7EXAMPLE";

    private static JsonElement Call(string id, string name, object input) =>
        JsonSerializer.SerializeToElement(new { type = "assistant", message = new { id = $"m-{id}", content = new object[] { new { type = "tool_use", id, name, input } } } });

    private static JsonElement Bash(string id, string command) => Call(id, "Bash", new { command });

    private static JsonElement Result(string id, string output, bool error = false) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "user",
            message = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = id, content = output, is_error = error } } },
        });

    private static JsonElement Review(string id) => Call(id, "mcp__chargehand__orchestrate", new { context = new { preset = "review" }, text = "review the change" });

    private static HandoffFacts Facts(IReadOnlyList<ToolCall>? calls = null, IReadOnlyList<string>? commits = null, IReadOnlyList<ChangedPath>? changes = null, TestRun? test = null,
        IReadOnlyList<TodoItem>? todos = null) =>
        new("checkpoint_stuck", 37, "aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", commits ?? ["feat: add the retry", "WIP: session stopped by checkpoint_stuck after 37 turns"],
            changes ?? [new ChangedPath("src/Client.cs", 20, 4)], test ?? new TestRun("dotnet test", 1, true),
            [new StepState("research", true), new StepState("write", true), new StepState("test", false), new StepState("review", false)], todos ?? [], calls ?? []);

    private static ToolCall Tool(string name, string command) => new(name, new Dictionary<string, string> { ["command"] = command });

    [Fact]
    public void The_trace_keeps_the_last_sixty_calls_with_their_arguments_and_no_output()
    {
        var trace = new SessionTrace();
        for (var i = 1; i <= 70; i++)
        {
            trace.OnEvent(Bash($"c{i}", $"echo {i}"));
            trace.OnEvent(Result($"c{i}", "OUTPUT-MUST-NOT-APPEAR"));
        }
        trace.OnEvent(Bash("c70", "echo again"));       // the same call in a later partial message of the turn counts once

        Assert.Equal(SessionTrace.MaxCalls, trace.Calls.Count);
        Assert.Equal("echo 11", trace.Calls[0].Arguments["command"]);
        Assert.Equal("echo 70", trace.Calls[^1].Arguments["command"]);
        Assert.DoesNotContain("OUTPUT-MUST-NOT-APPEAR", JsonSerializer.Serialize(trace.Calls));
    }

    [Fact]
    public void A_long_argument_is_cut_and_a_call_with_many_arguments_keeps_the_first_few()
    {
        var trace = new SessionTrace();
        trace.OnEvent(Call("w1", "Write", new { file_path = "src/A.cs", content = new string('x', 5000) }));
        var call = Assert.Single(trace.Calls);
        Assert.Equal("src/A.cs", call.Arguments["file_path"]);
        Assert.True(call.Arguments["content"].Length < 300);
    }

    [Fact]
    public void The_last_test_call_gives_its_command_and_the_exit_code_the_result_names()
    {
        var trace = new SessionTrace();
        trace.OnEvent(Bash("t1", "dotnet test"));
        Assert.Equal(new TestRun("dotnet test", null, null), trace.LastTest);       // stopped inside it: no result
        trace.OnEvent(Result("t1", "Exit code 1\nFailed!  - Failed: 3", error: true));
        Assert.Equal(new TestRun("dotnet test", 1, true), trace.LastTest);
        trace.OnEvent(Bash("t2", "dotnet test --no-build"));
        trace.OnEvent(Result("t2", "Passed!"));
        Assert.Equal(new TestRun("dotnet test --no-build", 0, false), trace.LastTest);
        trace.OnEvent(Bash("t3", "ls"));        // not a test call
        Assert.Equal("dotnet test --no-build", trace.LastTest!.Command);
    }

    [Fact]
    public void A_steps_passes_flag_follows_its_latest_call_and_comes_from_the_stream()
    {
        var trace = new SessionTrace();
        Assert.All(trace.Steps, s => Assert.False(s.Passes));
        trace.OnEvent(Call("r1", "mcp__chargehand__orchestrate", new { context = new { preset = "default" }, text = "how does the client retry" }));
        trace.OnEvent(Result("r1", "claims"));
        trace.OnEvent(Call("e1", "Edit", new { file_path = "src/A.cs", old_string = "a", new_string = "b" }));
        trace.OnEvent(Result("e1", "ok"));
        trace.OnEvent(Bash("t1", "dotnet test"));
        trace.OnEvent(Result("t1", "Exit code 1", error: true));
        trace.OnEvent(Review("v1"));
        trace.OnEvent(Result("v1", "boom", error: true));
        Assert.Equal([("research", true), ("write", true), ("test", false), ("review", false)], trace.Steps.Select(s => (s.Name, s.Passes)));

        trace.OnEvent(Bash("t2", "dotnet test"));
        trace.OnEvent(Result("t2", "Passed!"));
        trace.OnEvent(Review("v2"));
        trace.OnEvent(Result("v2", "findings"));
        Assert.Equal([("research", true), ("write", true), ("test", true), ("review", true)], trace.Steps.Select(s => (s.Name, s.Passes)));
    }

    [Fact]
    public void The_sessions_own_todo_list_is_the_latest_one_it_wrote()
    {
        var trace = new SessionTrace();
        trace.OnEvent(Call("d1", "TodoWrite", new { todos = new[] { new { content = "write the retry", status = "in_progress", activeForm = "Writing" } } }));
        trace.OnEvent(Call("d2", "TodoWrite", new { todos = new[] { new { content = "write the retry", status = "completed", activeForm = "Writing" }, new { content = "run the tests", status = "pending", activeForm = "Running" } } }));
        Assert.Equal([new TodoItem("write the retry", "completed"), new TodoItem("run the tests", "pending")], trace.Todos);
    }

    [Fact]
    public void The_builder_writes_the_facts_as_json_and_as_text()
    {
        var files = HandoffBuilder.Build(Facts(calls: [Tool("Bash", "dotnet test"), new ToolCall("Edit", new Dictionary<string, string> { ["file_path"] = "src/Client.cs", ["new_string"] = "retry" })],
            todos: [new TodoItem("finish the retry", "in_progress")]), []);

        var json = JsonDocument.Parse(files.Json).RootElement;
        Assert.Equal("checkpoint_stuck", json.GetProperty("reason").GetString());
        Assert.Equal("aaaaaaaaaaaaaaaa", json.GetProperty("base_commit").GetString());
        Assert.Equal("bbbbbbbbbbbbbbbb", json.GetProperty("head_commit").GetString());
        Assert.Equal(["feat: add the retry", "WIP: session stopped by checkpoint_stuck after 37 turns"], json.GetProperty("commits").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(20, json.GetProperty("changes")[0].GetProperty("added").GetInt64());
        Assert.Equal(1, json.GetProperty("last_test").GetProperty("exit_code").GetInt32());
        Assert.True(json.GetProperty("last_test").GetProperty("is_error").GetBoolean());
        Assert.Equal(["research", "write", "test", "review"], json.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.False(json.GetProperty("tasks")[2].GetProperty("passes").GetBoolean());
        Assert.Equal("finish the retry", json.GetProperty("todos")[0].GetProperty("text").GetString());
        Assert.Equal("src/Client.cs", json.GetProperty("calls")[1].GetProperty("arguments").GetProperty("file_path").GetString());

        Assert.Contains("stopped by checkpoint stuck after 37 turns", files.Markdown);
        Assert.Contains("- [x] research", files.Markdown);
        Assert.Contains("- [ ] test", files.Markdown);
        Assert.Contains("`dotnet test`: failed, exit code 1", files.Markdown);
        Assert.Contains("- feat: add the retry", files.Markdown);
        Assert.Contains("- src/Client.cs (+20 -4)", files.Markdown);
        Assert.Contains("- Bash: dotnet test", files.Markdown);
        Assert.Contains("finish the retry (in_progress)", files.Markdown);
        Assert.Empty(files.Findings);
    }

    [Fact]
    public void An_item_that_matches_a_secret_pattern_is_dropped_and_recorded_without_its_value()
    {
        var files = HandoffBuilder.Build(Facts(calls: [Tool("Bash", "ls"), Tool("Bash", $"curl -H 'x: {AwsKey}' https://api.example.test"), Tool("Bash", "make")],
            commits: ["feat: ok", $"fix: rotate {AwsKey}"], changes: [new ChangedPath("src/A.cs", 1, 0), new ChangedPath("config/.env", 3, 0)]), []);

        Assert.DoesNotContain(AwsKey, files.Json);
        Assert.DoesNotContain(AwsKey, files.Markdown);
        Assert.DoesNotContain(".env", files.Json);
        Assert.Equal([new HandoffFinding("aws", "commit 2"), new HandoffFinding("secret-file", "changed path 2"), new HandoffFinding("aws", "tool call 2")], files.Findings);
        var json = JsonDocument.Parse(files.Json).RootElement;
        Assert.Equal(["ls", "make"], json.GetProperty("calls").EnumerateArray().Select(c => c.GetProperty("arguments").GetProperty("command").GetString()));
        Assert.Single(json.GetProperty("commits").EnumerateArray());
        Assert.Single(json.GetProperty("changes").EnumerateArray());
        Assert.Equal(3, json.GetProperty("findings").GetArrayLength());
        Assert.Contains("matched a secret pattern", files.Markdown);
    }

    [Fact]
    public void The_sessions_secrets_are_redacted_wherever_they_appear()
    {
        var files = HandoffBuilder.Build(Facts(calls: [Tool("Bash", $"make CANARY={Canary}")], commits: [$"feat: use {Canary}"], test: new TestRun($"make test TOKEN={Canary}", 2, true)), [Canary]);

        Assert.DoesNotContain(Canary, files.Json);
        Assert.DoesNotContain(Canary, files.Markdown);
        Assert.Contains("make CANARY=[redacted]", files.Markdown);
        Assert.Empty(files.Findings);
    }

    [Fact]
    public void The_text_is_cut_to_sixteen_kib_by_dropping_the_oldest_calls_first()
    {
        var calls = Enumerable.Range(1, 60).Select(i => Tool("Bash", $"echo {i} " + new string('y', 700))).ToList();
        var files = HandoffBuilder.Build(Facts(calls: calls), []);

        Assert.True(Encoding.UTF8.GetByteCount(files.Markdown) <= HandoffBuilder.MaxMarkdownBytes);
        Assert.Contains("echo 60 ", files.Markdown);
        Assert.DoesNotContain("- Bash: echo 1 ", files.Markdown);
        Assert.Contains("## Changed files", files.Markdown);
        Assert.Equal(60, JsonDocument.Parse(files.Json).RootElement.GetProperty("calls").GetArrayLength());       // the JSON keeps them all
    }

    [Fact]
    public void A_long_list_of_paths_is_capped_and_says_how_many_more()
    {
        var changes = Enumerable.Range(1, 260).Select(i => new ChangedPath($"src/F{i}.cs", 1, 0)).ToList();
        var files = HandoffBuilder.Build(Facts(changes: changes), []);

        Assert.Equal(HandoffBuilder.MaxChanges, JsonDocument.Parse(files.Json).RootElement.GetProperty("changes").GetArrayLength());
        Assert.Equal(60, JsonDocument.Parse(files.Json).RootElement.GetProperty("changes_omitted").GetInt32());
        Assert.Contains("and 160 more", files.Markdown);
    }

    [Fact]
    public void A_handoff_with_no_git_facts_still_builds()
    {
        var files = HandoffBuilder.Build(new HandoffFacts("turn_limit", 80, null, null, [], [], null, [], [], []), []);
        Assert.Contains("stopped by turn limit after 80 turns", files.Markdown);
        Assert.Contains("None.", files.Markdown);
        Assert.False(JsonDocument.Parse(files.Json).RootElement.TryGetProperty("last_test", out _));
    }
}
