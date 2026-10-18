using System.Globalization;
using System.Text;

namespace Chargehand.Automation;

/// <summary>This server's own numbers for the window the morning report covers. Top-level runs and batches by how they ended; a batch counts once, not its tasks.</summary>
/// <param name="Spend">The priced cost of the finished ones: a subscription run adds none.</param>
/// <param name="Open">The needs-you inbox: the total and the newest titles.</param>
public sealed record MorningData(int Started, int Completed, int Failed, int NeedsInput, int Denied, int Unfinished, decimal Spend, int Open, IReadOnlyList<string> OpenTitles);

/// <summary>Reads <see cref="MorningData"/> from the server's run log and inbox; the server implements it over the console's builders.</summary>
public interface IMorningSource
{
    /// <param name="since">The start of the window.</param>
    Task<MorningData> ReadAsync(DateTimeOffset since, CancellationToken ct);
}

/// <summary>The <c>morning-report</c> text (ADR 0068): no model call, only what the server already keeps.</summary>
public static class MorningReport
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private const int MaxTitles = 8;

    public static string Title(DateTimeOffset now) => $"chargehand morning report {now.UtcDateTime:yyyy-MM-dd}";

    public static string Format(MorningData data)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Last 24 hours: {data.Started} runs and batches started. ")
            .Append(CultureInfo.InvariantCulture, $"{data.Completed} finished, {data.Failed} failed, {data.NeedsInput} need you, {data.Denied} denied, {data.Unfinished} still going. ")
            .Append(CultureInfo.InvariantCulture, $"Spend ${data.Spend:0.00}.");
        text.Append(CultureInfo.InvariantCulture, $"\n\nNeeds you: {data.Open} open inbox item(s).");
        foreach (var title in data.OpenTitles.Take(MaxTitles))
            text.Append("\n- ").Append(title);
        if (data.Open > MaxTitles)
            text.Append(CultureInfo.InvariantCulture, $"\n(and {data.Open - MaxTitles} more)");
        return ScheduleResults.Cut(text.ToString());
    }
}
