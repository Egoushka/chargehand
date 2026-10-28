using Chargehand.Kernel;
using Chargehand.Modules.Research.Api;

namespace Chargehand.Runtime;

/// <summary>
/// Stands in for a runtime that could not start, so `chargehand mcp` still answers initialize and each call fails with
/// the reason (a missing or signed-out agent CLI) instead of the process dying before the client sees a tool.
/// </summary>
public sealed class UnavailableRuntime(ChargehandException reason) : IWorkerRuntime
{
    private T Fail<T>() => throw reason;

    public Task<WorkerSession> CreateAsync(NodeSpec spec, CancellationToken ct) => Fail<Task<WorkerSession>>();

    public Task SetInstructionAsync(string sessionId, string key, string value, CancellationToken ct) => Fail<Task>();

    public Task SubmitAsync(string sessionId, string text, CancellationToken ct) => Fail<Task>();

    public Task<IdleOutcome> AwaitIdleAsync(string sessionId, CancellationToken ct) => Fail<Task<IdleOutcome>>();

    public Task InterruptAsync(string sessionId, CancellationToken ct) => Fail<Task>();

    public Task<IReadOnlyList<WorkerMessage>> ReadMessagesAsync(string sessionId, CancellationToken ct) => Fail<Task<IReadOnlyList<WorkerMessage>>>();

    public Task<IReadOnlyList<PermissionRequest>> PendingPermissionsAsync(string sessionId, CancellationToken ct) => Fail<Task<IReadOnlyList<PermissionRequest>>>();

    public Task AnswerPermissionAsync(string sessionId, string requestId, PermissionDecision decision, string? message, CancellationToken ct) => Fail<Task>();

    public Task<WorkerSession> ForkAsync(string sessionId, string? beforeMessageId, CancellationToken ct) => Fail<Task<WorkerSession>>();

    public Task CompactAsync(string sessionId, CancellationToken ct) => Fail<Task>();

    public Task<IReadOnlyList<FileDiff>> DiffAsync(string sessionId, CancellationToken ct) => Fail<Task<IReadOnlyList<FileDiff>>>();

    public Task<string> GenerateAsync(ModelRef? model, string prompt, CancellationToken ct) => Fail<Task<string>>();
}
