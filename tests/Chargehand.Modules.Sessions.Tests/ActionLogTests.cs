using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Sessions.Tests;

public class ActionLogTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    // A recorded session stream, scrubbed: a read, a tool call repeated in a partial message, an edit, a test run and its result.
    private static readonly string[] Stream =
    [
        """{"type":"system","subtype":"init"}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"src/Foo.cs"}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"class Foo {}"}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"text","text":"editing"},{"type":"tool_use","id":"toolu_2","name":"Edit","input":{"file_path":"src/Foo.cs","old_string":"a","new_string":"b"}}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"Edit","input":{"file_path":"src/Foo.cs","old_string":"a","new_string":"b"}}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_3","name":"Bash","input":{"command":"dotnet test --token sekret-value-123"}}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_4","name":"mcp__chargehand__orchestrate","input":{"text":"review"}}]}}""",
    ];

    private static List<ActionEvent> Record()
    {
        var actions = new SessionActions("run-1", ["sekret-value-123"]);
        return [.. Stream.SelectMany(l => actions.OnEvent(JsonDocument.Parse(l).RootElement, At))];
    }

    [Fact]
    public void Every_tool_call_of_a_session_is_one_event_in_the_order_it_was_made()
    {
        var events = Record();
        Assert.Equal(["toolu_1", "toolu_2", "toolu_3", "toolu_4"], events.Select(e => e.Id));
        Assert.Equal(["read", "write", "write", "external"], events.Select(e => e.Effect));
        Assert.Equal("Edit src/Foo.cs", events[1].Summary);
        // The summary carries no secret and no input beyond its hint; the input is kept as a hash only.
        Assert.Equal("Bash dotnet test --token [redacted]", events[2].Summary);
        Assert.All(events, e => Assert.Matches("^[0-9a-f]{64}$", e.InputSha256!));
    }

    [Fact]
    public void Each_logged_line_is_a_valid_events_v1_envelope()
    {
        foreach (var e in Record().Append(new ActionEvent("handover:run-1:pushed", "chargehand/handover", "chargehand.handover.pushed", "pushed", At, "run-1", "pushed chargehand/run-1", "external")))
        {
            using var doc = JsonDocument.Parse(ActionLog.Line(e));
            Assert.Empty(ContractSchemas.Validate(ContractSchemas.Events, doc.RootElement));
        }
    }

    [Fact]
    public void Replay_of_a_recorded_run_prints_the_same_steps_in_order_and_changes_nothing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            foreach (var e in Record())
                ActionLog.Append(dir.FullName, e);
            ActionLog.Append(dir.FullName, new ActionEvent("handover:run-1:verify_finished", "chargehand/handover", "chargehand.handover.verify_finished", "verify_finished", At, "run-1", "tests passed", "write"));
            var file = Path.Combine(dir.FullName, ActionLog.FileName);
            var before = File.ReadAllBytes(file);
            var output = new StringWriter();

            Assert.Equal(0, ReplayCli.Run([file], output, TextWriter.Null));

            Assert.Equal(
            [
                "1 2026-11-08T12:00:00.000Z chargehand.tool.called Read: Read src/Foo.cs",
                "2 2026-11-08T12:00:00.000Z chargehand.tool.called Edit: Edit src/Foo.cs",
                "3 2026-11-08T12:00:00.000Z chargehand.tool.called Bash: Bash dotnet test --token [redacted]",
                "4 2026-11-08T12:00:00.000Z chargehand.tool.called mcp__chargehand__orchestrate: mcp__chargehand__orchestrate",
                "5 2026-11-08T12:00:00.000Z chargehand.handover.verify_finished verify_finished: tests passed",
            ], output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')));
            Assert.Equal(before, File.ReadAllBytes(file));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Replay_says_which_line_is_not_an_event_and_needs_one_file()
    {
        Assert.Equal(["1 (not an event)", "2 - x.y.z -: "], ActionLog.Replay(["not json", """{"type":"x.y.z"}""", ""]));
        Assert.Equal(2, ReplayCli.Run([], TextWriter.Null, TextWriter.Null));
        Assert.Equal(2, ReplayCli.Run(["/nonexistent/actions.jsonl"], TextWriter.Null, TextWriter.Null));
    }
}
