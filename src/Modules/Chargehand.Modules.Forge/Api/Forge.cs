using System.Text.RegularExpressions;

namespace Chargehand.Modules.Forge.Api;

public enum ForgeKind { GitHub, GitLab }

/// <summary>The forge extension point (ADR 0047): what chargehand does on a code host, in two parts. <see cref="IPullRequests"/> opens a draft; <see cref="IIntegrationHost"/> reads its state and
/// marks it ready, comments, updates the branch and merges (auto-merge pinned to a head commit). GitHub implements the two with separate classes, because they act with different credentials;
/// a forge that needs one credential for both implements this.</summary>
public interface IForge : IPullRequests, IIntegrationHost;

public sealed partial record RemoteRepository
{
    /// <summary>The remote on the forge the profile names, or else the forge its host says: <c>gitlab.com</c> and a host whose first label is <c>gitlab</c> are GitLab, every other host is GitHub.
    /// A GitHub remote is read as <see cref="Parse(string)"/> does; a GitLab one may nest groups (<c>git@host:group/sub/project.git</c>). Null for anything else, including a local path.</summary>
    public static RemoteRepository? Parse(string url, DrivenForge? forge)
    {
        var host = HostOf(url);
        var kind = forge?.Kind switch { "gitlab" => ForgeKind.GitLab, "github" => ForgeKind.GitHub, _ => host is not null && IsGitLabHost(host) ? ForgeKind.GitLab : ForgeKind.GitHub };
        if (kind == ForgeKind.GitHub)
            return Parse(url);
        var match = GitLabHttps().Match(url);
        if (!match.Success)
            match = GitLabScp().Match(url);
        return match.Success ? new RemoteRepository(match.Groups["o"].Value, match.Groups["r"].Value, ForgeKind.GitLab, match.Groups["h"].Value.ToLowerInvariant()) : null;
    }

    private static string? HostOf(string url) =>
        GitLabHttps().Match(url) is { Success: true } https ? https.Groups["h"].Value : GitLabScp().Match(url) is { Success: true } scp ? scp.Groups["h"].Value : null;

    private static bool IsGitLabHost(string host) => host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase) || host.StartsWith("gitlab.", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(https|ssh)://([^@/]+@)?(?<h>[^/@:]+)(:\d+)?/(?<o>[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*)/(?<r>[A-Za-z0-9_.-]+?)(\.git)?/?$")]
    private static partial Regex GitLabHttps();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+@(?<h>[^/@:]+):(?<o>[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*)/(?<r>[A-Za-z0-9_.-]+?)(\.git)?$")]
    private static partial Regex GitLabScp();
}
