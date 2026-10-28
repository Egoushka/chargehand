using Chargehand.Runtime;
using Chargehand.Runtimes.Acp.Api;

namespace Chargehand.Acp;

/// <summary>One node's state: its messages (oldest first, guarded by a lock on the node), its turn, and the agent connection once the first turn starts it.</summary>
internal sealed class AcpNode(string id, NodeSpec spec, string? baseCommit)
{
    public string Id { get; } = id;

    public NodeSpec Spec { get; } = spec;

    public string? BaseCommit { get; } = baseCommit;

    /// <summary>In the order set; ACP has no system prompt, so they head the first prompt.</summary>
    public List<(string Key, string Value)> Instructions { get; } = [];

    public List<WorkerMessage> Messages { get; } = [];

    public bool Started { get; set; }

    public volatile bool Interrupted;

    public Task? Turn { get; set; }

    public CancellationTokenSource? TurnCts { get; set; }

    public AcpConnection? Connection { get; set; }

    public AcpUsageMeter Meter { get; } = new();

    /// <summary>The assistant message of the turn that runs, once the agent has said or done anything; its place in <see cref="Messages"/>.</summary>
    public AcpTranscript? Current { get; set; }

    public int CurrentIndex { get; set; } = -1;
}
