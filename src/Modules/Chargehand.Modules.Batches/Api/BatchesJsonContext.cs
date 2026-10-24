using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>The planner's, the retro's and the continuation's JSON without reflection (ADR 0060). It has no options of its own: each instance has the options its user had before, so
/// every line is written and read as it was.</summary>
[JsonSerializable(typeof(PlanWire))]
[JsonSerializable(typeof(RetroLine))]
[JsonSerializable(typeof(AutopsyLine))]
[JsonSerializable(typeof(KeptWork))]
internal sealed partial class BatchesJsonContext : JsonSerializerContext
{
    /// <summary><c>ContractJson.Options</c> without its enum converter, which none of these types needs: snake_case, nulls left out.</summary>
    public static BatchesJsonContext Contract { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });

    /// <summary>The planner's answer: a model may change the case, leave a trailing comma or add a comment.</summary>
    public static BatchesJsonContext Wire { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    });
}
