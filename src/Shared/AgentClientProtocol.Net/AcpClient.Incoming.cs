using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AgentClientProtocol;

public sealed partial class AcpClient
{
    /// <summary>The calls an agent makes on the client: updates (notifications), permission requests, files and terminals.</summary>
    private sealed class Incoming(AcpClient client) : IRpcHandler
    {
        private readonly AcpClientOptions _options = client._options;

        public async ValueTask OnNotificationAsync(string method, JsonElement args, CancellationToken ct)
        {
            if (method != "session/update" || _options.OnSessionUpdate is not { } onUpdate)
                return;
            if (SessionUpdateParser.Parse(args) is { } notification)
                await onUpdate(notification, ct).ConfigureAwait(false);
        }

        public async ValueTask<byte[]> OnRequestAsync(string method, JsonElement args, CancellationToken ct)
        {
            try
            {
                return method switch
                {
                    "session/request_permission" => await PermissionAsync(args, ct).ConfigureAwait(false),
                    "fs/read_text_file" when _options.OnReadTextFile is { } read =>
                        Serialize(await read(Read(args, AcpJsonContext.Default.ReadTextFileRequest), ct).ConfigureAwait(false), AcpJsonContext.Default.ReadTextFileResponse),
                    "fs/write_text_file" when _options.OnWriteTextFile is { } write =>
                        await WriteAsync(write, args, ct).ConfigureAwait(false),
                    _ when method.StartsWith("terminal/", StringComparison.Ordinal) && _options.Terminal is { } terminal =>
                        await TerminalAsync(terminal, method, args, ct).ConfigureAwait(false),
                    _ => throw new RpcFailure(-32601, $"method not found: {method}"),
                };
            }
            catch (JsonException e)
            {
                throw new RpcFailure(-32602, $"invalid params for {method}: {e.Message}");
            }
        }

        private async ValueTask<byte[]> PermissionAsync(JsonElement args, CancellationToken ct)
        {
            var request = Read(args, AcpJsonContext.Default.RequestPermissionRequest);
            var outcome = _options.OnRequestPermission is { } ask
                ? await ask(request, ct).ConfigureAwait(false)
                : RequestPermissionOutcome.Cancelled;
            return Serialize(new RequestPermissionResponse(outcome), AcpJsonContext.Default.RequestPermissionResponse);
        }

        private static async ValueTask<byte[]> WriteAsync(Func<WriteTextFileRequest, CancellationToken, ValueTask> write, JsonElement args, CancellationToken ct)
        {
            await write(Read(args, AcpJsonContext.Default.WriteTextFileRequest), ct).ConfigureAwait(false);
            return EmptyResultJson;
        }

        private static async ValueTask<byte[]> TerminalAsync(IAcpTerminalHandler terminal, string method, JsonElement args, CancellationToken ct)
        {
            switch (method)
            {
                case "terminal/create":
                    return Serialize(await terminal.CreateAsync(Read(args, AcpJsonContext.Default.CreateTerminalRequest), ct).ConfigureAwait(false), AcpJsonContext.Default.CreateTerminalResponse);
                case "terminal/output":
                    return Serialize(await terminal.OutputAsync(Read(args, AcpJsonContext.Default.TerminalRequest), ct).ConfigureAwait(false), AcpJsonContext.Default.TerminalOutputResponse);
                case "terminal/wait_for_exit":
                    return Serialize(await terminal.WaitForExitAsync(Read(args, AcpJsonContext.Default.TerminalRequest), ct).ConfigureAwait(false), AcpJsonContext.Default.TerminalExitStatus);
                case "terminal/kill":
                    await terminal.KillAsync(Read(args, AcpJsonContext.Default.TerminalRequest), ct).ConfigureAwait(false);
                    return EmptyResultJson;
                case "terminal/release":
                    await terminal.ReleaseAsync(Read(args, AcpJsonContext.Default.TerminalRequest), ct).ConfigureAwait(false);
                    return EmptyResultJson;
                default:
                    throw new RpcFailure(-32601, $"method not found: {method}");
            }
        }

        private static readonly byte[] EmptyResultJson = "{}"u8.ToArray();

        private static T Read<T>(JsonElement args, JsonTypeInfo<T> info) =>
            args.Deserialize(info) ?? throw new RpcFailure(-32602, "missing params");

        private static byte[] Serialize<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.SerializeToUtf8Bytes(value, info);
    }
}
