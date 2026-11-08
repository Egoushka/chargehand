using Chargehand.Modules.Watch.Api;

namespace Chargehand.Modules.Watch.Tests;

/// <summary>The acceptance checks of the claim-drift watch (docs/specs/2026-11-08-claim-drift-watch-design.md).</summary>
public sealed class ClaimDriftTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claim-drift-" + Guid.NewGuid().ToString("N"));

    public ClaimDriftTests()
    {
        Write("src/Parser.cs", "namespace App;\n\npublic class Parser\n{\n    public int Parse(string s) => s.Length;\n}\n");
        Write("tests/ParserTests.cs", "public class ParserTests\n{\n    [Fact]\n    public void Parses() { }\n}\n");
        Write("scripts/smoke.sh", "#!/bin/sh\necho ok\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Claims_that_match_the_code_report_nothing()
    {
        Write("docs/guide/parse.md", "# Parse\n\nParsing works: [ParserTests](../../tests/ParserTests.cs), [`Parser`](../../src/Parser.cs#L3-L6), [smoke.sh](../../scripts/smoke.sh).\n");

        Assert.Equal(3, ClaimDrift.Citations(_root, "docs/guide").Count);
        Assert.Empty(ClaimDrift.Scan(_root));
    }

    [Fact]
    public void A_seeded_stale_claim_is_reported_with_its_doc_line()
    {
        Write("docs/guide/parse.md", "# Parse\n\nFine: [ParserTests](../../tests/ParserTests.cs).\n\nStale: [LexerTests](../../tests/LexerTests.cs).\n");

        var finding = Assert.Single(ClaimDrift.Scan(_root));

        Assert.Equal("docs/guide/parse.md", finding.Citation.Doc);
        Assert.Equal(5, finding.Citation.Line);
        Assert.Equal("tests/LexerTests.cs", finding.Citation.Target);
        Assert.Equal("docs/guide/parse.md:5: tests/LexerTests.cs: the cited file does not exist", finding.ToString());
    }

    [Fact]
    public void A_symbol_the_file_no_longer_contains_is_reported()
    {
        Write("docs/guide/parse.md", "Tokens: [`Tokenizer`](../../src/Parser.cs).\n");

        var finding = Assert.Single(ClaimDrift.Scan(_root));

        Assert.Contains("`Tokenizer`", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_anchor_past_the_end_of_the_file_is_reported()
    {
        Write("docs/guide/parse.md", "Parse: [Parser.cs](../../src/Parser.cs#L40-L42).\n");

        var finding = Assert.Single(ClaimDrift.Scan(_root));

        Assert.Contains("40-42", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_outside_code_and_tests_and_inside_code_fences_are_not_claims()
    {
        Write("docs/guide/parse.md", "[ADR](../adr/0001.md), [web](https://example.com/src/x.cs)\n\n```md\n[Gone](../../src/Gone.cs)\n```\n");

        Assert.Empty(ClaimDrift.Citations(_root, "docs/guide"));
    }

    [Fact]
    public void The_issue_for_a_broken_claim_names_the_line()
    {
        Write("docs/guide/parse.md", "\n[LexerTests](../../tests/LexerTests.cs#L3)\n");

        var (title, body) = ClaimDrift.Issue(Assert.Single(ClaimDrift.Scan(_root)));

        Assert.Equal("docs: stale claim at docs/guide/parse.md:2", title);
        Assert.Contains("`tests/LexerTests.cs#L3`", body, StringComparison.Ordinal);
        Assert.Contains("the cited file does not exist", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_guide_of_this_repository_has_no_stale_claims() =>
        Assert.Empty(ClaimDrift.Scan(RepositoryRoot()).Select(f => f.ToString()));

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Chargehand.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Chargehand.slnx not found above the test output.");
    }
}
