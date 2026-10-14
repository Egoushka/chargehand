using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Kernel;

namespace Chargehand.Driven;

/// <param name="Added">Lines added; 0 for a binary file.</param>
public sealed record ChangedPath(string Path, long Added, long Removed);

/// <param name="ExitCode">The command's exit code when the result named one; null if it did not.</param>
/// <param name="IsError">The tool result's error flag; null when no result came back because the session was stopped inside the command.</param>
public sealed record TestRun(string Command, int? ExitCode, bool? IsError);

/// <summary>One tool call with its arguments, each value cut short; the output is never kept.</summary>
public sealed record ToolCall(string Name, IReadOnlyDictionary<string, string> Arguments);

/// <param name="Passes">Set by the driver from the stream, never from the model's word (see <see cref="SessionTrace.Steps"/>).</param>
public sealed record StepState(string Name, bool Passes);

/// <summary>An item of the session's own todo list, as it wrote it.</summary>
public sealed record TodoItem(string Text, string Status);

/// <summary>Everything a handoff is built from (ADR 0045): the git facts the driver reads and what <see cref="SessionTrace"/> took from the stream.</summary>
public sealed record HandoffFacts(string Reason, int Turns, string? BaseCommit, string? HeadCommit, IReadOnlyList<string> Commits, IReadOnlyList<ChangedPath> Changes,
    TestRun? LastTest, IReadOnlyList<StepState> Steps, IReadOnlyList<TodoItem> Todos, IReadOnlyList<ToolCall> Calls);

/// <summary>An item left out of the handoff because it matched a secret pattern: the pattern's name and which item, never the text.</summary>
public sealed record HandoffFinding(string Kind, string Item);

/// <param name="Json"><c>handoff.json</c>.</param>
/// <param name="Markdown"><c>progress.md</c>, at most <see cref="HandoffBuilder.MaxMarkdownBytes"/> bytes: what the next session's prompt carries.</param>
public sealed record HandoffFiles(string Json, string Markdown, IReadOnlyList<HandoffFinding> Findings);

/// <summary>Builds <c>handoff.json</c> and <c>progress.md</c> from facts, with no model call and no I/O (ADR 0045). Every text item is redacted with the session's secrets and
/// scanned with <see cref="DiffScan"/>'s patterns, because the text becomes the next session's prompt and stream redaction covers three environment variables only; an item that
/// matches is dropped and recorded as a finding without its value.</summary>
public static class HandoffBuilder
{
    public const int MaxMarkdownBytes = 16 * 1024;
    public const int MaxCommits = 50;
    public const int MaxChanges = 200;

    private const int MarkdownChanges = 100;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Document(int Version, string Reason, int Turns, string? BaseCommit, string? HeadCommit, IReadOnlyList<string> Commits, IReadOnlyList<ChangedPath> Changes,
        int ChangesOmitted, TestRun? LastTest, IReadOnlyList<StepState> Tasks, IReadOnlyList<TodoItem> Todos, IReadOnlyList<ToolCall> Calls, IReadOnlyList<HandoffFinding> Findings);

    public static HandoffFiles Build(HandoffFacts facts, IReadOnlyList<string> secrets)
    {
        List<HandoffFinding> findings = [];
        string? Clean(string text, string item)
        {
            var redacted = SessionDriver.Redact(text, secrets);
            var kinds = DiffScan.ScanText(redacted, []);
            findings.AddRange(kinds.Select(k => new HandoffFinding(k, item)));
            return kinds.Count == 0 ? redacted : null;
        }

        var commits = facts.Commits.Take(MaxCommits).Select((c, i) => Clean(c, $"commit {i + 1}")).OfType<string>().ToList();
        var changes = new List<ChangedPath>();
        foreach (var (change, i) in facts.Changes.Select((c, i) => (c, i)))
        {
            var item = $"changed path {i + 1}";
            if (DiffScan.Scan("", [], [change.Path]) is { Count: > 0 } byName)
                findings.AddRange(byName.Select(f => new HandoffFinding(f.Kind, item)));
            else if (Clean(change.Path, item) is { } path)
                changes.Add(change with { Path = path });
        }
        var omitted = Math.Max(changes.Count - MaxChanges, 0);
        changes = [.. changes.Take(MaxChanges)];
        var lastTest = facts.LastTest is { } test ? (Clean(test.Command, "last test command") is { } command ? test with { Command = command } : null) : null;
        var todos = facts.Todos.Select((t, i) => Clean(t.Text, $"todo {i + 1}") is { } text ? t with { Text = text } : null).OfType<TodoItem>().ToList();
        var calls = new List<ToolCall>();
        foreach (var (call, i) in facts.Calls.Select((c, i) => (c, i)))
        {
            var item = $"tool call {i + 1}";
            var arguments = new Dictionary<string, string>();
            var dropped = Clean(call.Name, item) is null;
            foreach (var (key, value) in call.Arguments)
                if (Clean(value, item) is { } clean)
                    arguments[key] = clean;
                else
                    dropped = true;
            if (!dropped)
                calls.Add(call with { Arguments = arguments });
        }

        var document = new Document(1, facts.Reason, facts.Turns, facts.BaseCommit, facts.HeadCommit, commits, changes, omitted, lastTest, facts.Steps, todos, calls, findings);
        return new HandoffFiles(JsonSerializer.Serialize(document, Json), Markdown(document), findings);
    }

