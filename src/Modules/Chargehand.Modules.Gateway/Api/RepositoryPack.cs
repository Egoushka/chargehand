using System.Text;

namespace Chargehand.Modules.Gateway.Api;

/// <summary>
/// A brief about a repository at one commit: its ecosystems, its test command and its own conventions, built once from
/// <see cref="RepositoryDetector"/> and the repository's own <c>CLAUDE.md</c> or <c>AGENTS.md</c> (issue #435). Research and
/// review steps load this first instead of spending their own budget rediscovering what the repository already says about
/// itself. Cached per repository and commit by <see cref="RepositoryPackCache"/>.
/// </summary>
public sealed record RepositoryPack(IReadOnlyList<DetectedEcosystem> Ecosystems, VerifyCandidate? Verify, string? Conventions, string? ConventionsSource)
{
    /// <summary>Bounds the pack's own cost: a brief that itself burned the budget it was meant to save would defeat the point.</summary>
    private const int MaxConventionsChars = 8000;

    private static readonly string[] ConventionsFiles = ["CLAUDE.md", "AGENTS.md"];

    public static RepositoryPack Build(string directory)
    {
        var scan = RepositoryDetector.Scan(directory);
        var (text, source) = ReadConventions(directory);
        return new RepositoryPack(scan.Ecosystems, scan.BestVerify, text, source);
    }

    private static (string? Text, string? Source) ReadConventions(string directory)
    {
        foreach (var name in ConventionsFiles)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
                continue;
            var text = File.ReadAllText(path);
            return (text.Length > MaxConventionsChars ? text[..MaxConventionsChars] + "\n[... truncated]" : text, name);
        }
        return (null, null);
    }

    /// <summary>The block appended to a node's prompt; no leading or trailing blank line, so a caller controls the spacing.</summary>
    public string Render()
    {
        var note = new StringBuilder("Repository context pack (built once per commit from the repository's own files; load this instead of rediscovering its map, test command and conventions):\n");
        note.Append(Ecosystems.Count > 0
            ? $"- Map: uses {string.Join(", ", Ecosystems.Select(e => e.Name))} ({string.Join("; ", Ecosystems.Select(e => $"{e.Name}: {string.Join(", ", e.Markers)}"))}).\n"
            : "- Map: no ecosystem markers at the top level.\n");
        note.Append(Verify is { } v
            ? $"- Test command: `{string.Join(' ', v.Command)}` (from {v.Source}).\n"
            : "- Test command: none detected; find it in the repository's own files.\n");
        if (Conventions is { Length: > 0 } text)
            note.Append("- Conventions and known traps, from this repository's own ").Append(ConventionsSource).Append(":\n").Append(text).Append('\n');
        return note.ToString();
    }
}
