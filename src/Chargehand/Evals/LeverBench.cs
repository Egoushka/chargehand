using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Config;

namespace Chargehand.Evals;

/// <summary>
/// One token-saving lever (issue #780), applied to a copy of the base's <c>prompts/</c> and <c>presets/</c>:
/// <c>prompt</c> replaces a block's text with <see cref="File"/> (prompt language), <c>effort</c> sets every node kind's
/// model variant to <see cref="Value"/> (the output and thinking budget the runtime gives the model), and <c>compaction</c>
/// sets <c>trigger_tokens</c> to <see cref="Value"/> where a preset compacts (context trimming).
/// </summary>
/// <param name="Block">prompt: the block to replace, e.g. <c>core/worker</c>.</param>
/// <param name="File">prompt: the replacement text, relative to the lever file's directory; its own front matter is dropped.</param>
public sealed record Lever(string Id, string Kind, string? Block = null, string? File = null, string? Value = null, string? Note = null)
{
    public static IReadOnlyList<Lever> Load(string path) => JsonSerializer.Deserialize<LeverFile>(System.IO.File.ReadAllText(path), Profile.Json)!.Levers;

    private sealed record LeverFile(IReadOnlyList<Lever> Levers);
}

/// <summary>One lever on one cell: paired runs of the base and the lever, tokens and pass rate of each, and the gate's verdict.</summary>
/// <param name="Skipped">Why the lever did not run on the cell (it changes none of the cell's files), else null.</param>
public sealed record LeverRow(string Cell, string? Preset, string Lever, int Items, long? BaseTokens, long? LeverTokens, double BasePass, double LeverPass,
    Verdict? Verdict, string? Skipped)
{
    /// <summary>Lever tokens over base tokens, minus 1; null when either arm reported none.</summary>
    public double? TokenChange => BaseTokens is > 0 && LeverTokens is { } l ? (double)l / BaseTokens.Value - 1 : null;

    /// <summary>Worth keeping on this cell: it saves tokens and the cell's gate passes it.</summary>
    public bool Keep => Skipped is null && TokenChange < 0 && Verdict is { Blocked: false, Items: > 0 };
}

/// <summary>
/// Measures levers over the eval cells (issue #780): each lever becomes a change root next to the base, and
/// <see cref="EvalRunner"/> pairs every item of a cell under both, as Prompt CI does. A lever that touches none of a
/// cell's files is skipped for it, not run.
/// </summary>
public static partial class LeverBench
{
    public const double DefaultPassQuality = 0.7;

    public static async Task<IReadOnlyList<LeverRow>> RunAsync(EvalRunner runner, IReadOnlyList<EvalCell> cells, IReadOnlyList<Lever> levers, string leverDirectory,
        Func<EvalCell, Task<IReadOnlyList<EvalItem>>> items, string baseRoot, string workRoot, string name, double passQuality, CancellationToken ct)
    {
        var rows = new List<LeverRow>();
        foreach (var lever in levers)
        {
            var root = Apply(lever, baseRoot, Path.Combine(workRoot, lever.Id), leverDirectory);
            foreach (var cell in cells)
            {
                if (!cell.Files.Any(f => Changed(baseRoot, root, f)))
                {
                    rows.Add(new LeverRow(cell.Name, cell.Preset, lever.Id, 0, null, null, 0, 0, null, $"changes none of {string.Join(", ", cell.Files)}"));
                    continue;
                }
                var (_, pairs) = await runner.PairsAsync(cell, await items(cell), baseRoot, root, $"{name}-{lever.Id}", ct);
                rows.Add(Row(cell, lever.Id, pairs, passQuality));
            }
        }
        return rows;
    }

    public static LeverRow Row(EvalCell cell, string lever, IReadOnlyList<Pair> pairs, double passQuality)
    {
        double Rate(Func<Pair, double> quality) => pairs.Count == 0 ? 0 : pairs.Count(p => quality(p) >= passQuality) / (double)pairs.Count;
        long? Sum(Func<Pair, long?> tokens) => pairs.Any(p => tokens(p) is null) || pairs.Count == 0 ? null : pairs.Sum(p => tokens(p)!.Value);
        return new LeverRow(cell.Name, cell.Preset, lever, pairs.Count, Sum(p => p.BaseTokens), Sum(p => p.ChangeTokens), Rate(p => p.BaseQuality),
            Rate(p => p.ChangeQuality), Gate.Decide(cell, pairs, null), null);
    }

