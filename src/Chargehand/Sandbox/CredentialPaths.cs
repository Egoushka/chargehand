namespace Chargehand.Sandbox;

/// <summary>Where a user's credentials usually live, relative to the home directory; a sandboxed command may not read them.</summary>
internal static class CredentialPaths
{
    internal static readonly string[] Relative =
    [
        ".ssh", ".aws", ".gnupg", ".config/gh", ".netrc", ".git-credentials", ".docker", ".kube", ".claude", ".npmrc", "Library/Keychains",
    ];

    /// <summary>The paths of <see cref="Relative"/> that exist under <paramref name="home"/>.</summary>
    internal static IReadOnlyList<string> Existing(string home) =>
        [.. Relative.Select(r => Path.Combine(home, r)).Where(p => Directory.Exists(p) || File.Exists(p))];

    /// <summary>A path with every symbolic link resolved, as the macOS sandbox matches real paths (/var is /private/var).</summary>
    internal static string Real(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget is { } target)
                current = Real(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(current)!, target));
        }
        return current;
    }
}
