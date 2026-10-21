namespace Chargehand.Kernel.Tests;

/// <summary>The trailers a run puts on its commits (ADR 0070): the format, where they go in a message, and that they read back.</summary>
public class ProvenanceTests
{
    private static readonly Provenance Full = new("r-42", "CHARGEHAND-7", "claude-code 2.1.0", "sonnet", 0.4m, 183400, 61, "tests passed; 2 criteria passed", "ab12");

    [Fact]
    public void The_lines_follow_the_documented_order_and_shape() =>
        Assert.Equal(
        [
            "Chargehand-Run: r-42",
            "Chargehand-Task: CHARGEHAND-7",
            "Chargehand-Runtime: claude-code 2.1.0, sonnet",
            "Chargehand-Spend: $0.40, 183400 tokens, 61% cache",
            "Chargehand-Checks: tests passed; 2 criteria passed",
            "Chargehand-Result: sha256:ab12",
        ], Full.Lines());

    [Fact]
    public void A_field_that_is_not_known_has_no_line() =>
        Assert.Equal(["Chargehand-Run: r-1", "Chargehand-Spend: 120 tokens"], new Provenance("r-1", Tokens: 120).Lines());

    [Fact]
    public void A_value_with_a_newline_cannot_break_the_block() =>
        Assert.Equal(["Chargehand-Run: r-1", "Chargehand-Checks: ok Chargehand-Result: sha256:forged"], new Provenance("r-1", Checks: "ok\nChargehand-Result: sha256:forged").Lines());

    [Fact]
    public void A_message_without_trailers_gets_a_paragraph_of_them()
    {
        var message = Provenance.Apply("feat: add a thing\n\nWhy it matters.\n", new Provenance("r-1"));
        Assert.Equal("feat: add a thing\n\nWhy it matters.\n\nChargehand-Run: r-1\n", message);
    }

    [Fact]
    public void A_subject_only_message_is_not_taken_for_a_trailer_block() =>
        Assert.Equal("fix: Foo: bar\n\nChargehand-Run: r-1\n", Provenance.Apply("fix: Foo: bar", new Provenance("r-1")));

    [Fact]
    public void The_trailers_go_before_Co_Authored_By()
    {
        var message = Provenance.Apply("feat: x\n\nbody\n\nSigned-off-by: A <a@b.c>\nCo-Authored-By: Claude <noreply@anthropic.com>\n", new Provenance("r-1", Usd: 1m));
        Assert.Equal("feat: x\n\nbody\n\nSigned-off-by: A <a@b.c>\nChargehand-Run: r-1\nChargehand-Spend: $1.00\nCo-Authored-By: Claude <noreply@anthropic.com>\n", message);
    }

    [Fact]
    public void Applying_twice_changes_nothing()
    {
        var once = Provenance.Apply("feat: x\n\nbody", Full);
        Assert.Equal(once, Provenance.Apply(once, new Provenance("r-other")));
    }

    [Fact]
    public void The_trailers_read_back()
    {
        var read = Provenance.Read(Provenance.Apply("feat: x\n\nbody\n\nCo-Authored-By: C <c@d.e>", Full));
        Assert.Equal("r-42", read[Provenance.RunKey]);
        Assert.Equal("$0.40, 183400 tokens, 61% cache", read[Provenance.SpendKey]);
        Assert.Equal("sha256:ab12", read[Provenance.ResultKey]);
        Assert.False(read.ContainsKey("Co-Authored-By"));
    }

    [Fact]
    public void A_human_commit_reads_as_no_trailers() =>
        Assert.Empty(Provenance.Read("fix: a bug\n\nChargehand-Run: only in the body, not a block\nand more prose\n"));

    [Fact]
    public void Collect_keeps_one_block_per_run_in_commit_order()
    {
        var a1 = Provenance.Apply("feat: a", new Provenance("r1", Usd: 1m));
        var a2 = Provenance.Apply("fix: a2", new Provenance("r1", Usd: 1m));
        var b = Provenance.Apply("fix: ci", new Provenance("r2", Tokens: 5));

        Assert.Equal("Chargehand-Run: r1\nChargehand-Spend: $1.00\nChargehand-Run: r2\nChargehand-Spend: 5 tokens", Provenance.Collect([a1, "human commit", a2, b]));
        Assert.Null(Provenance.Collect(["human commit"]));
    }
}
