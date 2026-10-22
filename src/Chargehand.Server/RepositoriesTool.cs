using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Workspace;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Chargehand.Server;

/// <summary>
/// MCP tool "repositories": the checkouts this server reads (<see cref="RepositoryNames"/>), each with the names
/// <c>context.repository.path</c> takes and the commit it has checked out, so a caller pins one instead of guessing a path. Read-only;
/// an account limited to some repositories sees only those.
/// </summary>
public static class RepositoriesTool
{
    public const string Name = "repositories";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static McpServerTool Create()
    {
        var tool = McpServerTool.Create(CallAsync, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = "List the repositories this chargehand server can read: for each, the folder name and owner/name that orchestrate's context.repository.path "
                        + "accepts (a GitHub URL works too), the server's path, and the commit and branch it has checked out now, to use as context.repository.commit.",
            ReadOnly = true,
        });
        tool.ProtocolTool.InputSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();
        return tool;
    }

    private static async Task<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var runs = context.Services!.GetRequiredService<RunService>();
        var who = PrincipalHttp.Of(context.Services!.GetService<IHttpContextAccessor>()?.HttpContext);
        var heads = (await runs.RepositoriesAsync(ct)).Where(h => who?.Account is not { Repositories: { Count: > 0 } allowed } || allowed.Any(a => Within(a, h.Path))).ToList();
        var text = JsonSerializer.Serialize(heads, Json);
        return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
    }

    private static bool Within(string root, string path)
    {
        var (r, p) = (Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        return p == r || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
