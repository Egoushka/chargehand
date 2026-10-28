namespace Chargehand.Modules.Conversations.Core;

internal static class ConversationIds
{
    /// <summary>Sorts by start, like a run's id, and names a journal file: <c>conv-20261028-090000-a1b2c3</c>.</summary>
    public static string New(DateTimeOffset now) => $"conv-{now.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
}
