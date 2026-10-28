using Chargehand.Contracts;
using Chargehand.Modules.Conversations.Api;

namespace Chargehand.Modules.Conversations.Tests;

/// <summary>Builds the event list of a conversation the way the service writes it, ids from 1.</summary>
internal sealed class Events
{
    public static readonly DateTimeOffset Start = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    public static readonly RepositoryRef Repository = new("acme/widgets", "0123456789abcdef0123456789abcdef01234567");

    private readonly List<ConversationEvent> _list = [];

    public IReadOnlyList<ConversationEvent> List => _list;

    public Events Add(ConversationEventData data)
    {
        _list.Add(ConversationJson.ToEvent(_list.Count + 1, Start.AddSeconds(_list.Count), data));
        return this;
    }

    /// <summary>starting, then idle: the first two events of every conversation.</summary>
    public static Events Opened(string? actor = "alice", string runtime = "fake", string? model = null, string? preset = null) =>
        new Events()
            .Add(new StateData(ConversationState.Starting, Repository: Repository, Runtime: runtime, Model: model, Preset: preset, Actor: actor))
            .Add(new StateData(ConversationState.Idle));
}
