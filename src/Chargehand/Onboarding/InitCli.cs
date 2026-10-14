using System.Text.Json;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Onboarding;

/// <summary><c>chargehand init [&lt;repo path&gt;] [--profile &lt;out&gt;] [--force]</c>: reads a repository's marker files, prints the ecosystems, the proposed
/// <c>context.verify</c> and the package hosts the driven egress allowlist lacks, and writes a starter profile. It needs no profile and makes no
/// network call. Exit codes: 0 written, 1 a profile exists there and <c>--force</c> was not given, 2 usage error.</summary>
public static class InitCli
{
    public const string Usage = "usage: chargehand init [<repository path>] [--profile <out path>] [--force]";

    /// <param name="defaultProfilePath">Where the profile goes without <c>--profile</c>: the path the global profile option or environment names.</param>
    /// <param name="presetAllow">The hosts the driven preset allows already.</param>
    public static int Run(IReadOnlyList<string> args, string workingDirectory, string defaultProfilePath, IReadOnlyList<string> presetAllow, TextWriter output, TextWriter error)
    {
        string? repository = null, profileOut = null;
        var force = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--profile" when i + 1 < args.Count:
                    profileOut = args[++i];
                    break;
                case "--force":
                    force = true;
                    break;
                case var positional when !positional.StartsWith("--", StringComparison.Ordinal) && repository is null:
                    repository = positional;
                    break;
                default:
                    error.WriteLine(Usage);
                    return 2;
            }
        }
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository ?? ".", workingDirectory));
        if (!Directory.Exists(path))
        {
            error.WriteLine($"{path} is not a directory");
            return 2;
        }
        var target = Path.GetFullPath(profileOut ?? defaultProfilePath, workingDirectory);

        var scan = RepositoryDetector.Scan(path);
        var missing = RepositoryDetector.Uncovered(scan.Hosts, presetAllow);
        output.WriteLine($"repository: {path}");
        if (scan.Ecosystems.Count == 0)
            output.WriteLine("found: no known ecosystem (looked for .NET, Node, Python, Go, Rust, Gradle and Maven markers at the top level)");
        foreach (var e in scan.Ecosystems)
            output.WriteLine($"found: {e.Name} ({string.Join(", ", e.Markers)})");
        if (scan.BestVerify is { } best)
        {
            output.WriteLine($"test command (context.verify), from {best.Source}: {JsonSerializer.Serialize(best.Command)}");
            foreach (var other in scan.Verify.Skip(1))
                output.WriteLine($"  also found, from {other.Source}: {JsonSerializer.Serialize(other.Command)}");
        }
        else
            output.WriteLine("test command (context.verify): none found");
        if (scan.Hosts.Count == 0)
            output.WriteLine("package hosts: none needed");
        else
        {
            output.WriteLine($"package hosts: {string.Join(", ", scan.Hosts)}");
            output.WriteLine(missing.Count == 0
                ? "  the driven preset already allows all of them"
                : $"  not allowed by the driven preset, so added to driven.network.allow: {string.Join(", ", missing)}");
        }

        switch (StarterProfile.Write(target, StarterProfile.Render(path, missing), force))
        {
            case WriteOutcome.Exists:
                error.WriteLine($"{target} exists; nothing written. Pass --force to overwrite it, or --profile <path> to write elsewhere.");
                return 1;
            case var written:
                output.WriteLine($"{(written == WriteOutcome.Overwritten ? "overwrote" : "wrote")} {target}");
                break;
        }

        output.WriteLine("still to fill in:");
        if (scan.BestVerify is null)
            output.WriteLine("  - context.verify: no test command was found; name one in each request's context.verify");
        else
            output.WriteLine("  - context.verify: check the proposed command passes in a clean container, then send it in each request");
        output.WriteLine("  - driven.images: build the session image, pin it by digest (name@sha256:...) and set driven.enabled to true");
        output.WriteLine($"  - secrets: put {StarterProfile.ApiKeyItem} and {StarterProfile.PushItem} in your secret source (an environment variable by default); the file holds names only");
        output.WriteLine("  - model credential: sign in to Claude Code or OpenCode, or set ANTHROPIC_API_KEY");
        output.WriteLine("then run: chargehand doctor");
        return 0;
    }
}
