using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Sessions.Api;
using static Chargehand.Modules.Sessions.Tests.SessionDriverTests;

namespace Chargehand.Modules.Sessions.Tests;

/// <summary>The goal reminder: a long session is sent its goal and criteria again, through a hook after its next tool call.</summary>
public class GoalReminderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_reminder_is_due_once_per_interval_of_minutes()
    {
        var r = new GoalReminder(TimeSpan.FromMinutes(60), null, T0);
        Assert.False(r.Due(T0.AddMinutes(59), 40));
        Assert.True(r.Due(T0.AddMinutes(60), 41));
        Assert.False(r.Due(T0.AddMinutes(90), 60));
        Assert.True(r.Due(T0.AddMinutes(121), 70));
        Assert.Equal(2, r.Sent);
    }

    [Fact]
    public void A_reminder_by_turns_counts_assistant_messages_since_the_last_one()
    {
        var r = new GoalReminder(null, 10, T0);
        Assert.False(r.Due(T0.AddHours(5), 9));
        Assert.True(r.Due(T0.AddHours(5), 10));
        Assert.False(r.Due(T0.AddHours(6), 19));
        Assert.True(r.Due(T0.AddHours(6), 20));
    }

    [Fact]
    public void A_task_without_an_interval_has_no_reminder()
    {
        Assert.Null(GoalReminder.For(Task(), T0));
        Assert.NotNull(GoalReminder.For(Task() with { ReminderMinutes = 60 }, T0));
    }

    [Fact]
    public void The_text_carries_the_goal_the_rules_and_the_criteria()
    {
        var text = GoalReminder.Text(Task() with { Acceptance = [new AcceptanceCheck("curl -s localhost/health", Output: "ok")] }, ["dotnet test"]);
        Assert.Contains("Add a retry to the fetch client", text);
        Assert.Contains("- `dotnet test` exits 0", text);
        Assert.Contains("- `curl -s localhost/health` exits 0 and prints `ok`", text);
    }

    [Fact]
    public void The_settings_hook_every_tool_call_and_the_payload_is_post_tool_use_context()
    {
        using var settings = JsonDocument.Parse(GoalReminder.Settings());
        var hook = settings.RootElement.GetProperty("hooks").GetProperty("PostToolUse")[0];
        Assert.Equal("*", hook.GetProperty("matcher").GetString());
        Assert.Equal(GoalReminder.HookCommand, hook.GetProperty("hooks")[0].GetProperty("command").GetString());
        using var payload = JsonDocument.Parse(GoalReminder.Payload("keep going"));
        var specific = payload.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PostToolUse", specific.GetProperty("hookEventName").GetString());
        Assert.Equal("keep going", specific.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public async Task A_due_reminder_reaches_the_hook_once_and_the_outcome_counts_it()
    {
        using var w = new Workspace();
        var hook = w.Dir.Write("hook.sh", GoalReminder.HookCommand);
        // The stand-in runs the hook as Claude Code would after a tool call: once the reminder is due, and once more after it was claimed.
        var script = Emit(Init, Assistant("m1", "working\n" + Reported)) + $"""

            for i in $(seq 1 40); do [ -f "$CHARGEHAND_REMINDER_FILE" ] && break; sleep 0.1; done
            sh '{hook}' > '{w.Out}/hook1.txt'
            sh '{hook}' > '{w.Out}/hook2.txt'
            case " $* " in *" --settings "*) echo yes > '{w.Out}/settings-passed' ;; esac

            """ + Emit(Result());
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task() with { ReminderTurns = 1 }), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.True(File.Exists(Path.Combine(w.Out, "settings-passed")));
        using var first = JsonDocument.Parse(File.ReadAllText(Path.Combine(w.Out, "hook1.txt")));
        Assert.Contains("Add a retry to the fetch client", first.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString());
        Assert.Equal("", File.ReadAllText(Path.Combine(w.Out, "hook2.txt")));
        Assert.Equal(1, outcome.GoalReminders);
    }
}
