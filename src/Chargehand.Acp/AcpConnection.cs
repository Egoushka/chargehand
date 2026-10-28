using AgentClientProtocol;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Runtime;
using Chargehand.Runtimes.Acp.Api;
using Chargehand.Sandbox;

namespace Chargehand.Acp;

/// <summary>What an agent's callbacks reach: the node that owns the connection.</summary>
internal sealed record AcpCallbacks(Func<SessionNotification, ValueTask> OnUpdate, Func<RequestPermissionRequest, RequestPermissionOutcome> OnPermission);

/// <summary>One agent process and its one ACP session (ADR 0086, decision 8). The process starts inside the sandbox with a private home and
/// the key in its environment, and is read-only three ways: permission requests are rejected (<see cref="AcpReadOnlyPolicy"/>), the agent's
/// own read-only or plan mode is set when it has one, and the sandbox mounts the checkout read-only.</summary>
internal sealed class AcpConnection : IAsyncDisposable
{
    private readonly AcpClient _client;
    private readonly SandboxLaunch _launch;
    private readonly string _work;

    private AcpConnection(AcpClient client, SandboxLaunch launch, string work, AcpSession session, string? readOnlyMode, string? model)
    {
        (_client, _launch, _work, Session, ReadOnlyMode, Model) = (client, launch, work, session, readOnlyMode, model);
    }

    public AcpSession Session { get; }

    /// <summary>The mode id set to make the agent read-only; null when the agent lists none (the other two guards still hold).</summary>
    public string? ReadOnlyMode { get; }

    /// <summary>The model the session runs, as the agent names it; null when it does not say.</summary>
    public string? Model { get; }

    public static async Task<AcpConnection> StartAsync(AcpAgentSpec agent, IReadOnlyDictionary<string, string> environment, ISandbox sandbox, string directory,
        ModelRef? model, AcpCallbacks callbacks, CancellationToken ct)
    {
        var work = Directory.CreateTempSubdirectory("chargehand-acp-").FullName;
        // The agent's cwd is the private directory, so the checkout is outside what the sandbox lets it write. Network stays on: it calls its model.
        var launch = sandbox.Wrap(new SandboxSpec([agent.Command, .. agent.Args], work, Timeout.InfiniteTimeSpan, Network: true, PassEnv: [], Env: environment));
        AcpClient? client = null;
        try
        {
            client = AcpClient.Launch(
                new AcpLaunchOptions
                {
                    FileName = launch.FileName,
                    Arguments = launch.Arguments,
                    WorkingDirectory = launch.WorkingDirectory,
                    Environment = launch.Environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
                    ClearEnvironment = true,
                },
                new AcpClientOptions
                {
                    ClientInfo = new Implementation("chargehand"),
                    OnSessionUpdate = (n, _) => callbacks.OnUpdate(n),
                    OnRequestPermission = (r, _) => ValueTask.FromResult(callbacks.OnPermission(r)),
                });
            return await OpenAsync(agent, client, launch, work, directory, model, ct);
        }
        catch (AcpException e)
        {
            var tail = client?.StderrTail ?? "";
            if (client is not null)
                await client.DisposeAsync();
            launch.Dispose();
            Cleanup(work);
            throw Unavailable(agent, e, tail);
        }
        catch
        {
            if (client is not null)
                await client.DisposeAsync();
            launch.Dispose();
            Cleanup(work);
            throw;
        }
    }

    private static async Task<AcpConnection> OpenAsync(AcpAgentSpec agent, AcpClient client, SandboxLaunch launch, string work, string directory, ModelRef? model, CancellationToken ct)
    {
        var init = await client.InitializeAsync(ct);
        if (agent.AuthMethod is { } method)
        {
            var offered = init.AuthMethods?.Select(m => m.Id).ToList() ?? [];
            if (!offered.Contains(method))
                throw new ChargehandException(ErrorCode.RuntimeUnavailable, $"{agent.Name} offers no authentication method '{method}' (it offers: {(offered.Count == 0 ? "none" : string.Join(", ", offered))})",
                    $"Correct auth_method for '{agent.Id}' in {AcpAgentCatalog.FileName} to the method that reads the API key from the environment.");
            await client.AuthenticateAsync(method, ct);
        }

        var session = await client.NewSessionAsync(directory, ct: ct);
        var mode = AcpModes.ReadOnly(session.Modes, agent.ReadOnlyMode);
        if (mode is not null && !string.Equals(mode, session.Modes?.CurrentModeId, StringComparison.Ordinal))
            await session.SetModeAsync(mode, ct);
        var current = model is null ? CurrentModel(session) : await SetModelAsync(agent, session, model, ct);
        return new AcpConnection(client, launch, work, session, mode, current);
    }

    private static string? CurrentModel(AcpSession session)
    {
        if (session.Models?.CurrentModelId is { } id)
            return id;
        var option = session.ConfigOptions.FirstOrDefault(o => o.Category == "model" || o.Id == "model");
        return option?.CurrentValue is { ValueKind: System.Text.Json.JsonValueKind.String } value ? value.GetString() : null;
    }

    private static async Task<string> SetModelAsync(AcpAgentSpec agent, AcpSession session, ModelRef model, CancellationToken ct)
    {
        if (session.Models?.AvailableModels?.Any(m => m.ModelId == model.ModelId) == true)
            await session.SetModelAsync(model.ModelId, ct);
        else if (session.ConfigOptions.FirstOrDefault(o => o.Category == "model" || o.Id == "model") is { } option && option.Values().Contains(model.ModelId))
            await session.SetConfigOptionAsync(option.Id, model.ModelId, ct);
        else
            throw new ChargehandException(ErrorCode.InvalidRequest, $"{agent.Name} does not offer the model '{model.ModelId}'",
                "Name a model the agent lists, or leave the model unset to use its default.");
        return model.ModelId;
    }

    private static ChargehandException Unavailable(AcpAgentSpec agent, AcpException e, string stderrTail)
    {
        var detail = e is AcpConnectionException { StderrTail.Length: > 0 } c ? c.StderrTail : stderrTail;
        return new ChargehandException(ErrorCode.RuntimeUnavailable, $"{agent.Name} could not start as an ACP agent: {e.Message}{(detail.Length > 0 ? $" ({detail.Trim()})" : "")}",
            $"Run `{agent.Command} {string.Join(' ', agent.Args)}` by hand to see why, and check its key and network.");
    }

    public string StderrTail => _client.StderrTail;

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _launch.Dispose();
        Cleanup(_work);
    }

    private static void Cleanup(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the operating system clears temp directories.
        }
    }
}
