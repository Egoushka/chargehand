using System.Collections.Concurrent;
using System.Text;
using AgentClientProtocol;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Research.Api;
using Chargehand.Runtime;
using Chargehand.Runtimes.Acp.Api;
using Chargehand.Sandbox;

namespace Chargehand.Acp;

/// <summary>
/// A research worker on any ACP agent (ADR 0086, decisions 8 to 10): Codex, Gemini CLI and the rest of <c>acp-agents.json</c>. One agent process per node,
/// started inside the sandbox on the node's first prompt, with an API key or a gateway's key and nothing else to sign in with. Read-only three ways
/// (see <see cref="AcpConnection"/>); permission requests never wait for anyone, so there is nothing to answer. Usage is what the agent reports.
/// </summary>
/// <param name="environment">What <see cref="AcpLaunchEnvironment.Build"/> made: the agent's key and gateway, resolved before anything is spent.</param>
public sealed class AcpWorkerRuntime(AcpAgentSpec agent, IReadOnlyDictionary<string, string> environment, ISandbox sandbox) : IWorkerRuntime, IRunCleanup, IServiceHealth
{
    private readonly ConcurrentDictionary<string, AcpNode> _nodes = new();

    public string Credential => "api_key";

    public bool ReportsUsage => agent.ReportsUsage;

    public bool ResearchOnly => true;

    public bool EndsEveryRun => true;

    /// <summary>The pinned version of the agent's package, for the run log.</summary>
    public string Version => agent.Version;

    /// <summary>How long an agent has to start, initialize and open a session. A package fetched by npx can be slow; one that hangs must not hold a run.</summary>
    internal TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public async Task<WorkerSession> CreateAsync(NodeSpec spec, CancellationToken ct)
    {
        var id = $"acp_{Guid.NewGuid():N}";
        _nodes[id] = new AcpNode(id, spec, await AcpGit.HeadAsync(spec.Directory, ct));
        return new WorkerSession(id, spec.Directory);
    }

    public Task SetInstructionAsync(string sessionId, string key, string value, CancellationToken ct)
    {
        var node = Get(sessionId);
        lock (node)
        {
            if (node.Started)
                throw new InvalidOperationException("instruction entries are fixed once the session's first prompt is sent");
            var at = node.Instructions.FindIndex(i => i.Key == key);
            if (at >= 0)
                node.Instructions[at] = (key, value);
            else
                node.Instructions.Add((key, value));
        }

        return Task.CompletedTask;
    }

    public async Task SubmitAsync(string sessionId, string text, CancellationToken ct)
    {
        var node = Get(sessionId);
        string prompt;
        lock (node)
        {
            if (node.Turn is { IsCompleted: false })
                throw new InvalidOperationException($"session {sessionId} is busy");
            prompt = node.Started ? text : Preamble(node.Instructions) + text;
            node.Started = true;
            node.Interrupted = false;
            node.Meter.Reset();
            node.Messages.Add(new WorkerMessage($"user_{Guid.NewGuid():N}", WorkerMessageKind.User, DateTimeOffset.UtcNow, text, null));
        }

        // A failure to start is raised here, with its own code, rather than as a turn that ended failed with no model call.
        node.Connection ??= await StartAsync(node, ct);
        var cts = node.TurnCts = new CancellationTokenSource();
        node.Turn = Task.Run(() => RunTurnAsync(node, prompt, cts.Token), CancellationToken.None);
    }

    public async Task<IdleOutcome> AwaitIdleAsync(string sessionId, CancellationToken ct)
    {
        var node = Get(sessionId);
        if (node.Turn is { } turn)
            await turn.WaitAsync(ct);
        lock (node)
            return node.Messages.LastOrDefault(m => m.Kind == WorkerMessageKind.Idle)?.Outcome ?? IdleOutcome.Failed;
    }

    public async Task InterruptAsync(string sessionId, CancellationToken ct)
    {
        var node = Get(sessionId);
        node.Interrupted = true;
        // The client sends session/cancel and waits its grace for the agent to end the turn; the turn then ends cancelled either way.
        node.TurnCts?.Cancel();
        if (node.Turn is { } turn)
            await turn.WaitAsync(ct);
    }

    /// <summary>Newest first, like the other adapters.</summary>
    public Task<IReadOnlyList<WorkerMessage>> ReadMessagesAsync(string sessionId, CancellationToken ct)
    {
        var node = Get(sessionId);
        lock (node)
            return Task.FromResult<IReadOnlyList<WorkerMessage>>([.. Enumerable.Reverse(node.Messages)]);
    }

