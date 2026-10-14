using System.Text;
using Chargehand.Kernel;

namespace Chargehand.Driven;

/// <summary>The end of a session's transcript as it may leave the server (ADR 0059): whole lines, each redacted.</summary>
/// <param name="Lines">The lines of the tail, oldest first. Each is one <c>stream-json</c> event as the session wrote it, or, for one over <see cref="Transcript.MaxLine"/>, <c>{"type":"chargehand_cut","bytes":N}</c>.</param>
/// <param name="Truncated">The file is at least as long as the window that was read, so earlier lines exist (and the first, partial line was dropped).</param>
/// <param name="Bytes">How many bytes the runner returned, before redaction and before the partial first line was dropped.</param>
/// <param name="Redacted">The kinds of secret that were replaced (<c>anthropic-key</c>, <c>credential-literal</c>, ...); never a value.</param>
/// <param name="Cut">Lines replaced by the stub for their length.</param>
public sealed record TranscriptView(IReadOnlyList<string> Lines, bool Truncated, int Bytes, IReadOnlyList<string> Redacted, int Cut);

/// <summary>What <see cref="DrivenRun.ReadTranscriptAsync"/> found: a view, or why there is none (<c>session_gone</c>, <c>runner_unreachable</c>, <c>runner_too_old</c>, <c>not_supported</c>, <c>driven_off</c>).</summary>
public sealed record TranscriptRead(TranscriptView? View, string? Unavailable);

/// <summary>Makes the tail the runner returns safe to show (ADR 0059): drops the partial line the window cut, cuts a line that is too long, and redacts what <see cref="DiffScan.Redact"/> knows.</summary>
public static class Transcript
{
    public const int DefaultBytes = 64 * 1024;

    public const int MinBytes = 1024;

    public const int MaxBytes = Containers.ContainerTemplate.MaxTailBytes;

    /// <summary>A line longer than this is a tool result or a message too large to show; it is replaced by a stub that says how long it was.</summary>
    public const int MaxLine = 32 * 1024;

    /// <param name="raw">What the runner returned: at most <paramref name="requested"/> bytes from the end of the file.</param>
    /// <param name="literals">The credentials this server holds, to be replaced wherever they appear.</param>
    public static TranscriptView Prepare(byte[] raw, int requested, IReadOnlyList<string> literals)
    {
        var truncated = raw.Length >= requested;
        var text = Encoding.UTF8.GetString(raw);
        if (truncated)
        {
            // The window starts inside a line, and perhaps inside a character.
            var first = text.IndexOf('\n', StringComparison.Ordinal);
            text = first < 0 ? "" : text[(first + 1)..];
        }
        List<string> lines = [];
        HashSet<string> kinds = [];
        var cut = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
                continue;
            if (trimmed.Length > MaxLine)
            {
                lines.Add($"{{\"type\":\"chargehand_cut\",\"bytes\":{Encoding.UTF8.GetByteCount(trimmed)}}}");
                cut++;
                continue;
            }
            var (redacted, found) = DiffScan.Redact(trimmed, literals);
            lines.Add(redacted);
            kinds.UnionWith(found);
        }
        return new TranscriptView(lines, truncated, raw.Length, [.. kinds.Order(StringComparer.Ordinal)], cut);
    }
}