    /// <summary>Copies the base's prompts/ and presets/ to <paramref name="target"/> and applies the lever there.</summary>
    public static string Apply(Lever lever, string baseRoot, string target, string leverDirectory)
    {
        foreach (var dir in new[] { "prompts", "presets" })
            Copy(Path.Combine(baseRoot, dir), Path.Combine(target, dir));
        switch (lever.Kind)
        {
            case "prompt":
                var path = Path.Combine(target, "prompts", Need(lever, lever.Block, "block") + ".md");
                if (!System.IO.File.Exists(path))
                    throw new ArgumentException($"lever {lever.Id}: no prompt block {lever.Block}");
                var head = FrontMatter().Match(System.IO.File.ReadAllText(path).ReplaceLineEndings("\n")).Value;
                var text = System.IO.File.ReadAllText(Path.Combine(leverDirectory, Need(lever, lever.File, "file"))).ReplaceLineEndings("\n");
                System.IO.File.WriteAllText(path, head + text[FrontMatter().Match(text).Length..]);
                break;
            case "effort":
                var effort = Need(lever, lever.Value, "value");
                if (!Effort().IsMatch(effort))
                    throw new ArgumentException($"lever {lever.Id}: effort '{effort}' is not a lower-case word");
                Presets(target, yaml => ModelLine().Replace(yaml, m => $"{m.Groups["key"].Value}{m.Groups["model"].Value}#{effort}"));
                break;
            case "compaction":
                var tokens = long.Parse(Need(lever, lever.Value, "value"), CultureInfo.InvariantCulture);
                Presets(target, yaml => Trigger().Replace(yaml, m => $"{m.Groups["key"].Value}{tokens.ToString(CultureInfo.InvariantCulture)}"));
                break;
            default:
                throw new ArgumentException($"lever {lever.Id}: unknown kind '{lever.Kind}' (prompt, effort or compaction)");
        }
        return target;
    }

    private static string Need(Lever lever, string? value, string key) => value ?? throw new ArgumentException($"lever {lever.Id} ({lever.Kind}) needs {key}");

    private static bool Changed(string baseRoot, string root, string file)
    {
        var (a, b) = (Path.Combine(baseRoot, file), Path.Combine(root, file));
        return System.IO.File.Exists(b) && (!System.IO.File.Exists(a) || System.IO.File.ReadAllText(a) != System.IO.File.ReadAllText(b));
    }

    private static void Presets(string root, Func<string, string> edit)
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "presets"), "*.yaml"))
            System.IO.File.WriteAllText(path, edit(System.IO.File.ReadAllText(path)));
    }

    private static void Copy(string from, string to)
    {
        if (Directory.Exists(to))
            Directory.Delete(to, recursive: true);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            System.IO.File.Copy(file, dest);
        }
    }

    /// <summary>The table <c>eval levers</c> prints: one row per cell and lever.</summary>
    public static string Table(IReadOnlyList<LeverRow> rows)
    {
        var w = new StringWriter(CultureInfo.InvariantCulture);
        w.WriteLine("| cell | preset | lever | items | tokens, base → lever | token change | pass rate, base → lever | quality change | verdict | keep |");
        w.WriteLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
            w.WriteLine(r.Skipped is { } why
                ? $"| {r.Cell} | {r.Preset ?? "-"} | {r.Lever} | 0 | | | | | skipped: {why} | |"
                : FormattableString.Invariant($"| {r.Cell} | {r.Preset ?? "-"} | {r.Lever} | {r.Items} | {Tokens(r.BaseTokens)} → {Tokens(r.LeverTokens)} | {(r.TokenChange is { } c ? c.ToString("+0%;-0%", CultureInfo.InvariantCulture) : "n/a")} | {r.BasePass:0%} → {r.LeverPass:0%} | {r.Verdict!.QualityDelta:+0.000;-0.000} | {(r.Verdict.Blocked ? "BLOCK" : "pass")}: {r.Verdict.Reason} | {(r.Keep ? "yes" : "no")} |"));
        return w.ToString();
    }

    private static string Tokens(long? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "n/a";

    [GeneratedRegex(@"^---\n(?:.*\n)*?---\n")]
    private static partial Regex FrontMatter();

    [GeneratedRegex(@"^[a-z]+$")]
    private static partial Regex Effort();

    // Node kinds only: a model on its own indented line. The critic's inline map keeps its model.
    [GeneratedRegex(@"^(?<key>[ ]+model:[ ]*)(?<model>[^\s#]+/[^\s#]+)(#[a-z]+)?[ ]*$", RegexOptions.Multiline)]
    private static partial Regex ModelLine();

    [GeneratedRegex(@"(?<key>trigger_tokens:[ ]*)\d+")]
    private static partial Regex Trigger();
}