    /// <summary>The newest calls and the longest list of paths that fit: the oldest calls go first, then paths, then the tail of the text.</summary>
    private static string Markdown(Document d)
    {
        int calls = d.Calls.Count, changes = Math.Min(d.Changes.Count, MarkdownChanges);
        var text = Render(d, calls, changes);
        while (Encoding.UTF8.GetByteCount(text) > MaxMarkdownBytes && (calls > 0 || changes > 0))
        {
            if (calls > 0)
                calls = Math.Max(calls - 10, 0);
            else
                changes = Math.Max(changes - 25, 0);
            text = Render(d, calls, changes);
        }
        while (Encoding.UTF8.GetByteCount(text) > MaxMarkdownBytes)
            text = text[..(text.Length * 9 / 10)];
        return text;
    }

    private static string Render(Document d, int calls, int changes)
    {
        var b = new StringBuilder();
        b.Append("# Handoff from the previous session\n\n")
            .Append(string.Create(CultureInfo.InvariantCulture, $"The session was stopped by {d.Reason.Replace('_', ' ')} after {d.Turns} turns. Everything below comes from git and the session's tool calls, not from the model's own account.\n\n"));
        if (d.BaseCommit is not null || d.HeadCommit is not null)
            b.Append("Base commit ").Append(Short(d.BaseCommit)).Append(", head commit ").Append(Short(d.HeadCommit)).Append(".\n\n");

        b.Append("## Steps of the change skill\n\n");
        foreach (var step in d.Tasks)
            b.Append(step.Passes ? "- [x] " : "- [ ] ").Append(step.Name).Append('\n');
        b.Append("\nA step is checked when its latest tool call returned without an error.\n\n");

        if (d.Todos.Count > 0)
        {
            b.Append("## The session's own todo list\n\n");
            foreach (var todo in d.Todos)
                b.Append(todo.Status == "completed" ? "- [x] " : "- [ ] ").Append(OneLine(todo.Text)).Append(todo.Status is "completed" or "pending" ? "" : $" ({todo.Status})").Append('\n');
            b.Append('\n');
        }

        b.Append("## Last test run\n\n");
        if (d.LastTest is { } test)
            b.Append('`').Append(OneLine(test.Command)).Append("`: ").Append(test.IsError switch
            {
                null => "no result, the session was stopped while it ran",
                true => test.ExitCode is { } code ? $"failed, exit code {code.ToString(CultureInfo.InvariantCulture)}" : "failed",
                false => "passed",
            }).Append("\n\n");
        else
            b.Append("None.\n\n");

        b.Append("## Commits on the branch\n\n");
        if (d.Commits.Count == 0)
            b.Append("None.\n");
        foreach (var commit in d.Commits)
            b.Append("- ").Append(OneLine(commit)).Append('\n');

        b.Append("\n## Changed files\n\n");
        if (d.Changes.Count == 0)
            b.Append("None.\n");
        foreach (var change in d.Changes.Take(changes))
            b.Append(string.Create(CultureInfo.InvariantCulture, $"- {OneLine(change.Path)} (+{change.Added} -{change.Removed})\n"));
        if (d.Changes.Count + d.ChangesOmitted > changes && d.Changes.Count > 0)
            b.Append(string.Create(CultureInfo.InvariantCulture, $"- and {d.Changes.Count + d.ChangesOmitted - changes} more\n"));

        b.Append("\n## Last tool calls, oldest first\n\n");
        if (calls == 0)
            b.Append(d.Calls.Count == 0 ? "None.\n" : "Left out to keep this short.\n");
        foreach (var call in d.Calls.Skip(d.Calls.Count - calls))
            b.Append("- ").Append(call.Name).Append(": ").Append(OneLine(call.Arguments.Count == 1 && call.Arguments.ContainsKey("command")
                ? call.Arguments["command"] : string.Join("; ", call.Arguments.Select(a => $"{a.Key}={a.Value}")))).Append('\n');

        if (d.Findings.Count > 0)
            b.Append("\n## Left out\n\n").Append(string.Create(CultureInfo.InvariantCulture, $"{d.Findings.Count} item(s) matched a secret pattern and were dropped: "))
                .AppendJoin(", ", d.Findings.Select(f => $"{f.Kind} ({f.Item})")).Append(".\n");
        return b.ToString();
    }

    private static string Short(string? commit) => commit is null ? "unknown" : commit[..Math.Min(commit.Length, 10)];

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
