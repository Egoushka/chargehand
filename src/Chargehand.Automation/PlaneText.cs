using System.Net;
using System.Text.RegularExpressions;

namespace Chargehand.Automation;

/// <summary>Plane keeps descriptions as HTML. A goal is plain text: block tags become line breaks, list items become bullets, every other tag goes, and entities are decoded.</summary>
public static partial class PlaneText
{
    public static string FromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var text = ListItem().Replace(html, "- ");
        text = Paragraph().Replace(text, "\n\n");
        text = LineBreak().Replace(text, "\n");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace(" ", " ", StringComparison.Ordinal);
        text = Blank().Replace(text.ReplaceLineEndings("\n"), "\n\n");
        return text.Trim();
    }

    /// <summary>A plain text as the HTML Plane's comment endpoint takes: one paragraph per blank-line-separated block.</summary>
    public static string ToHtml(string text) =>
        string.Join("", Blank().Split(text.ReplaceLineEndings("\n").Trim()).Select(p => $"<p>{WebUtility.HtmlEncode(p).Replace("\n", "<br>", StringComparison.Ordinal)}</p>"));

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"</(?:p|ul|ol|h[1-6]|pre|blockquote)>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Paragraph();

    [GeneratedRegex(@"<br\s*/?>|</(?:div|li|tr)>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.None, 1000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t]*\n(?:[ \t]*\n)+", RegexOptions.None, 1000)]
    private static partial Regex Blank();
}
