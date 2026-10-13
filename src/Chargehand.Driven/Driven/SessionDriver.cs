using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chargehand.Budget;
using Chargehand.Config;
using Chargehand.Containers;
using Chargehand.Runtime;

namespace Chargehand.Driven;

/// <param name="Goal">What the change should do; it goes after <c>/chargehand:change</c>, so it cannot replace the skill.</param>
/// <param name="Branch">The branch the skill is told to use, <c>chargehand/&lt;run&gt;</c>.</param>
/// <param name="MaxTokens">Input plus output tokens the session may use; counted from the stream, so it binds with a subscription too.</param>
/// <param name="MaxUsd">Passed to <c>--max-budget-usd</c>; only meaningful where the credential is priced.</param>
/// <param name="Prices">The profile's price table, for the dollar-equivalent estimate in <c>session-usage.json</c>; left out of the file when null, so a profile without prices still
/// starts an older session image.</param>
/// <param name="Governor">ADR 0045's governor for this session; null or <c>off</c>: none.</param>
public sealed record SessionTask(string RunId, string Goal, string Branch, int MaxTurns, int MaxMinutes, int NoProgressSeconds,
    long? MaxTokens = null, string? Model = null, decimal? MaxUsd = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, ModelPrice>? Prices = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionGovernor? Governor = null);

/// <summary>How to start Claude Code: the program and arguments that go before its own (a test runs a script as <c>sh script</c>).</summary>
public sealed record SessionCommand(string File, IReadOnlyList<string> LeadingArgs)
{
    public static SessionCommand Claude { get; } = new("claude", []);
}

/// <param name="Secrets">Values replaced by <c>[redacted]</c> in everything the driver stores.</param>
/// <param name="PromptText">Appended to Claude Code's system prompt.</param>
/// <param name="McpUrl">The chargehand server's MCP address on the batch network, for the skill's research and review calls.</param>
/// <param name="McpToken">The run-scoped token for it.</param>
/// <param name="GitTimeout">How long one of the stop snapshot's git calls may run before it is killed and the snapshot skipped; null: 3 minutes.</param>
/// <param name="GitRunning">Whether some git process is alive (a lock file may then be live); null: look at the process table.</param>
public sealed record SessionOptions(string WorkDirectory, string OutDirectory, SessionTask Task, SessionCommand Command, IReadOnlyList<string> Secrets,
    string PromptText, string? McpUrl, string? McpToken, string PluginDirectory = "/opt/chargehand-plugin", TimeSpan? GitTimeout = null, Func<bool>? GitRunning = null);

/// <summary>The running tally the session writes to <c>session-usage.json</c>: input plus output tokens so far, assistant messages so far, and the <c>change</c> skill's step of the
/// latest tool call that names one (<see cref="Adherence.StageOf"/>). <c>CacheRead</c> and <c>CacheWrite</c> are summed over the assistant messages so far; <c>UsdEstimate</c> prices
/// all four token classes with the task's price table, cache reads at the cache-read rate. It is a dollar equivalent, not a bill, so it exists on a subscription too; null
/// when the task carries no prices or no message could be priced; if only some could, it sums those and <c>UsdEstimatePartial</c> is true. The real dollars (<c>total_cost_usd</c>) arrive only in the stream's <c>result</c>.</summary>
public sealed record SessionUsage(long Tokens, int? Turns = null, string? Stage = null, long? CacheRead = null, long? CacheWrite = null, decimal? UsdEstimate = null,
    bool UsdEstimatePartial = false);

/// <summary><c>RateLimited</c>: the session ended on a usage or rate limit, so the provider, not the task, is why it stopped.</summary>
public enum SessionStatus { Completed, NeedsInput, Stalled, TokenCap, Failed, RateLimited }

