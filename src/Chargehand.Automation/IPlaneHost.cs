namespace Chargehand.Automation;

/// <param name="Name">The state's name, as the profile names it.</param>
public sealed record PlaneWorkflowState(string Id, string Name);

/// <param name="Description">The item's description as plain text.</param>
/// <param name="SequenceId">The number after the project's identifier (<c>ABC-12</c> is 12).</param>
public sealed record PlaneItem(string Id, string Name, string Description, string StateId, int SequenceId, DateTimeOffset UpdatedAt);

/// <summary>What intake does on Plane (ADR 0067), each call with the API key. It reads states and work items and writes comments and states, nothing else.</summary>
public interface IPlaneHost
{
    /// <summary>The project's short identifier (<c>ABC</c>), which prefixes its items' numbers.</summary>
    Task<string> ProjectIdentifierAsync(string project, CancellationToken ct);

    Task<IReadOnlyList<PlaneWorkflowState>> StatesAsync(string project, CancellationToken ct);

    /// <summary>The work items of the project that are in the state. Plane's query filter is not on every edition, so the project's items are paged through and filtered here.</summary>
    Task<IReadOnlyList<PlaneItem>> ItemsInStateAsync(string project, string stateId, CancellationToken ct);

    Task<PlaneItem> GetAsync(string project, string itemId, CancellationToken ct);

    Task CommentAsync(string project, string itemId, string text, CancellationToken ct);

    Task SetStateAsync(string project, string itemId, string stateId, CancellationToken ct);
}
