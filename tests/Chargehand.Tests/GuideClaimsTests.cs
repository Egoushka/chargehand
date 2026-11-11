using Xunit.Abstractions;

namespace Chargehand.Tests;

/// <summary>Docs as claims (#786), a ratchet. A behaviour sentence is a sentence of four or more words in a prose paragraph or list
/// item of docs/guide/**/*.md; front matter, headings, code blocks, tables, blockquotes, HTML block tags and link-only lines are not.
/// Citations are checked by pattern and path existence, not for truth, and per paragraph or list item: every sentence in a block is
/// cited when the block holds a link or backticked path into src/ or tests/ that exists, a backticked test class or method that exists
/// under tests/, or a `cites:` / `cites-text:` HTML comment marker whose path exists (the format shared with #785).
/// Today's uncited sentences sit in guide-uncited.baseline.txt as a multiset; a sentence beyond it fails. To accept the guide as it
/// is, or after citing, run locally with CHARGEHAND_UPDATE_GUIDE_BASELINE=1 (refused when CI is set).</summary>
public class GuideClaimsTests(ITestOutputHelper output)
{
    private static readonly GuideEvidence Stub = new(p => p == "src/Foo.cs", t => t == "FooTests.Bar_works");

    private const string Fixture = """
        ---
        title: "x"
        ---

        # Heading is skipped

        Cited sentence here with enough words, see [the code](../../src/Foo.cs#L3).

        A link to nowhere does not cite, see [the code](../../src/Gone.cs).

        The server restarts every worker that stops answering. Second sentence also has no evidence at all.

        - A list item that names `FooTests.Bar_works` as its proof.
        - A list item that claims something and cites nothing.
        - A list item that prints `Console.WriteLine` and is not a test.

        | a | b |
        |---|---|
        | table rows are skipped always | yes |

        ````
        ```
        code blocks are skipped for sure here
        ```
        still inside the longer fence here
        ````

        > A quote is skipped as well by this rule.

        [Only a link line](x.md)

        <kbd>Ctrl</kbd> starts an inline tag line without a citation.

        Marked sentence cites through the shared marker. <!-- cites: src/Foo.cs#Foo -->

        Another marked sentence on its own line below.
        <!-- cites-text: src/Foo.cs "Bar = 4" -->

        A marker to a missing path cites nothing here. <!-- cites: src/Gone.cs#Foo -->

        Short one.
        """;

    private static string[] Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public void An_uncited_sentence_is_listed_and_cited_ones_are_not()
    {
        var found = GuideClaims.Uncited("fixture.md", Lines(Fixture), Stub).Select(c => c.Sentence).ToList();
        Assert.Equal(
        [
            "A link to nowhere does not cite, see [the code](../../src/Gone.cs).",
            "The server restarts every worker that stops answering.",
            "Second sentence also has no evidence at all.",
            "A list item that claims something and cites nothing.",
            "A list item that prints `Console.WriteLine` and is not a test.",
            "<kbd>Ctrl</kbd> starts an inline tag line without a citation.",
            "A marker to a missing path cites nothing here.",
        ], found);
    }

    [Fact]
    public void A_file_without_front_matter_counts_its_first_line()
    {
        var found = GuideClaims.Uncited("fixture.md", ["The very first line claims something.", "", "# Title"], Stub);
        Assert.Single(found);
    }

    [Fact]
    public void Front_matter_with_trailing_spaces_is_skipped()
    {
        var found = GuideClaims.Uncited("fixture.md", ["--- ", "title: \"a title with many words\"", "---  ", "", "Body claims something here."], Stub);
        Assert.Equal("Body claims something here.", Assert.Single(found).Sentence);
    }

    [Fact]
    public void A_new_uncited_sentence_is_not_in_the_baseline()
    {
        var old = GuideClaims.Uncited("fixture.md", ["# T", "", "An old claim with no evidence."], Stub);
        Assert.NotEmpty(old);
        var baseline = GuideClaims.ReadBaseline(old.Select(c => c.BaselineLine));
        var fresh = GuideClaims.Uncited("fixture.md", ["# T", "", "An old claim with no evidence.", "", "A brand new claim with no evidence."], Stub);
        var added = GuideClaims.Added(fresh, baseline);
        Assert.Equal("A brand new claim with no evidence.", Assert.Single(added).Sentence);
    }

    [Fact]
    public void A_third_copy_of_a_baselined_sentence_fails()
    {
        var two = GuideClaims.Uncited("f.md", ["# T", "", "The same claim without evidence.", "", "The same claim without evidence."], Stub);
        Assert.Equal(2, two.Count);
        var baseline = GuideClaims.ReadBaseline(two.Select(c => c.BaselineLine));
        var three = GuideClaims.Uncited("f.md", ["# T", "", "The same claim without evidence.", "", "The same claim without evidence.", "", "The same claim without evidence."], Stub);
        Assert.Single(GuideClaims.Added(three, baseline));
        Assert.Equal(1, GuideClaims.Stale([], GuideClaims.ReadBaseline(two.Take(1).Select(c => c.BaselineLine))));
    }

    [Fact]
    public void The_guide_has_no_uncited_sentence_beyond_the_baseline()
    {
        var root = Repo.Path("docs", "guide");
        var all = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal)
            .SelectMany(f => GuideClaims.Uncited(f, File.ReadAllLines(Path.Combine(root, f)))).ToList();
        var baselinePath = Repo.Path("tests", "Chargehand.Tests", "guide-uncited.baseline.txt");

        if (Environment.GetEnvironmentVariable("CHARGEHAND_UPDATE_GUIDE_BASELINE") == "1")
        {
            Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")),
                "CHARGEHAND_UPDATE_GUIDE_BASELINE=1 is set while CI is set: regenerate the baseline locally, never in CI.");
            File.WriteAllLines(baselinePath, all.Select(c => c.BaselineLine));
            return;
        }

        var baseline = GuideClaims.ReadBaseline(File.ReadAllLines(baselinePath));
        var added = GuideClaims.Added(all, baseline);
        var stale = GuideClaims.Stale(all, baseline);
        var line = $"docs/guide: {all.Count} uncited behaviour sentences, {stale} stale baseline entries (baseline ratchet, #786)";
        output.WriteLine(line);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
            File.AppendAllText(summary, line + "\n");

        Assert.True(added.Count == 0,
            $"{added.Count} new uncited sentence(s) in docs/guide: cite code or a test (a link or backticked path into src/ or tests/ that exists) in the same paragraph or list item.\n"
            + string.Join('\n', added.Select(c => $"  {c.File}: {c.Sentence}")));
    }
}
