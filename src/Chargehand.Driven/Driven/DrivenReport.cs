using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chargehand.Driven;

public sealed record ReportClaim(string Text, IReadOnlyList<string> Evidence);

/// <summary>The JSON block a driven session ends with (ADR 0039): what it did, the claims it makes about the change with the code lines each rests on, and the test run
/// it says it made. It is the session's word; chargehand checks the citations and runs the tests itself.</summary>
/// <param name="Dropped">Claims beyond <see cref="DrivenReport.MaxClaims"/> that were not read.</param>
public sealed partial record DrivenReport(string Summary, IReadOnlyList<ReportClaim> Claims, string? TestsCommand, int? TestsExitCode, int Dropped)
{
    public const int MaxClaims = 50;

    private const int MaxTextLength = 2000;

    /// <returns>Null when the text is not a report: not JSON, no summary, or claims that are not a list of objects with text and string evidence.</returns>
    public static DrivenReport? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("summary", out var s) || s.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(s.GetString()))
                return null;
            List<ReportClaim> claims = [];
            var total = 0;
            if (root.TryGetProperty("claims", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array)
                    return null;
                foreach (var c in list.EnumerateArray())
                {
                    total++;
                    if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString()))
                        return null;
                    List<string> evidence = [];
                    if (c.TryGetProperty("evidence", out var ev))
                    {
                        if (ev.ValueKind != JsonValueKind.Array)
                            return null;
                        foreach (var e in ev.EnumerateArray())
                        {
                            if (e.ValueKind != JsonValueKind.String)
                                return null;
                            evidence.Add(e.GetString()!);
                        }
                    }
                    if (claims.Count < MaxClaims)
                        claims.Add(new ReportClaim(Cut(text.GetString()!.Trim()), evidence));
                }
            }
            string? command = null;
            int? exit = null;
            if (root.TryGetProperty("tests", out var tests) && tests.ValueKind == JsonValueKind.Object)
            {
                command = tests.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String ? Cut(cmd.GetString()!) : null;
                exit = tests.TryGetProperty("exit_code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var n) ? n : null;
            }
            return new DrivenReport(Cut(s.GetString()!.Trim()), claims, command, exit, Math.Max(0, total - MaxClaims));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether a citation is a file and line range chargehand can check: <c>path:line</c> or <c>path:start-end</c>.</summary>
    public static bool IsFileLocator(string locator) => FileLocator().IsMatch(locator);

    private static string Cut(string text) => text.Length > MaxTextLength ? text[..MaxTextLength] : text;

    [GeneratedRegex(@"^[A-Za-z0-9_./@+-][^:\s]*:\d+(-\d+)?$")]
    private static partial Regex FileLocator();
}
