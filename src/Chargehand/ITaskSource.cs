namespace Chargehand;

/// <summary>Turns a tracker item id into a goal.</summary>
public interface ITaskSource
{
    /// <exception cref="TaskSourceException">The item cannot be read or has no title.</exception>
    Task<string> ResolveAsync(string @ref, CancellationToken ct);
}

public sealed class TaskSourceException(string message) : Exception(message);
