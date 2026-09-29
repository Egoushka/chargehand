using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.Verification;

public enum SupportVerdict { Supported, Partial, Unsupported }

/// <param name="Index">The claim's position in <c>contract.Claims</c>.</param>
public sealed record ClaimVerdict(int Index, SupportVerdict Verdict, string Reason);

/// <summary>
/// Whether the text a claim cites supports it (ADR 0036): one stateless generate call on the intake model for all of a node's
/// claims. The prompt lives in the trusted build, not in <c>prompts/</c>, so a pull request cannot change how its own answers are
/// judged. It is a model's opinion, not a proof.
/// </summary>
public static class SupportJudge
{
    public static async Task<IReadOnlyList<ClaimVerdict>> JudgeAsync(IWorkerRuntime runtime, ModelRef? model, IReadOnlyList<CitedClaim> claims, CancellationToken ct)
    {
        var prompt = Prompt(claims);
        string? error = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var text = await runtime.GenerateAsync(model, error is null ? prompt : $"{prompt}\nYour previous output was invalid: {error}. Reply with the JSON object only.", ct);
            if (Parse(text, claims, out var verdicts, out error))
                return verdicts;
        }
        throw new InvalidOperationException($"the support judge returned no valid verdicts after one retry: {error}");
    }

    internal static string Prompt(IReadOnlyList<CitedClaim> claims)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You check whether the text a claim cites supports the claim. Judge only the cited text shown; do not use your own knowledge of the subject, and do not judge whether the claim is true in general.")
            .AppendLine()
            .AppendLine("supported: the cited text says the claim, or something that plainly implies it, in any wording.")
            .AppendLine("partial: the cited text supports only part of the claim, or a weaker statement than the one made.")
            .AppendLine("unsupported: the cited text does not say it, contradicts it, or is about something else.")
            .AppendLine();
        for (var i = 0; i < claims.Count; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Claim {i + 1}: {claims[i].Claim.Text}").AppendLine("Cited text:").AppendLine(claims[i].Text);
            if (claims[i].Truncated)
                sb.AppendLine("(The cited text was cut for length; judge what is shown.)");
            sb.AppendLine();
        }
        return sb.AppendLine("""Reply with one JSON object only, covering every claim once: {"verdicts":[{"claim":1,"verdict":"supported","reason":"one short sentence"}]}. verdict is supported, partial or unsupported.""").ToString();
    }

    /// <summary>The verdict object, anywhere in the text; every claim must appear once, numbered from 1.</summary>
    internal static bool Parse(string text, IReadOnlyList<CitedClaim> claims, out IReadOnlyList<ClaimVerdict> verdicts, out string? error)
    {
        verdicts = [];
        var (start, end) = (text.IndexOf('{'), text.LastIndexOf('}'));
        if (start < 0 || end < start)
            return Fail("no JSON object", out error);
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var parsed = new Dictionary<int, ClaimVerdict>();
            foreach (var v in doc.RootElement.GetProperty("verdicts").EnumerateArray())
            {
                var number = v.GetProperty("claim").ValueKind == JsonValueKind.String ? int.Parse(v.GetProperty("claim").GetString()!.Trim().TrimStart('C', 'c'), CultureInfo.InvariantCulture) : v.GetProperty("claim").GetInt32();
                if (number < 1 || number > claims.Count)
                    return Fail($"claim {number} does not exist (1..{claims.Count})", out error);
                var verdict = Enum.TryParse<SupportVerdict>(v.GetProperty("verdict").GetString()?.Trim(), ignoreCase: true, out var parsedVerdict)
                    ? parsedVerdict
                    : throw new FormatException($"unknown verdict '{v.GetProperty("verdict").GetString()}'");
                parsed[number] = new ClaimVerdict(claims[number - 1].Index, verdict, v.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()!.Trim() : "");
            }
            var missing = Enumerable.Range(1, claims.Count).Where(n => !parsed.ContainsKey(n)).ToList();
            if (missing.Count > 0)
                return Fail($"no verdict for claim {string.Join(", ", missing)}", out error);
            verdicts = [.. parsed.OrderBy(p => p.Key).Select(p => p.Value)];
            error = null;
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Fail(e.Message, out error);
        }
    }

    private static bool Fail(string reason, out string? error)
    {
        error = reason;
        return false;
    }
}
