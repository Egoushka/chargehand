using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Runtimes.Acp.Core;

namespace Chargehand.Runtimes.Acp.Api;

/// <summary>
/// Conversations with any agent of the pinned list (ADR 0086, decisions 6, 8 and 9): <c>runtime: "acp:codex"</c> on a conversation start. One agent process per conversation, started by the
/// launcher inside the sandbox with an API key or a gateway's key and nothing else to sign in with. The checkout is read-only at the operating system and the agent is put in its own
/// read-only mode when it has one; a request to read is allowed once, and any other request (an edit, a command, a fetch) waits for the person's <c>allow_once</c> or <c>reject</c>.
/// </summary>
/// <param name="settings">The profile's <c>acp</c> block: the key and gateway by agent id.</param>
/// <param name="secret">Resolves a secret item name to its value.</param>
/// <param name="checkout">The directory holding the repository at the commit, for a preset (null: the default one); it refuses what a conversation may not read.</param>
public sealed class AcpConversationRuntime(AcpAgentCatalog catalog, AcpSettings settings, Func<string, string> secret,
    Func<RepositoryRef, string?, CancellationToken, Task<string>> checkout, IAcpLauncher launcher) : IConversationRuntime
{
    /// <summary>What a request's <c>runtime</c> starts with.</summary>
    public const string Prefix = "acp:";

    private readonly ConcurrentDictionary<string, AcpConversation> _conversations = new();

    /// <summary>How long an agent has to start, initialize and open a session. A package fetched by npx can be slow; one that hangs must not hold a conversation.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>An agent of the pinned list that the profile gave a key.</summary>
    public bool Supports(string runtime) =>
        runtime.StartsWith(Prefix, StringComparison.Ordinal) && runtime[Prefix.Length..] is var id && catalog.Contains(id) && !string.IsNullOrWhiteSpace(settings.CredentialFor(id)?.ApiKeySecret);

    /// <summary>The agent's key is the credential: a provider that rejects it pauses conversations on that agent alone.</summary>
    public string? CredentialKey(string runtime) => runtime;

    public async Task<RuntimeStarted> StartAsync(RuntimeStart start, CancellationToken ct)
    {
        var agent = catalog.Get(start.Runtime[Prefix.Length..]);
        var environment = AcpLaunchEnvironment.Build(agent, settings.CredentialFor(agent.Id), secret);
        var directory = await checkout(start.Repository, start.Preset, ct);
        var conversation = new AcpConversation(agent, directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartupTimeout);
        IAcpAgent started;
        try
        {
            var callbacks = new AcpAgentCallbacks(conversation.OnUpdate, conversation.OnPermission);
            started = await launcher.StartAsync(agent, environment, directory, ModelId(agent, start.Model), callbacks, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChargehandException(ErrorCode.RuntimeUnavailable, $"{agent.Name} did not start within {StartupTimeout.TotalSeconds:0} seconds",
                $"Run `{agent.Command} {string.Join(' ', agent.Args)}` by hand; a package fetched by npx on first use can be slow, so install it once to warm the cache.");
        }

        conversation.Attach(started);
        _conversations[start.ConversationId] = conversation;
        return new RuntimeStarted(started.Model);
    }

    public async IAsyncEnumerable<ConversationUpdate> UpdatesAsync(string conversationId, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in Find(conversationId).Updates.Reader.ReadAllAsync(ct))
            yield return update;
    }

    public Task PromptAsync(string conversationId, string text, CancellationToken ct)
    {
        Find(conversationId).Prompt(text);
        return Task.CompletedTask;
    }

    public Task CancelAsync(string conversationId, CancellationToken ct)
    {
        Find(conversationId).Cancel();
        return Task.CompletedTask;
    }

    public Task AnswerPermissionAsync(string conversationId, string callId, PermissionAnswer answer, CancellationToken ct)
    {
        Find(conversationId).Answer(callId, answer);
        return Task.CompletedTask;
    }

    public async Task EndAsync(string conversationId, CancellationToken ct)
    {
        if (_conversations.TryRemove(conversationId, out var conversation))
        {
            await conversation.EndAsync();
            conversation.Dispose();
        }
    }

    /// <summary>The model as the agent lists it: a request may write <c>provider/model</c>, as the run log and the price table do.</summary>
    private static string? ModelId(AcpAgentSpec agent, string? model) =>
        model is not null && model.StartsWith(agent.ProviderId + "/", StringComparison.Ordinal) ? model[(agent.ProviderId.Length + 1)..] : model;

    private AcpConversation Find(string conversationId) =>
        _conversations.TryGetValue(conversationId, out var conversation) ? conversation : throw new InvalidOperationException($"no conversation {conversationId} is open");
}
