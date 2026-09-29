namespace Chargehand.Runtime;

/// <summary>
/// Optional on a runtime that keeps something for the length of a run: OpenCode registers a run's granted MCP servers with a
/// server every run at the location shares (ADR 0034), and takes them out once no run uses them. The orchestrator ends the run
/// when its nodes are done, however they ended (completed, failed or cancelled). A runtime finds the run in
/// <see cref="NodeSpec.Metadata"/> under <see cref="RunMetadataKey"/>.
/// </summary>
public interface IRunCleanup
{
    /// <summary>The <see cref="NodeSpec.Metadata"/> key that holds the run's id.</summary>
    public const string RunMetadataKey = "chargehand.run";

    /// <summary>Releases what the run's nodes left with the runtime. Best effort, bounded in time, and safe to repeat.</summary>
    Task EndRunAsync(string runId, CancellationToken ct);
}
