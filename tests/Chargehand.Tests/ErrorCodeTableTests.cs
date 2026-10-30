using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Tests;

/// <summary>Stability plan Task 5 (docs/plans/2026-09-30-stability-1.0.md): every value of <see cref="ErrorCode"/> has exactly one
/// row in docs/guide/reference.md's "## Error codes" table, documented with an action meaning, and the row's retryable column
/// matches <see cref="ChargehandException.Retryable"/> (ADR 0022: "retryable follows from the code").</summary>
public class ErrorCodeTableTests
{
    private sealed record ErrorCodeRow(string Code, string Action, bool Retryable);

    /// <summary>code name, action cell and the "yes"/"no" retryable cell of every row of the "## Error codes" table, in document order.</summary>
    private static readonly IReadOnlyList<ErrorCodeRow> Rows = ReadRows();

    private static readonly IReadOnlyDictionary<string, ErrorCode> BySnakeName =
        Enum.GetValues<ErrorCode>().ToDictionary(c => JsonNamingPolicy.SnakeCaseLower.ConvertName(c.ToString()), c => c, StringComparer.Ordinal);

    private static List<ErrorCodeRow> ReadRows()
    {
        var text = File.ReadAllText(Repo.Path("docs", "guide", "reference.md"));
        var start = text.IndexOf("\n## Error codes", StringComparison.Ordinal);
        Assert.True(start >= 0, "docs/guide/reference.md has no '## Error codes' section");
        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = text[start..(end < 0 ? text.Length : end)];

        var rows = Regex.Matches(section, @"^\| `([a-z_]+)` \|.*\| ([^|]+) \| (yes|no) \|\s*$", RegexOptions.Multiline)
            .Select(m => new ErrorCodeRow(m.Groups[1].Value, m.Groups[2].Value.Trim(), m.Groups[3].Value == "yes")).ToList();
        Assert.NotEmpty(rows);
        return rows;
    }

    public static IEnumerable<object[]> DocumentedActions() => Rows.Select(r => new object[] { r.Code, r.Action });

    [Theory]
    [MemberData(nameof(DocumentedActions))]
    public void Every_documented_code_has_an_action_meaning(string code, string action) =>
        Assert.True(action.Length > 0, $"'{code}' has no action meaning in reference.md's table");

    [Fact]
    public void Every_error_code_the_contract_can_produce_has_exactly_one_documented_row()
    {
        var documented = Rows.Select(r => r.Code).ToList();
        var codes = BySnakeName.Keys.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(documented.Count, documented.Distinct(StringComparer.Ordinal).Count());
        var missingFromDocs = codes.Except(documented).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var missingFromCode = documented.Except(codes).OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.True(missingFromDocs.Count == 0, $"ErrorCode has a member with no row in reference.md's table: {string.Join(", ", missingFromDocs)}");
        Assert.True(missingFromCode.Count == 0, $"reference.md documents a code ErrorCode has no member for: {string.Join(", ", missingFromCode)}");
    }

    public static IEnumerable<object[]> DocumentedCodes() => Rows.Select(r => new object[] { r.Code, r.Retryable });

    [Theory]
    [MemberData(nameof(DocumentedCodes))]
    public void The_documented_retryable_column_matches_the_codes_retryable_meaning(string code, bool retryable)
    {
        Assert.True(BySnakeName.TryGetValue(code, out var value), $"'{code}' is not a member of ErrorCode");
        Assert.Equal(retryable, ChargehandException.Retryable(value));
    }
}
