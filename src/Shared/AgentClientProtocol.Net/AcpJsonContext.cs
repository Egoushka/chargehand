using System.Text.Json.Serialization;

namespace AgentClientProtocol;

/// <summary>Source-generated JSON for every wire type, so the library publishes with Native AOT and no trim warning.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(InitializeRequest))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(AuthenticateRequest))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(NewSessionRequest))]
[JsonSerializable(typeof(NewSessionResult))]
[JsonSerializable(typeof(LoadSessionRequest))]
[JsonSerializable(typeof(LoadSessionResult))]
[JsonSerializable(typeof(SetModeRequest))]
[JsonSerializable(typeof(SetModelRequest))]
[JsonSerializable(typeof(SetConfigOptionRequest))]
[JsonSerializable(typeof(SetConfigOptionResult))]
[JsonSerializable(typeof(CancelNotification))]
[JsonSerializable(typeof(PromptRequest))]
[JsonSerializable(typeof(PromptResult))]
[JsonSerializable(typeof(UserMessageChunk))]
[JsonSerializable(typeof(AgentMessageChunk))]
[JsonSerializable(typeof(AgentThoughtChunk))]
[JsonSerializable(typeof(ToolCall))]
[JsonSerializable(typeof(ToolCallUpdate))]
[JsonSerializable(typeof(PlanUpdate))]
[JsonSerializable(typeof(AvailableCommandsUpdate))]
[JsonSerializable(typeof(CurrentModeUpdate))]
[JsonSerializable(typeof(ConfigOptionUpdate))]
[JsonSerializable(typeof(UsageUpdate))]
[JsonSerializable(typeof(SessionInfoUpdate))]
[JsonSerializable(typeof(RequestPermissionRequest))]
[JsonSerializable(typeof(RequestPermissionResponse))]
[JsonSerializable(typeof(ReadTextFileRequest))]
[JsonSerializable(typeof(ReadTextFileResponse))]
[JsonSerializable(typeof(WriteTextFileRequest))]
[JsonSerializable(typeof(CreateTerminalRequest))]
[JsonSerializable(typeof(CreateTerminalResponse))]
[JsonSerializable(typeof(TerminalRequest))]
[JsonSerializable(typeof(TerminalOutputResponse))]
[JsonSerializable(typeof(TerminalExitStatus))]
internal sealed partial class AcpJsonContext : JsonSerializerContext;
