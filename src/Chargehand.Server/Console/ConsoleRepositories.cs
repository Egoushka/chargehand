using System.Diagnostics;
using Chargehand.Config;
using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Onboarding;

namespace Chargehand.Server;

public sealed record EcosystemBody(string Name, IReadOnlyList<string> Markers);

public sealed record VerifyBody(IReadOnlyList<string> Command, string Source);

/// <param name="Remote">The checkout's <c>origin</c> with any credential in the URL removed.</param>
/// <param name="Repository">The forge name (<c>owner/name</c>, with the host in front on a host that is not github.com) when the remote parses.</param>
/// <param name="Verify">The command <c>chargehand init</c> would propose for <c>context.verify</c> (a guess from marker files, never run); the candidates follow.</param>
public sealed record RepositoryBody(string Name, string Path, string Root, string? Remote, string? Repository, string? DefaultBranch, string? Head, string? Error, IReadOnlyList<EcosystemBody> Ecosystems,
    VerifyBody? Verify, IReadOnlyList<VerifyBody> VerifyCandidates, string IntegratorMode, bool WatchedByIntake, IReadOnlyList<PullRequestBody> OpenPullRequests);

public sealed record RootBody(string Path, bool Exists);

/// <summary><c>repositories/v1</c>: the checkouts under <c>repository_roots</c> (ADR 0023), what is detected in them, and the open pull requests chargehand knows of.</summary>
public sealed record RepositoriesBody(string ContractVersion, DateTimeOffset GeneratedAt, IReadOnlyList<RootBody> Roots, IReadOnlyList<RepositoryBody> Repositories, IReadOnlyList<string> NotRecorded);

internal static class ConsoleRepositories
{
    public const string Contract = "repositories/v1";

    /// <summary>The most directories one root contributes, as <c>chargehand doctor</c> scans.</summary>
    private const int MaxChildren = 200;

    private const int MaxOpenPullRequests = 20;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "Open pull requests are the ones chargehand opened and has a record of, with the state last read back from the forge; the server does not list a forge's pull requests itself.",
        "Detection reads marker files only (RepositoryDetector); the test command is a proposal, not what a run used.",
        "HEAD and the default branch are what the checkout has now; the server fetches before a batch, not for this route.",
    ];

    public static async Task<RepositoriesBody> BuildAsync(ConsoleContext c, Principal? who, CancellationToken ct)
    {
        var profile = c.Profile;
        IReadOnlyList<string> roots = profile?.Roots ?? [];
        var learning = (await c.LearningAsync(ct)).Where(e => Principal.Sees(who, e.Record.Actor) && e.Record.Pr is not null).ToList();
        var watched = profile?.Automation?.Github?.Repositories?.Values.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Path))).ToHashSet(StringComparer.Ordinal) ?? [];
        var found = new List<(string Root, string Path)>();
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var path in Candidates(root))
                if (who?.Account is not { Repositories: { Count: > 0 } allowed } || allowed.Any(a => Within(a, path)))
                    found.Add((root, path));
        using var limit = new SemaphoreSlim(8);
        var bodies = await Task.WhenAll(found.Select(async f =>
        {
            await limit.WaitAsync(ct);
            try
            {
                return await DescribeAsync(c, f.Root, f.Path, learning, watched, ct);
            }
            finally
            {
                limit.Release();
            }
        }));
        return new RepositoriesBody(Contract, c.Now, [.. roots.Select(r => new RootBody(r, Directory.Exists(r)))], [.. bodies.OrderBy(b => b.Path, StringComparer.Ordinal)], NotRecorded);
    }

    private static IEnumerable<string> Candidates(string root)
    {
        if (IsRepository(root))
            return [Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))];
        try
        {
            return [.. Directory.EnumerateDirectories(root).Where(IsRepository).Order(StringComparer.Ordinal).Take(MaxChildren).Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsRepository(string directory) => Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    private static bool Within(string root, string path)
    {
        var (r, p) = (Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        return p == r || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static async Task<RepositoryBody> DescribeAsync(ConsoleContext c, string root, string path, IReadOnlyList<LearningEntry> learning, HashSet<string> watched, CancellationToken ct)
    {
        RepositoryScan scan;
        try
        {
            scan = RepositoryDetector.Scan(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            scan = new RepositoryScan([], []);
        }
        var head = await GitAsync(path, ["rev-parse", "--verify", "--quiet", "HEAD"], ct);
        var origin = await GitAsync(path, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], ct);
        var current = origin.Output.Length == 0 ? await GitAsync(path, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct) : origin;
        var remoteUrl = await GitAsync(path, ["config", "--get", "remote.origin.url"], ct);
        var remote = remoteUrl.ExitCode == 0 && remoteUrl.Output.Length > 0 ? Sanitize(remoteUrl.Output) : null;
        var parsed = remote is null ? null : RemoteRepository.Parse(remote, c.Profile?.Driven?.Forge);
        var name = ConsoleBatches.RepositoryName(parsed);
        var open = name is null ? [] : learning.Where(e => ConsoleBatches.RepositoryName(e.Record.Pr!.Remote) == name && (e.PrOutcome?.Outcome ?? PrOutcomes.Open) == PrOutcomes.Open)
            .OrderByDescending(e => e.Record.At).Take(MaxOpenPullRequests).Select(e => ConsoleBatches.PullRequestOf(c, e)!).ToList();
        var defaultBranch = (origin.Output.Length > 0 ? origin.Output : current.Output) is { Length: > 0 } text ? text.StartsWith("origin/", StringComparison.Ordinal) ? text["origin/".Length..] : text : null;
        return new RepositoryBody(System.IO.Path.GetFileName(path), path, root, remote, name, defaultBranch, head.ExitCode == 0 && head.Output.Length > 0 ? head.Output : null,
            head.ExitCode == 0 ? null : "git could not read HEAD", [.. scan.Ecosystems.Select(e => new EcosystemBody(e.Name, e.Markers))], scan.BestVerify is { } best ? new VerifyBody(best.Command, best.Source) : null,
            [.. scan.Verify.Select(v => new VerifyBody(v.Command, v.Source))], ConsoleBatches.ModeName(c.IntegrationOf(parsed)), watched.Contains(path), open);
    }

    /// <summary>A remote URL without the user and password part, so a token in an <c>https://token@host/...</c> origin never leaves the server.</summary>
    internal static string Sanitize(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
            return new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri;
        // scp-style (git@host:path) carries a user name, not a secret.
        return url;
    }

    private static async Task<(int ExitCode, string Output)> GitAsync(string directory, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return (-1, "");
            process.StandardInput.Close();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(GitTimeout);
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token);
                _ = await error;
                return (process.ExitCode, (await output).Trim());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return (-1, "");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "");
        }
    }
}
