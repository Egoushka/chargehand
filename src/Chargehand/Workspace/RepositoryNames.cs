using System.Diagnostics;
using System.Text.RegularExpressions;
using Chargehand.Config;

namespace Chargehand.Workspace;

/// <summary>A checkout the server reads: its folder name, its path, and its forge name (<c>owner/name</c>) when the profile's GitHub intake maps one to it or it is a managed clone.</summary>
public sealed record KnownRepository(string Name, string Path, string? Forge);

/// <summary>A known checkout with what it has checked out now: the commit a caller pins when it has no other, and the branch.</summary>
public sealed record RepositoryHead(string Name, string Path, string? Forge, string? Commit, string? Branch);

/// <summary>
/// Callers name a repository the way they know it: <c>owner/name</c>, a GitHub URL, a folder name, or a path from their own machine. A
/// <c>context.repository.path</c> that is not a checkout on this server resolves through the profile: the GitHub intake's repository map
/// (<c>automation.github.repositories</c>), then the checkouts at or directly under <c>repository_roots</c> by folder name, and the managed
/// clones by <c>owner/name</c>. One match only; none or several leave the request to fail with the known names listed.
/// </summary>
public static partial class RepositoryNames
{
    /// <summary>The most directories one root contributes, as the console's repository list scans.</summary>
    private const int MaxChildren = 200;

    /// <summary>The most names an error lists.</summary>
    private const int MaxListed = 30;

    public static IReadOnlyList<KnownRepository> List(Profile profile)
    {
        var known = new List<KnownRepository>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string path, string? forge)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (IsCheckout(full) && seen.Add(full))
                known.Add(new(Path.GetFileName(full), full, forge));
        }
        if (profile.Automation?.Github?.Repositories is { } map)
            foreach (var (forge, repository) in map)
                Add(repository.Path, forge);
        foreach (var root in profile.Roots.Where(Directory.Exists))
        {
            var managed = profile.Automation?.Github is not null && SamePath(root, profile.ClonesRoot);
            if (IsCheckout(root))
                Add(root, null);
            foreach (var child in Children(root))
            {
                if (!managed)
                    Add(child, null);
                else
                    foreach (var clone in Children(child))
                        Add(clone, $"{Path.GetFileName(child)}/{Path.GetFileName(clone)}");
            }
        }
        return known;
    }

    /// <summary>The path <paramref name="given"/> names, or null when it names none or several.</summary>
    public static string? Resolve(string given, IReadOnlyList<KnownRepository> known)
    {
        var name = Normalize(given);
        if (name.Length == 0)
            return null;
        var byForge = known.Where(k => string.Equals(k.Forge, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byForge.Count == 1)
            return byForge[0].Path;
        var folder = name[(name.LastIndexOf('/') + 1)..];
        var byFolder = known.Where(k => string.Equals(k.Name, folder, StringComparison.OrdinalIgnoreCase)).ToList();
        return byFolder.Count == 1 ? byFolder[0].Path : null;
    }

    /// <summary>The known repositories as a caller may name them, for an error.</summary>
    public static string Describe(IReadOnlyList<KnownRepository> known) => known.Count == 0
        ? "this server reads no repository: repository_roots holds no git checkout"
        : string.Join(", ", known.Take(MaxListed).Select(k => k.Forge is null ? k.Name : $"{k.Forge} ({k.Name})")) + (known.Count > MaxListed ? $" and {known.Count - MaxListed} more" : "");

    /// <summary>Each known checkout with the commit and branch it has checked out now; a checkout git cannot read has neither.</summary>
    public static async Task<IReadOnlyList<RepositoryHead>> HeadsAsync(IReadOnlyList<KnownRepository> known, CancellationToken ct)
    {
        using var limit = new SemaphoreSlim(8);
        return await Task.WhenAll(known.Select(async k =>
        {
            await limit.WaitAsync(ct);
            try
            {
                return new RepositoryHead(k.Name, k.Path, k.Forge, await Git(k.Path, ct, "rev-parse", "HEAD"), await Git(k.Path, ct, "symbolic-ref", "--short", "-q", "HEAD"));
            }
            finally
            {
                limit.Release();
            }
        }));
    }

    /// <summary><c>owner/name</c> from a GitHub URL; otherwise the value without a trailing slash, <c>.git</c> directory or <c>.git</c> suffix.</summary>
    internal static string Normalize(string given)
    {
        var name = given.Trim().TrimEnd('/', '\\');
        if (ForgeUrl().Match(name) is { Success: true } url)
            name = $"{url.Groups["owner"].Value}/{url.Groups["name"].Value}";
        if (name.EndsWith("/.git", StringComparison.Ordinal))
            name = name[..^"/.git".Length];
        if (name.EndsWith(".git", StringComparison.Ordinal))
            name = name[..^".git".Length];
        return name.TrimEnd('/', '\\') is "." or ".." ? "" : name.TrimEnd('/', '\\');
    }

    [GeneratedRegex(@"^(?:https?://|ssh://git@|git@)(?:www\.)?github\.com[:/](?<owner>[^/\s]+)/(?<name>[^/\s]+?)(?:\.git)?(?:/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ForgeUrl();

    private static bool IsCheckout(string path) => Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.Ordinal);

    private static IEnumerable<string> Children(string root)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(root).Where(d => !Path.GetFileName(d).StartsWith('.')).Order(StringComparer.Ordinal).Take(MaxChildren)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task<string?> Git(string directory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git", ["-C", directory, .. args]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 && output.Trim() is { Length: > 0 } text ? text : null;
    }
}
