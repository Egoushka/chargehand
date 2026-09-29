using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.ClaudeCode;

/// <summary>
/// IWorkerRuntime over the Claude Code CLI in print mode (ADR 0020): one <c>claude -p</c> process per turn, the first
/// with <c>--session-id</c>, later ones with <c>--resume</c>. Sessions live in this process; the CLI persists the
/// transcript. Create with <see cref="ConnectAsync"/>, which pins the version.
/// Two credentials: an API key (billed per token, <c>--bare</c>) or a subscription OAuth token from
/// <c>claude setup-token</c> (the owner's plan limits, no per-token bill); with neither, the CLI's own login.
/// <c>--bare</c> ignores OAuth and the keychain, so the subscription and login modes isolate with
/// <c>--setting-sources ""</c> instead.
/// Granted services (ADR 0034) reach the worker as a private <c>--mcp-config</c> file, written per turn and removed when
/// the process exits, with <c>--allowedTools</c> naming exactly the granted tools.
/// </summary>
public sealed class ClaudeCodeWorkerRuntime : IWorkerRuntime, IServiceHealth
{
    /// <summary>Prefix of the per-turn directories that hold the MCP config, which carries resolved secrets.</summary>
    private const string ConfigDirPrefix = "chargehand-mcp-";
    private const string ConfigFile = "mcp.json";

    private readonly string _binary;
    private readonly ClaudeCodeCredential _credential;
    private readonly Uri? _baseUrl;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    internal ClaudeCodeWorkerRuntime(string binary, ClaudeCodeCredential credential, string version, Uri? baseUrl = null)
    {
        (_binary, _credential, _baseUrl) = (binary, credential, baseUrl);
        Version = $"claude-code/{version}";
    }

    public string Version { get; }

    /// <summary>Where the per-turn MCP config directories go: the system temp directory, never a session's checkout.</summary>
    internal string ConfigRoot { get; set; } = Path.GetTempPath();

    /// <summary>Refuses a CLI whose version differs from the pinned one. <c>claude --version</c> prints "2.1.283 (Claude Code)".</summary>
    public static async Task<ClaudeCodeWorkerRuntime> ConnectAsync(string binary, string pinnedVersion, ClaudeCodeCredential credential, CancellationToken ct,
        Uri? baseUrl = null)
    {
        (int Exit, string Stdout, string Stderr) run;
        try
        {
            run = await Exec(binary, Path.GetTempPath(), ["--version"], null, ct);
        }
        catch (Win32Exception e)
        {
            throw new ChargehandException(ErrorCode.RuntimeUnavailable, $"cannot start {binary}: {e.Message}", "Install Claude Code, or set claude_code.binary in the profile.");
        }
        var version = run.Stdout.Split(' ', 2)[0].Trim();
        if (run.Exit != 0)
            throw new ChargehandException(ErrorCode.RuntimeUnavailable, $"{binary} --version exited {run.Exit}: {run.Stderr.Trim()}",
                $"Run {binary} --version by hand and fix what it reports.");
        if (version != pinnedVersion)
            throw new ChargehandException(ErrorCode.RuntimeVersionMismatch, $"Claude Code CLI is {version}; this adapter is pinned to {pinnedVersion}.",
                $"Install Claude Code {pinnedVersion}, or change claude_code.version in the profile.");
        var runtime = new ClaudeCodeWorkerRuntime(binary, credential, version, baseUrl);
        // The CLI login fails only on the first model call otherwise; `auth status` exits 1 when signed out, no call made.
        if (credential.Secret is null && (await Exec(binary, Path.GetTempPath(), ["auth", "status"], null, ct, runtime.Env)).Exit != 0)
            throw new ChargehandException(ErrorCode.RuntimeUnavailable, $"{binary} is not signed in and no Claude Code credential is set",
                $"Run {binary} and sign in, or set CLAUDE_CODE_OAUTH_TOKEN (from claude setup-token) or ANTHROPIC_API_KEY.");
        SweepStaleConfigs(runtime.ConfigRoot, TimeSpan.FromDays(1));
        return runtime;
    }

