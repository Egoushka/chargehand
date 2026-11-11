using System.Text.RegularExpressions;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Handover.Api;

/// <summary>Issue 430: what a branch must not carry to a remote although it is no secret. Scratch output (a <c>TMPDIR</c> inside the workspace, a package manager's
/// <c>.tmp</c> files, a <c>.scratch</c> probe), more paths than a task's change plausibly touches, a file the size of a download; and the subject of a <c>WIP:</c> commit, which
/// the stop snapshot makes and which a repository's commit-message check refuses. Path rules only, applied to every path the branch's history touched.</summary>
public static partial class PushGuard
{
    /// <summary>More paths than this touched by the branch's commits is generated output, not a task's change.</summary>
    public const int MaxBranchFiles = 300;

    /// <summary>A file bigger than this in any of the branch's commits is a build output or a download; the stop snapshot keeps the same files out.</summary>
    public const long MaxFileBytes = SessionDriver.SnapshotMaxFileBytes;

    /// <summary>The prefix a <c>WIP:</c> subject is given instead, a Conventional Commits type a commit-message check takes.</summary>
    public const string WipPrefix = "chore: unfinished work, ";

    /// <summary>The kind of junk <paramref name="path"/> is: <c>tmpdir</c> (a <c>.tmp</c> directory, where a <c>TMPDIR</c> in the workspace puts its files), <c>tmp-file</c> (a
    /// <c>*.tmp</c> file) or <c>scratch</c> (a <c>.scratch*</c> file or directory); null for an ordinary path.</summary>
    public static string? Junk(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        if (parts.Any(p => p.StartsWith(".scratch", StringComparison.OrdinalIgnoreCase)))
            return "scratch";
        if (parts[..^1].Any(p => p.Equals(".tmp", StringComparison.OrdinalIgnoreCase)))
            return "tmpdir";
        return parts[^1].EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ? "tmp-file" : null;
    }

    /// <summary>The paths of <paramref name="paths"/> that are junk, each with its kind.</summary>
    public static IReadOnlyList<(string Kind, string Path)> FindJunk(IEnumerable<string> paths) =>
        [.. paths.Select(p => (Kind: Junk(p), Path: p)).Where(j => j.Kind is not null).Select(j => (j.Kind!, j.Path))];

    /// <summary><paramref name="message"/> with a <c>WIP</c> subject (<c>WIP: x</c>, <c>wip x</c>, <c>[WIP] x</c>) renamed to <see cref="WipPrefix"/><c>x</c>; the body is kept.
    /// Any other message is returned as it is, so the rename is the same on every call.</summary>
    public static string RenameWip(string message)
    {
        var end = message.IndexOf('\n', StringComparison.Ordinal);
        var subject = end < 0 ? message : message[..end];
        var match = WipSubject().Match(subject);
        if (!match.Success)
            return message;
        var rest = match.Groups["rest"].Value.Trim();
        return WipPrefix + (rest.Length == 0 ? "the session's work so far" : rest) + (end < 0 ? "" : message[end..]);
    }

    [GeneratedRegex(@"^\s*(\[wip\]|wip\b:?)\s*(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex WipSubject();
}
