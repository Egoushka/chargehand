using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chargehand.Kernel;

/// <summary>Which chargehand run wrote a commit (ADR 0074), as git trailers: <c>git log --format='%(trailers:key=Chargehand-Run)'</c> reads them back.</summary>
/// <param name="RunId">The run that wrote the commit; for a driven session, the session's run id.</param>
/// <param name="Task">The task id or tracker reference, when there is one.</param>
/// <param name="Runtime">The runtime and its version, for example <c>opencode 1.4.2</c> or <c>claude-code 2.1.0</c>.</param>
/// <param name="Model">The model the run used.</param>
/// <param name="Usd">Priced dollars, or the dollar equivalent where the credential has no price; null when unknown.</param>
/// <param name="Tokens">Input plus output tokens.</param>
/// <param name="CachePercent">Cache reads as a share of the input side, whole percent.</param>
/// <param name="Checks">The verification and acceptance results in a few words.</param>
/// <param name="ResultSha256">Hex SHA-256 of the signed <c>result/v1</c>; only a result that exists before the commit can be named, so a run whose result cites its own commit leaves it out.</param>
public sealed partial record Provenance(string RunId, string? Task = null, string? Runtime = null, string? Model = null, decimal? Usd = null, long? Tokens = null, int? CachePercent = null,
    string? Checks = null, string? ResultSha256 = null)
{
    public const string RunKey = "Chargehand-Run";
    public const string TaskKey = "Chargehand-Task";
    public const string RuntimeKey = "Chargehand-Runtime";
    public const string SpendKey = "Chargehand-Spend";
    public const string ChecksKey = "Chargehand-Checks";
    public const string ResultKey = "Chargehand-Result";

    private const int MaxValue = 200;

    /// <summary>The trailer lines in their fixed order; a field that is not known has no line.</summary>
    public IReadOnlyList<string> Lines()
    {
        List<string> lines = [$"{RunKey}: {Clean(RunId)}"];
        if (Clean(Task) is { Length: > 0 } task)
            lines.Add($"{TaskKey}: {task}");
        var runtime = string.Join(", ", new[] { Clean(Runtime), Clean(Model) }.Where(v => v.Length > 0));
        if (runtime.Length > 0)
            lines.Add($"{RuntimeKey}: {runtime}");
        List<string> spend = [];
        if (Usd is { } usd)
            spend.Add(string.Create(CultureInfo.InvariantCulture, $"${usd:0.00}"));
        if (Tokens is { } tokens)
            spend.Add(string.Create(CultureInfo.InvariantCulture, $"{tokens} tokens"));
        if (CachePercent is { } cache)
            spend.Add(string.Create(CultureInfo.InvariantCulture, $"{cache}% cache"));
        if (spend.Count > 0)
            lines.Add($"{SpendKey}: {string.Join(", ", spend)}");
        if (Clean(Checks) is { Length: > 0 } checks)
            lines.Add($"{ChecksKey}: {checks}");
        if (Clean(ResultSha256) is { Length: > 0 } hash)
            lines.Add($"{ResultKey}: sha256:{hash}");
        return lines;
    }

    /// <summary>The lines as one block, for the end of a pull request body: GitHub's squash merge keeps a trailer only when it is in the message it is given.</summary>
    public string Block() => string.Join('\n', Lines());

    /// <summary>One line, no control characters, cut to <see cref="MaxValue"/>: a value never breaks the trailer block.</summary>
    private static string Clean(string? value)
    {
        if (value is null)
            return "";
        var text = Whitespace().Replace(value, " ").Trim();
        return text.Length <= MaxValue ? text : text[..(MaxValue - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// <paramref name="message"/> with the trailers after its body: in its last paragraph when that is a trailer block, before the first <c>Co-Authored-By</c> line there; otherwise in a new paragraph.
    /// A message that already has a <c>Chargehand-Run</c> trailer is returned as it is.
    /// </summary>
    public static string Apply(string message, Provenance provenance)
    {
        var rows = message.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').ToList();
        if (rows.Any(r => r.StartsWith(RunKey + ":", StringComparison.OrdinalIgnoreCase)))
            return string.Join('\n', rows) + "\n";
        var lines = provenance.Lines();
        var block = TrailerBlockStart(rows);
        if (block < 0)
        {
            rows.Add("");
            rows.AddRange(lines);
        }
        else if (rows.FindIndex(block, r => CoAuthor().IsMatch(r)) is var at and >= 0)
            rows.InsertRange(at, lines);
        else
            rows.AddRange(lines);
        return string.Join('\n', rows) + "\n";
    }

    /// <summary>The <c>Chargehand-*</c> trailers of a commit message, by key; empty for a commit no run wrote.</summary>
    public static IReadOnlyDictionary<string, string> Read(string message)
    {
        var rows = message.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').ToList();
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var block = TrailerBlockStart(rows);
        if (block < 0)
            return found;
        foreach (var row in rows.Skip(block))
            if (TrailerLine().Match(row) is { Success: true } m && m.Groups["k"].Value.StartsWith("Chargehand-", StringComparison.OrdinalIgnoreCase))
                found[m.Groups["k"].Value] = m.Groups["v"].Value.Trim();
        return found;
    }

    /// <summary>
    /// The trailer blocks of several commit messages as one: the lines of each run's first commit, in order, so a pull request that several runs wrote (a task and the CI fixes on its branch) keeps all of
    /// them in its squash commit. Null when no message has a <c>Chargehand-Run</c> trailer.
    /// </summary>
    public static string? Collect(IEnumerable<string> messages)
    {
        List<string> lines = [];
        HashSet<string> runs = [];
        foreach (var message in messages)
        {
            var rows = message.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').ToList();
            var block = TrailerBlockStart(rows);
            var ours = block < 0 ? [] : rows.Skip(block).Where(r => r.StartsWith("Chargehand-", StringComparison.OrdinalIgnoreCase)).ToList();
            if (ours.FirstOrDefault(r => r.StartsWith(RunKey + ":", StringComparison.OrdinalIgnoreCase)) is { } run && runs.Add(run))
                lines.AddRange(ours);
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    /// <summary>Index of the first row of the last paragraph when every row of it is a trailer (or a continuation of one) and a subject precedes it; -1 otherwise.</summary>
    private static int TrailerBlockStart(List<string> rows)
    {
        var blank = rows.FindLastIndex(r => r.Trim().Length == 0);
        if (blank < 1 || blank == rows.Count - 1)
            return -1;
        var start = blank + 1;
        return rows.Skip(start).All(r => TrailerLine().IsMatch(r) || (r.StartsWith(' ') && r.Trim().Length > 0)) && TrailerLine().IsMatch(rows[start]) ? start : -1;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(?<k>[A-Za-z][A-Za-z0-9-]*):[ \t]+(?<v>\S.*)$")]
    private static partial Regex TrailerLine();

    [GeneratedRegex(@"^Co-authored-by:", RegexOptions.IgnoreCase)]
    private static partial Regex CoAuthor();
}
