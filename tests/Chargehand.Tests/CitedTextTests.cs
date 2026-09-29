using Chargehand.Contracts;
using Chargehand.Runtime;
using Chargehand.Verification;

namespace Chargehand.Tests;

/// <summary>Goal 0.8 (ADR 0036): the text each claim cites, for the support check.</summary>
public class CitedTextTests
{
    private static ResultContract Contract(Claim[] claims, params Evidence[] evidence) =>
        new("result/v1", "r", "n", new string('0', 32), new PromptChain([], new AsSent("2.0.16", "build", "p/m", "2026-09-30")), ResultStatus.Completed, "s", claims, evidence, [], [], 0.9,
            new Usage(0, 0, 0, 0, 0));

    private static EvidenceScope Scope(RepositoryRef repo, IReadOnlyList<FileDiff>? diff = null, string inputs = "") =>
        new(repo.Path, repo.Commit, new HashSet<string>(), new HashSet<string>(), inputs, diff ?? [], null, inputs);

    private static RepositoryRef Repo(TempDir dir, string file, string content)
    {
        var repo = Runs.GitRepo(dir.Path);
        dir.Write(Path.Combine("repo", file), content);
        return Runs.Commit(repo, file);
    }

    [Fact]
    public async Task A_file_citation_gives_the_cited_lines_with_their_numbers()
    {
        using var dir = new TempDir();
        var repo = Repo(dir, "a.txt", "one\ntwo\nthree\nfour\n");
        var claim = new Claim("It says two and three.", ["e1"], 0.9);
        var cited = await CitedText.ForAsync(Contract([claim], new Evidence("e1", EvidenceKind.File, "a.txt:2-3")), Scope(repo), default);
        var c = Assert.Single(cited);
        Assert.Equal(0, c.Index);
        Assert.Contains("[e1] file a.txt:2-3", c.Text, StringComparison.Ordinal);
        Assert.Contains("2: two", c.Text, StringComparison.Ordinal);
        Assert.Contains("3: three", c.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("four", c.Text, StringComparison.Ordinal);
        Assert.False(c.Truncated);
    }

    [Fact]
    public async Task A_range_past_the_end_returns_what_exists()
    {
        using var dir = new TempDir();
        var repo = Repo(dir, "a.txt", "one\ntwo\n");
        var cited = await CitedText.ForAsync(Contract([new Claim("c", ["e1"], 0.9)], new Evidence("e1", EvidenceKind.File, "a.txt:2-9")), Scope(repo), default);
        Assert.Contains("2: two", Assert.Single(cited).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_huge_citation_is_cut_to_200_lines()
    {
        using var dir = new TempDir();
        var repo = Repo(dir, "big.txt", string.Join("\n", Enumerable.Range(1, 5000).Select(i => $"line {i}")) + "\n");
        var cited = await CitedText.ForAsync(Contract([new Claim("c", ["e1"], 0.9)], new Evidence("e1", EvidenceKind.File, "big.txt:1-5000")), Scope(repo), default);
        var c = Assert.Single(cited);
        Assert.True(c.Truncated);
        Assert.Contains("200: line 200", c.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("201: line 201", c.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_input_citation_gives_that_inputs_text_only()
    {
        using var dir = new TempDir();
        var repo = Runs.GitRepo(dir.Path);
        const string inputs = "- id \"goal\" (goal): Make it faster.\nSecond line.\n- id \"diff\" (diff): +added line";
        var cited = await CitedText.ForAsync(Contract([new Claim("c", ["e1"], 0.9)], new Evidence("e1", EvidenceKind.Input, "goal")), Scope(repo, inputs: inputs), default);
        var c = Assert.Single(cited);
        Assert.Contains("Make it faster.", c.Text, StringComparison.Ordinal);
        Assert.Contains("Second line.", c.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("added line", c.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_diff_citation_gives_the_overlapping_hunk_only()
    {
        using var dir = new TempDir();
        var repo = Runs.GitRepo(dir.Path);
        const string patch = "@@ -1,2 +1,2 @@\n a\n-b\n+B\n@@ -40,2 +40,2 @@\n x\n-y\n+Y\n";
        var diff = new[] { new FileDiff("f.txt", patch, 2, 2, "modified") };
        var cited = await CitedText.ForAsync(Contract([new Claim("c", ["e1"], 0.9)], new Evidence("e1", EvidenceKind.Diff, "f.txt:40-41")), Scope(repo, diff), default);
        var text = Assert.Single(cited).Text;
        Assert.Contains("+Y", text, StringComparison.Ordinal);
        Assert.DoesNotContain("+B", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claims_that_cite_only_a_url_or_a_commit_are_left_out_and_two_citations_are_both_included()
    {
        using var dir = new TempDir();
        var repo = Repo(dir, "a.txt", "one\ntwo\n");
        var contract = Contract(
            [new Claim("url only", ["e1"], 0.5), new Claim("two files", ["e2", "e3"], 0.9)],
            new Evidence("e1", EvidenceKind.Url, "https://example.com"), new Evidence("e2", EvidenceKind.File, "a.txt:1"), new Evidence("e3", EvidenceKind.File, "README.md:1"));
        var cited = await CitedText.ForAsync(contract, Scope(repo), default);
        var c = Assert.Single(cited);
        Assert.Equal(1, c.Index);
        Assert.Contains("[e2]", c.Text, StringComparison.Ordinal);
        Assert.Contains("[e3]", c.Text, StringComparison.Ordinal);
    }
}