/// <param name="Reason">For a stall, its wire name; for a failure, a short cause.</param>
/// <param name="BundleWritten">A bundle of the session's branch is in the output directory; the handover decides whether to use it.</param>
/// <param name="StreamSha256">The SHA-256 of the stored stream (<c>stream.jsonl</c>, secrets redacted), so a result can cite the log without carrying it.</param>
/// <param name="SessionId">Claude Code's session id, which its own telemetry carries as <c>session.id</c>.</param>
/// <param name="Model">The model the session started on (the stream's <c>init</c>).</param>
/// <param name="ModelUsage">Tokens per model from the stream's <c>result</c>: a session can call more than one model.</param>
/// <param name="UsdEstimate">The session's dollar equivalent from its per-message tokens (see <see cref="SessionUsage"/>).</param>
/// <param name="RateLimitResetsAt">For <see cref="SessionStatus.RateLimited"/>: when the limit lifts, if the result said.</param>
/// <param name="Commits">Commits on the session's branch past the commit the workspace started at (the base), the seed's included; 0 with no bundle.</param>
/// <param name="SnapshotNote">What the stop snapshot did not do: files left out for their size, or why nothing was committed; null when there is nothing to say.</param>
/// <param name="Governor">The governor's decisions (ADR 0045), the first <see cref="SessionDriver.MaxGovernorDecisions"/>; null when no governor ran. <c>governor.jsonl</c> has them all.</param>
/// <param name="GovernorLogSha256">The SHA-256 of <c>governor.jsonl</c>; null when no governor ran.</param>
public sealed record SessionOutcome(SessionStatus Status, string Reason, int Turns, long InputTokens, long OutputTokens, long CacheReadTokens,
    decimal? CostUsd, int? ExitCode, IReadOnlyList<string> Questions, bool BundleWritten, string? StreamSha256 = null, long CacheWriteTokens = 0, string? SessionId = null,
    string? Model = null, IReadOnlyDictionary<string, TokenCounts>? ModelUsage = null, decimal? UsdEstimate = null, DateTimeOffset? RateLimitResetsAt = null, bool UsdEstimatePartial = false,
    int Commits = 0, string? SnapshotNote = null, IReadOnlyList<GovernorDecision>? Governor = null, string? GovernorLogSha256 = null);

