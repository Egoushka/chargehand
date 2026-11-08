using System.Text;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Chargehand.Tests;

/// <summary>Docs as claims (issue 786): every behaviour sentence in docs/guide cites the code or test it rests on. A sentence
/// cites when it links to a file or directory of the repository outside docs/ and the top-level Markdown files, or names
/// one in an inline code span (`src/...`). Prose sentences count as behaviour sentences; headings, front matter, code
/// blocks, tables, HTML comments and lead-ins ending in a colon do not. The sentences that cited nothing when the check
/// landed are listed in tests/Chargehand.Tests/DocClaims.uncited.txt: a sentence not on that list fails the check, and so does a listed one that
/// now cites or is gone, so the list only shrinks. CHARGEHAND_UPDATE_UNCITED=1 rewrites the list from the guide.</summary>
public partial class DocClaimsTests(ITestOutputHelper output)
{
    private static readonly string BaselinePath = Repo.Path("tests", "Chargehand.Tests", "DocClaims.uncited.txt");

    private static readonly string[] CodeRoots =
        ["src", "tests", "schemas", "scripts", "presets", "plugins", "prompts", "profiles", "deploy", "samples", "web",
         "evals", "benchmarks", "images", ".github", ".githooks"];

    internal sealed record Sentence(string File, int Line, string Text, bool Cited)
    {
        public string Key => $"{File}: {Text}";
    }

    [Fact]
    public void No_new_uncited_behaviour_sentence_in_the_guide()
    {
        var sentences = Directory.GetFiles(Repo.Path("docs", "guide"), "*.md").Order(StringComparer.Ordinal)
            .SelectMany(path => Read(Path.GetFileName(path), File.ReadAllLines(path))).ToList();
        var uncited = sentences.Where(s => !s.Cited).ToList();
        var keys = uncited.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        Report(sentences.Count, uncited.Count);

        if (Environment.GetEnvironmentVariable("CHARGEHAND_UPDATE_UNCITED") == "1")
        {
            File.WriteAllLines(BaselinePath, Header.Concat(keys.Order(StringComparer.Ordinal)));
            return;
        }

        var baseline = File.ReadAllLines(BaselinePath).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
        var added = uncited.Where(s => !baseline.Contains(s.Key)).Select(s => $"{s.File}:{s.Line}: {s.Text}").ToList();
        var stale = baseline.Where(k => !keys.Contains(k)).ToList();
        Assert.True(added.Count == 0,
            "These sentences in docs/guide cite no code or test. Link each to the file that shows the behaviour " +
            "(for example [the check](../../tests/Chargehand.Tests/DocClaimsTests.cs)):\n" + string.Join('\n', added));
        Assert.True(stale.Count == 0,
            "These lines of DocClaims.uncited.txt now cite or are gone; delete them (or run the tests with " +
            "CHARGEHAND_UPDATE_UNCITED=1):\n" + string.Join('\n', stale));
    }

    [Fact]
    public void A_sentence_cites_through_a_link_into_the_code_or_an_inline_path()
    {
        string[] page =
        [
            "---", "title: \"x\"", "---", "# Heading", "",
            "The runner retries twice ([Retry](../../src/Chargehand/Chargehand.csproj)). It then gives up.",
            "See [the ADR](../adr/0060-rewrite.md) for why.", "", "- `tests/Chargehand.Tests/Repo.cs` finds the root.",
            "- A link to [a missing file](../../src/nothing-here.cs) cites nothing.", "", "```sh", "echo not prose.", "```",
            "| a | b |", "|---|---|", "", "The steps:",
        ];
        var read = Read("x.md", page).Select(s => (s.Text, s.Cited)).ToList();
        Assert.Equal(
        [
            ("The runner retries twice ([Retry](../../src/Chargehand/Chargehand.csproj)).", true),
            ("It then gives up.", false),
            ("See [the ADR](../adr/0060-rewrite.md) for why.", false),
            ("`tests/Chargehand.Tests/Repo.cs` finds the root.", true),
            ("A link to [a missing file](../../src/nothing-here.cs) cites nothing.", false),
        ], read);
    }

