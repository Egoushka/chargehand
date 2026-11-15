namespace Chargehand.Automation;

/// <summary>Splits an epic into sub-issues.</summary>
public interface IEpicSplitter
{
    /// <returns>The number of sub-issues filed.</returns>
    Task<int> SplitAsync(string repository, IntakeIssue epic, CancellationToken ct);
}