    /// <summary>
    /// Removes config directories a crash left behind (the process died between writing the file and the turn ending). A turn lasts
    /// minutes, so a directory a day old is nobody's. Best effort: what cannot be removed stays for the next connect.
    /// </summary>
    internal static void SweepStaleConfigs(string root, TimeSpan olderThan)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, ConfigDirPrefix + "*"))
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow - olderThan)
                    RemoveConfigDir(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable temp directory is not a reason to refuse to connect.
        }
    }

    public async Task<WorkerSession> CreateAsync(NodeSpec spec, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        var (exit, head, _) = await Exec("git", spec.Directory, ["rev-parse", "HEAD"], null, ct);
        _sessions[id] = new Session(id, spec, exit == 0 ? head.Trim() : null);
        return new WorkerSession(id, spec.Directory);
    }

    public Task SetInstructionAsync(string sessionId, string key, string value, CancellationToken ct)
    {
        var s = Get(sessionId);
        lock (s)
        {
            if (s.Started)
                throw new InvalidOperationException("instruction entries are fixed once the session's first prompt is sent");
            s.Instructions[key] = value;
        }
        return Task.CompletedTask;
    }

    public async Task SubmitAsync(string sessionId, string text, CancellationToken ct)
    {
        var s = Get(sessionId);
        if (s.Turn is { IsCompleted: false })
            throw new InvalidOperationException($"session {sessionId} is busy");
        if (s.CompactPending)
        {
            s.CompactPending = false;
            await (s.Turn = Start(s, "/compact", compact: true));
        }
        lock (s)
            s.Messages.Add(new WorkerMessage($"user_{Guid.NewGuid():N}", WorkerMessageKind.User, DateTimeOffset.UtcNow, text, null));
        s.Turn = Start(s, text, compact: false);
    }

    public async Task<IdleOutcome> AwaitIdleAsync(string sessionId, CancellationToken ct)
    {
        var s = Get(sessionId);
        if (s.Turn is { } turn)
            await turn.WaitAsync(ct);
        lock (s)
            return s.Messages.LastOrDefault(m => m.Kind == WorkerMessageKind.Idle)?.Outcome ?? IdleOutcome.Failed;
    }

    public async Task InterruptAsync(string sessionId, CancellationToken ct)
    {
        var s = Get(sessionId);
        s.Interrupted = true;
        try
        {
            s.Process?.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        if (s.Turn is { } turn)
            await turn.WaitAsync(ct);
    }

    /// <summary>Newest first, like the OpenCode adapter.</summary>
    public Task<IReadOnlyList<WorkerMessage>> ReadMessagesAsync(string sessionId, CancellationToken ct)
    {
        var s = Get(sessionId);
        lock (s)
            return Task.FromResult<IReadOnlyList<WorkerMessage>>(Enumerable.Reverse(s.Messages).ToList());
    }

    /// <summary>What the CLI's <c>init</c> event said about the granted servers, latest turn included; see <see cref="IServiceHealth"/>.</summary>
    public IReadOnlyDictionary<string, string> UnavailableServices(string sessionId)
    {
        var s = Get(sessionId);
        lock (s)
            return new Dictionary<string, string>(s.UnavailableServices);
    }

    /// <summary>Always empty: the CLI runs in <c>dontAsk</c> mode, where anything not allowed is denied without asking.</summary>
    public Task<IReadOnlyList<PermissionRequest>> PendingPermissionsAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PermissionRequest>>([]);

    public Task AnswerPermissionAsync(string sessionId, string requestId, PermissionDecision decision, string? message, CancellationToken ct) =>
        throw new InvalidOperationException("Claude Code runs in dontAsk mode and never has a pending permission request");

    /// <summary>
    /// Supports only a fork before the first user message (the one WorkerNode makes): a fresh session with the same
    /// spec and instruction entries. The prompt cache is keyed by content, so it still reads the sibling's prefix.
    /// </summary>
    public Task<WorkerSession> ForkAsync(string sessionId, string? beforeMessageId, CancellationToken ct)
    {
        var source = Get(sessionId);
        Session fork;
        lock (source)
        {
            if (beforeMessageId is null || source.Messages.FirstOrDefault(m => m.Kind == WorkerMessageKind.User)?.Id != beforeMessageId)
                throw new NotSupportedException("Claude Code forks only before a session's first user message");
            fork = new Session(Guid.NewGuid().ToString(), source.Spec, source.BaseCommit);
            foreach (var (k, v) in source.Instructions)
                fork.Instructions[k] = v;
        }
        _sessions[fork.Id] = fork;
        return Task.FromResult(new WorkerSession(fork.Id, fork.Spec.Directory));
    }

    /// <summary>A print-mode session cannot be compacted mid-turn; <c>/compact</c> runs before the next prompt.</summary>
    public Task CompactAsync(string sessionId, CancellationToken ct)
    {
        Get(sessionId).CompactPending = true;
        return Task.CompletedTask;
    }

    /// <summary>Working tree against the commit at session creation, untracked files included; empty outside git.</summary>
    public async Task<IReadOnlyList<FileDiff>> DiffAsync(string sessionId, CancellationToken ct)
    {
        var s = Get(sessionId);
        if (s.BaseCommit is null)
            return [];
        var dir = s.Spec.Directory;
        var patches = new StringBuilder((await Exec("git", dir, ["-c", "core.quotepath=off", "diff", "--no-color", "--no-ext-diff", s.BaseCommit], null, ct)).Stdout);
        foreach (var file in (await Exec("git", dir, ["-c", "core.quotepath=off", "ls-files", "--others", "--exclude-standard"], null, ct)).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            patches.Append((await Exec("git", dir, ["diff", "--no-color", "--no-index", "--", "/dev/null", file], null, ct)).Stdout);
        return ParseDiff(patches.ToString());
    }

    /// <summary>
    /// One-shot, no tools, not persisted, in the temp directory. Measured on intake (2.1.195, a small model): the CLI
    /// thinks by default, 80% of the output and 15-19 s of API time per call, and its agent system prompt is a 5.4k-token
    /// cache write that the next call does not read. Without thinking and with a one-line system prompt the call takes
    /// ~4 s at 30% of the cost.
    /// </summary>
    public async Task<string> GenerateAsync(ModelRef? model, string prompt, CancellationToken ct)
    {
        var env = new Dictionary<string, string?>(Env) { ["MAX_THINKING_TOKENS"] = "0" };
        var (exit, stdout, stderr) = await Exec(_binary, Path.GetTempPath(),
            [.. CommonArgs(model), "--output-format", "json", "--no-session-persistence", "--tools", "",
                "--system-prompt", "Follow the instructions in the user message exactly."], prompt, ct, env);
        var result = exit == 0 && stdout.Length > 0 ? JsonDocument.Parse(stdout).RootElement : default;
        if (result.ValueKind != JsonValueKind.Object || result.GetProperty("is_error").GetBoolean())
            throw new InvalidOperationException($"claude -p exited {exit}: {(result.ValueKind == JsonValueKind.Object ? ErrorText(result) : stderr.Trim())}");
        return result.GetProperty("result").GetString() ?? "";
    }

    private Session Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : throw new KeyNotFoundException($"no Claude Code session {sessionId} in this process");

    /// <summary>
    /// Only the profile decides credential and endpoint: an inherited API key would outrank the OAuth token, and an
    /// inherited base URL or auth token would reroute the worker, so the variables this runtime does not set are removed.
    /// With the CLI login none is set, so an inherited one cannot switch the worker to another account.
    /// </summary>
    private IReadOnlyDictionary<string, string?> Env => new Dictionary<string, string?>
    {
        ["ANTHROPIC_API_KEY"] = _credential.Subscription ? null : _credential.Secret,
        ["CLAUDE_CODE_OAUTH_TOKEN"] = _credential.Subscription ? _credential.Secret : null,
        ["ANTHROPIC_AUTH_TOKEN"] = null,
        ["ANTHROPIC_BASE_URL"] = _baseUrl?.ToString().TrimEnd('/'),
    };

    /// <summary>
    /// <c>--bare</c> keeps the owner's hooks, plugins, CLAUDE.md, auto-memory and keychain out of the worker (the
    /// ADR 0003 isolation); with a subscription, no setting sources keeps the owner's hooks and plugins out. The
    /// prompt goes on stdin because the tool flags are variadic. A null model omits <c>--model</c>: the CLI's own
    /// default (ADR 0026).
    /// </summary>
    private List<string> CommonArgs(ModelRef? model)
    {
        List<string> args = ["-p", .. _credential.Subscription ? ["--setting-sources", ""] : new[] { "--bare" }, "--strict-mcp-config"];
        if (model is not null)
        {
            args.AddRange(["--model", model.ModelId]);
            if (model.Variant is { } effort)
                args.AddRange(["--effort", effort]);
        }
        return args;
    }

    private Task Start(Session s, string prompt, bool compact)
    {
        List<string> args = [.. CommonArgs(s.Spec.Model), "--output-format", "stream-json", "--verbose", "--exclude-dynamic-system-prompt-sections"];
        lock (s)
        {
            args.AddRange(s.Started ? ["--resume", s.Id] : ["--session-id", s.Id]);
            s.Started = true;
            if (s.Instructions.Count > 0)
                args.AddRange(["--append-system-prompt", string.Join("\n\n", s.Instructions.Values)]);
        }
        var (tools, allowedTools, disallowedTools) = Permissions(s.Spec.Permissions);
        IReadOnlyList<string> allowed = allowedTools, disallowed = disallowedTools;
        string? configDir = null;
        if (s.Spec.Services is { Count: > 0 } grants)
        {
            // --mcp-config takes a list, so it goes before another flag; --strict-mcp-config (in CommonArgs) keeps every other server out.
            var (json, granted, hidden) = ServiceConfig(grants);
            configDir = WriteConfig(json);
            args.AddRange(["--mcp-config", Path.Combine(configDir, ConfigFile)]);
            allowed = [.. allowed, .. granted];
            disallowed = [.. disallowed, .. hidden];
        }
        args.AddRange(["--permission-mode", "dontAsk", "--tools", string.Join(",", tools)]);
        if (allowed.Count > 0)
            args.AddRange(["--allowedTools", .. allowed]);
        if (disallowed.Count > 0)
            args.AddRange(["--disallowedTools", .. disallowed]);

        s.Interrupted = false;
        Process p;
        try
        {
            p = Process.Start(Psi(_binary, s.Spec.Directory, args, Env))!;
        }
        catch
        {
            RemoveConfigDir(configDir);
            throw;
        }
        s.Process = p;
        return Task.Run(async () =>
        {
            try
            {
                await p.StandardInput.WriteAsync(prompt);
                p.StandardInput.Close();
                var stderr = p.StandardError.ReadToEndAsync();
                var sawResult = false;
                while (await p.StandardOutput.ReadLineAsync() is { } line)
                    if (line.StartsWith('{'))
                    {
                        using var doc = JsonDocument.Parse(line);
                        lock (s)
                            sawResult |= Apply(s, doc.RootElement, compact);
                    }
                await p.WaitForExitAsync();
                lock (s)
                    if (!sawResult && !compact)
                        s.Messages.Add(Idle(s.Interrupted ? IdleOutcome.Interrupted : IdleOutcome.Failed,
                            Redact($"claude exited {p.ExitCode}: {stderr.Result.Trim()}", s.Spec.Services)));
                s.Process = null;
                p.Dispose();
            }
            finally
            {
                RemoveConfigDir(configDir);
            }
        });
    }

    /// <summary>
    /// The <c>--mcp-config</c> content for the granted servers, and the tool lists for the CLI: the granted tools to allow
    /// (<c>dontAsk</c> refuses any MCP tool not named, 2.1.283) and the server's other tools to disallow, which the CLI would
    /// otherwise still list and send to the model (about 50 tokens each). <c>--tools</c> does not touch MCP tools.
    /// The JSON holds the resolved secrets: it goes to a private file, never to a command line or a log.
    /// </summary>
    internal static (string Json, IReadOnlyList<string> Allowed, IReadOnlyList<string> Disallowed) ServiceConfig(IReadOnlyList<ServiceGrant> grants)
    {
        var servers = new Dictionary<string, Dictionary<string, object>>();
        foreach (var grant in grants)
        {
            var entry = new Dictionary<string, object>();
            switch (grant.Transport)
            {
                case HttpServiceTransport http:
                    entry["type"] = http.Protocol == HttpServiceProtocol.Sse ? "sse" : "http";
                    entry["url"] = http.Url.AbsoluteUri;
                    if (http.Headers.Count > 0)
                        entry["headers"] = http.Headers;
                    break;
                case StdioServiceTransport stdio:
                    entry["command"] = stdio.Command[0];
                    entry["args"] = stdio.Command.Skip(1).ToList();
                    if (stdio.Env.Count > 0)
                        entry["env"] = stdio.Env;
                    break;
                default:
                    throw new NotSupportedException($"no Claude Code config for {grant.Transport.GetType().Name}");
            }
            servers[grant.Server] = entry;
        }
        return (JsonSerializer.Serialize(new { mcpServers = servers }),
            [.. grants.SelectMany(g => g.Tools.Select(t => ToolName(g.Server, t)))],
            [.. grants.SelectMany(g => (g.Hidden ?? []).Select(t => ToolName(g.Server, t)))]);
    }

    /// <summary><c>mcp__server__tool</c>, each part with what the CLI would not keep replaced by "_" (2.1.283: a tool <c>echo.fact</c> is listed
    /// as <c>echo_fact</c>). Profile server names are already letters, digits and hyphens.</summary>
    private static string ToolName(string server, string tool) => $"mcp__{Spell(server)}__{Spell(tool)}";

    private static string Spell(string name) => string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_'));

    /// <summary>A fresh directory only the runtime's user can enter, holding the config file only that user can read.</summary>
    private string WriteConfig(string json)
    {
        var dir = Path.Combine(ConfigRoot, ConfigDirPrefix + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(dir);
        else
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(Path.Combine(dir, ConfigFile), options);
            file.Write(Encoding.UTF8.GetBytes(json));
            return dir;
        }
        catch
        {
            RemoveConfigDir(dir);
            throw;
        }
    }

    /// <summary>Best effort: a directory that cannot be removed now is swept on a later connect.</summary>
    private static void RemoveConfigDir(string? dir)
    {
        if (dir is null)
            return;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Text the CLI printed, without the exact header and environment values of the session's grants: the CLI may echo what it was
    /// given, and this text reaches the run log. Values under four characters are left, as they would mangle ordinary words.
    /// </summary>
    private static string Redact(string text, IReadOnlyList<ServiceGrant>? grants)
    {
        foreach (var value in (grants ?? []).SelectMany(g => g.Transport switch
                 {
                     HttpServiceTransport http => http.Headers.Values,
                     StdioServiceTransport stdio => stdio.Env.Values,
                     _ => [],
                 }).Where(v => v.Length >= 4).OrderByDescending(v => v.Length))
            text = text.Replace(value, "[redacted]", StringComparison.Ordinal);
        return text;
    }

    /// <summary>Folds one stream-json event into the session's messages. Returns true on the final result event.</summary>
    internal static bool Apply(Session s, JsonElement e, bool compact)
    {
        var now = DateTimeOffset.UtcNow;
        var last = s.Messages.Count > 0 ? s.Messages[^1] : null;
        switch (e.GetProperty("type").GetString())
        {
            case "assistant":
                // The CLI emits one event per content block; blocks of one API message share its id.
                var m = e.GetProperty("message");
                var id = m.GetProperty("id").GetString()!;
                var (text, tools) = Content(m);
                var error = e.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null;
                if (last is { Kind: WorkerMessageKind.Assistant } && last.Id == id)
                    s.Messages[^1] = last with { Text = last.Text + text, ToolOutput = last.ToolOutput + tools, Tokens = Tokens(m) ?? last.Tokens, Error = error ?? last.Error };
                else
                {
                    Complete(s, now);
                    s.Messages.Add(new WorkerMessage(id, WorkerMessageKind.Assistant, now, text, Tokens(m), null,
                        s.Spec.Model is { } sm ? $"{sm.ProviderId}/{sm.ModelId}" : null, tools, error));
                }
                return false;
            case "user":
                // Tool results belong to the assistant message that called the tools (evidence scope).
                if (s.Messages.FindLastIndex(x => x.Kind == WorkerMessageKind.Assistant) is var i and >= 0)
                    s.Messages[i] = s.Messages[i] with { ToolOutput = s.Messages[i].ToolOutput + Content(e.GetProperty("message")).Tools };
                return false;
            case "system" when e.TryGetProperty("subtype", out var kind) && kind.GetString() == "init":
                NoteServers(s, e);
                return false;
            case "system" when e.TryGetProperty("subtype", out var st) && st.GetString() == "compact_boundary":
                Complete(s, now);
                s.Messages.Add(new WorkerMessage($"compact_{Guid.NewGuid():N}", WorkerMessageKind.Compaction, now, null, null, now));
                return false;
            case "result":
                Complete(s, now);
                if (compact)
                {
                    // The steered compaction's own call: its usage counts toward the node's budget.
                    var tokens = e.TryGetProperty("usage", out var u) ? Tokens(u) : null;
                    var c = s.Messages.FindLastIndex(x => x.Kind == WorkerMessageKind.Compaction);
                    if (c >= 0 && s.Messages[c].Tokens is null)
                        s.Messages[c] = s.Messages[c] with { Tokens = tokens };
                    else
                        s.Messages.Add(new WorkerMessage($"compact_{Guid.NewGuid():N}", WorkerMessageKind.Compaction, now, null, tokens, now));
                }
                else
                {
                    ReconcileOutput(s, e);
                    var isError = e.GetProperty("is_error").GetBoolean();
                    s.Messages.Add(Idle(s.Interrupted ? IdleOutcome.Interrupted : isError ? IdleOutcome.Failed : IdleOutcome.Succeeded,
                        isError ? Redact(ErrorText(e), s.Spec.Services) : null));
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The <c>init</c> event lists every server of the CLI's config with a status (<c>connected</c>, <c>failed</c>, ...). A granted one
    /// that is not connected gives the worker none of its tools for the turn; it is kept, by server, for <see cref="UnavailableServices"/>
    /// and stays once seen, so a turn that lost a server is not forgotten by a later one that got it back.
    /// </summary>
    private static void NoteServers(Session s, JsonElement init)
    {
        if (s.Spec.Services is not { Count: > 0 } grants)
            return;
        var status = new Dictionary<string, string>();
        if (init.TryGetProperty("mcp_servers", out var listed) && listed.ValueKind == JsonValueKind.Array)
            foreach (var server in listed.EnumerateArray())
                if (server.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    status[name.GetString()!] = server.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()! : "unknown";
        foreach (var grant in grants)
            if (status.GetValueOrDefault(grant.Server, "absent") is var state && !state.Equals("connected", StringComparison.OrdinalIgnoreCase))
                s.UnavailableServices[grant.Server] = state;
    }

    /// <summary>
    /// Why an error result failed: its <c>result</c> text, else what the event does carry. The <c>error_*</c> subtypes have no
    /// <c>result</c> (the SDK reference lists <c>errors</c> for them; 2.1.283 emits it), and a <c>success</c> one flagged
    /// <c>is_error</c> can carry it empty. Without this a failed turn with no assistant message reaches WorkerNode with no reason.
    /// </summary>
    internal static string ErrorText(JsonElement e)
    {
        if (e.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(result.GetString()))
            return result.GetString()!;
        var subtype = e.TryGetProperty("subtype", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        var text = new StringBuilder("claude reported ").Append(subtype switch { null => "an error result", "success" => "success with is_error", _ => subtype });
        if (e.TryGetProperty("num_turns", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var turns))
            text.Append(CultureInfo.InvariantCulture, $" after {turns} turn{(turns == 1 ? "" : "s")}");
        if (e.TryGetProperty("api_error_status", out var status) && status.ValueKind == JsonValueKind.Number)
            text.Append(" (API status ").Append(status.GetRawText()).Append(')');
        if (e.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            && errors.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString())).Select(x => x.GetString()!).ToList() is { Count: > 0 } lines)
            text.Append(": ").Append(string.Join("; ", lines));
        return text.ToString();
    }

    /// <summary>
    /// Assistant events carry the message_start usage, whose output count is a stub (1-3 tokens); only the result
    /// event totals the turn's output. The shortfall goes to the turn's last assistant message, so node totals and
    /// cost are right. ponytail: per-call output is lumped onto one call; read message_delta events via
    /// --include-partial-messages if per-call output matters.
    /// </summary>
    private static void ReconcileOutput(Session s, JsonElement result)
    {
        if (!result.TryGetProperty("usage", out var u) || !u.TryGetProperty("output_tokens", out var total))
            return;
        var turnStart = s.Messages.FindLastIndex(x => x.Kind == WorkerMessageKind.User);
        var last = s.Messages.FindLastIndex(x => x.Kind == WorkerMessageKind.Assistant && x.Tokens is not null);
        if (last <= turnStart)
            return;
        var counted = s.Messages.Skip(turnStart + 1).Where(x => x.Kind == WorkerMessageKind.Assistant && x.Tokens is not null).Sum(x => x.Tokens!.Output);
        if (total.GetInt64() > counted)
            s.Messages[last] = s.Messages[last] with { Tokens = s.Messages[last].Tokens! with { Output = s.Messages[last].Tokens!.Output + total.GetInt64() - counted } };
    }

    private static WorkerMessage Idle(IdleOutcome outcome, string? error) =>
        new($"idle_{Guid.NewGuid():N}", WorkerMessageKind.Idle, DateTimeOffset.UtcNow, null, null, Error: error, Outcome: outcome);

    /// <summary>Marks the open assistant message completed: a later event means its call has finished.</summary>
    private static void Complete(Session s, DateTimeOffset now)
    {
        if (s.Messages.FindLastIndex(x => x.Kind == WorkerMessageKind.Assistant) is var i and >= 0 && s.Messages[i].Completed is null)
            s.Messages[i] = s.Messages[i] with { Completed = now };
    }

    private static (string Text, string Tools) Content(JsonElement message)
    {
        var text = new StringBuilder();
        var tools = new StringBuilder();
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                switch (block.GetProperty("type").GetString())
                {
                    case "text":
                        text.Append(block.GetProperty("text").GetString());
                        break;
                    case "tool_use":
                    case "tool_result":
                        tools.Append(block.GetRawText()).Append('\n');
                        break;
                }
        return (text.ToString(), tools.ToString());
    }

    /// <summary>Anthropic usage; reasoning tokens are billed as output and not reported apart.</summary>
    private static TokenCounts? Tokens(JsonElement m)
    {
        var u = m.TryGetProperty("usage", out var inner) ? inner : m;
        if (u.ValueKind != JsonValueKind.Object || !u.TryGetProperty("input_tokens", out var input))
            return null;
        long Get(string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        return new TokenCounts(input.GetInt64(), Get("output_tokens"), 0, Get("cache_read_input_tokens"), Get("cache_creation_input_tokens"));
    }

    private static readonly Dictionary<string, string[]> ToolsByAction = new()
    {
        ["read"] = ["Read"],
        ["glob"] = ["Glob"],
        ["grep"] = ["Grep"],
        ["edit"] = ["Edit", "Write", "NotebookEdit"],
        ["shell"] = ["Bash"],
        ["webfetch"] = ["WebFetch"],
        ["websearch"] = ["WebSearch"],
        ["question"] = ["AskUserQuestion"],
        ["subagent"] = ["Agent"],
        ["todowrite"] = ["TodoWrite"],
        ["skill"] = ["Skill"],
    };

    private static readonly HashSet<string> PathTools = ["Read", "Glob", "Grep", "Edit", "Write", "NotebookEdit"];

    /// <summary>
    /// Translates preset rules (last match wins) to Claude Code's tool catalog and allow/deny lists (deny wins).
    /// A tool whose rules end in a whole-action deny with no allowed pattern is left out of the catalog. Actions with
    /// no Claude Code tool (external_directory) are dropped: dontAsk already denies paths outside the directory.
    /// ponytail: a specific allow inside an earlier specific deny (read *.env.* deny, then *.env.example allow) stays
    /// denied, because deny wins; this fails closed. Split the deny pattern if such an exception matters.
    /// </summary>
    internal static (IReadOnlyList<string> Tools, IReadOnlyList<string> Allowed, IReadOnlyList<string> Disallowed) Permissions(IReadOnlyList<PermissionRule> rules)
    {
        var state = ToolsByAction.Values.SelectMany(t => t).Distinct().ToDictionary(t => t, _ => (All: false, Allow: new List<string>(), Deny: new List<string>()));
        foreach (var rule in rules)
        {
            var tools = rule.Action == "*" ? state.Keys.ToArray() : ToolsByAction.GetValueOrDefault(rule.Action) ?? [];
            foreach (var tool in tools)
            {
                var t = state[tool];
                if (rule.Resource == "*")
                    state[tool] = (rule.Effect == PermissionEffect.Allow, [], []);
                else
                {
                    var pattern = PathTools.Contains(tool) && !rule.Resource.Contains('/') ? "**/" + rule.Resource : rule.Resource;
                    // Ask never reaches anyone in print mode, so it is a deny.
                    var (add, remove) = rule.Effect == PermissionEffect.Allow ? (t.Allow, t.Deny) : (t.Deny, t.Allow);
                    remove.Remove(pattern);
                    if (!(rule.Effect == PermissionEffect.Allow && t.All))
                        add.Add(pattern);
                }
            }
        }
        var catalog = state.Where(kv => kv.Value.All || kv.Value.Allow.Count > 0).Select(kv => kv.Key).ToList();
        var allowed = state.SelectMany(kv => kv.Value.All ? [kv.Key] : kv.Value.Allow.Select(p => $"{kv.Key}({p})")).ToList();
        var disallowed = state.Where(kv => catalog.Contains(kv.Key)).SelectMany(kv => kv.Value.Deny.Select(p => $"{kv.Key}({p})")).ToList();
        return (catalog, allowed, disallowed);
    }

    /// <summary>Splits unified diff output into files.</summary>
    internal static IReadOnlyList<FileDiff> ParseDiff(string diff)
    {
        var files = new List<FileDiff>();
        foreach (var chunk in ("\n" + diff).Split("\ndiff --git ", StringSplitOptions.RemoveEmptyEntries))
        {
            string? from = null, to = null;
            int add = 0, del = 0;
            foreach (var line in chunk.Split('\n'))
                if (to is null && line.StartsWith("--- ", StringComparison.Ordinal))
                    from = line[4..];
                else if (to is null && line.StartsWith("+++ ", StringComparison.Ordinal))
                    to = line[4..];
                else if (to is null)
                    continue;
                else if (line.StartsWith('+'))
                    add++;
                else if (line.StartsWith('-'))
                    del++;
            if (from is null || to is null)
                continue;
            var status = from == "/dev/null" ? "added" : to == "/dev/null" ? "deleted" : "modified";
            var path = status == "deleted" ? from : to;
            files.Add(new FileDiff(path[(path.IndexOf('/') + 1)..], "diff --git " + chunk.TrimEnd('\n') + "\n", add, del, status));
        }
        return files;
    }

    private static ProcessStartInfo Psi(string file, string directory, IEnumerable<string> args, IReadOnlyDictionary<string, string?>? env)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string?>())
            if (v is null)
                psi.Environment.Remove(k);
            else
                psi.Environment[k] = v;
        return psi;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> Exec(string file, string directory, IEnumerable<string> args, string? stdin, CancellationToken ct,
        IReadOnlyDictionary<string, string?>? env = null)
    {
        using var p = Process.Start(Psi(file, directory, args, env))!;
        await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw;
        }
        return (p.ExitCode, await stdout, await stderr);
    }

    internal sealed class Session(string id, NodeSpec spec, string? baseCommit)
    {
        public string Id { get; } = id;
        public NodeSpec Spec { get; } = spec;
        public string? BaseCommit { get; } = baseCommit;
        /// <summary>Ordered by first set; sent as the appended system prompt on every turn so the prefix stays cached.</summary>
        public Dictionary<string, string> Instructions { get; } = [];
        /// <summary>Oldest first. Guarded by a lock on the session.</summary>
        public List<WorkerMessage> Messages { get; } = [];
        public bool Started { get; set; }
        public volatile bool Interrupted;
        public volatile bool CompactPending;
        /// <summary>Granted servers the CLI reported as not connected, by name. Guarded by a lock on the session.</summary>
        public Dictionary<string, string> UnavailableServices { get; } = [];
        public Task? Turn { get; set; }
        public Process? Process { get; set; }
    }
}

/// <param name="Subscription">True: <paramref name="Secret"/> is a <c>claude setup-token</c> OAuth token; false: an API key.</param>
public sealed record ClaudeCodeCredential(string? Secret, bool Subscription)
{
    /// <summary>No secret: the CLI's own signed-in login (what <c>/login</c> stored), in subscription mode.</summary>
    public static readonly ClaudeCodeCredential CliLogin = new(null, Subscription: true);

    public override string ToString() => $"ClaudeCodeCredential {{ Subscription = {Subscription} }}";
}
