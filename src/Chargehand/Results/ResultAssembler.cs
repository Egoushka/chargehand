using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Verification;

namespace Chargehand.Results;

/// <summary>Orchestrator-owned fields added to a worker's result block.</summary>
public sealed record ResultEnvelope(string TaskId, string NodeId, string TraceId, PromptChain PromptChain, Usage Usage);

public sealed record AssembleOutcome(ResultContract? Contract, IReadOnlyList<string> Errors);

public static partial class ResultBlocks
{
    /// <summary>The last fenced <c>```json</c> block in a message, or null.</summary>
    public static string? ExtractLast(string text)
    {
        var matches = JsonFence().Matches(text);
        return matches.Count == 0 ? null : matches[^1].Groups["body"].Value;
    }

    [GeneratedRegex(@"```json[ \t]*\n(?<body>.*?)\n[ \t]*```", RegexOptions.Singleline)]
    private static partial Regex JsonFence();
}

/// <summary>Turns a worker's final message into a result/v1 contract (ADR 0009).</summary>
public static class ResultAssembler
{
    /// <summary>result/v1: inline artifact content is at most 64 KiB (ADR 0009).</summary>
    public const int MaxInlineBytes = 65536;

    private static readonly string[] WorkerKeys = ["status", "summary", "claims", "evidence", "artifacts", "open_questions", "confidence"];

    public static AssembleOutcome Assemble(string finalMessage, ResultEnvelope envelope)
    {
        var raw = ResultBlocks.ExtractLast(finalMessage);
        if (raw is null)
            return new(null, ["no ```json result block in the final message"]);
        JsonObject worker;
        try
        {
            worker = JsonNode.Parse(raw)?.AsObject() ?? throw new JsonException("not an object");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return new(null, [$"result block is not a JSON object: {e.Message}"]);
        }

        var contract = new JsonObject
        {
            ["contract_version"] = "result/v1",
            ["task_id"] = envelope.TaskId,
            ["node_id"] = envelope.NodeId,
            ["trace_id"] = envelope.TraceId,
            ["prompt_chain"] = JsonSerializer.SerializeToNode(envelope.PromptChain, ContractJson.Options),
            ["usage"] = JsonSerializer.SerializeToNode(envelope.Usage, ContractJson.Options),
        };
        foreach (var key in WorkerKeys)
            if (worker[key] is { } value)
                contract[key] = value.DeepClone();

        // Inline artifacts (a draft): the orchestrator hashes the content itself and bounds it in bytes, not characters.
        var oversize = new List<string>();
        foreach (var artifact in (contract["artifacts"] as JsonArray ?? []).OfType<JsonObject>())
            if (artifact["content"] is JsonValue content && content.TryGetValue<string>(out var text))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                artifact["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (bytes.Length > MaxInlineBytes)
                    oversize.Add($"artifact {artifact["kind"]} content is {bytes.Length} bytes; inline content is at most {MaxInlineBytes}");
            }
        if (oversize.Count > 0)
            return new(null, oversize);

        var element = JsonSerializer.SerializeToElement(contract);
        var errors = ContractSchemas.Validate(ContractSchemas.Result, element);
        return errors.Count > 0
            ? new(null, errors)
            : new(element.Deserialize<ResultContract>(ContractJson.Options), []);
    }

    /// <summary>
    /// Drops evidence that failed to resolve. A claim left without any resolved reference moves to
    /// open_questions with the reasons, so the caller sees it as unverified rather than losing it.
    /// </summary>
    public static ResultContract MoveUnresolved(ResultContract contract, IReadOnlyList<EvidenceFailure> failures)
    {
        if (failures.Count == 0)
            return contract;
        var failed = failures.GroupBy(f => f.EvidenceId).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(f => f.Reason)));
        var claims = new List<Claim>();
        var questions = contract.OpenQuestions.ToList();
        foreach (var claim in contract.Claims)
        {
            var kept = claim.Evidence.Where(id => !failed.ContainsKey(id)).ToList();
            if (kept.Count > 0)
                claims.Add(claim with { Evidence = kept });
            else
                questions.Add($"Unverified: {claim.Text} ({string.Join("; ", claim.Evidence.Select(id => $"{id}: {failed.GetValueOrDefault(id, "unresolved")}"))})");
        }
        var cited = claims.SelectMany(c => c.Evidence).ToHashSet();
        return contract with
        {
            Claims = claims,
            Evidence = contract.Evidence.Where(e => cited.Contains(e.Id)).ToList(),
            OpenQuestions = questions,
        };
    }
}
