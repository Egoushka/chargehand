using Xunit.Abstractions;

namespace Chargehand.Tests;

/// <summary>Docs as claims (#786), a ratchet. A behaviour sentence is a sentence of four or more words in a prose paragraph or list
/// item of docs/guide/*.md; front matter, headings, code blocks, tables, blockquotes and link-only lines are not. A sentence is cited
/// when its paragraph or list item holds a link or backticked path into src/ or tests/, or a backticked test name (`Type.Method`,
/// `SomeTests` or a snake_case `Method_name`), or a `cites:` / `cites-text:` HTML comment marker (the format shared with #785). Today's uncited sentences sit in guide-uncited.baseline.txt; a sentence not in it
/// fails. To accept the guide as it is, or after citing, run with CHARGEHAND_UPDATE_GUIDE_BASELINE=1.</summary>
public class GuideClaimsTests(ITestOutputHelper output)
{
    private const string Fixture = """
        ---
        title: "x"
        ---

        # Heading is skipped

        Cited sentence here with enough words, see [the code](../../src/Chargehand/Foo.cs).

        The server restarts every worker that stops answering. Second sentence also has no evidence at all.

        - A list item that names `FooTests.Bar_works` as its proof.
        - A list item that claims something and cites nothing.

        | a | b |
        |---|---|
        | table rows are skipped always | yes |

        ```
        code blocks are skipped for sure here
        ```

        > A quote is skipped as well by this rule.

        [Only a link line](x.md)

        Marked sentence cites through the shared marker. <!-- cites: src/Foo.cs#Foo -->

        Another marked sentence on its own line below.
        <!-- cites-text: src/Foo.cs "Bar = 4" -->

        Short one.
        """;

    [Fact]
    public void An_uncited_sentence_is_listed_and_cited_ones_are_not()
    {
        var found = GuideClaims.Uncited("fixture.md", Fixture.Split('\n').Select(l => l.TrimEnd('\r')).ToArray())
            .Select(c => c.Sentence).ToList();
        Assert.Equal(
        [
            "The server restarts every worker that stops answering.",
            "Second sentence also has no evidence at all.",
            "A list item that claims something and cites nothing.",
        ], found);
    }

    [Fact]
    public void A_new_uncited_sentence_is_not_in_the_baseline()
    {
        var old = GuideClaims.Uncited("fixture.md", ["An old claim with no evidence."]);
        var baseline = GuideClaims.ReadBaseline(old.Select(c => c.BaselineLine));
        var fresh = GuideClaims.Uncited("fixture.md", ["An old claim with no evidence.", "", "A brand new claim with no evidence."]);
        var added = fresh.Where(c => !baseline.Contains(c.Key)).ToList();
        Assert.Equal("A brand new claim with no evidence.", Assert.Single(added).Sentence);
    }

    [Fact]
    public void The_guide_has_no_uncited_sentence_beyond_the_baseline()
    {
        var all = Directory.EnumerateFiles(Repo.Path("docs", "guide"), "*.md").Order(StringComparer.Ordinal)
            .SelectMany(f => GuideClaims.Uncited(Path.GetFileName(f), File.ReadAllLines(f))).ToList();
        var baselinePath = Repo.Path("tests", "Chargehand.Tests", "guide-uncited.baseline.txt");
        var line = $"docs/guide: {all.Count} uncited behaviour sentences (baseline ratchet, #786)";
        output.WriteLine(line);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
            File.AppendAllText(summary, line + "\n");

        if (Environment.GetEnvironmentVariable("CHARGEHAND_UPDATE_GUIDE_BASELINE") == "1")
        {
            File.WriteAllLines(baselinePath, all.Select(c => c.BaselineLine));
            return;
        }

        var baseline = GuideClaims.ReadBaseline(File.ReadAllLines(baselinePath));
        var added = all.Where(c => !baseline.Contains(c.Key)).ToList();
        Assert.True(added.Count == 0,
            $"{added.Count} new uncited sentence(s) in docs/guide: cite code or a test (a link or backticked path into src/ or tests/) in the same paragraph or list item.\n"
            + string.Join('\n', added.Select(c => $"  {c.File}: {c.Sentence}")));
    }
}
