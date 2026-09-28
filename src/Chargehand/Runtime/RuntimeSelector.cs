using Chargehand.Contracts;

namespace Chargehand.Runtime;

public enum RuntimeKind { Opencode, ClaudeCode }

/// <summary>
/// Which runtime to drive (ADR 0026): a profile field or environment variable naming one wins; otherwise PATH is
/// probed for a known agent CLI. No priority-order fallback: none found or more than one found is an error naming
/// what was seen.
/// </summary>
public static class RuntimeSelector
{
    private static readonly (string Name, string Binary, RuntimeKind Kind)[] Known =
    [
        ("opencode", "opencode", RuntimeKind.Opencode),
        ("claude_code", "claude", RuntimeKind.ClaudeCode),
    ];

    public static RuntimeKind Select(string? namedByProfile, string? namedByEnvironment, Func<string, bool> onPath)
    {
        if (Parse(namedByProfile) is { } fromProfile)
            return fromProfile;
        if (Parse(namedByEnvironment) is { } fromEnvironment)
            return fromEnvironment;
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

    private static RuntimeKind? Parse(string? name) => name switch
    {
        null => null,
        "opencode" => RuntimeKind.Opencode,
        "claude_code" => RuntimeKind.ClaudeCode,
        _ => throw new ChargehandException(ErrorCode.InvalidRequest, $"unknown runtime '{name}'; expected opencode or claude_code"),
    };

    /// <summary>Whether <paramref name="binary"/> resolves on PATH. A plain existence check; never executes it.</summary>
    public static bool OnPath(string binary) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Where(dir => dir.Length > 0)
            .Any(dir => File.Exists(Path.Combine(dir, binary)));
}