    private static readonly string[] Header =
    [
        "# Sentences in docs/guide that cite no code or test (DocClaimsTests). The list only shrinks: cite a sentence",
        "# and delete its line here. A new sentence must cite; it is never added to this list by hand.",
    ];

    private void Report(int total, int uncited)
    {
        var line = $"docs/guide: {uncited} of {total} behaviour sentences cite no code or test.";
        output.WriteLine(line);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
            File.AppendAllText(summary, $"### Docs as claims\n\n{line}\n\n");
    }

    internal static IEnumerable<Sentence> Read(string file, string[] lines)
    {
        foreach (var (start, text) in Blocks(lines))
            foreach (var sentence in Split(text))
                if (IsBehaviour(sentence))
                    yield return new Sentence(file, start, sentence, Cites(sentence));
    }

    private static IEnumerable<(int Line, string Text)> Blocks(string[] lines)
    {
        var i = 0;
        if (lines.Length > 0 && lines[0] == "---")
            for (i = 1; i < lines.Length && lines[i++] != "---";) { }
        var block = new StringBuilder();
        var start = 0;
        for (; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++) { }
            else if (line.StartsWith("<!--", StringComparison.Ordinal))
                for (; i < lines.Length && !lines[i].Contains("-->", StringComparison.Ordinal); i++) { }
            var item = ListItem().Match(line);
            var breaks = line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith('#')
                || line.StartsWith('|') || line.StartsWith("<!--", StringComparison.Ordinal) || item.Success;
            if (breaks && block.Length > 0)
            {
                yield return (start, block.ToString());
                block.Clear();
            }
            if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith('#')
                || line.StartsWith('|') || line.StartsWith("<!--", StringComparison.Ordinal))
                continue;
            if (block.Length == 0)
                start = i + 1;
            else
                block.Append(' ');
            block.Append(item.Success ? line[item.Length..] : line.TrimStart('>', ' '));
        }
        if (block.Length > 0)
            yield return (start, block.ToString());
    }

    /// <summary>Splits after . ! or ? followed by a space, outside inline code, link text and link targets.</summary>
    private static IEnumerable<string> Split(string text)
    {
        var code = false;
        var depth = 0;
        var from = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '`')
                code = !code;
            else if (!code && c is '[' or '(')
                depth++;
            else if (!code && c is ']' or ')' && depth > 0)
                depth--;
            else if (!code && depth == 0 && c is '.' or '!' or '?' && i + 1 < text.Length && text[i + 1] == ' '
                && (i + 2 >= text.Length || !char.IsLower(text[i + 2])))
            {
                yield return Normal(text[from..(i + 1)]);
                from = i + 2;
            }
        }
        if (from < text.Length)
            yield return Normal(text[from..]);
    }

    private static string Normal(string sentence) => Spaces().Replace(sentence, " ").Trim();

    private static bool IsBehaviour(string sentence) =>
        sentence.Split(' ').Length >= 3 && !sentence.EndsWith(':');

    private static bool Cites(string sentence) =>
        Link().Matches(sentence).Any(m => IsCode(Resolve(m.Groups[1].Value)))
        || InlinePath().Matches(sentence).Any(m => IsCode(m.Groups[1].Value));

    private static string Resolve(string target)
    {
        var path = target.Split('#')[0];
        if (path.Length == 0 || path.Contains("://", StringComparison.Ordinal))
            return "";
        var full = Path.GetFullPath(Path.Combine(Repo.Path("docs", "guide"), path));
        return Path.GetRelativePath(Repo.Root, full).Replace('\\', '/');
    }

    private static bool IsCode(string relative)
    {
        var path = relative.TrimEnd('/');
        var root = path.Split('/')[0];
        return CodeRoots.Contains(root) && path.Contains('/')
            && (File.Exists(Repo.Path(path)) || Directory.Exists(Repo.Path(path)));
    }

    [GeneratedRegex(@"^(?:[-*+]|\d+\.)\s+")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\]\(([^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"`([A-Za-z0-9_.\-]+/[^`\s]+)`")]
    private static partial Regex InlinePath();
}
