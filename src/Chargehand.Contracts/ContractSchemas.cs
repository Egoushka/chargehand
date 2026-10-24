using System.Text.Json;
using Json.Schema;

namespace Chargehand.Contracts;

/// <summary>The published contract schemas, embedded in this assembly.</summary>
public static class ContractSchemas
{
    public const string Request = "request/v1";
    public const string TaskSpec = "task-spec/v1";
    public const string Result = "result/v1";
    public const string Preset = "preset/v1";
    public const string RunStatus = "run-status/v1";
    public const string RunSummary = "run-summary/v1";

    // Built once; the global schema registry rejects a second registration of the same $id.
    private static readonly Dictionary<string, Lazy<JsonSchema>> Schemas =
        new[] { Request, TaskSpec, Result, Preset, RunStatus, RunSummary }.ToDictionary(n => n, n => new Lazy<JsonSchema>(() => JsonSchema.FromText(Text(n))));

    /// <summary>Raw schema text, e.g. for serving it as an MCP tool's outputSchema.</summary>
    public static string Text(string name)
    {
        // "result/v1" is embedded as "v1/result.schema.json".
        var slash = name.IndexOf('/');
        var resource = $"{name[(slash + 1)..]}/{name[..slash]}.schema.json";
        using var stream = typeof(ContractSchemas).Assembly.GetManifestResourceStream(resource)
            ?? throw new ArgumentException($"Unknown contract schema '{name}'.", nameof(name));
        return new StreamReader(stream).ReadToEnd();
    }

    /// <summary>
    /// The outputSchema of the MCP tools that start or read a run: result/v1 once it finished, run-status/v1 while it goes on (ADR 0079).
    /// Each member keeps its own $id, so the result schema's local $ref still resolves.
    /// </summary>
    public static string McpOutputText() =>
        $$"""{"type":"object","oneOf":[{{Text(Result)}},{{Text(RunStatus)}}]}""";

    /// <summary>Validates a tool's structuredContent against <see cref="McpOutputText"/>: exactly one of its two members must accept it. Empty list means valid.</summary>
    public static IReadOnlyList<string> ValidateMcpOutput(JsonElement document)
    {
        // The members are registered under their $id already, so the combined schema cannot be loaded a second time.
        var matches = new[] { Result, RunStatus }.Count(n => Validate(n, document).Count == 0);
        return matches == 1 ? [] : ["matches " + matches + " of result/v1, run-status/v1"];
    }

    public static JsonSchema Get(string name) =>
        Schemas.TryGetValue(name, out var schema) ? schema.Value : throw new ArgumentException($"Unknown contract schema '{name}'.", nameof(name));

    /// <summary>Validates a JSON document against a contract schema. Empty list means valid.</summary>
    public static IReadOnlyList<string> Validate(string name, JsonElement document)
    {
        var result = Get(name).Evaluate(document, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
            return [];
        return (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
            .DefaultIfEmpty("invalid")
            .ToList();
    }
}
