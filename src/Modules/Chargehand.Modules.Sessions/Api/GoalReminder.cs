using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Contracts;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>Re-sends a long session its goal and acceptance criteria against drift. The driver writes <see cref="Payload"/> to the file named by <see cref="FileVariable"/> when a
/// reminder is due; Claude Code's <c>PostToolUse</c> hook (<see cref="HookCommand"/>, set with <c>--settings</c>) claims the file after the next tool call and its text reaches the
/// model as context. Pure apart from the clock the caller passes.</summary>
public sealed class GoalReminder(TimeSpan? every, int? everyTurns, DateTimeOffset start)
{
    public const string FileVariable = "CHARGEHAND_REMINDER_FILE";

    /// <summary>Renames before reading, so a reminder written while the hook runs is kept for the next tool call.</summary>
    public const string HookCommand = """f="$CHARGEHAND_REMINDER_FILE"; [ -n "$f" ] && mv "$f" "$f.sent" 2>/dev/null && cat "$f.sent"; exit 0""";

    public const int MaxGoalChars = 8000;

    private (DateTimeOffset At, int Turn) _last = (start, 0);

    public int Sent { get; private set; }

    public static GoalReminder? For(SessionTask task, DateTimeOffset start) =>
        task.ReminderMinutes is null && task.ReminderTurns is null ? null
            : new GoalReminder(task.ReminderMinutes is { } m ? TimeSpan.FromMinutes(m) : null, task.ReminderTurns, start);

    /// <summary>True, and counts one, when the interval in minutes or in turns has passed since the last reminder (or the start).</summary>
    public bool Due(DateTimeOffset now, int turns)
    {
        if (!(every is { } e && now - _last.At >= e) && !(everyTurns is { } t && turns - _last.Turn >= t))
            return false;
        _last = (now, turns);
        Sent++;
        return true;
    }

    public static string Text(SessionTask task, IReadOnlyList<string> rules)
    {
        var goal = task.Goal.Length <= MaxGoalChars ? task.Goal : task.Goal[..MaxGoalChars] + "…";
        var text = new StringBuilder("Reminder from chargehand: the task has not changed. Keep to it and do not start unrelated work.\n\nGoal:\n").Append(goal).Append('\n');
        var checks = SessionChecks.All(rules, task.Acceptance);
        if (checks.Count > 0)
        {
            text.Append("\nThe branch is done only when these pass (chargehand runs them after you finish):\n");
            foreach (var c in checks)
                text.Append(CultureInfo.InvariantCulture, $"- `{c.Command}` exits {c.ExitCode}{(c.Output is null ? "" : $" and prints `{c.Output}`")}\n");
        }
        return text.ToString();
    }

    /// <summary>What the hook prints: Claude Code adds <c>additionalContext</c> to the conversation after the tool's result.</summary>
    public static string Payload(string text) =>
        JsonSerializer.Serialize(new ReminderHookOutput(new ReminderHookSpecific("PostToolUse", text)), SessionsJsonContext.Default.ReminderHookOutput);

    public static string Settings() =>
        JsonSerializer.Serialize(new ReminderSettings(new ReminderHooks([new ReminderMatcher("*", [new ReminderHook("command", HookCommand)])])), SessionsJsonContext.Default.ReminderSettings);
}

internal sealed record ReminderHookOutput([property: JsonPropertyName("hookSpecificOutput")] ReminderHookSpecific HookSpecificOutput);

internal sealed record ReminderHookSpecific([property: JsonPropertyName("hookEventName")] string HookEventName, [property: JsonPropertyName("additionalContext")] string AdditionalContext);

internal sealed record ReminderSettings([property: JsonPropertyName("hooks")] ReminderHooks Hooks);

internal sealed record ReminderHooks([property: JsonPropertyName("PostToolUse")] IReadOnlyList<ReminderMatcher> PostToolUse);

internal sealed record ReminderMatcher([property: JsonPropertyName("matcher")] string Matcher, [property: JsonPropertyName("hooks")] IReadOnlyList<ReminderHook> Hooks);

internal sealed record ReminderHook([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("command")] string Command);
