using System.Net.Http.Headers;
using System.Text.Json;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>What GitHub says about a repository. Only <see cref="Public"/> lets a run into the free pool (ADR 0093); every other value, and every failure to ask, keeps it out.</summary>
public enum RepositoryVisibility { Unknown, Public, Private, Internal }

/// <summary>Reads a repository's visibility from its forge. Must not throw for a repository it cannot read: it answers <see cref="RepositoryVisibility.Unknown"/>.</summary>
public interface IRepositoryVisibility
{
    Task<RepositoryVisibility> LookupAsync(string forge, CancellationToken ct);
}

/// <summary><c>GET /repos/{owner}/{name}</c> on GitHub. Public needs a 200 whose <c>visibility</c> is exactly <c>public</c> and whose <c>private</c> is <c>false</c>. A 404 is what GitHub answers for
/// a private repository the token cannot see, so it is Unknown, not "not found".</summary>
public sealed class GitHubRepositoryVisibility(HttpClient http, string? token) : IRepositoryVisibility
{
    public const string ApiBase = "https://api.github.com/repos/";

    public async Task<RepositoryVisibility> LookupAsync(string forge, CancellationToken ct)
    {
        if (!PublicFreePool.IsForgeName(forge))
            return RepositoryVisibility.Unknown;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + forge);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("chargehand", "1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return response.StatusCode == System.Net.HttpStatusCode.OK ? Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) : RepositoryVisibility.Unknown;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException && !ct.IsCancellationRequested)
        {
            return RepositoryVisibility.Unknown;
        }
    }

    /// <summary>The visibility a 200 body states; Unknown for anything but an object with both fields in agreement.</summary>
    public static RepositoryVisibility Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("visibility", out var visibility) || visibility.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("private", out var isPrivate) || isPrivate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return RepositoryVisibility.Unknown;
            var closed = isPrivate.ValueKind == JsonValueKind.True;
            return (visibility.GetString(), closed) switch
            {
                ("public", false) => RepositoryVisibility.Public,
                ("internal", _) => RepositoryVisibility.Internal,
                ("private", _) or ("public", true) => RepositoryVisibility.Private,
                _ => RepositoryVisibility.Unknown,
            };
        }
        catch (JsonException)
        {
            return RepositoryVisibility.Unknown;
        }
    }
}
