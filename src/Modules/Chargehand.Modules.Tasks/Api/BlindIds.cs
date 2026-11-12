namespace Chargehand.Modules.Tasks.Api;

/// <summary>The id of the blind sibling of a task (issue 789): the task's own id and <see cref="Suffix"/>.</summary>
public static class BlindIds
{
    public const string Suffix = ".blind";

    public static string Of(string id) => id + Suffix;

    /// <returns>The id of the task the blind task writes tests for; null when <paramref name="id"/> is not a blind task's.</returns>
    public static string? For(string id) => id.Length > Suffix.Length && id.EndsWith(Suffix, StringComparison.Ordinal) ? id[..^Suffix.Length] : null;
}
