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

    private const int MaxTrackedLinks = 5_000;

    private const int MaxListingBytes = 64 * 1024 * 1024;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The symlinks <paramref name="commit"/> tracks whose target leaves the repository (absolute, or climbing out of it), or climbs after passing through another tracked link, are
    /// refused: the clone a worker reads is not this directory, so where such a link would lead cannot be known here.</summary>
    private static string? TrackedLinkRefusal(string top, string commit)
    {
        const string Refused = "the pinned commit tracks a symlink that leaves the repository";
        var listing = Git(top, null, MaxListingBytes, "ls-tree", "-r", "-z", commit);
        var links = new List<(string Path, string Oid)>();
        foreach (var line in System.Text.Encoding.UTF8.GetString(listing).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            var meta = line[..tab].Split(' ');
            if (meta[0] == "120000")
                links.Add((line[(tab + 1)..], meta[2]));
        }
        if (links.Count > MaxTrackedLinks)
            return "the pinned commit tracks too many symlinks to check";
        if (links.Count == 0)
            return null;
        // One process reads every link target: "<oid> blob <size>\n<content>\n" per object.
        var blobs = Git(top, string.Concat(links.Select(l => l.Oid + "\n")), MaxListingBytes, "cat-file", "--batch");
        var paths = links.Select(l => l.Path).ToHashSet(StringComparer.Ordinal);
        var at = 0;
        foreach (var (path, _) in links)
        {
            var eol = Array.IndexOf(blobs, (byte)'\n', at);
            var header = System.Text.Encoding.UTF8.GetString(blobs, at, eol - at).Split(' ');
            var size = int.Parse(header[2], System.Globalization.CultureInfo.InvariantCulture);
            var target = System.Text.Encoding.UTF8.GetString(blobs, eol + 1, size);
            at = eol + 1 + size + 1;
            if (Escapes(Path.GetDirectoryName(path) ?? "", target, paths))
                return Refused;
        }
        return null;
    }

    /// <summary>Whether <paramref name="target"/>, read from <paramref name="linkDir"/> (relative to the repository root), ends outside the root, or climbs with <c>..</c> from a place that is itself a
    /// tracked link (<paramref name="trackedLinks"/>, root-relative paths), where the count of names no longer says where the path is. Only <c>/</c> separates names: a backslash is part of a name on Linux.</summary>
    internal static bool Escapes(string linkDir, string target, IReadOnlySet<string>? trackedLinks = null)
    {
        if (target.StartsWith('/') || target.StartsWith('\\') || target.Contains(':', StringComparison.Ordinal))
            return true;
        var stack = new List<string>(Parts(linkDir));
        foreach (var part in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part != "..")
            {
                stack.Add(part);
                continue;
            }
            if (stack.Count == 0 || (trackedLinks?.Contains(string.Join('/', stack)) ?? false))
                return true;
            stack.RemoveAt(stack.Count - 1);
        }
        return false;
    }

    private static string[] Parts(string path) => path.Split(Path.DirectorySeparatorChar == '/' ? ['/'] : ['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Runs git and returns its standard output; throws <see cref="InvalidOperationException"/> on a failure, a timeout or more than <paramref name="limit"/> bytes, so the fence refuses.</summary>
    private static byte[] Git(string dir, string? stdin, int limit, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardInput = stdin is not null, RedirectStandardOutput = true, RedirectStandardError = true };
        // Read-only calls on a checkout another user owns would otherwise fail git's ownership check and refuse every such repository.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.directory=*");
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var errors = p.StandardError.ReadToEndAsync();
        var output = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(output);
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        if (!p.WaitForExit(GitTimeout))
        {
            p.Kill(entireProcessTree: true);
            throw new InvalidOperationException("git timed out");
        }
        copy.Wait(GitTimeout);
        errors.Wait(GitTimeout);
        return p.ExitCode == 0 && output.Length <= limit ? output.ToArray() : throw new InvalidOperationException("git could not read the commit");
    }

    private static bool Denied(string path, IReadOnlyList<string> deny) =>
        Parts(path).Any(seg => deny.Any(d => seg.Equals(d, StringComparison.OrdinalIgnoreCase)));

    /// <summary>A directory that has a denied directory right inside it (a home or a checkout root): reading it reads the denied one.</summary>
    private static bool Holds(string path, IReadOnlyList<string> deny) =>
        Directory.Exists(path) && deny.Any(d => Directory.Exists(Path.Combine(path, d)));

    /// <summary><paramref name="path"/> with every symlink on it followed, wherever it sits: in a component of the path or in the middle of a link's own target.
    /// Throws <see cref="IOException"/> on a loop.</summary>
    public static string RealPath(string path)
    {
        var pending = new Stack<string>(Parts(Path.GetFullPath(path)).Reverse());
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
            foreach (var piece in Parts(link).Reverse())
                pending.Push(piece);
        }
        return current;
    }
}
