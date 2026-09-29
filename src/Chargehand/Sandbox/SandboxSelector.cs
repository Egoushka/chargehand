using Chargehand.Contracts;

namespace Chargehand.Sandbox;

public static class SandboxSelector
{
    /// <summary>
    /// The sandbox for a writing run (ADR 0035). "auto" takes the platform's and refuses when it is missing; "none" is an explicit,
    /// recorded opt-in; a named kind must be present.
    /// </summary>
    /// <param name="onPath">Whether an executable of that name is on PATH.</param>
    public static ISandbox Select(SandboxSettings? settings, Func<string, bool> onPath, bool isMacOs, bool isLinux)
    {
        var kind = settings?.Kind ?? "auto";
        return kind switch
        {
            "none" => new NoSandbox(),
            "seatbelt" when isMacOs && onPath("sandbox-exec") => new SeatbeltSandbox(),
            "bubblewrap" when isLinux && onPath("bwrap") => new BubblewrapSandbox(),
            "auto" when isMacOs && onPath("sandbox-exec") => new SeatbeltSandbox(),
            "auto" when isLinux && onPath("bwrap") => new BubblewrapSandbox(),
            "auto" or "seatbelt" or "bubblewrap" => throw new ChargehandException(ErrorCode.SandboxUnavailable,
                $"no sandbox is available for this machine (sandbox.kind is '{kind}')",
                isLinux ? "Install bubblewrap (the bwrap command), or set sandbox.kind to none in the profile if you accept running a repository's tests unconfined."
                        : "Run on macOS or Linux with bwrap, or set sandbox.kind to none in the profile if you accept running a repository's tests unconfined."),
            _ => throw new ChargehandException(ErrorCode.InvalidRequest, $"unknown sandbox.kind '{kind}'", "Use auto, seatbelt, bubblewrap or none."),
        };
    }

    /// <summary>Select for the running machine.</summary>
    public static ISandbox Select(SandboxSettings? settings) =>
        Select(settings, OnPath, OperatingSystem.IsMacOS(), OperatingSystem.IsLinux());

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Any(d => File.Exists(Path.Combine(d, name)));
}
