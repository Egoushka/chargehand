namespace Chargehand.Modules.Conversations.Api;

/// <summary>The profile's <c>http.conversations</c>: present turns the routes on. Off by default, because a conversation lets a person steer an agent.</summary>
/// <param name="Runtime">The runtime the server reaches agents through; <c>fake</c> is a scripted agent that echoes, for trying the API.</param>
/// <param name="MaxMessages">Messages one conversation takes before it refuses more with 429.</param>
public sealed record ConversationSettings(string Runtime = "fake", int MaxMessages = 200)
{
    public const int MostMessages = 10_000;

    /// <summary>The problems of the settings, named by key; empty when they are valid.</summary>
    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(Runtime)) yield return "runtime must name a runtime";
        if (MaxMessages is < 1 or > MostMessages) yield return $"max_messages must be 1 to {MostMessages}";
    }
}
