using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Contracts;

/// <summary>Serializer settings matching the schemas: snake_case names, snake_case enum values, nulls omitted.</summary>
public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
