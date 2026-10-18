using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chargehand.Automation;

/// <summary>The goal of a task started from a Plane work item (ADR 0067): the item's name, its description as text and the item's number. The first line is the name, which the pull request's title is cut from.
/// Members of the workspace write the item, so unlike GitHub intake the text is not fenced as untrusted; the key's reach is the guard.</summary>
public static partial class PlaneGoal
{
    public const int MaxName = 200;
    public const int MaxDescription = 6000;

    public static string Build(string identifier, string name, string description)
    {
        var text = new StringBuilder(OneLine(name));
        if (description.Trim().Length > 0)
            text.Append("\n\n").Append(Cut(description.Trim(), MaxDescription));
        return text.Append(CultureInfo.InvariantCulture, $"\n\nTracker item: {identifier}").ToString();
    }

    /// <summary>A task id the request schema takes (<c>^[a-z0-9][a-z0-9_-]{0,63}$</c>) from an item's number.</summary>
    public static string TaskId(string identifier)
    {
        var id = "plane-" + Unsafe().Replace(identifier.ToLowerInvariant(), "-");
        return id.Length <= 64 ? id : id[..64];
    }

    public static string OneLine(string text) => Cut(Whitespace().Replace(text, " ").Trim(), MaxName);

    public static string Cut(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + " [cut]";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^a-z0-9_-]")]
    private static partial Regex Unsafe();
}