    /// <summary>Always empty: a permission request is answered the moment it arrives, by <see cref="AcpReadOnlyPolicy"/>.</summary>
    public Task<IReadOnlyList<PermissionRequest>> PendingPermissionsAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PermissionRequest>>([]);

    public Task AnswerPermissionAsync(string sessionId, string requestId, PermissionDecision decision, string? message, CancellationToken ct) =>
        throw new InvalidOperationException("a research worker on an ACP agent never has a pending permission request");

    /// <summary>Only a fork before the first user message (the one WorkerNode makes): a fresh session with the same spec and instructions. ACP has no
    /// stable fork, so the sibling pays for its own prefix.</summary>
    public Task<WorkerSession> ForkAsync(string sessionId, string? beforeMessageId, CancellationToken ct)
    {
        var source = Get(sessionId);
        AcpNode fork;
        lock (source)
        {
            if (beforeMessageId is null || source.Messages.FirstOrDefault(m => m.Kind == WorkerMessageKind.User)?.Id != beforeMessageId)
                throw new NotSupportedException("an ACP agent forks only before a session's first user message");
            fork = new AcpNode($"acp_{Guid.NewGuid():N}", source.Spec, source.BaseCommit);
            fork.Instructions.AddRange(source.Instructions);
        }

        _nodes[fork.Id] = fork;
        return Task.FromResult(new WorkerSession(fork.Id, fork.Spec.Directory));
    }

    public Task CompactAsync(string sessionId, CancellationToken ct) =>
        throw new NotSupportedException("ACP agents do not advertise compaction");

    public Task<IReadOnlyList<FileDiff>> DiffAsync(string sessionId, CancellationToken ct)
    {
        var node = Get(sessionId);
        return node.BaseCommit is null ? Task.FromResult<IReadOnlyList<FileDiff>>([]) : AcpGit.DiffAsync(node.Spec.Directory, node.BaseCommit, ct);
    }

