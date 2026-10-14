using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Sessions.Api;

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

    public const string GovernorFile = ContainerTemplate.GovernorFile;

    private static readonly SessionsJsonContext LineJson = new(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    private static readonly SessionsJsonContext OutcomeJson = new(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true });

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
        JsonSerializer.Serialize(new McpConfigFile(new McpConfigServers(new McpConfigServer("http", url, new McpConfigHeaders($"Bearer {token}")))), SessionsJsonContext.Default.McpConfigFile);

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

            var psi = StartInfo(options, mcpFile, promptFile);
            var started = DateTimeOffset.UtcNow;
            var detector = new StallDetector(TimeSpan.FromSeconds(task.NoProgressSeconds), 5, task.MaxTurns, TimeSpan.FromMinutes(task.MaxMinutes), started);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start Claude Code");
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await using var stream = new StreamWriter(new FileStream(Path.Combine(options.OutDirectory, "stream.jsonl"), FileMode.Create, FileAccess.Write), new UTF8Encoding(false));
            var governor = task.Governor is { Mode: not GovernorMode.Off } governed ? new Governor(governed) : null;
            await using var governorLog = governor is null ? null
                : new StreamWriter(new FileStream(Path.Combine(options.OutDirectory, GovernorFile), FileMode.Create, FileAccess.Write), new UTF8Encoding(false));
            var run = new Run(options, process, stream, detector, governor, governorLog, new SemaphoreSlim(1, 1));

            var reader = Task.Run(() => run.ReadAsync(ct), ct);
            var exit = process.WaitForExitAsync(ct);
            var measuring = await run.WatchAsync(exit, start, started, ct);
            await exit;
            await reader;
            _ = await stderr;
            if (measuring is not null)
                await measuring;
            return await run.FinishAsync(start, ct);
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static ProcessStartInfo StartInfo(SessionOptions options, string mcpFile, string promptFile)
    {
        var psi = new ProcessStartInfo(options.Command.File)
        {
            WorkingDirectory = options.WorkDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in options.Command.LeadingArgs.Concat(ClaudeArgs(options.Task, options.PluginDirectory, mcpFile, promptFile)))
            psi.ArgumentList.Add(a);
        psi.Environment["CHARGEHAND_BRANCH"] = options.Task.Branch;
        psi.Environment["CHARGEHAND_DRIVEN"] = "1";
        return psi;
    }

    /// <summary>The session's status and reason: the cause it was stopped for, else what the stream's result and the questions say.</summary>
    private static (SessionStatus Status, string Reason) Conclude(StopCause stoppedBy, Tally tally, IReadOnlyList<string> questions)
    {
        var rateLimited = tally.ResultIsError && UsageLimit.Matches(tally.ResultText);
        var status = stoppedBy.Cause?.Status
            ?? (rateLimited ? SessionStatus.RateLimited : questions.Count > 0 ? SessionStatus.NeedsInput : tally.HasResult && !tally.ResultIsError ? SessionStatus.Completed : SessionStatus.Failed);
        var reason = stoppedBy.Cause?.Reason ?? (status == SessionStatus.RateLimited ? "rate_limited" : status == SessionStatus.Failed ? (tally.HasResult ? "session_error" : "no_result") : "");
        return (status, reason);
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
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(usage, OutcomeJson.SessionUsage));
            File.Move(file + ".tmp", file, overwrite: true);
            last = usage;
        }
        catch (IOException)
        {
        }
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

    internal static string Redact(string text, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
            if (secret.Length >= 8)
                text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return text;
    }
}
