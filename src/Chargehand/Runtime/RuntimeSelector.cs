using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Runtime;

public enum RuntimeKind { Opencode, ClaudeCode, Acp }

/// <summary>
/// Which runtime to drive (ADR 0026): a profile field or environment variable naming one wins, then the profile's only
/// runtime block (ADR 0032); otherwise PATH is probed for a known agent CLI. No priority-order fallback: none found or
/// more than one found is an error naming what was seen. An ACP agent (ADR 0086) is named <c>acp:&lt;agent&gt;</c> and never found by PATH.
/// </summary>
public static class RuntimeSelector
{
    public const string AcpPrefix = "acp:";

    private static readonly (string Name, string Binary, RuntimeKind Kind)[] Known =
    [
        ("opencode", "opencode", RuntimeKind.Opencode),
        ("claude_code", "claude", RuntimeKind.ClaudeCode),
    ];

    /// <param name="blocks">The runtimes the profile has a block for (<c>opencode</c>, <c>claude_code</c>).</param>
    public static RuntimeKind Select(string? namedByProfile, string? namedByEnvironment, Func<string, bool> onPath, IReadOnlyList<RuntimeKind>? blocks = null)
    {
        if (Parse(namedByProfile) is { } fromProfile)
            return fromProfile;
        if (Parse(namedByEnvironment) is { } fromEnvironment)
            return fromEnvironment;
        if (blocks is [var only])
            return only;
        var found = Known.Where(k => onPath(k.Binary)).ToList();
        return found.Count switch
        {
            0 => throw new ChargehandException(ErrorCode.RuntimeUnavailable, "no agent CLI found on PATH (claude, opencode)",
                "Install Claude Code or OpenCode, or name one with the profile's runtime field or CHARGEHAND_RUNTIME."),
            1 => found[0].Kind,
            _ => throw new ChargehandException(ErrorCode.RuntimeAmbiguous,
                $"more than one agent CLI found on PATH: {string.Join(", ", found.Select(f => f.Name))}",
                "Name one with the profile's runtime field or CHARGEHAND_RUNTIME."),
        };
    }

    /// <summary>The agent an <c>acp:&lt;agent&gt;</c> name picks, from the name <see cref="Select"/> lets win; null when that name is not an ACP one.</summary>
    public static string? AcpAgent(string? namedByProfile, string? namedByEnvironment) =>
        (namedByProfile ?? namedByEnvironment) is { } name && IsAcp(name) ? name[AcpPrefix.Length..] : null;

    private static bool IsAcp(string name) => name.Length > AcpPrefix.Length && name.StartsWith(AcpPrefix, StringComparison.Ordinal);

    private static RuntimeKind? Parse(string? name) => name switch
    {
        null => null,
        "opencode" => RuntimeKind.Opencode,
        "claude_code" => RuntimeKind.ClaudeCode,
        _ when IsAcp(name) => RuntimeKind.Acp,
        _ => throw new ChargehandException(ErrorCode.InvalidRequest, $"unknown runtime '{name}'; expected opencode, claude_code or acp:<agent>",
            "Set the profile's runtime field or CHARGEHAND_RUNTIME to opencode, claude_code or acp:<agent>."),
    };

    /// <summary>Whether <paramref name="binary"/> resolves on PATH. A plain existence check; never executes it.</summary>
    public static bool OnPath(string binary) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Where(dir => dir.Length > 0)
            .Any(dir => File.Exists(Path.Combine(dir, binary)));
}
