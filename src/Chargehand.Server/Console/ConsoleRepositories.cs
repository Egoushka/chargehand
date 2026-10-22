using System.Diagnostics;
using System.Text.Json.Nodes;
using Chargehand.Config;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Server;

public sealed record EcosystemBody(string Name, IReadOnlyList<string> Markers);

public sealed record VerifyBody(IReadOnlyList<string> Command, string Source);

/// <param name="Proposal">A request/v1 for one driven task that fixes <paramref name="Gaps"/>, for the console to open in Start a batch; null when there is no gap.</param>
public sealed record ReadinessBody(int? Score, int Threshold, bool Below, IReadOnlyList<ReadinessSignal> Signals, IReadOnlyList<string> Gaps, JsonObject? Proposal);

/// <param name="Remote">The checkout's <c>origin</c> with any credential in the URL removed.</param>
/// <param name="Repository">The forge name (<c>owner/name</c>, with the host in front on a host that is not github.com) when the remote parses.</param>
/// <param name="Verify">The command <c>chargehand init</c> would propose for <c>context.verify</c> (a guess from marker files, never run); the candidates follow.</param>
/// <param name="Readiness">How ready the repository is for driven work (<see cref="RepositoryReadiness"/>).</param>
public sealed record RepositoryBody(string Name, string Path, string Root, string? Remote, string? Repository, string? DefaultBranch, string? Head, string? Error, IReadOnlyList<EcosystemBody> Ecosystems,
    VerifyBody? Verify, IReadOnlyList<VerifyBody> VerifyCandidates, string IntegratorMode, bool WatchedByIntake, IReadOnlyList<PullRequestBody> OpenPullRequests, ReadinessBody? Readiness = null);

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
        "Readiness counts the driven tasks this key may see; first-time CI is read from the checks the integrator kept, so a repository it does not watch has none.",
    ];

    public static async Task<RepositoriesBody> BuildAsync(ConsoleContext c, Principal? who, CancellationToken ct)
    {
        var profile = c.Profile;
        IReadOnlyList<string> roots = profile?.Roots ?? [];
        var learning = (await c.LearningAsync(ct)).Where(e => Principal.Sees(who, e.Record.Actor)).ToList();
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
        var open = name is null ? [] : learning.Where(e => e.Record.Pr is not null && ConsoleBatches.RepositoryName(e.Record.Pr.Remote) == name && (e.PrOutcome?.Outcome ?? PrOutcomes.Open) == PrOutcomes.Open)
            .OrderByDescending(e => e.Record.At).Take(MaxOpenPullRequests).Select(e => ConsoleBatches.PullRequestOf(c, e)!).ToList();
        var defaultBranch = (origin.Output.Length > 0 ? origin.Output : current.Output) is { Length: > 0 } text ? text.StartsWith("origin/", StringComparison.Ordinal) ? text["origin/".Length..] : text : null;
        return new RepositoryBody(System.IO.Path.GetFileName(path), path, root, remote, name, defaultBranch, head.ExitCode == 0 && head.Output.Length > 0 ? head.Output : null,
            head.ExitCode == 0 ? null : "git could not read HEAD", [.. scan.Ecosystems.Select(e => new EcosystemBody(e.Name, e.Markers))], scan.BestVerify is { } best ? new VerifyBody(best.Command, best.Source) : null,
            [.. scan.Verify.Select(v => new VerifyBody(v.Command, v.Source))], ConsoleBatches.ModeName(c.IntegrationOf(parsed)), watched.Contains(path), open,
            ReadinessOf(c, path, parsed, name, learning, scan));
    }

    /// <summary>The repository's readiness from the learning records that name it (by forge name or by path) and the checks the integrator kept for their pull requests.</summary>
    private static ReadinessBody ReadinessOf(ConsoleContext c, string path, RemoteRepository? remote, string? name, IReadOnlyList<LearningEntry> learning, RepositoryScan scan)
    {
        var identity = remote is null ? null : $"{remote.Owner}/{remote.Name}";
        var mine = learning.Where(e => e.Record.Repository == path || (identity is not null && string.Equals(e.Record.Repository, identity, StringComparison.OrdinalIgnoreCase))
            || (name is not null && e.Record.Pr is not null && ConsoleBatches.RepositoryName(e.Record.Pr.Remote) == name)).ToList();
        Dictionary<string, BatchTaskRecords> batches = [];
        var firstCi = mine.Where(e => e.Record.Pr is not null)
            .Select(e => FirstCiPassed(batches.TryGetValue(e.Record.BatchId, out var kept) ? kept : batches[e.Record.BatchId] = c.TaskRecordsOf(e.Record.BatchId), e.Record.TaskId))
            .OfType<bool>().ToList();
        var verify = identity is null ? null : c.Profile?.Driven?.Services?.VerifyFor(null, identity);
        bool? hasCi;
        try
        {
            hasCi = RepositoryReadiness.CiPresent(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            hasCi = null;
        }
        var readiness = RepositoryReadiness.Score(new ReadinessInput(name ?? path, mine, verify, scan.BestVerify?.Command, firstCi, hasCi));
        return new ReadinessBody(readiness.Score, readiness.Threshold, readiness.Below, readiness.Signals, readiness.Gaps, Proposal(readiness, name ?? Path.GetFileName(path), path));
    }

    /// <summary>Whether a task's pull request passed its checks with no CI fix: false once the CI fixer was asked to act or gave up, true once a look ended the watch, null while there is neither.</summary>
    internal static bool? FirstCiPassed(BatchTaskRecords records, string taskId)
    {
        if (records.Attempts.Any(a => a.TaskId == taskId) || records.Checks.Any(k => k.TaskId == taskId && k.Decision is "fix" or "give_up"))
            return false;
        return records.Checks.Any(k => k.TaskId == taskId && k.Decision is "done" or "merged") ? true : null;
    }

    private static JsonObject? Proposal(Readiness readiness, string repository, string path) =>
        readiness.ProposalGoal(repository) is not { } goal ? null : new JsonObject
        {
            ["contract_version"] = "request/v1",
            ["text"] = $"Make {repository} agent-ready",
            ["context"] = new JsonObject { ["repository"] = new JsonObject { ["path"] = path } },
            ["driven"] = new JsonObject { ["tasks"] = new JsonArray(new JsonObject { ["id"] = "agent-ready", ["goal"] = goal, ["size"] = "M" }) },
        };

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
