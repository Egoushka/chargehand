using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Chargehand.Modules.Journal.Api;

/// <summary>One recorded action of a driven run (ADR 0090, first slice), written as an <c>events/v1</c> envelope.</summary>
/// <param name="Id">Unique per <paramref name="Source"/>: the tool call's id, or a deterministic id for a handover step.</param>
/// <param name="Source"><c>chargehand/session</c> or <c>chargehand/handover</c>.</param>
/// <param name="Type"><c>&lt;domain&gt;.&lt;noun&gt;.&lt;past verb&gt;</c>, e.g. <c>chargehand.tool.called</c>.</param>
/// <param name="Subject">The tool's name or the handover step.</param>
/// <param name="Summary">One line that says what was done; never a file's content or a command's output.</param>
/// <param name="Effect"><c>read</c>, <c>write</c>, <c>external</c> or <c>irreversible</c>.</param>
/// <param name="InputSha256">The SHA-256 of the tool call's full input; null for a handover step.</param>
public sealed record ActionEvent(string Id, string Source, string Type, string Subject, DateTimeOffset Time, string Run, string Summary, string Effect, string? InputSha256 = null);

/// <summary>A run's action log: <see cref="FileName"/> in its output directory, one <c>events/v1</c> envelope per line, appended in the order the actions happened. Replay reads it back and
/// changes nothing.</summary>
public static class ActionLog
{
    public const string FileName = "actions.jsonl";

    public const string Contract = "events/v1";

    /// <summary>The envelope of <paramref name="e"/> as one line of JSON.</summary>
    public static string Line(ActionEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("specversion", "1.0");
            w.WriteString("id", e.Id);
            w.WriteString("source", e.Source);
            w.WriteString("type", e.Type);
            w.WriteString("subject", e.Subject);
            w.WriteString("time", e.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            w.WriteString("datacontenttype", "application/json");
            w.WriteStartObject("data");
            w.WriteString("summary", e.Summary);
            if (e.InputSha256 is { } hash)
                w.WriteString("input_sha256", hash);
            w.WriteEndObject();
            w.WriteString("correlationid", e.Run);
            w.WriteStartObject("actor");
            w.WriteString("kind", e.Source.EndsWith("/session", StringComparison.Ordinal) ? "agent" : "service");
            w.WriteString("id", e.Source);
            w.WriteString("run", e.Run);
            w.WriteEndObject();
            w.WriteString("effect", e.Effect);
            w.WriteString("scope", $"run:{e.Run}");
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Appends <paramref name="e"/> to the log in <paramref name="directory"/>, creating it when missing.</summary>
    public static void Append(string directory, ActionEvent e) =>
        File.AppendAllText(Path.Combine(directory, FileName), Line(e) + "\n", new UTF8Encoding(false));

    /// <summary>The steps of a recorded log, one line each, in the order they were written: <c>&lt;n&gt; &lt;time&gt; &lt;type&gt; &lt;subject&gt;: &lt;summary&gt;</c>. A line that is not an
    /// envelope is skipped and said so.</summary>
    public static IEnumerable<string> Replay(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var n = 0;
        foreach (var line in lines.Where(l => l.Trim().Length > 0))
        {
            n++;
            yield return Step(n, line);
        }
    }

    private static string Step(int n, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return $"{n} (not an event)";
            var summary = e.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : "";
            return $"{n} {Text(e, "time")} {type.GetString()} {Text(e, "subject")}: {summary}";
        }
        catch (JsonException)
        {
            return $"{n} (not an event)";
        }
    }

    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "-";
}

/// <summary><c>chargehand runs replay &lt;actions.jsonl&gt;</c>: prints a recorded run's steps in order, read-only.</summary>
public static class ReplayCli
{
    public const string Usage = "usage: chargehand runs replay <actions.jsonl>";

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Count != 1)
        {
            error.WriteLine(Usage);
            return 2;
        }
        if (!File.Exists(args[0]))
        {
            error.WriteLine($"{args[0]}: no such file");
            return 2;
        }
        foreach (var step in ActionLog.Replay(File.ReadLines(args[0])))
            output.WriteLine(step);
        return 0;
    }
}
