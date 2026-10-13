using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Kernel;

/// <summary>The body of a host's <c>GET /healthz</c>.</summary>
public sealed record HostHealth(string Status, string Version)
{
    public static HostHealth Ok { get; } = new("ok", BuildInfo.Version);
}

/// <summary>Source-generated JSON for the kernel's wire types: no reflection, so hosts publish with Native AOT.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HostHealth))]
public sealed partial class KernelJsonContext : JsonSerializerContext;
