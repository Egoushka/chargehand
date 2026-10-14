using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Accounts.Api;

/// <summary><c>http.accounts</c> bound without reflection (ADR 0060), with the profile's options: snake_case, comments skipped, unknown keys ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(IReadOnlyList<AccountSettings>))]
public sealed partial class AccountsSettingsJsonContext : JsonSerializerContext;
