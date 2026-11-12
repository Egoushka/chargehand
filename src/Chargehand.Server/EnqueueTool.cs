using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Pool.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Chargehand.Server;

/// <summary>
/// MCP tool "enqueue": puts one task into the pool (ADR 0099). It never starts work; the pool's governor batches the queued items of an area into one
/// worker when its checks allow. Owner only: pool workers are charged to the automation account, so an account principal could otherwise spend it.
/// </summary>
public static class EnqueueTool
{
    public const string Name = "enqueue";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static McpServerTool Create()
    {
        var tool = McpServerTool.Create(CallAsync, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = "Queue one task for chargehand's pool instead of doing it now. Items of the same repository and area run together in one worker, oldest first; "
                        + "items tied to a pull request and higher priority start first. Returns the item's key and its position in the queue.",
            ReadOnly = false,
            Destructive = false,
            Idempotent = false,
        });
        // The published schema is the tool's input schema, so the two cannot drift.
        tool.ProtocolTool.InputSchema = JsonDocument.Parse(ContractSchemas.Text(ContractSchemas.Enqueue)).RootElement.Clone();
        return tool;
    }

    private static async Task<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        if (context.Services!.GetService<TaskPool>() is not { } pool)
            return Error("the pool is off (http.pool)");
        var http = context.Services!.GetService<IHttpContextAccessor>()?.HttpContext;
        // A session's run token resolves to the owner too; a run must not queue work for workers outside its own grant.
        if (http is null || http.Items.ContainsKey(RunScope.ItemKey) || PrincipalHttp.Of(http) is not { IsOwner: true })
            return Error("forbidden: only the owner enqueues into the pool");
        var request = Read(context.Params?.Arguments);
        if (request is null)
            return Error("invalid: priority and pull_request are integers; the other arguments are strings");
        if (request.Repository is { } repository && context.Services!.GetService<PoolRunStarter>() is { } starter && !starter.Knows(repository))
            return Error("invalid: repository is not one of automation.github.repositories");
        var result = await pool.EnqueueAsync(request, ct).ConfigureAwait(false);
        if (result.Refusal is { } refusal)
            return Error($"{refusal.Code}: {refusal.Message}");
        var item = result.Item!;
        var text = JsonSerializer.Serialize(new Reply(item.Item.Key, item.Item.Repository, item.Item.Area, item.Position), Json);
        return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
    }

    internal static EnqueueRequest? Read(IDictionary<string, JsonElement>? arguments)
    {
        string? Text(string name) => arguments?.TryGetValue(name, out var v) == true && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Wrong(string name, JsonValueKind kind) => arguments?.TryGetValue(name, out var v) == true && v.ValueKind is not JsonValueKind.Null && v.ValueKind != kind;
        if (Wrong("repository", JsonValueKind.String) || Wrong("area", JsonValueKind.String) || Wrong("title", JsonValueKind.String) || Wrong("body", JsonValueKind.String)
            || Wrong("priority", JsonValueKind.Number) || Wrong("pull_request", JsonValueKind.Number))
            return null;
        int? Number(string name)
        {
            if (arguments?.TryGetValue(name, out var v) != true || v.ValueKind != JsonValueKind.Number)
                return null;
            return v.TryGetInt32(out var n) ? n : -1;
        }
        return new EnqueueRequest(Text("repository"), Text("area"), Text("title"), Text("body"), Number("priority") ?? 0, Number("pull_request"), Source: Name);
    }

    private static CallToolResult Error(string text) => new() { IsError = true, Content = [new TextContentBlock { Text = text }] };

    private sealed record Reply(string Key, string Repository, string Area, int Position);
}
