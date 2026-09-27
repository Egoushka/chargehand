using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Chargehand.Runtime;

namespace Chargehand.ClaudeCode;

/// <summary>
/// IWorkerRuntime over the Claude Code CLI in print mode (ADR 0020): one <c>claude -p</c> process per turn, the first
/// with <c>--session-id</c>, later ones with <c>--resume</c>. Sessions live in this process; the CLI persists the
/// transcript. Create with <see cref="ConnectAsync"/>, which pins the version.
/// Two credentials: an API key (billed per token, <c>--bare</c>) or a subscription OAuth token from
/// <c>claude setup-token</c> (the owner's plan limits, no per-token bill). <c>--bare</c> ignores OAuth, so the
/// subscription mode isolates with <c>--setting-sources ""</c> instead.
/// </summary>
public sealed class ClaudeCodeWorkerRuntime : IWorkerRuntime
{
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

    /// <summary>Refuses a CLI whose version differs from the pinned one. <c>claude --version</c> prints "2.1.195 (Claude Code)".</summary>
    public static async Task<ClaudeCodeWorkerRuntime> ConnectAsync(string binary, string pinnedVersion, ClaudeCodeCredential credential, CancellationToken ct,
        Uri? baseUrl = null)
    {
        var (exit, stdout, stderr) = await Exec(binary, Path.GetTempPath(), ["--version"], null, ct);
        var version = stdout.Split(' ', 2)[0].Trim();
        if (exit != 0)
            throw new InvalidOperationException($"{binary} --version exited {exit}: {stderr.Trim()}");
        return version == pinnedVersion
            ? new ClaudeCodeWorkerRuntime(binary, credential, version, baseUrl)
            : throw new InvalidOperationException($"Claude Code CLI is {version}; this adapter is pinned to {pinnedVersion}.");
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
    public async Task<string> GenerateAsync(ModelRef model, string prompt, CancellationToken ct)
    {
        var env = new Dictionary<string, string?>(Env) { ["MAX_THINKING_TOKENS"] = "0" };
        var (exit, stdout, stderr) = await Exec(_binary, Path.GetTempPath(),
            [.. CommonArgs(model), "--output-format", "json", "--no-session-persistence", "--tools", "",
                "--system-prompt", "Follow the instructions in the user message exactly."], prompt, ct, env);
        var result = exit == 0 && stdout.Length > 0 ? JsonDocument.Parse(stdout).RootElement : default;
        if (result.ValueKind != JsonValueKind.Object || result.GetProperty("is_error").GetBoolean())
            throw new InvalidOperationException($"claude -p exited {exit}: {(result.ValueKind == JsonValueKind.Object ? result.GetProperty("result").GetString() : stderr.Trim())}");
        return result.GetProperty("result").GetString() ?? "";
    }

    private Session Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : throw new KeyNotFoundException($"no Claude Code session {sessionId} in this process");

    /// <summary>
    /// Only the profile decides credential and endpoint: an inherited API key would outrank the OAuth token, and an
    /// inherited base URL or auth token would reroute the worker, so the variables this runtime does not set are removed.
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
    /// prompt goes on stdin because the tool flags are variadic.
    /// </summary>
    private List<string> CommonArgs(ModelRef model)
    {
        List<string> args = ["-p", .. _credential.Subscription ? ["--setting-sources", ""] : new[] { "--bare" }, "--strict-mcp-config", "--model", model.ModelId];
        if (model.Variant is { } effort)
            args.AddRange(["--effort", effort]);
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
        var (tools, allowed, disallowed) = Permissions(s.Spec.Permissions);
        args.AddRange(["--permission-mode", "dontAsk", "--tools", string.Join(",", tools)]);
        if (allowed.Count > 0)
            args.AddRange(["--allowedTools", .. allowed]);
        if (disallowed.Count > 0)
            args.AddRange(["--disallowedTools", .. disallowed]);

        s.Interrupted = false;
        var p = Process.Start(Psi(_binary, s.Spec.Directory, args, Env))!;
        s.Process = p;
        return Task.Run(async () =>
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
                    s.Messages.Add(Idle(s.Interrupted ? IdleOutcome.Interrupted : IdleOutcome.Failed, $"claude exited {p.ExitCode}: {stderr.Result.Trim()}"));
            s.Process = null;
            p.Dispose();
        });
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
                        $"{s.Spec.Model.ProviderId}/{s.Spec.Model.ModelId}", tools, error));
                }
                return false;
            case "user":
                // Tool results belong to the assistant message that called the tools (evidence scope).
                if (s.Messages.FindLastIndex(x => x.Kind == WorkerMessageKind.Assistant) is var i and >= 0)
                    s.Messages[i] = s.Messages[i] with { ToolOutput = s.Messages[i].ToolOutput + Content(e.GetProperty("message")).Tools };
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
                        isError && e.TryGetProperty("result", out var r) ? r.GetString() : null));
                }
                return true;
            default:
                return false;
        }
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
        public Task? Turn { get; set; }
        public Process? Process { get; set; }
    }
}

/// <param name="Subscription">True: <paramref name="Secret"/> is a <c>claude setup-token</c> OAuth token; false: an API key.</param>
public sealed record ClaudeCodeCredential(string Secret, bool Subscription)
{
    public override string ToString() => $"ClaudeCodeCredential {{ Subscription = {Subscription} }}";
}
