using System.Diagnostics;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>The path fence of free-model routing (spec 2026-11-25, D4), ported from the maintainer's router: a repository whose real path has a denied directory name
/// (<c>work</c>, <c>.devbox</c> by default), or that holds a symlink leading to one, never goes to a free model. Fail closed: a walk past <see cref="MaxEntries"/>, a link loop or
/// an unreadable entry refuses instead of throwing, so the run goes to its paid model.</summary>
public static class FreeFence
{
    public const int MaxEntries = 50_000;

    private const int MaxLinkHops = 40;

    public static readonly IReadOnlyList<string> DefaultDeny = ["work", ".devbox"];

    /// <summary>Why <paramref name="repositoryPath"/> must not go to a free model, or null when the fence lets it through. The whole checkout is checked, not only a subdirectory of it,
    /// and, when <paramref name="commit"/> is given, so are the symlinks that commit tracks (a worker reads a fresh clone of it, not the working tree on disk).</summary>
    public static string? Refusal(string repositoryPath, IReadOnlyList<string> deny, string? commit = null)
    {
        try
        {
            if (!Directory.Exists(repositoryPath))
                return "the repository path does not exist here";
            var real = RealPath(repositoryPath);
            if (Denied(real, deny))
                return "the repository is under a directory that never goes to a free model";
            var top = Toplevel(real);
            return LinkRefusal(top, deny) ?? (commit is null ? null : TrackedLinkRefusal(top, commit));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "the repository could not be checked for links into denied directories";
        }
    }

    /// <summary>The nearest directory at or above <paramref name="path"/> that holds a <c>.git</c>, else <paramref name="path"/>.</summary>
    private static string Toplevel(string path)
    {
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        return path;
    }

    private static string? LinkRefusal(string root, IReadOnlyList<string> deny)
    {
        const string Leads = "a link inside the repository leads to a directory that never goes to a free model";
        var seen = new HashSet<string> { root };
        var queue = new Queue<(string Dir, bool Outside)>([(root, false)]);
        var entries = 0;
        while (queue.Count > 0)
        {
            var (dir, outside) = queue.Dequeue();
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (++entries > MaxEntries)
                    return "the repository is too large to check for links into denied directories";
                var isLink = new FileInfo(entry).LinkTarget is not null;
                var target = isLink ? RealPath(entry) : entry;
                var isDir = Directory.Exists(target);
                // A directory the walk reached through a link is not the repository's own: a denied name anywhere in it is a way to work/.
                if ((isLink && (Denied(target, deny) || Holds(target, deny))) || (outside && isDir && Denied(Path.GetFileName(target), deny)))
                    return Leads;
                if (isDir && Path.GetFileName(entry) != ".git" && seen.Add(target))
                    queue.Enqueue((target, outside || isLink));
            }
        }
        return null;
    }

    /// <summary>The symlinks <paramref name="commit"/> tracks whose target leaves the repository (absolute, or climbing out of it) are refused: the clone a worker reads is not this directory,
    /// so where such a link would lead cannot be known here.</summary>
    private static string? TrackedLinkRefusal(string top, string commit)
    {
        var listing = Git(top, "ls-tree", "-r", "-z", commit);
        foreach (var line in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            var meta = line[..tab].Split(' ');
            if (meta[0] != "120000")
                continue;
            var target = Git(top, "cat-file", "blob", meta[2]).TrimEnd('\n');
            if (Escapes(Path.GetDirectoryName(line[(tab + 1)..]) ?? "", target))
                return "the pinned commit tracks a symlink that leaves the repository";
        }
        return null;
    }

    /// <summary>Whether <paramref name="target"/>, read from <paramref name="linkDir"/> (relative to the repository root), ends outside the root.</summary>
    internal static bool Escapes(string linkDir, string target)
    {
        if (target.StartsWith('/') || target.StartsWith('\\') || target.Contains(':', StringComparison.Ordinal))
            return true;
        var depth = linkDir.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).Length;
        foreach (var part in target.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            depth += part switch { ".." => -1, "." => 0, _ => 1 };
            if (depth < 0)
                return true;
        }
        return false;
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : throw new InvalidOperationException("git could not read the commit");
    }

    private static bool Denied(string path, IReadOnlyList<string> deny) =>
        path.Split('/', '\\').Any(seg => deny.Any(d => seg.Equals(d, StringComparison.OrdinalIgnoreCase)));

    /// <summary>A directory that has a denied directory right inside it (a home or a checkout root): reading it reads the denied one.</summary>
    private static bool Holds(string path, IReadOnlyList<string> deny) =>
        Directory.Exists(path) && deny.Any(d => Directory.Exists(Path.Combine(path, d)));

    /// <summary><paramref name="path"/> with every symlink on it followed, wherever it sits: in a component of the path or in the middle of a link's own target.
    /// Throws <see cref="IOException"/> on a loop.</summary>
    public static string RealPath(string path)
    {
        var pending = new Stack<string>(Path.GetFullPath(path).Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).Reverse());
        var current = Path.GetPathRoot(Path.GetFullPath(path)) ?? "/";
        for (var hops = 0; pending.Count > 0;)
        {
            var part = pending.Pop();
            if (part == ".")
                continue;
            if (part == "..")
            {
                current = Path.GetDirectoryName(current.TrimEnd('/', '\\')) ?? current;
                continue;
            }
            var next = Path.Combine(current, part);
            var link = new FileInfo(next).LinkTarget;
            if (link is null)
            {
                current = next;
                continue;
            }
            if (++hops > MaxLinkHops)
                throw new IOException("too many levels of symbolic links");
            if (Path.IsPathRooted(link))
                current = Path.GetPathRoot(link) ?? "/";
            foreach (var piece in link.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).Reverse())
                pending.Push(piece);
        }
        return current;
    }
}
