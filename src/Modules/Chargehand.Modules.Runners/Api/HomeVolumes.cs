using System.Text.RegularExpressions;

namespace Chargehand.Modules.Runners.Api;

/// <summary>The home volume of ADR 0078, which a profile with <c>driven.environment: "mise"</c> mounts in place of the home directory's tmpfs: one named volume per repository,
/// mounted at the session user's home directory, with the toolchains mise installs and every package manager's default cache under it. The name, the mount point and the variables are
/// fixed here, so a caller cannot name a path or another repository's volume. Java reads the home directory from the account, not from <c>$HOME</c>, so the mount point is the account's.</summary>
public static partial class HomeVolumes
{
    /// <summary>The session user's home directory, where the volume is mounted.</summary>
    public const string MountPoint = "/home/session";

    /// <summary>mise's data directory, on the volume.</summary>
    public const string MiseDataDir = MountPoint + "/.local/share/mise";

    /// <summary>Claude Code's state, in a tmpfs on top of the volume: transcripts and session state do not persist.</summary>
    public const string ClaudeConfigDir = MountPoint + "/.claude";

    /// <summary>The shell start-up files removed from the home directory before each session, so what a session wrote there cannot run in a later one.</summary>
    public static readonly IReadOnlyList<string> StartupFiles = [".bashrc", ".profile", ".bash_profile", ".zshrc"];

    /// <summary>The tools whose idiomatic version files (<c>.nvmrc</c>, <c>global.json</c>, <c>.python-version</c>, ...) mise reads. mise reads them only for the tools it is told to.</summary>
    public const string IdiomaticTools = "node,python,java,go,ruby,dotnet,bun,deno,terraform,yarn,pnpm";

    /// <summary>The variables a container with a home volume gets, with their fixed values: the template writes them, a caller cannot. The checkout is trusted so mise reads its <c>mise.toml</c>; the
    /// install helper and the session read the same pins, so a tool the helper installed is the one the shims resolve.</summary>
    public static readonly IReadOnlyList<(string Name, string Value)> Environment =
    [
        ("MISE_DATA_DIR", MiseDataDir),
        ("CLAUDE_CONFIG_DIR", ClaudeConfigDir),
        ("MISE_IDIOMATIC_VERSION_FILE_ENABLE_TOOLS", IdiomaticTools),
        ("MISE_TRUSTED_CONFIG_PATHS", "/work"),
        ("MISE_YES", "1"),
        ("MISE_NO_ENV", "1"),
    ];

    /// <summary>The hosts the install helper's egress reaches, and no other container's: toolchain downloads and the version indexes of mise. A session that a model drives never gets them,
    /// because a release download from <c>github.com</c> would let it fetch any binary there (ADR 0078, decision 2).</summary>
    public static readonly IReadOnlyList<string> ToolchainHosts =
    [
        "mise-versions.jdx.dev", "mise-java.jdx.dev", "mise.jdx.dev", "github.com", "api.github.com", "*.githubusercontent.com",
        "nodejs.org", "dl.google.com", "archive.apache.org", "dlcdn.apache.org", "services.gradle.org", "downloads.gradle.org",
        "sh.rustup.rs", "static.rust-lang.org", "dot.net", "builds.dotnet.microsoft.com", "dotnetcli.azureedge.net", "api.nuget.org", "pypi.org", "files.pythonhosted.org",
        "go.dev", "proxy.golang.org", "cdn.azul.com", "api.adoptium.net",
    ];

    /// <summary>The volume of the repository <paramref name="repository"/> names (<c>owner/repo</c> or a remote URL; case does not matter): the cache volumes' prefix and hash, without an ecosystem.</summary>
    public static string Name(string repository) => $"{CacheVolumes.Prefix}{CacheVolumes.Hash(repository)}";

    /// <summary>Whether <paramref name="name"/> is exactly a home volume's name.</summary>
    public static bool IsHomeVolume(string name) => VolumeName().IsMatch(name);

    [GeneratedRegex(@"^chargehand-cache-[0-9a-f]{12}$")]
    private static partial Regex VolumeName();
}
