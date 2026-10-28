using System.Text.Json;

namespace AgentClientProtocol;

/// <summary>
/// One conversation with an agent. Prompts run one at a time, in the order they were called: a prompt sent while a turn runs waits
/// for that turn to end, then goes out. Updates arrive through <see cref="AcpClientOptions.OnSessionUpdate"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A SemaphoreSlim needs disposing only when AvailableWaitHandle was used, and this one never is.")]
public sealed class AcpSession
{
    private readonly AcpClient _client;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private int _queued;
    private volatile bool _active;

    internal AcpSession(AcpClient client, string id, SessionModeState? modes, SessionModelState? models, IReadOnlyList<ConfigOption> configOptions)
    {
        _client = client;
        Id = id;
        Modes = modes;
        Models = models;
        ConfigOptions = configOptions;
    }

    public string Id { get; }

    public SessionModeState? Modes { get; private set; }

    public SessionModelState? Models { get; private set; }

    public IReadOnlyList<ConfigOption> ConfigOptions { get; private set; }

    /// <summary>True while a prompt is with the agent.</summary>
    public bool IsTurnActive => _active;

    /// <summary>Prompts waiting behind the active turn.</summary>
    public int QueuedPrompts => Volatile.Read(ref _queued);

    public Task<PromptResult> PromptAsync(string text, CancellationToken ct = default) => PromptAsync([ContentBlock.FromText(text)], ct);

    /// <summary>
    /// Sends a prompt and returns when the agent ends the turn. If <paramref name="ct"/> is cancelled while the turn runs, the client sends
    /// <c>session/cancel</c> and waits up to <see cref="AcpClientOptions.CancelGrace"/> for the agent to end the turn: the result then has
    /// <see cref="AcpStopReason.Cancelled"/>. If it does not, or the prompt was still queued, this throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<PromptResult> PromptAsync(IReadOnlyList<ContentBlock> prompt, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _queued);
        try
        {
            await _turn.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }

        try
        {
            _active = true;
            return await RunTurnAsync(prompt, ct).ConfigureAwait(false);
        }
        finally
        {
            _active = false;
            _turn.Release();
        }
    }

    /// <summary>Asks the agent to stop the active turn. The agent ends it with <see cref="AcpStopReason.Cancelled"/>; prompts queued behind it still run.</summary>
    public Task CancelAsync() => _client.NotifyAsync("session/cancel", new CancelNotification(Id), AcpJsonContext.Default.CancelNotification);

    public async Task SetModeAsync(string modeId, CancellationToken ct = default)
    {
        await _client.CallAsync("session/set_mode", new SetModeRequest(Id, modeId), AcpJsonContext.Default.SetModeRequest, AcpJsonContext.Default.EmptyResult, ct).ConfigureAwait(false);
        Modes = Modes is null ? null : Modes with { CurrentModeId = modeId };
    }

    /// <summary>Changes a model through <c>session/set_model</c>, which agents that list <see cref="Models"/> accept. Agents that expose the model as a config option take <see cref="SetConfigOptionAsync"/> instead.</summary>
    public async Task SetModelAsync(string modelId, CancellationToken ct = default)
    {
        await _client.CallAsync("session/set_model", new SetModelRequest(Id, modelId), AcpJsonContext.Default.SetModelRequest, AcpJsonContext.Default.EmptyResult, ct).ConfigureAwait(false);
        Models = Models is null ? null : Models with { CurrentModelId = modelId };
    }

    /// <summary>Sets a config option to one of the values <see cref="ConfigOption.Values"/> lists, and returns the options as they stand after.</summary>
    public async Task<IReadOnlyList<ConfigOption>> SetConfigOptionAsync(string configId, string value, CancellationToken ct = default)
    {
        var request = new SetConfigOptionRequest(Id, configId, JsonSerializer.SerializeToElement(value, AcpJsonContext.Default.String));
        var result = await _client.CallAsync("session/set_config_option", request, AcpJsonContext.Default.SetConfigOptionRequest, AcpJsonContext.Default.SetConfigOptionResult, ct).ConfigureAwait(false);
        if (result.ConfigOptions is { } options)
            ConfigOptions = options;
        return ConfigOptions;
    }

    private async Task<PromptResult> RunTurnAsync(IReadOnlyList<ContentBlock> prompt, CancellationToken ct)
    {
        var turn = _client.CallAsync("session/prompt", new PromptRequest(Id, prompt), AcpJsonContext.Default.PromptRequest, AcpJsonContext.Default.PromptResult, CancellationToken.None);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (ct.Register(() => cancelled.TrySetResult()).ConfigureAwait(false))
        {
            if (await Task.WhenAny(turn, cancelled.Task).ConfigureAwait(false) == turn)
                return await turn.ConfigureAwait(false);
        }

        await CancelAsync().ConfigureAwait(false);
        using var grace = new CancellationTokenSource(_client.CancelGrace);
        try
        {
            return await turn.WaitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (grace.IsCancellationRequested)
        {
            throw new OperationCanceledException("the agent did not end the cancelled turn in time", ct);
        }
    }
}
