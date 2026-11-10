using System.Text.Json.Serialization;

namespace Chargehand.Modules.Research.Prompts;

/// <summary>The instruction-file hash input, serialised without reflection (ADR 0060); the bytes match the earlier reflection output.</summary>
[JsonSerializable(typeof(string[][]))]
internal sealed partial class PromptsJsonContext : JsonSerializerContext;
