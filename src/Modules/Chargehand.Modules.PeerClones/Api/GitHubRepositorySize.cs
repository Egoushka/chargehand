using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.PeerClones.Api;

/// <summary>A repository's size, checked before anything is cloned so an oversized repository costs nothing to refuse.</summary>
public interface IPeerRepositorySize
{
    /// <summary>Bytes GitHub reports for the repository (its <c>diskUsage</c>, in KB, times 1024); null when GitHub has no such repository.</summary>
    /// <exception cref="ChargehandException">GitHub could not be asked.</exception>
    Task<long?> DiskUsageBytesAsync(string owner, string name, CancellationToken ct);
}

/// <summary>GitHub's GraphQL <c>repository.diskUsage</c> (KB), the size check ADR … (peer clones) asks for before a shallow clone. GitHub's
/// GraphQL API takes no anonymous calls, so even a public repository's size needs a token.</summary>
public sealed class GitHubRepositorySize(HttpClient http, Func<CancellationToken, Task<string>> token) : IPeerRepositorySize
{
    private const string Query = "query($owner:String!,$name:String!){repository(owner:$owner,name:$name){diskUsage}}";

    public async Task<long?> DiskUsageBytesAsync(string owner, string name, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
        {
            Content = JsonContent.Create(new GraphQlRequest(Query, new Dictionary<string, string> { ["owner"] = owner, ["name"] = name }),
                PeerCloneGraphQlJsonContext.Default.GraphQlRequest),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(ct));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("chargehand", "1"));
        string text;
        try
        {
            using var response = await http.SendAsync(request, ct);
            text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new ChargehandException(ErrorCode.RepositoryNotAllowed,
                    $"GitHub answered {(int)response.StatusCode} to the size check for {owner}/{name}", "Check the owner, the name and the peer_clone token.");
        }
        catch (HttpRequestException e)
        {
            throw new ChargehandException(ErrorCode.RepositoryNotAllowed, $"GitHub could not be reached for the size check for {owner}/{name}: {e.Message}",
                "Check the server's network access to api.github.com.");
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
                return null;
            var repository = doc.RootElement.GetProperty("data").GetProperty("repository");
            return repository.ValueKind == JsonValueKind.Null ? null : repository.GetProperty("diskUsage").GetInt64() * 1024;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ChargehandException(ErrorCode.RepositoryNotAllowed, $"GitHub answered the size check for {owner}/{name} with a body that holds no diskUsage",
                "Retry; if it keeps happening, check the peer_clone token's scope.");
        }
    }
}

internal sealed record GraphQlRequest(string Query, Dictionary<string, string> Variables);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GraphQlRequest))]
internal sealed partial class PeerCloneGraphQlJsonContext : JsonSerializerContext;
