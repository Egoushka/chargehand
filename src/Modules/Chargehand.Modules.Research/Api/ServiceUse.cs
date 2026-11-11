namespace Chargehand.Modules.Research.Api;

/// <summary>A server of the profile's <c>mcp_servers</c> and the tools of it a node may use: exact names, or globs where <c>*</c> is
/// any run of characters (never the server's whole set, ADR 0034).</summary>
public sealed record ServiceUse(string Server, IReadOnlyList<string> Tools)
{
    public bool Equals(ServiceUse? other) => other is not null && Server == other.Server && Tools.SequenceEqual(other.Tools);

    public override int GetHashCode() => HashCode.Combine(Server, Tools.Count);
}