    /// <summary>One-shot, no repository: a session in an empty directory, ended when the answer is in. Intake uses it.</summary>
    public async Task<string> GenerateAsync(ModelRef? model, string prompt, CancellationToken ct)
    {
        var directory = Directory.CreateTempSubdirectory("chargehand-acp-gen-");
        var text = new StringBuilder();
        try
        {
            var callbacks = new AcpCallbacks(
                n =>
                {
                    if (n.Update is AgentMessageChunk { Content: { Type: "text", Text: { } chunk } })
                        lock (text)
                            text.Append(chunk);
                    return ValueTask.CompletedTask;
                },
                r => AcpReadOnlyPolicy.Decide(r).Outcome);
            await using var connection = await StartAsync(directory.FullName, model, callbacks, ct);
            var result = await connection.Session.PromptAsync(prompt, ct);
            if (result.StopReason != AcpStopReason.EndTurn)
                throw new InvalidOperationException($"{agent.Name} ended the turn with '{result.StopReason}'");
            lock (text)
                return text.ToString();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>A granted server is not handed over: ACP cannot limit an agent to a server's granted tools, so the preset's services are reported unavailable.</summary>
    public IReadOnlyDictionary<string, string> UnavailableServices(string sessionId) =>
        (Get(sessionId).Spec.Services ?? []).ToDictionary(g => g.Server, _ => "unsupported: an ACP agent cannot be limited to a server's granted tools");

    public async Task EndRunAsync(string runId, CancellationToken ct)
    {
        var ended = _nodes.Values.Where(n => n.Spec.Metadata.TryGetValue(IRunCleanup.RunMetadataKey, out var id) && id == runId).ToList();
        foreach (var node in ended)
        {
            _nodes.TryRemove(node.Id, out _);
            node.TurnCts?.Cancel();
            if (node.Turn is { } turn)
                await turn.WaitAsync(ct).ConfigureAwait(false);
            if (node.Connection is { } connection)
                await connection.DisposeAsync();
        }
    }

    private async Task RunTurnAsync(AcpNode node, string prompt, CancellationToken ct)
    {
        IdleOutcome outcome;
        string? error = null;
        try
        {
            var result = await node.Connection!.Session.PromptAsync(prompt, ct);
            lock (node)
                node.Meter.Finish(result.Usage);
            (outcome, error) = Outcome(result.StopReason, agent.Name);
        }
        catch (OperationCanceledException)
        {
            outcome = IdleOutcome.Interrupted;
        }
        catch (AcpException e)
        {
            outcome = IdleOutcome.Failed;
            error = ChargehandException.Scrub($"{e.Message}{(e is AcpConnectionException { StderrTail.Length: > 0 } c ? $" ({c.StderrTail.Trim()})" : "")}");
        }
        catch (Exception e)
        {
            // A turn that ends without an idle message would leave AwaitIdle with nothing to read.
            outcome = IdleOutcome.Failed;
            error = ChargehandException.Scrub(string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message);
        }

        lock (node)
        {
            Refresh(node, DateTimeOffset.UtcNow);
            node.Current = null;
            node.CurrentIndex = -1;
            node.Messages.Add(new WorkerMessage($"idle_{Guid.NewGuid():N}", WorkerMessageKind.Idle, DateTimeOffset.UtcNow, null, null, DateTimeOffset.UtcNow, Error: error, Outcome: outcome));
        }
    }

    private static (IdleOutcome Outcome, string? Error) Outcome(string stopReason, string name) => stopReason switch
    {
        AcpStopReason.EndTurn => (IdleOutcome.Succeeded, null),
        AcpStopReason.Cancelled => (IdleOutcome.Interrupted, null),
        AcpStopReason.Refusal => (IdleOutcome.Failed, $"{name} refused to continue"),
        AcpStopReason.MaxTokens => (IdleOutcome.Failed, $"{name} ended the turn at its output token limit"),
        AcpStopReason.MaxTurnRequests => (IdleOutcome.Failed, $"{name} ended the turn at its limit of model requests"),
        _ => (IdleOutcome.Failed, $"{name} ended the turn with '{stopReason}'"),
    };

    private ValueTask OnUpdate(AcpNode node, SessionNotification note)
    {
        lock (node)
        {
            if (note.Update is UsageUpdate usage)
                node.Meter.Observe(usage);
            else if (note.Update is not (AgentMessageChunk or ToolCall or ToolCallUpdate))
                return ValueTask.CompletedTask;
            if (node.Current is null)
            {
                node.Current = new AcpTranscript($"asst_{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
                node.Messages.Add(node.Current.Snapshot(null, null, null));
                node.CurrentIndex = node.Messages.Count - 1;
            }

            node.Current.Apply(note.Update);
            Refresh(node, null);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Rejects an edit or execute request and says so in the transcript, where <c>chargehand show</c> prints it.</summary>
    private static RequestPermissionOutcome OnPermission(AcpNode node, RequestPermissionRequest request)
    {
        var verdict = AcpReadOnlyPolicy.Decide(request);
        if (verdict.Refused)
            lock (node)
                node.Messages.Add(new WorkerMessage($"refused_{Guid.NewGuid():N}", WorkerMessageKind.Other, DateTimeOffset.UtcNow, verdict.Reason, null));
        return verdict.Outcome;
    }

    private void Refresh(AcpNode node, DateTimeOffset? completed)
    {
        if (node.Current is not null)
            node.Messages[node.CurrentIndex] = node.Current.Snapshot(node.Meter.Tokens, Label(node), completed);
    }

    private string? Label(AcpNode node) =>
        node.Spec.Model is { } pinned ? $"{pinned.ProviderId}/{pinned.ModelId}" : node.Connection?.Model is { } current ? $"{agent.ProviderId}/{current}" : null;

    private async Task<AcpConnection> StartAsync(AcpNode node, CancellationToken ct) =>
        await StartAsync(node.Spec.Directory, node.Spec.Model, new AcpCallbacks(n => OnUpdate(node, n), r => OnPermission(node, r)), ct);

    private async Task<AcpConnection> StartAsync(string directory, ModelRef? model, AcpCallbacks callbacks, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartupTimeout);
        try
        {
            return await AcpConnection.StartAsync(agent, environment, sandbox, directory, model, callbacks, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChargehandException(ErrorCode.RuntimeUnavailable, $"{agent.Name} did not start within {StartupTimeout.TotalSeconds:0} seconds",
                $"Run `{agent.Command} {string.Join(' ', agent.Args)}` by hand; a package fetched by npx on first use can be slow, so install it once to warm the cache.");
        }
    }

    /// <summary>ACP has no system prompt, so the instruction entries head the first prompt, each in a block that names it.</summary>
    internal static string Preamble(IEnumerable<(string Key, string Value)> instructions) =>
        string.Concat(instructions.Select(i => $"<instructions name=\"{i.Key}\">\n{i.Value}\n</instructions>\n\n"));

    /// <summary>The mode set to make the session read-only, or null; for tests.</summary>
    internal string? ReadOnlyMode(string sessionId) => Get(sessionId).Connection?.ReadOnlyMode;

    private AcpNode Get(string sessionId) =>
        _nodes.TryGetValue(sessionId, out var node) ? node : throw new InvalidOperationException($"unknown session {sessionId}");
}
