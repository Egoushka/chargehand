namespace Chargehand.Kernel;

/// <summary>The id of a run: its start time in UTC and a random suffix, so ids sort by start.</summary>
public static class RunIds
{
    public static string New() => $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
}
