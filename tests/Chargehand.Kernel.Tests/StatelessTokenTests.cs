namespace Chargehand.Kernel.Tests;

/// <summary>Since 2026-10-02 a GitHub App installation token is gh + s_ + app id + _ + a JWT-shaped body (390 to 520 characters, with
/// '_', '-' and '.' inside). A pattern that stops at the first underscore leaves the whole token in the text (landing stage 0, item 2).</summary>
public sealed class StatelessTokenTests
{
    // Synthetic: the real shape, no real value.
    private static string Token(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";
        var prefix = "gh" + "s_5298046_";
        var body = length - prefix.Length;
        string Part(int n, int seed) => new([.. Enumerable.Range(0, n).Select(i => alphabet[(i * 7 + seed * 13 + i / 5) % alphabet.Length])]);
        return prefix + Part(36, 1) + "." + Part(body - 38 - 90, 2) + "." + Part(90, 3);
    }

    [Theory]
    [InlineData(390)]
    [InlineData(520)]
    public void DiffScan_redacts_the_whole_stateless_token(int length)
    {
        var token = Token(length);
        var (text, kinds) = DiffScan.Redact($"{{\"cmd\":\"export GH_TOKEN={token} && gh pr list\"}}", []);
        Assert.Equal("{\"cmd\":\"export GH_TOKEN=[redacted:github-token] && gh pr list\"}", text);
        Assert.Contains("github-token", kinds);
    }

    [Fact]
    public void DiffScan_flags_a_stateless_token_added_in_a_diff()
    {
        var diff = "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -0,0 +1 @@\n+" + Token(520) + "\n";
        Assert.Contains(new ScanFinding("github-token", "f.txt"), DiffScan.Scan(diff, [], ["f.txt"]));
    }

    [Fact]
    public void A_classic_token_is_still_redacted_and_short_prose_is_not()
    {
        Assert.Equal("[redacted:github-token]", DiffScan.Redact("gh" + "p_" + new string('a', 36), []).Text);
        const string prose = "the ghs_total_count_of_things column";
        Assert.Equal(prose, DiffScan.Redact(prose, []).Text);
    }

    [Theory]
    [InlineData(390)]
    [InlineData(520)]
    public void Scrub_leaves_no_tail_of_a_stateless_token_after_a_bearer_scheme(int length)
    {
        var scrubbed = Redaction.Scrub("GitHub answered 401 to Authorization: Bearer " + Token(length) + " for the request");
        Assert.Equal("GitHub answered 401 to Authorization: Bearer [redacted] for the request", scrubbed);
    }
}
