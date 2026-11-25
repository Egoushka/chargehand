namespace Chargehand.Modules.Accounts.Api;

/// <summary>The path fence of free-model routing (spec 2026-11-25, D4), ported from the maintainer's router: a repository whose real path has a denied directory name
/// (<c>work</c>, <c>.devbox</c> by default), or that holds a symlink leading to one, never goes to a free model. Fail closed: a walk past <see cref="MaxEntries"/> refuses.</summary>
public static class FreeFence
{
    public const int MaxEntries = 50_000;

    public static readonly IReadOnlyList<string> DefaultDeny = ["work", ".devbox"];

    /// <summary>Why <paramref name="repositoryPath"/> must not go to a free model, or null when the fence lets it through.</summary>
    public static string? Refusal(string repositoryPath, IReadOnlyList<string> deny)
    {
        if (!Directory.Exists(repositoryPath))
            return "the repository path does not exist here";
        var real = RealPath(repositoryPath);
        if (Denied(real, deny))
            return "the repository is under a directory that never goes to a free model";
        return LinkRefusal(real, deny);
    }

    private static string? LinkRefusal(string root, IReadOnlyList<string> deny)
    {
        var seen = new HashSet<string> { root };
        var queue = new Queue<string>([root]);
        var entries = 0;
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            foreach (var entry in SafeEntries(dir))
            {
                if (++entries > MaxEntries)
                    return "the repository is too large to check for links into denied directories";
                var info = new FileInfo(entry);
                if (info.LinkTarget is null)
                {
                    if (Directory.Exists(entry) && Path.GetFileName(entry) != ".git")
                        queue.Enqueue(entry);
                    continue;
                }
                var target = RealPath(entry);
                if (Denied(target, deny) || Holds(target, deny))
                    return "a link inside the repository leads to a directory that never goes to a free model";
                if (Directory.Exists(target) && seen.Add(target))
                    queue.Enqueue(target);
            }
        }
        return null;
    }

    private static List<string> SafeEntries(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool Denied(string path, IReadOnlyList<string> deny) =>
        path.Split('/', '\\').Any(seg => deny.Any(d => seg.Equals(d, StringComparison.OrdinalIgnoreCase)));

    /// <summary>A directory that has a denied directory right inside it (a home or a checkout root): reading it reads the denied one.</summary>
    private static bool Holds(string path, IReadOnlyList<string> deny) =>
        Directory.Exists(path) && deny.Any(d => Directory.Exists(Path.Combine(path, d)));

    /// <summary><paramref name="path"/> with every symlink on it followed.</summary>
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full) ?? "/";
        foreach (var segment in full[current.Length..].Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (new DirectoryInfo(current).ResolveLinkTarget(true) is { } target)
                current = target.FullName;
        }
        return current;
    }
}
