using System.Text.RegularExpressions;

namespace Chargehand.Modules.Handover.Api;

/// <summary>The title of a task's pull request: short enough to read in a list, and shaped as a Conventional Commit, since a squash merge makes it the
/// commit on main (CHARGEHAND-298). The whole goal goes in the body, so a title does not have to hold it.</summary>
public static class PullRequestTitle
{
    /// <summary>The longest title derived from a goal.</summary>
    public const int MaxLength = 72;

    /// <summary>What a title does not end in: white space and the punctuation of a sentence or of a clause the cut left hanging.</summary>
    private static readonly char[] Trailing = [' ', '\t', '.', ',', ';', ':', '!', '?', '-', '–', '—'];

    /// <summary>The type <see cref="Conventional"/> gives a title that is not already one: the repository's own title check (<c>scripts/check-commit-msg.sh</c>) does not know what the
    /// change was for, so a neutral type that never claims a feature or a fix is the only one that is never wrong.</summary>
    private const string DefaultType = "chore";

    /// <summary>The <c>type(scope)?!?: </c> a title already starts with, the shape <c>scripts/check-commit-msg.sh</c> accepts.</summary>
    private static readonly Regex ConventionalCommit =
        new(@"^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([a-z0-9._/-]+\))?!?: ", RegexOptions.Compiled);

    /// <summary>The task's own <paramref name="title"/> when the request gave one, else the title <see cref="FromGoal"/> makes; either way, shaped as a Conventional Commit, with
    /// <paramref name="tag"/> (for example <c>"[partial] "</c>) right after the type so the type stays first.</summary>
    public static string Of(string? title, string goal, string? tag = null) => Conventional(title?.Trim() is { Length: > 0 } own ? own : FromGoal(goal), tag);

    /// <summary><paramref name="title"/>'s own <c>type(scope)?!?: </c> when it already has one, else <see cref="DefaultType"/>'s, with <paramref name="tag"/> inserted right after it.</summary>
    private static string Conventional(string title, string? tag)
    {
        var match = ConventionalCommit.Match(title);
        var (prefix, subject) = match.Success ? (match.Value, title[match.Length..]) : ($"{DefaultType}: ", title);
        return tag is null ? prefix + subject : prefix + tag + subject;
    }

    /// <summary>The goal's first sentence, cut at the last word that fits <see cref="MaxLength"/>, without the punctuation it ends in. A sentence ends at a line break, or at <c>.</c>, <c>!</c> or <c>?</c> that
    /// is followed by white space or the end.</summary>
    public static string FromGoal(string goal)
    {
        var text = goal.Trim();
        var title = Cut(text[..SentenceEnd(text)]).TrimEnd(Trailing);
        return title.Length > 0 ? title : "Chargehand task";
    }

    private static int SentenceEnd(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (text[i] is '\n' or '\r' || (text[i] is '.' or '!' or '?' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]))))
                return i;
        return text.Length;
    }

    private static string Cut(string text)
    {
        if (text.Length <= MaxLength)
            return text;
        var head = text[..MaxLength];
        if (char.IsWhiteSpace(text[MaxLength]))
            return head;
        var space = head.LastIndexOfAny([' ', '\t']);
        if (space > 0)
            return head[..space];
        return char.IsHighSurrogate(head[^1]) ? head[..^1] : head;
    }
}