/// <summary>Runs headless Claude Code on the <c>change</c> skill inside a session container (ADR 0039), watches its stream, ends it when it
/// stalls or passes its token cap, and leaves three files in the output directory: <c>stream.jsonl</c> (secrets redacted), <c>chargehand.bundle</c>
/// (the session's branch) and <c>driven-report.json</c> (the report the session printed), plus <c>session-outcome.json</c>. A session that is stopped has its working tree
/// committed to the branch first (a <c>WIP:</c> commit), so what it wrote is not lost; one that finds <c>seed.bundle</c> in the output directory starts from that branch (ADR 0044).</summary>
public static partial class SessionDriver
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <summary>How often the driver measures the branch's diff for the governor's no-growth rule; a measure is taken only when a turn has passed since the last.</summary>
    private static readonly TimeSpan DiffEvery = TimeSpan.FromSeconds(30);

    /// <summary>The governor decisions <c>session-outcome.json</c> carries; the log file has every one.</summary>
    public const int MaxGovernorDecisions = 50;

    public const string GovernorFile = "governor.jsonl";

    private static readonly JsonSerializerOptions LineJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static readonly JsonSerializerOptions OutcomeJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    /// <summary>No shell tool is withheld: the container is the boundary (ADR 0039), and the model needs one to run the repository's tests.</summary>
    private const string AllowedTools = "Bash,Read,Edit,Write,Glob,Grep,mcp__chargehand__orchestrate";

    public static IReadOnlyList<string> ClaudeArgs(SessionTask task, string pluginDirectory, string mcpConfigFile, string promptFile)
    {
        List<string> args =
        [
            "-p", $"/chargehand:change {task.Goal}",
            "--plugin-dir", pluginDirectory,
            "--permission-mode", "dontAsk",
            "--allowedTools", AllowedTools,
            "--strict-mcp-config", "--mcp-config", mcpConfigFile,
            "--append-system-prompt-file", promptFile,
            "--output-format", "stream-json", "--verbose",
        ];
        if (task.Model is { Length: > 0 })
            args.AddRange(["--model", task.Model]);
        if (task.MaxUsd is { } usd)
            args.AddRange(["--max-budget-usd", usd.ToString(CultureInfo.InvariantCulture)]);
        return args;
    }

    public static string McpConfig(string url, string token) =>
        JsonSerializer.Serialize(new { mcpServers = new { chargehand = new { type = "http", url, headers = new { Authorization = $"Bearer {token}" } } } });

    public static async Task<SessionOutcome> RunAsync(SessionOptions options, CancellationToken ct)
    {
        Directory.CreateDirectory(options.OutDirectory);
        var task = options.Task;
        // The commit the workspace starts at: what "commits made" is counted from, whether or not a seed moves the branch past it.
        var start = (await Git(options.WorkDirectory, ["rev-parse", "--verify", "--quiet", "HEAD"], ct)) is { Exit: 0 } head ? head.Stdout.Trim() : null;
        await SeedAsync(options, ct);
        var scratch = Directory.CreateTempSubdirectory("chargehand-session-");
        try
        {
            var promptFile = Path.Combine(scratch.FullName, "prompt.md");
            await File.WriteAllTextAsync(promptFile, options.PromptText, ct);
            var mcpFile = Path.Combine(scratch.FullName, "mcp.json");
            await WritePrivate(mcpFile, options.McpUrl is { } url && options.McpToken is { } token ? McpConfig(url, token) : """{"mcpServers":{}}""", ct);

            var psi = new ProcessStartInfo(options.Command.File)
            {
                WorkingDirectory = options.WorkDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (var a in options.Command.LeadingArgs.Concat(ClaudeArgs(task, options.PluginDirectory, mcpFile, promptFile)))
                psi.ArgumentList.Add(a);
            psi.Environment["CHARGEHAND_BRANCH"] = task.Branch;
            psi.Environment["CHARGEHAND_DRIVEN"] = "1";

            var started = DateTimeOffset.UtcNow;
            var detector = new StallDetector(TimeSpan.FromSeconds(task.NoProgressSeconds), 5, task.MaxTurns, TimeSpan.FromMinutes(task.MaxMinutes), started);
            var tally = new Tally(task.Prices);
            var trace = new SessionTrace();
            SessionUsage? lastUsage = null;
            string? stage = null;
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start Claude Code");
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await using var stream = new StreamWriter(new FileStream(Path.Combine(options.OutDirectory, "stream.jsonl"), FileMode.Create, FileAccess.Write), new UTF8Encoding(false));
            var stoppedBy = new StopCause();
            var governor = task.Governor is { Mode: not GovernorMode.Off } governed ? new Governor(governed) : null;
            await using var governorLog = governor is null ? null
                : new StreamWriter(new FileStream(Path.Combine(options.OutDirectory, GovernorFile), FileMode.Create, FileAccess.Write), new UTF8Encoding(false));
            var decisions = new List<GovernorDecision>();
            decimal? spentSoFar = null;
            var governorGate = new SemaphoreSlim(1, 1);
            async Task Govern(IReadOnlyList<GovernorDecision> made)
            {
                if (governorLog is null || made.Count == 0)
                    return;
                await governorGate.WaitAsync(CancellationToken.None);
                try
                {
                    // In enforce mode the strongest decision (a stop over a checkpoint) ends the session if nothing else has; only that one is logged as acted on.
                    if (task.Governor is { Mode: GovernorMode.Enforce } && made.Where(d => Governor.StopReason(d) is not null).OrderByDescending(d => d.Action).FirstOrDefault() is { } acting
                        && stoppedBy.Set(SessionStatus.Stalled, Governor.StopReason(acting)!))
                    {
                        made = [.. made.Select(d => d == acting ? d with { Acted = true } : d)];
                        Interrupt(process);
                    }
                    foreach (var d in made)
                    {
                        if (decisions.Count < MaxGovernorDecisions)
                            decisions.Add(d);
                        await governorLog.WriteLineAsync(Redact(JsonSerializer.Serialize(d, LineJson), options.Secrets));
                    }
                    await governorLog.FlushAsync(CancellationToken.None);
                }
                finally
                {
                    governorGate.Release();
                }
            }

            var reader = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
                {
                    await stream.WriteLineAsync(Redact(line, options.Secrets));
                    await stream.FlushAsync(ct);
                    JsonElement e;
                    try { e = JsonDocument.Parse(line).RootElement; }
                    catch (JsonException) { continue; }
                    var now = DateTimeOffset.UtcNow;
                    detector.OnEvent(e, now);
                    tally.Add(e);
                    trace.OnEvent(e);
                    stage = Adherence.StageOf(e) ?? stage;
                    if (governor is not null)
                    {
                        IReadOnlyList<GovernorDecision> made;
                        lock (governor)
                        {
                            spentSoFar = tally.UsdEstimate;
                            made = governor.OnEvent(e, detector.Turns, spentSoFar, now);
                        }
                        await Govern(made);
                    }
                    WriteUsage(options.OutDirectory, new SessionUsage(tally.Input + tally.Output, detector.Turns, stage, tally.CacheRead, tally.CacheWrite, tally.UsdEstimate, tally.UsdEstimatePartial), ref lastUsage);
                    if (task.MaxTokens is { } cap && tally.Input + tally.Output >= cap && stoppedBy.Set(SessionStatus.TokenCap, "token_cap"))
                        Interrupt(process);
                }
            }, ct);

            using var watch = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            var exit = process.WaitForExitAsync(ct);
            Task? measuring = null;
            var measuredAt = (Turn: 0, At: started);
            while (!exit.IsCompleted)
            {
                var tick = watch.WaitForNextTickAsync(ct).AsTask();
                await Task.WhenAny(exit, tick);
                if (exit.IsCompleted)
                    break;
                if (governor is not null && start is not null && measuring is not { IsCompleted: false } && detector.Turns > measuredAt.Turn
                    && DateTimeOffset.UtcNow - measuredAt.At >= DiffEvery)
                {
                    measuredAt = (detector.Turns, DateTimeOffset.UtcNow);
                    var turn = measuredAt.Turn;
                    measuring = MeasureAsync(options.WorkDirectory, start, lines =>
                    {
                        lock (governor)
                            return governor.OnDiff(lines, turn, spentSoFar, DateTimeOffset.UtcNow);
                    }, Govern, ct);
                }
                if (detector.Check(DateTimeOffset.UtcNow) is { } stall && stoppedBy.Set(SessionStatus.Stalled, StallDetector.Wire(stall)))
                    Interrupt(process);
                if (stoppedBy.Cause is not null && !exit.IsCompleted && DateTimeOffset.UtcNow - stoppedBy.At > Grace)
                    process.Kill(entireProcessTree: true);
            }
            await exit;
            await reader;
            _ = await stderr;
            if (measuring is not null)
                await measuring;
            string? governorHash = null;
            if (governorLog is not null)
            {
                await governorLog.DisposeAsync();
                governorHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(options.OutDirectory, GovernorFile), ct)));
            }

            var snapshotNote = stoppedBy.Cause is { } stopped ? await SnapshotAsync(options, stopped.Reason, detector.Turns, ct) : null;
            if (stoppedBy.Cause is { } stoppedFor)
                await WriteHandoffAsync(options, start, stoppedFor.Reason, detector.Turns, trace, ct);
            var bundle = await BundleAsync(options, ct);
            var commits = bundle && start is not null ? await CommitsAsync(options, start, ct) : 0;
            var (report, questions) = Extract(tally);
            if (report is not null)
                await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "driven-report.json"), Redact(report, options.Secrets), ct);
            var rateLimited = tally.ResultIsError && UsageLimit.Matches(tally.ResultText);
            var status = stoppedBy.Cause?.Status
                ?? (rateLimited ? SessionStatus.RateLimited : questions.Count > 0 ? SessionStatus.NeedsInput : tally.HasResult && !tally.ResultIsError ? SessionStatus.Completed : SessionStatus.Failed);
            var reason = stoppedBy.Cause?.Reason ?? (status == SessionStatus.RateLimited ? "rate_limited" : status == SessionStatus.Failed ? (tally.HasResult ? "session_error" : "no_result") : "");
            await stream.DisposeAsync();
            var streamHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(options.OutDirectory, "stream.jsonl"), ct)));
            var outcome = new SessionOutcome(status, reason, detector.Turns, tally.Input, tally.Output, tally.CacheRead, tally.CostUsd, process.ExitCode,
                [.. questions.Select(q => Redact(q, options.Secrets))], bundle, streamHash, tally.CacheWrite, tally.SessionId, tally.Model, tally.ModelUsage.Count == 0 ? null : tally.ModelUsage,
                tally.UsdEstimate, status == SessionStatus.RateLimited ? UsageLimit.ResetsAt(tally.ResultText) : null, tally.UsdEstimatePartial, commits, snapshotNote,
                governor is null ? null : decisions, governorHash);
            await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "session-outcome.json"), Redact(JsonSerializer.Serialize(outcome, OutcomeJson), options.Secrets), ct);
            return outcome;
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    /// <summary>Keeps <see cref="ContainerTemplate.UsageFile"/> current for the host's poll. It is written beside and renamed, so a read never sees half a file; a failed write only
    /// delays the host's view, never the session.</summary>
    private static void WriteUsage(string directory, SessionUsage usage, ref SessionUsage? last)
    {
        if (usage == last)
            return;
        try
        {
            var file = Path.Combine(directory, ContainerTemplate.UsageFile);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(usage, OutcomeJson));
            File.Move(file + ".tmp", file, overwrite: true);
            last = usage;
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Feeds the governor the branch's added plus removed lines against the commit the workspace started at, tracked files only. <c>--no-optional-locks</c> keeps the
    /// measure from taking the index lock the session's own git may need; a git that fails or hangs only skips this measure.</summary>
    private static async Task MeasureAsync(string work, string start, Func<long, IReadOnlyList<GovernorDecision>> onDiff, Func<IReadOnlyList<GovernorDecision>, Task> govern,
        CancellationToken ct)
    {
        var diff = await Git(work, ["--no-optional-locks", "diff", "--numstat", start], ct, TimeSpan.FromSeconds(20));
        if (diff.Exit != 0)
            return;
        long lines = 0;
        foreach (var row in diff.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cells = row.Split('\t');
            if (cells.Length >= 2 && long.TryParse(cells[0], NumberStyles.None, CultureInfo.InvariantCulture, out var added) && long.TryParse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture, out var removed))
                lines += added + removed;
        }
        await govern(onDiff(lines));
    }

    private static async Task WritePrivate(string path, string text, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var writer = new StreamWriter(new FileStream(path, options));
        await writer.WriteAsync(text.AsMemory(), ct);
    }

    private static void Interrupt(Process process)
    {
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", ["-INT", process.Id.ToString(CultureInfo.InvariantCulture)]) { RedirectStandardError = true });
            kill?.WaitForExit(2000);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The grace period ends in a kill either way.
        }
    }

    private static async Task<bool> BundleAsync(SessionOptions options, CancellationToken ct)
    {
        var branch = options.Task.Branch;
        if ((await Git(options.WorkDirectory, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], ct)).Exit != 0)
            return false;
        return (await Git(options.WorkDirectory, ["bundle", "create", Path.Combine(options.OutDirectory, "chargehand.bundle"), branch, "--not", "--remotes"], ct)).Exit == 0;
    }

    private static async Task<int> CommitsAsync(SessionOptions options, string start, CancellationToken ct)
    {
        var counted = await Git(options.WorkDirectory, ["rev-list", "--count", $"{start}..{options.Task.Branch}"], ct);
        return counted.Exit == 0 && int.TryParse(counted.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    /// <summary>Writes <see cref="ContainerTemplate.HandoffFile"/> and <see cref="ContainerTemplate.ProgressFile"/> after a stop (ADR 0045), from git and the stream with no model call: the
    /// commit the workspace started at and the branch's head, the commit subjects and changed paths between them, and what <paramref name="trace"/> kept. A git call that fails
    /// leaves its facts out; a file that cannot be written only means the next session starts without it.</summary>
    private static async Task WriteHandoffAsync(SessionOptions options, string? start, string reason, int turns, SessionTrace trace, CancellationToken ct)
    {
        var timeout = options.GitTimeout ?? SnapshotGitTimeout;
        async Task<string?> Read(params string[] args) => await Git(options.WorkDirectory, args, ct, timeout) is { Exit: 0 } r ? r.Stdout : null;
        var head = (await Read("rev-parse", "--verify", "--quiet", $"refs/heads/{options.Task.Branch}") ?? await Read("rev-parse", "--verify", "--quiet", "HEAD"))?.Trim();
        List<string> commits = [];
        List<ChangedPath> changes = [];
        if (start is not null && head is not null)
        {
            commits = [.. (await Read("log", "--reverse", "--format=%s", $"{start}..{head}") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            foreach (var row in (await Read("diff", "--numstat", "--no-renames", "-z", start, head) ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (row.Split('\t', 3) is [var added, var removed, var path])
                    changes.Add(new ChangedPath(path, long.TryParse(added, NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : 0,
                        long.TryParse(removed, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : 0));
        }
        var files = HandoffBuilder.Build(new HandoffFacts(reason, turns, start, head, commits, changes, trace.LastTest, trace.Steps, trace.Todos, trace.Calls), options.Secrets);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, ContainerTemplate.HandoffFile), Redact(files.Json, options.Secrets), ct);
            await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, ContainerTemplate.ProgressFile), Redact(files.Markdown, options.Secrets), ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A continuation session's workspace is the base commit on the task branch; the previous session's branch replaces it.</summary>
    private static async Task SeedAsync(SessionOptions options, CancellationToken ct)
    {
        var seed = Path.Combine(options.OutDirectory, ContainerTemplate.SeedFile);
        if (!File.Exists(seed))
            return;
        var fetch = await Git(options.WorkDirectory, ["-c", "fetch.fsckObjects=true", "-c", "core.hooksPath=/dev/null", "fetch", "--quiet", seed, $"refs/heads/{options.Task.Branch}"], ct);
        if (fetch.Exit != 0 || (await Git(options.WorkDirectory, ["reset", "--quiet", "--hard", "FETCH_HEAD"], ct)).Exit != 0)
            throw new InvalidOperationException($"the seed bundle does not apply to the workspace: {fetch.Stderr.Trim()}");
    }

    /// <summary>A file the stop snapshot leaves out: bigger than this is a build output or a download, not the session's work.</summary>
    public const long SnapshotMaxFileBytes = 5L * 1024 * 1024;

    private static readonly TimeSpan SnapshotGitTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Commits the working tree to the task branch (made if the session never made it) so a stopped session's files reach the bundle: the files over
    /// <see cref="SnapshotMaxFileBytes"/> stay out and the commit message names them. Nothing to commit leaves the branch as it is. A git that hangs or fails, or an
    /// <c>index.lock</c> a live git holds, skips the snapshot. A lock no git process holds is a killed session's leftover and is removed. The handover scans every commit's patch.</summary>
    /// <returns>What was not done and why, for the handover's text; null when there is nothing to say.</returns>
    private static async Task<string?> SnapshotAsync(SessionOptions options, string reason, int turns, CancellationToken ct)
    {
        var dir = options.WorkDirectory;
        var branch = options.Task.Branch;
        var timeout = options.GitTimeout ?? SnapshotGitTimeout;
        Task<(int Exit, string Stdout, string Stderr)> G(params string[] args) => Git(dir, args, ct, timeout);
        static string Skipped(string step, (int Exit, string Stdout, string Stderr) r) =>
            $"the working tree was not committed: git {step} {(r.Exit == GitTimedOut ? "timed out" : "failed")}";

        var gitLock = await G("rev-parse", "--git-path", "index.lock");
        if (gitLock.Exit != 0)
            return Skipped("rev-parse", gitLock);
        var lockFile = Path.GetFullPath(gitLock.Stdout.Trim(), dir);
        if (File.Exists(lockFile))
        {
            if ((options.GitRunning ?? AnyGitProcess)())
                return "the working tree was not committed: .git/index.lock exists and a git process is running";
            try
            {
                File.Delete(lockFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return "the working tree was not committed: .git/index.lock exists and could not be removed";
            }
        }
        var exists = await G("rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        if (exists.Exit == GitTimedOut)
            return Skipped("rev-parse", exists);
        if (exists.Exit != 0 && await G("checkout", "--quiet", "-b", branch) is { Exit: not 0 } made)
            return Skipped("checkout", made);
        var status = await G("status", "--porcelain");
        if (status.Exit != 0)
            return Skipped("status", status);
        if (status.Stdout.Length == 0)
            return null;
        var added = await G("add", "-A");
        if (added.Exit != 0)
            return Skipped("add", added);

        var staged = await G("diff", "--cached", "--name-only", "-z");
        if (staged.Exit != 0)
            return Skipped("diff", staged);
        var large = staged.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => new FileInfo(Path.Combine(dir, path)) is { Exists: true } file && file.Length > SnapshotMaxFileBytes).ToList();
        for (var i = 0; i < large.Count; i += 100)
            if (await G(["--literal-pathspecs", "reset", "--quiet", "HEAD", "--", .. large.Skip(i).Take(100)]) is { Exit: not 0 } unstaged)
                return Skipped("reset", unstaged);
        var note = large.Count == 0 ? null : $"not committed (over {SnapshotMaxFileBytes / 1024 / 1024} MiB): {string.Join(", ", large)}";
        if ((await G("diff", "--cached", "--quiet")).Exit == 0)
            return note;
        var message = new List<string> { "-m", $"WIP: session stopped by {reason} after {turns.ToString(CultureInfo.InvariantCulture)} turns" };
        if (note is not null)
            message.AddRange(["-m", note]);
        var commit = await G(["-c", "core.hooksPath=/dev/null", "commit", "--quiet", "--no-verify", .. message]);
        return commit.Exit == 0 ? note : Skipped("commit", commit);
    }

    private const int GitTimedOut = -1;

    private static bool AnyGitProcess()
    {
        var running = Process.GetProcessesByName("git");
        try
        {
            return running.Length > 0;
        }
        finally
        {
            foreach (var p in running)
                p.Dispose();
        }
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> Git(string directory, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } limit)
            deadline.CancelAfter(limit);
        try
        {
            await p.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            p.Kill(entireProcessTree: true);
            return (GitTimedOut, "", "git timed out");
        }
        return (p.ExitCode, await stdout, await stderr);
    }

    internal static string Redact(string text, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
            if (secret.Length >= 8)
                text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return text;
    }

    /// <summary>The report is the last <c>```json</c> block of the last assistant text that has one; the questions are the lines after the last
    /// <c>NEEDS_INPUT:</c> line.</summary>
    private static (string? Report, List<string> Questions) Extract(Tally tally)
    {
        string? report = null;
        foreach (var text in tally.Texts)
            foreach (Match m in JsonBlock().Matches(text))
                try
                {
                    _ = JsonDocument.Parse(m.Groups[1].Value);
                    report = m.Groups[1].Value.Trim();
                }
                catch (JsonException) { }
        var questions = new List<string>();
        foreach (var text in tally.Texts)
        {
            var at = text.LastIndexOf("NEEDS_INPUT:", StringComparison.Ordinal);
            if (at < 0)
                continue;
            questions.Clear();
            foreach (var line in text[(at + "NEEDS_INPUT:".Length)..].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                questions.Add(Numbering().Replace(line, "").Trim());
        }
        return (report, questions.Where(q => q.Length > 0).ToList());
    }

    [GeneratedRegex(@"```json\s*(.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();

    [GeneratedRegex(@"^(\d+[.)]|[-*])\s*")]
    private static partial Regex Numbering();

    private sealed class StopCause
    {
        private readonly object _lock = new();

        public (SessionStatus Status, string Reason)? Cause { get; private set; }

        public DateTimeOffset At { get; private set; }

        /// <returns>True for the first cause only.</returns>
        public bool Set(SessionStatus status, string reason)
        {
            lock (_lock)
            {
                if (Cause is not null)
                    return false;
                Cause = (status, reason);
                At = DateTimeOffset.UtcNow;
                return true;
            }
        }
    }

    /// <summary>Running totals from the stream: per-message tokens while it runs (so a cap can bite), the <c>result</c> event's totals at the end. Each assistant message's usage
    /// carries its model and cache counts, so the cache totals and the dollar estimate are live too.</summary>
    private sealed class Tally(IReadOnlyDictionary<string, ModelPrice>? prices = null)
    {
        private readonly Dictionary<string, (string? Model, TokenCounts Tokens)> _perMessage = [];
        private long _resultIn, _resultOut, _resultCache, _resultCacheWrite;

        public List<string> Texts { get; } = [];
        public bool HasResult { get; private set; }
        public bool ResultIsError { get; private set; }
        public string? ResultText { get; private set; }
        public decimal? CostUsd { get; private set; }
        public long Input => HasResult ? _resultIn : _perMessage.Values.Sum(v => v.Tokens.Input);
        public long Output => HasResult ? _resultOut : _perMessage.Values.Sum(v => v.Tokens.Output);
        public long CacheRead => HasResult ? _resultCache : _perMessage.Values.Sum(v => v.Tokens.CacheRead);
        public long CacheWrite => HasResult ? _resultCacheWrite : _perMessage.Values.Sum(v => v.Tokens.CacheWrite);
        public string? SessionId { get; private set; }
        public string? Model { get; private set; }
        public Dictionary<string, TokenCounts> ModelUsage { get; } = [];

        /// <summary>The per-message tokens priced with the task's table, model by model; null without a table or when no message could be priced (unknown, never 0).</summary>
        public decimal? UsdEstimate => Estimate().Usd;

        /// <summary>Some message's model had no price, so <see cref="UsdEstimate"/> sums only the others.</summary>
        public bool UsdEstimatePartial => Estimate().Partial;

        private (decimal? Usd, bool Partial) Estimate()
        {
            if (prices is null)
                return (null, false);
            var table = new PriceTable(prices);
            decimal total = 0;
            var any = false;
            var partial = false;
            foreach (var group in _perMessage.Values.GroupBy(v => v.Model ?? Model))
            {
                var sum = new TokenCounts(group.Sum(v => v.Tokens.Input), group.Sum(v => v.Tokens.Output), 0, group.Sum(v => v.Tokens.CacheRead), group.Sum(v => v.Tokens.CacheWrite));
                if (group.Key is { } model && DrivenTelemetry.Find(prices, model) is { } key && table.PriceUsd(key, sum) is { } usd)
                {
                    total += usd;
                    any = true;
                }
                else
                    partial = true;
            }
            return any ? (total, partial) : (null, false);
        }

        public void Add(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type))
                return;
            if (e.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                SessionId = sid.GetString();
            switch (type.GetString())
            {
                case "system" when e.TryGetProperty("subtype", out var sub) && sub.GetString() == "init" && e.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String:
                    Model = model.GetString();
                    break;
                case "assistant" when e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object:
                    if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && m.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    {
                        var counts = new TokenCounts(Long(u, "input_tokens"), Long(u, "output_tokens"), 0, Long(u, "cache_read_input_tokens"), Long(u, "cache_creation_input_tokens"));
                        // A message with no usage (Claude Code's synthetic error messages) has no price and nothing to count.
                        if (counts.Input + counts.Output + counts.CacheRead + counts.CacheWrite > 0)
                            _perMessage[id.GetString()!] = (m.TryGetProperty("model", out var mm) && mm.ValueKind == JsonValueKind.String ? mm.GetString() : null, counts);
                    }
                    if (m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        foreach (var block in content.EnumerateArray())
                            if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var text) && text.GetString() is { } s)
                                Texts.Add(s);
                    break;
                case "result":
                    HasResult = true;
                    ResultIsError = e.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
                    if (e.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
                        CostUsd = cost.GetDecimal();
                    if (e.TryGetProperty("usage", out var ru) && ru.ValueKind == JsonValueKind.Object)
                    {
                        _resultIn = Long(ru, "input_tokens");
                        _resultOut = Long(ru, "output_tokens");
                        _resultCache = Long(ru, "cache_read_input_tokens");
                        _resultCacheWrite = Long(ru, "cache_creation_input_tokens");
                    }
                    if (e.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
                        foreach (var p in mu.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object))
                            ModelUsage[p.Name] = new TokenCounts(Long(p.Value, "inputTokens"), Long(p.Value, "outputTokens"), 0, Long(p.Value, "cacheReadInputTokens"),
                                Long(p.Value, "cacheCreationInputTokens"));
                    if (e.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String && result.GetString() is { } r)
                    {
                        ResultText = r;
                        Texts.Add(r);
                    }
                    break;
            }
        }

        private static long Long(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }
}
