using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.Evals;

/// <summary>
/// Which of an item's reference facts an answer states, and which known wrong statements it repeats (ADR 0019): one
/// stateless generate call on the intake model. The prompt lives in the trusted build, not in <c>prompts/</c>, so a
/// pull request cannot change how its own answers are judged. The judge reads the answer only; whether a stated fact
/// is backed is the evidence resolver's job.
/// </summary>
public static class FactJudge
{
    public static async Task<FactCheck> JudgeAsync(IWorkerRuntime runtime, ModelRef model, ResultContract result, IReadOnlyList<string> facts,
        IReadOnlyList<string> wrong, CancellationToken ct)
    {
        var prompt = Prompt(result, facts, wrong);
        string? error = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var text = await runtime.GenerateAsync(model, error is null ? prompt : $"{prompt}\nYour previous output was invalid: {error}. Reply with the JSON object only.", ct);
            if (Parse(text, facts.Count, wrong.Count, out var check, out error))
                return check;
        }
        throw new InvalidOperationException($"the fact judge returned no valid verdict after one retry: {error}");
    }

    internal static string Prompt(ResultContract result, IReadOnlyList<string> facts, IReadOnlyList<string> wrong)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You check an answer against numbered facts. Judge only what the answer says; do not use your own knowledge of the subject.")
            .AppendLine()
            .AppendLine("Answer:").AppendLine(result.Summary);
        foreach (var claim in result.Claims)
            sb.Append("- ").AppendLine(claim.Text);
        sb.AppendLine().AppendLine("Facts:");
        for (var i = 0; i < facts.Count; i++)
            sb.AppendLine($"F{i + 1}. {facts[i]}");
        sb.AppendLine().AppendLine("Statements known to be false:");
        for (var i = 0; i < wrong.Count; i++)
            sb.AppendLine($"W{i + 1}. {wrong[i]}");
        return sb.AppendLine()
            .AppendLine("A fact is stated when the answer says it, or something that plainly implies it, in any wording. A fact with several parts is stated only when every part is. A false statement is repeated when the answer asserts it.")
            .AppendLine("""Reply with one JSON object only, listing numbers without their letter, for example {"stated": [1, 3], "repeated": [2]}; use [] when none.""")
            .ToString();
    }

    /// <summary>The verdict object, anywhere in the text; numbers must name a fact or statement that exists.</summary>
    internal static bool Parse(string text, int facts, int wrong, out FactCheck check, out string? error)
    {
        check = new FactCheck(0, facts, 0);
        var (start, end) = (text.IndexOf('{'), text.LastIndexOf('}'));
        if (start < 0 || end < start)
            return Fail("no JSON object", out error);
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var stated = Numbers(doc.RootElement, "stated", facts);
            var repeated = Numbers(doc.RootElement, "repeated", wrong);
            check = new FactCheck(stated, facts, repeated);
            error = null;
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Fail(e.Message, out error);
        }
    }

    /// <summary>Numbers as 3, "3" or "F3": small models do not keep to the requested form.</summary>
    private static int Numbers(JsonElement root, string name, int max)
    {
        var numbers = root.GetProperty(name).EnumerateArray()
            .Select(n => n.ValueKind == JsonValueKind.String ? int.Parse(n.GetString()!.TrimStart('F', 'W', 'f', 'w'), CultureInfo.InvariantCulture) : n.GetInt32())
            .Distinct().ToList();
        return numbers.All(n => n >= 1 && n <= max) ? numbers.Count : throw new FormatException($"{name} holds a number outside 1..{max}");
    }

    private static bool Fail(string reason, out string? error)
    {
        error = reason;
        return false;
    }
}

/// <param name="Stated">Reference facts the answer states.</param>
/// <param name="Facts">Reference facts on the item's checklist.</param>
/// <param name="Repeated">Known wrong statements the answer repeats.</param>
public sealed record FactCheck(int Stated, int Facts, int Repeated);
