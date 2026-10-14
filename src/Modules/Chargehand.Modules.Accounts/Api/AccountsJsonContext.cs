using System.Text.Json.Serialization;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>The keys file and the key-use file without reflection (ADR 0060), written as before: snake_case, indented, nulls left out.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(List<AccountKey>))]
[JsonSerializable(typeof(Dictionary<string, DateTimeOffset>))]
internal sealed partial class AccountsJsonContext : JsonSerializerContext;
