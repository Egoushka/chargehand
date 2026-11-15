namespace Chargehand.Automation;

/// <summary>A label the issue sweep keeps in every repository it works in.</summary>
public sealed record SweepLabel(string Name, string Color, string Description);

/// <summary>The labels the sweep reads and writes, with the colours and descriptions it syncs.</summary>
public static class SweepLabels
{
    public const string Ready = "agent:ready";
    public const string NeedsYou = "agent:needs-you";
    public const string Blocked = "agent:blocked";
    public const string Epic = "agent:epic";
    public const string Skip = "agent:skip";

    public static IReadOnlyList<SweepLabel> All { get; } =
    [
        new(Ready, "0e8a16", "An agent can pick this up"),
        new(NeedsYou, "d93f0b", "Waiting on a decision from the owner"),
        new(Blocked, "b60205", "Blocked by another issue or an outside step"),
        new(Epic, "5319e7", "Too large for one change; split into smaller issues"),
        new(Skip, "cccccc", "The sweep leaves this issue alone"),
        new("size:S", "c2e0c6", "A small change"),
        new("size:M", "fbca04", "A medium change"),
        new("size:L", "f9d0c4", "A large change"),
    ];
}
