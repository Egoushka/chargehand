namespace Chargehand.Contracts;

/// <summary>A command that provably checks nothing (CHARGEHAND-258, ADR 0078): empty, blank, or a shell no-op such as
/// <c>true</c>, <c>:</c>, <c>exit 0</c> or a bare <c>echo</c>, alone or under <c>sh -c</c>. A caller that names one is
/// not believed: chargehand works out its own command instead, or refuses the request.</summary>
public static class NoOpVerify
{
    private static readonly string[] Bodies = ["true", "/bin/true", "/usr/bin/true", ":", "exit 0"];

    /// <summary>Tokens that chain a bare <c>echo</c> to a real command, so the echo is not the whole check.</summary>
    private static readonly string[] ChainTokens = ["&&", "||", ";", "|", "`", "$("];

    public static bool IsNoOp(IReadOnlyList<string> argv) =>
        argv.Count == 0 || argv.All(a => a.Trim().Length == 0) || IsNoOpBody(argv[^1].Trim());

    /// <summary>The same check for a command given as one string, as an <see cref="Contracts.AcceptanceCheck"/> or <c>context.verify</c>
    /// joined already carries it.</summary>
    public static bool IsNoOp(string command) => IsNoOp([command]);

    private static bool IsNoOpBody(string body) => Bodies.Contains(body, StringComparer.Ordinal) || IsBareEcho(body);

    private static bool IsBareEcho(string body) =>
        (body.Equals("echo", StringComparison.Ordinal) || body.StartsWith("echo ", StringComparison.Ordinal))
        && !ChainTokens.Any(t => body.Contains(t, StringComparison.Ordinal));
}
