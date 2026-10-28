namespace Chargehand.Modules.Conversations.Api;

/// <summary>The profile's <c>http.conversations</c>: present turns the routes on. Off by default, because a conversation lets a person steer an agent.</summary>
/// <param name="Runtime">The runtime the server reaches agents through: <c>fake</c>, a scripted agent that echoes, for trying the API, or <c>acp</c>, the agents of the pinned
/// list that the profile's <c>acp.agents</c> gives a key (a request names one as <c>acp:&lt;id&gt;</c>).</param>
/// <param name="MaxMessages">Messages one conversation takes before it refuses more with 429.</param>
public sealed record ConversationSettings(string Runtime = "fake", int MaxMessages = 200)
{
    public const int MostMessages = 10_000;

    public static readonly string[] Runtimes = ["fake", "acp"];

    /// <summary>The problems of the settings, named by key; empty when they are valid.</summary>
    public IEnumerable<string> Problems()
    {
        if (!Runtimes.Contains(Runtime)) yield return $"runtime must be one of {string.Join(", ", Runtimes)}";
        if (MaxMessages is < 1 or > MostMessages) yield return $"max_messages must be 1 to {MostMessages}";
    }
}
