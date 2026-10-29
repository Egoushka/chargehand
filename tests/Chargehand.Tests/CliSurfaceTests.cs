namespace Chargehand.Tests;

/// <summary>Stability plan Task 4 (docs/plans/2026-09-30-stability-1.0.md): docs/guide/reference.md's "## Commands" table is the
/// CLI's documented, frozen surface. chargehand has no --help flag (confirmed: no "--help" anywhere under src/); the usage text
/// Program.cs prints for a missing or unrecognized command is what a person reads instead, and <c>CliVerbsTests</c> already
/// treats it that way for a handful of verbs. This generalizes that check to every row of the table and pins the exit-code
/// convention the "run" row documents.</summary>
public class CliSurfaceTests
{
    private static readonly string ReferenceMd = File.ReadAllText(Repo.Path("docs", "guide", "reference.md"));
    private static readonly string ProgramCs = File.ReadAllText(Repo.Path("src", "Chargehand.Cli", "Program.cs"));

    private sealed record CommandRow(string Verb, string ExitCodes);

    /// <summary>Every row of the "## Commands" table: the verb (the command cell's leading words, before its first
    /// placeholder, option or redirection token) and the exit-codes cell, verbatim.</summary>
    private static readonly IReadOnlyList<CommandRow> Rows = ReadRows();

    private static List<CommandRow> ReadRows()
    {
        var lines = ReferenceMd.Split('\n');
        var heading = Array.FindIndex(lines, l => l.StartsWith("## Commands", StringComparison.Ordinal));
        Assert.True(heading >= 0, "docs/guide/reference.md has no '## Commands' section");
        var header = Array.FindIndex(lines, heading, l => l.StartsWith("| command ", StringComparison.Ordinal));
        Assert.True(header >= 0, "docs/guide/reference.md's '## Commands' section has no table header");

        var rows = new List<CommandRow>();
        for (var i = header + 2; i < lines.Length && lines[i].StartsWith("| `", StringComparison.Ordinal); i++)
        {
            // A markdown-escaped pipe (`\|`, e.g. in the "learn report" row's `[--since <days>d\|<time>]`) is not a
            // cell separator; hide it from Split, then put it back in the two cells this test reads.
            var cells = lines[i].Replace("\\|", "\u0001").Split('|');
            var command = cells[1].Trim().Trim('`').Replace('\u0001', '|');
            var exitCodes = cells[3].Trim().Replace('\u0001', '|');
            var words = command.Split(' ').TakeWhile(w => w.Length > 0 && w[0] is not ('<' or '[' or '-')).ToArray();
            Assert.True(words.Length > 0, $"docs/guide/reference.md: command cell '{command}' has no bare verb");
            rows.Add(new CommandRow(string.Join(' ', words), exitCodes));
        }
        Assert.NotEmpty(rows);
        return rows;
    }

    public static IEnumerable<object[]> CommandVerbs => Rows.Select(r => new object[] { r.Verb });

    [Theory]
    [MemberData(nameof(CommandVerbs))]
    public void A_documented_command_is_shown_in_the_usage_text(string verb) =>
        Assert.Contains($"      {verb} ", ProgramCs, StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(CommandVerbs))]
    public void A_documented_command_is_dispatched_in_the_cli(string verb)
    {
        // Both the early, profile-free dispatch ("argv is [...]") and the main switch's cases ("case [...]") pattern-match
        // the verb's words as a leading list of quoted string literals: ["eval", "seed", ...matches multi-word verbs too.
        var pattern = "[\"" + string.Join("\", \"", verb.Split(' ')) + "\"";
        Assert.Contains(pattern, ProgramCs, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_documented_exit_code_is_within_the_clis_convention()
    {
        // Program.cs's header comment names the convention; every command's codes must stay inside it.
        foreach (var row in Rows)
            foreach (var code in row.ExitCodes.Where(char.IsDigit))
                Assert.True(code is >= '0' and <= '3', $"'{row.Verb}' documents exit code {code}, outside 0-3: {row.ExitCodes}");
    }

    [Fact]
    public void The_run_commands_exit_codes_match_the_clis_documented_convention()
    {
        var run = Rows.Single(r => r.Verb == "run");
        Assert.Contains($"Exit codes: {run.ExitCodes}", ProgramCs, StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_no_help_flag_for_the_reference_table_to_match_against()
    {
        // If one is ever added, the test above should compare against its output instead of the Usage text.
        Assert.DoesNotContain("--help", ProgramCs, StringComparison.Ordinal);
    }
}
