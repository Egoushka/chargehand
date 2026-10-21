using System.Text;

namespace Chargehand.Automation;

/// <summary>The checkouts GitHub intake keeps for the repositories it discovers (ADR 0051, <c>discover</c>): one per repository at <c>&lt;root&gt;/&lt;owner&gt;/&lt;repo&gt;</c>, cloned once and fetched before each
/// start by <see cref="GitRepositoryHeads"/>. git authenticates with the App's installation token as an HTTP header passed in the environment of that one git process: it is not in the command line,
/// the remote URL or <c>.git/config</c>, and it never reaches a session, which works from its own clone of a commit (ADR 0023).</summary>
/// <param name="token">The installation token for <c>owner</c>, <c>repo</c>.</param>
public sealed class ManagedClones(string root, Func<string, string, CancellationToken, Task<string>> token)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string PathOf(string repository) => Path.Combine(Root, repository.Replace('/', Path.DirectorySeparatorChar));

    public bool Owns(string path) => Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>The checkout of <paramref name="repository"/>, cloned from <paramref name="cloneUrl"/> when there is none yet.</summary>
    /// <exception cref="IntakeException">The clone failed.</exception>
    public async Task<string> EnsureAsync(string repository, string cloneUrl, CancellationToken ct)
    {
        var path = PathOf(repository);
        if (Directory.Exists(Path.Combine(path, ".git")))
            return path;
        if (!cloneUrl.StartsWith("https://", StringComparison.Ordinal) || cloneUrl.Contains('@', StringComparison.Ordinal))
            throw new IntakeException($"{repository}: GitHub gave a clone URL intake does not use");
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        // Cloned beside the target and moved, so a clone cut short is never taken for a checkout.
        var temp = Path.Combine(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}");
        var clone = await GitRepositoryHeads.GitAsync(parent, ["clone", "--quiet", "--", cloneUrl, temp], ct, await AuthAsync(path, ct));
        if (clone.ExitCode != 0)
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
            throw new IntakeException($"{repository}: the clone failed: {clone.Output}");
        }
        Directory.Move(temp, path);
        return path;
    }

    /// <summary>The environment that makes git send the installation token for the repository at <paramref name="path"/>.</summary>
    /// <exception cref="IntakeException">No token could be had.</exception>
    public async Task<IReadOnlyDictionary<string, string>> AuthAsync(string path, CancellationToken ct)
    {
        var parts = Path.GetRelativePath(Root, Path.GetFullPath(path)).Split(Path.DirectorySeparatorChar);
        if (parts.Length < 2)
            throw new IntakeException($"{path} is not a managed clone");
        string value;
        try
        {
            value = await token(parts[0], parts[1], ct);
        }
        catch (Chargehand.Modules.Forge.Api.GitHubAppException e)
        {
            throw new IntakeException(e.Message);
        }
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{value}"));
        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "http.extraHeader",
            ["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {basic}",
        };
    }
}
