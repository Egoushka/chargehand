using System.Globalization;
using System.Text;
using Chargehand.Contracts;

namespace Chargehand.Automation;

/// <summary>The plain text a finished read-only run is posted as (ADR 0068): how it ended, its summary, and its findings with the places they cite. Cut to what a phone notification carries (ntfy's limit is 4096 bytes).</summary>
public static class ScheduleResults
{
    public const int MaxChars = 3500;

    public static string Title(string entry) => $"chargehand: {entry}";

    public static string Lost(string entry) => $"The run for '{entry}' was lost: the server that ran it stopped and did not take it up again.";

    public static string Format(ResultContract result)
    {
        var text = new StringBuilder();
        text.Append(result.Status == ResultStatus.Completed ? "Done." : $"Ended: {result.Status.ToString().ToLowerInvariant()}.");
        if (result.Error is { } error)
            text.Append(CultureInfo.InvariantCulture, $" {error.Message}");
        if (result.Summary.Length > 0)
            text.Append("\n\n").Append(result.Summary.Trim());
        if (result.Claims.Count > 0)
        {
            text.Append("\n\nFindings:");
            foreach (var claim in result.Claims)
                text.Append("\n- ").Append(claim.Text.Trim()).Append(Cites(result, claim));
        }
        if (result.OpenQuestions.Count > 0)
        {
            text.Append("\n\nOpen:");
            foreach (var question in result.OpenQuestions)
                text.Append("\n- ").Append(question.Trim());
        }
        return Cut(text.ToString());
    }

    private static string Cites(ResultContract result, Claim claim)
    {
        var places = claim.Evidence.Select(id => result.Evidence.FirstOrDefault(e => e.Id == id)?.Locator).Where(l => l is not null).ToList();
        return places.Count == 0 ? "" : $" ({string.Join("; ", places)})";
    }

    internal static string Cut(string text) => text.Length <= MaxChars ? text : text[..MaxChars].TrimEnd() + "\n[cut]";
}
