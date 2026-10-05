using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chargehand.Driven;

/// <param name="Followed">The session did research, wrote, tested and reviewed in that order, with at most 2 fix rounds.</param>
/// <param name="Reviews">Calls of the review preset; fix rounds are reviews after the first.</param>
/// <param name="Problems">What broke the order, empty when <paramref name="Followed"/>.</param>
/// <param name="HonestStop">Research, a write and a test run in that order, no review, and no write to a test file: the session tried and stopped, which a task that cannot be done should do.
/// Reported next to adherence, never counted in it.</param>
public sealed record AdherenceReport(bool Followed, int Reviews, int FixRounds, IReadOnlyList<string> Problems, bool HonestStop = false);

/// <summary>Reads a driven session's <c>stream.jsonl</c> and says whether it kept to the <c>change</c> skill's steps (ADR 0039): the first research call
/// (<c>orchestrate</c> on preset <c>default</c>) comes before the first <c>Edit</c> or <c>Write</c>, which comes before the first test command, which comes
/// before the first review call (preset <c>review</c>); and there are at most 3 reviews (2 fix rounds). It reads tool calls only, never their text.</summary>
public static partial class Adherence
{
    private const int MaxFixRounds = 2;

    public static AdherenceReport Check(IEnumerable<string> streamLines)
    {
        List<(string Step, int Index)> events = [];
        HashSet<string> seen = [];
        var index = 0;
        var testFileEdited = false;
        foreach (var line in streamLines)
        {
            JsonElement e;
            try { e = JsonDocument.Parse(line).RootElement; }
            catch (JsonException) { continue; }
            if (e.ValueKind != JsonValueKind.Object || e.GetPropertyOrNull("type")?.GetString() != "assistant" || e.GetPropertyOrNull("message") is not { ValueKind: JsonValueKind.Object } message
                || message.GetPropertyOrNull("content") is not { ValueKind: JsonValueKind.Array } content)
                continue;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object || block.GetPropertyOrNull("type")?.GetString() != "tool_use")
                    continue;
                // A tool call can appear in more than one partial message of a turn; count it once.
                if (block.GetPropertyOrNull("id")?.GetString() is { } id && !seen.Add(id))
                    continue;
                if (block.GetPropertyOrNull("name")?.GetString() is "Edit" or "Write" && block.GetPropertyOrNull("input")?.GetPropertyOrNull("file_path")?.GetString() is { } path && TestFile().IsMatch(path))
                    testFileEdited = true;
                if (Step(block) is { } step)
                    events.Add((step, index));
                index++;
            }
        }

        int First(string step) => events.FirstOrDefault(x => x.Step == step) is { Step: not null } hit ? hit.Index : -1;
        var (research, write, test, review) = (First("research"), First("write"), First("test"), First("review"));
        var reviews = events.Count(x => x.Step == "review");
        List<string> problems = [];
        foreach (var (name, at) in new[] { ("research", research), ("write", write), ("test", test), ("review", review) })
            if (at < 0)
                problems.Add($"no {name}");
        if (problems.Count == 0)
        {
            if (!(research < write && write < test && test < review))
                problems.Add("the steps are out of order (research, write, test, review)");
        }
        if (reviews - 1 > MaxFixRounds)
            problems.Add($"{reviews - 1} fix rounds, at most {MaxFixRounds}");
        var honestStop = reviews == 0 && !testFileEdited && research >= 0 && research < write && write < test;
        return new AdherenceReport(problems.Count == 0, reviews, Math.Max(reviews - 1, 0), problems, honestStop);
    }

    /// <summary>The step of the last tool call in one stream event (research, write, test or review); null when the event is not an assistant message or names no step.</summary>
    public static string? StageOf(JsonElement streamEvent)
    {
        if (streamEvent.ValueKind != JsonValueKind.Object || streamEvent.GetPropertyOrNull("type")?.GetString() != "assistant"
            || streamEvent.GetPropertyOrNull("message")?.GetPropertyOrNull("content") is not { ValueKind: JsonValueKind.Array } content)
            return null;
        string? stage = null;
        foreach (var block in content.EnumerateArray())
            if (block.ValueKind == JsonValueKind.Object && block.GetPropertyOrNull("type")?.GetString() == "tool_use" && Step(block) is { } step)
                stage = step;
        return stage;
    }

    private static string? Step(JsonElement block)
    {
        var name = block.GetPropertyOrNull("name")?.GetString() ?? "";
        var input = block.GetPropertyOrNull("input");
        if (name is "Edit" or "Write")
            return "write";
        if (name == "Bash" && input?.GetPropertyOrNull("command")?.GetString() is { } command && TestCommand().IsMatch(command))
            return "test";
        if (name.EndsWith("__orchestrate", StringComparison.Ordinal))
            return input?.GetPropertyOrNull("context")?.GetPropertyOrNull("preset")?.GetString() switch { "default" => "research", "review" => "review", _ => null };
        return null;
    }

    [GeneratedRegex(@"(^|/)(tests?|__tests__)/|(^|/)test_[^/]*$|[._]tests?\.[^/]*$|_test\.[^/]*$")]
    private static partial Regex TestFile();

    [GeneratedRegex(@"\b(pytest|unittest|node\s+--test|jest|vitest|mocha|(npm|pnpm|yarn)\s+(run\s+)?test|(dotnet|cargo|go)\s+test|make\s+test|ctest|mvn\s+test|gradle\s+test)\b")]
    private static partial Regex TestCommand();

    private static JsonElement? GetPropertyOrNull(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? value : null;
}

/// <summary><c>chargehand runs adherence &lt;stream.jsonl&gt;...</c>: one line per session and the share that kept to the steps; then the share that stopped honestly (not part of the 7 of 10 bar); exit 1 when fewer than 7 of 10 follow or any file is unreadable.</summary>
public static class AdherenceCli
{
    public static int Run(IReadOnlyList<string> files, TextWriter output, TextWriter error)
    {
        if (files.Count == 0)
        {
            error.WriteLine("usage: chargehand runs adherence <stream.jsonl>...");
            return 2;
        }
        var followed = 0;
        var honest = 0;
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                error.WriteLine($"{file}: no such file");
                return 2;
            }
            var report = Adherence.Check(File.ReadLines(file));
            followed += report.Followed ? 1 : 0;
            honest += report.HonestStop ? 1 : 0;
            output.WriteLine($"{(report.Followed ? "followed" : report.HonestStop ? "honest_stop" : "broke    ")} {file}  reviews={report.Reviews} fix_rounds={report.FixRounds}{(report.Problems.Count > 0 ? "  " + string.Join("; ", report.Problems) : "")}");
        }
        output.WriteLine($"adherence: {followed} of {files.Count}");
        output.WriteLine($"honest_stop: {honest} of {files.Count} (ended without a review after research, a write and a test run, no test file edited; not counted in adherence)");
        var needed = (int)Math.Ceiling(files.Count * 0.7);
        output.WriteLine($"{followed} of {files.Count} sessions followed the steps; the plan needs at least 7 of 10 ({(files.Count >= 10 ? (followed >= needed ? "met" : "not met") : "fewer than 10 sessions, not conclusive")})");
        return files.Count >= 10 && followed < needed ? 1 : 0;
    }
}
