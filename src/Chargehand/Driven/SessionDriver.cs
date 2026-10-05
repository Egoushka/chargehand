using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Containers;

namespace Chargehand.Driven;

/// <param name="Goal">What the change should do; it goes after <c>/chargehand:change</c>, so it cannot replace the skill.</param>
/// <param name="Branch">The branch the skill is told to use, <c>chargehand/&lt;run&gt;</c>.</param>
/// <param name="MaxTokens">Input plus output tokens the session may use; counted from the stream, so it binds with a subscription too.</param>
/// <param name="MaxUsd">Passed to <c>--max-budget-usd</c>; only meaningful where the credential is priced.</param>
public sealed record SessionTask(string RunId, string Goal, string Branch, int MaxTurns, int MaxMinutes, int NoProgressSeconds,
    long? MaxTokens = null, string? Model = null, decimal? MaxUsd = null);

/// <summary>How to start Claude Code: the program and arguments that go before its own (a test runs a script as <c>sh script</c>).</summary>
public sealed record SessionCommand(string File, IReadOnlyList<string> LeadingArgs)
{
    public static SessionCommand Claude { get; } = new("claude", []);
}

/// <param name="Secrets">Values replaced by <c>[redacted]</c> in everything the driver stores.</param>
/// <param name="PromptText">Appended to Claude Code's system prompt.</param>
/// <param name="McpUrl">The chargehand server's MCP address on the batch network, for the skill's research and review calls.</param>
/// <param name="McpToken">The run-scoped token for it.</param>
public sealed record SessionOptions(string WorkDirectory, string OutDirectory, SessionTask Task, SessionCommand Command, IReadOnlyList<string> Secrets,
    string PromptText, string? McpUrl, string? McpToken, string PluginDirectory = "/opt/chargehand-plugin");

/// <summary>The running tally the session writes to <c>session-usage.json</c>: input plus output tokens so far, assistant messages so far, and the <c>change</c> skill's step of the
/// latest tool call that names one (<see cref="Adherence.StageOf"/>). Dollars are not known until the stream's <c>result</c>.</summary>
public sealed record SessionUsage(long Tokens, int? Turns = null, string? Stage = null);

public enum SessionStatus { Completed, NeedsInput, Stalled, TokenCap, Failed }

/// <param name="Reason">For a stall, its wire name; for a failure, a short cause.</param>
/// <param name="BundleWritten">A bundle of the session's branch is in the output directory; the handover decides whether to use it.</param>
/// <param name="StreamSha256">The SHA-256 of the stored stream (<c>stream.jsonl</c>, secrets redacted), so a result can cite the log without carrying it.</param>
public sealed record SessionOutcome(SessionStatus Status, string Reason, int Turns, long InputTokens, long OutputTokens, long CacheReadTokens,
    decimal? CostUsd, int? ExitCode, IReadOnlyList<string> Questions, bool BundleWritten, string? StreamSha256 = null);

/// <summary>Runs headless Claude Code on the <c>change</c> skill inside a session container (ADR 0039), watches its stream, ends it when it
/// stalls or passes its token cap, and leaves three files in the output directory: <c>stream.jsonl</c> (secrets redacted), <c>chargehand.bundle</c>
/// (the session's branch) and <c>driven-report.json</c> (the report the session printed), plus <c>session-outcome.json</c>.</summary>
public static partial class SessionDriver
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

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
            var tally = new Tally();
            SessionUsage? lastUsage = null;
            string? stage = null;
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start Claude Code");
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await using var stream = new StreamWriter(new FileStream(Path.Combine(options.OutDirectory, "stream.jsonl"), FileMode.Create, FileAccess.Write), new UTF8Encoding(false));
            var stoppedBy = new StopCause();

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
                    stage = Adherence.StageOf(e) ?? stage;
                    WriteUsage(options.OutDirectory, new SessionUsage(tally.Input + tally.Output, detector.Turns, stage), ref lastUsage);
                    if (task.MaxTokens is { } cap && tally.Input + tally.Output >= cap && stoppedBy.Set(SessionStatus.TokenCap, "token_cap"))
                        Interrupt(process);
                }
            }, ct);

            using var watch = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            var exit = process.WaitForExitAsync(ct);
            while (!exit.IsCompleted)
            {
                var tick = watch.WaitForNextTickAsync(ct).AsTask();
                await Task.WhenAny(exit, tick);
                if (exit.IsCompleted)
                    break;
                if (detector.Check(DateTimeOffset.UtcNow) is { } stall && stoppedBy.Set(SessionStatus.Stalled, StallDetector.Wire(stall)))
                    Interrupt(process);
                if (stoppedBy.Cause is not null && !exit.IsCompleted && DateTimeOffset.UtcNow - stoppedBy.At > Grace)
                    process.Kill(entireProcessTree: true);
            }
            await exit;
            await reader;
            _ = await stderr;

            var bundle = await BundleAsync(options, ct);
            var (report, questions) = Extract(tally);
            if (report is not null)
                await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "driven-report.json"), Redact(report, options.Secrets), ct);
            var status = stoppedBy.Cause?.Status
                ?? (questions.Count > 0 ? SessionStatus.NeedsInput : tally.HasResult && !tally.ResultIsError ? SessionStatus.Completed : SessionStatus.Failed);
            var reason = stoppedBy.Cause?.Reason ?? (status == SessionStatus.Failed ? (tally.HasResult ? "session_error" : "no_result") : "");
            await stream.DisposeAsync();
            var streamHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(options.OutDirectory, "stream.jsonl"), ct)));
            var outcome = new SessionOutcome(status, reason, detector.Turns, tally.Input, tally.Output, tally.CacheRead, tally.CostUsd, process.ExitCode,
                [.. questions.Select(q => Redact(q, options.Secrets))], bundle, streamHash);
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
        if (await Git(options.WorkDirectory, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], ct) != 0)
            return false;
        return await Git(options.WorkDirectory, ["bundle", "create", Path.Combine(options.OutDirectory, "chargehand.bundle"), branch, "--not", "--remotes"], ct) == 0;
    }

    private static async Task<int> Git(string directory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync(ct);
        _ = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    private static string Redact(string text, IReadOnlyList<string> secrets)
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

    /// <summary>Running totals from the stream: per-message tokens while it runs (so a cap can bite), the <c>result</c> event's totals at the end.</summary>
    private sealed class Tally
    {
        private readonly Dictionary<string, (long In, long Out)> _perMessage = [];
        private long _resultIn, _resultOut, _resultCache;

        public List<string> Texts { get; } = [];
        public bool HasResult { get; private set; }
        public bool ResultIsError { get; private set; }
        public decimal? CostUsd { get; private set; }
        public long Input => HasResult ? _resultIn : _perMessage.Values.Sum(v => v.In);
        public long Output => HasResult ? _resultOut : _perMessage.Values.Sum(v => v.Out);
        public long CacheRead => _resultCache;

        public void Add(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type))
                return;
            switch (type.GetString())
            {
                case "assistant" when e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object:
                    if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && m.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                        _perMessage[id.GetString()!] = (Long(u, "input_tokens"), Long(u, "output_tokens"));
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
                    }
                    if (e.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String && result.GetString() is { } r)
                        Texts.Add(r);
                    break;
            }
        }

        private static long Long(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }
}
