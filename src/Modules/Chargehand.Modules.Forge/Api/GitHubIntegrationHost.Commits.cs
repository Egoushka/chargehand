using System.Globalization;
using System.Text.Json;

namespace Chargehand.Modules.Forge.Api;

/// <summary>One commit a pull request carries, with every path it changed (old and new names alike): what a person added to the branch after chargehand's own last push.</summary>
public sealed record PrCommit(string Sha, string Message, IReadOnlyList<string> ChangedPaths);

public partial interface IIntegrationHost
{
    /// <summary>The pull request's commits strictly after <paramref name="afterSha"/> (ADR 0045's <c>merged_edited</c>): what a person pushed once chargehand left the branch. Oldest first.
    /// A forge that cannot read this, or a pull request whose history no longer holds <paramref name="afterSha"/>, throws.</summary>
    Task<IReadOnlyList<PrCommit>> CommitsAfterAsync(RemoteRepository repository, int number, string afterSha, CancellationToken ct) =>
        throw new IntegrationException($"{Name} cannot list a pull request's commits from here");
}

public sealed partial class GitHubIntegrationHost
{
    public async Task<IReadOnlyList<PrCommit>> CommitsAfterAsync(RemoteRepository repository, int number, string afterSha, CancellationToken ct)
    {
        var commits = CommitList(await RestAsync(repository, HttpMethod.Get,
            $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/commits?per_page=100", null, "the pull request's commits", ct));
        var afterIndex = commits.FindIndex(c => string.Equals(c.Sha, afterSha, StringComparison.OrdinalIgnoreCase));
        if (afterIndex < 0)
            throw new IntegrationException("the pull request's history no longer holds the commit chargehand last pushed");
        var result = new List<PrCommit>();
        foreach (var (sha, message) in commits.Skip(afterIndex + 1))
            result.Add(new PrCommit(sha, message, await CommitPathsAsync(repository, sha, ct)));
        return result;
    }

    private static List<(string Sha, string Message)> CommitList(string commits)
    {
        try
        {
            using var doc = JsonDocument.Parse(commits);
            return [.. doc.RootElement.EnumerateArray().Select(c => (c.GetProperty("sha").GetString() ?? "", c.GetProperty("commit").GetProperty("message").GetString() ?? ""))];
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new IntegrationException("GitHub answered with a body that is not a list of commits");
        }
    }

    private async Task<IReadOnlyList<string>> CommitPathsAsync(RemoteRepository repository, string sha, CancellationToken ct)
    {
        var text = await RestAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/commits/{Uri.EscapeDataString(sha)}", null, "a commit's files", ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array
                ? [.. files.EnumerateArray().Select(f => f.GetProperty("filename").GetString() ?? "")]
                : [];
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new IntegrationException("GitHub answered with a body that is not a commit");
        }
    }
}
