using System.Text.RegularExpressions;

namespace Chargehand.Modules.Console.Api;

/// <summary>The profile's <c>inbox</c> (ADR 0085): who an inbox card belongs to.</summary>
/// <param name="Owners">A repository glob (<c>*</c> matches any run of characters, case-insensitive; matched against <c>owner/name</c>) to the name of the desk or person that answers for it. The first match in the file's order wins.</param>
public sealed record InboxSettings(IReadOnlyDictionary<string, string>? Owners = null)
{
    /// <summary>The owner of <paramref name="repository"/> (a path or <c>owner/name</c>); null when no glob matches.</summary>
    public string? OwnerOf(string? repository)
    {
        if (repository is null || Owners is null)
            return null;
        var parts = repository.TrimEnd('/').Split('/');
        var name = parts.Length > 2 ? string.Join('/', parts[^2..]) : repository;
        foreach (var (glob, owner) in Owners)
            if (Regex.IsMatch(name, "^" + Regex.Escape(glob).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return owner;
        return null;
    }
}
