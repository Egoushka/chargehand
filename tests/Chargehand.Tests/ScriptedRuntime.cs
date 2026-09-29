using System.Collections.Concurrent;
using System.Diagnostics;
using Chargehand.Budget;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.RunLog;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>
/// Whole runs without OpenCode: intake returns the next scripted Task Spec (the last one repeats), and every turn of
/// every session ends with the scripted final message. Turns wait on <see cref="Hold"/>, to observe a run unfinished.
/// </summary>
internal sealed class ScriptedRuntime(string reply, params string[] specs) : IWorkerRuntime, IServiceHealth
{
    private readonly Queue<string> _specs = new(specs.Length == 0 ? [Spec()] : specs);
    private readonly ConcurrentDictionary<string, List<WorkerMessage>> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _directories = new();
    private int _ids;

    /// <summary>Called at the start of each turn with the session's directory and the turn's number (1-based), as a worker that edits files would.</summary>
    public Action<string, int>? OnTurn { get; set; }

    public ConcurrentQueue<NodeSpec> Created { get; } = new();

    public ConcurrentQueue<string> Prompts { get; } = new();

    public ConcurrentQueue<string> IntakePrompts { get; } = new();

    /// <summary>Thrown, one per call, by the next generate calls (e.g. a gateway rate limit).</summary>
    public ConcurrentQueue<Exception> GenerateFailures { get; } = new();

    /// <summary>Thrown, one per call, by the next session creations (e.g. a runtime that cannot start a worker).</summary>
    public ConcurrentQueue<Exception> CreateFailures { get; } = new();

    /// <summary>What a tool of the worker's returned, kept on its assistant message like a runtime does; null: no tool call.</summary>
    public string? ToolResult { get; set; }

    /// <summary>Granted servers the workers could not use (server to status), as the runtime would report them once a session has run.</summary>
    public Dictionary<string, string> Unavailable { get; } = [];

    public IReadOnlyDictionary<string, string> UnavailableServices(string sessionId) => Unavailable;

    public TaskCompletionSource Hold { get; set; } = Released();

    public static TaskCompletionSource Released()
    {
        var released = new TaskCompletionSource();
        released.SetResult();
        return released;
    }

    public static string Spec(string action = "answer", string detail = "null") => $$"""
        {"contract_version":"task-spec/v1","id":"x","goal":"g","constraints":[],"acceptance_criteria":[],"risk":"low",
         "estimate":{"tokens_low":1,"tokens_high":2,"usd_low":0.01,"usd_high":0.02,"basis":"b"},"action":"{{action}}","action_detail":{{detail}}}
        """;

    public Task<string> GenerateAsync(ModelRef? model, string prompt, CancellationToken ct)
    {
        IntakePrompts.Enqueue(prompt);
        if (GenerateFailures.TryDequeue(out var failure))
            return Task.FromException<string>(failure);
        lock (_specs)
            return Task.FromResult(_specs.Count > 1 ? _specs.Dequeue() : _specs.Peek());
    }

    public Task<WorkerSession> CreateAsync(NodeSpec spec, CancellationToken ct)
    {
        if (CreateFailures.TryDequeue(out var failure))
            return Task.FromException<WorkerSession>(failure);
        Created.Enqueue(spec);
        var id = $"ses_{Interlocked.Increment(ref _ids)}";
        _sessions[id] = [];
        _directories[id] = spec.Directory;
        return Task.FromResult(new WorkerSession(id, spec.Directory));
    }

    public Task SetInstructionAsync(string sessionId, string key, string value, CancellationToken ct) => Task.CompletedTask;

    public Task SubmitAsync(string sessionId, string text, CancellationToken ct)
    {
        Prompts.Enqueue(text);
        Add(sessionId, new WorkerMessage($"usr_{Interlocked.Increment(ref _ids)}", WorkerMessageKind.User, DateTimeOffset.UtcNow, text, null));
        return Task.CompletedTask;
    }

    public async Task<IdleOutcome> AwaitIdleAsync(string sessionId, CancellationToken ct)
    {
        await Hold.Task.WaitAsync(ct);
        if (OnTurn is not null)
        {
            int turn;
            lock (_sessions[sessionId])
                turn = _sessions[sessionId].Count(m => m.Kind == WorkerMessageKind.User);
            OnTurn(_directories[sessionId], turn);
        }
        var now = DateTimeOffset.UtcNow;
        Add(sessionId, new WorkerMessage($"msg_{Interlocked.Increment(ref _ids)}", WorkerMessageKind.Assistant, now, reply, new TokenCounts(1000, 100, 0, 4000, 200), now.AddSeconds(1),
            ToolOutput: ToolResult, ToolResults: ToolResult is null ? null : [ToolResult]));
        return IdleOutcome.Succeeded;
    }

    public Task<IReadOnlyList<WorkerMessage>> ReadMessagesAsync(string sessionId, CancellationToken ct)
    {
        lock (_sessions[sessionId])
            return Task.FromResult<IReadOnlyList<WorkerMessage>>([.. _sessions[sessionId]]);
    }

    public Task InterruptAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<PermissionRequest>> PendingPermissionsAsync(string sessionId, CancellationToken ct) => Task.FromResult<IReadOnlyList<PermissionRequest>>([]);

    public Task AnswerPermissionAsync(string sessionId, string requestId, PermissionDecision decision, string? message, CancellationToken ct) => Task.CompletedTask;

    public Task<WorkerSession> ForkAsync(string sessionId, string? beforeMessageId, CancellationToken ct) => throw new NotSupportedException();

    public Task CompactAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<FileDiff>> DiffAsync(string sessionId, CancellationToken ct) => Task.FromResult<IReadOnlyList<FileDiff>>([]);

    private void Add(string sessionId, WorkerMessage message)
    {
        lock (_sessions[sessionId])
            _sessions[sessionId].Insert(0, message);
    }
}

/// <summary>Profiles, requests and final messages for runs on a <see cref="ScriptedRuntime"/>.</summary>
internal static class Runs
{
    /// <summary>A draft citing one input, plus a claim on a file, which cannot resolve without a checkout.</summary>
    public const string DraftReply = """
        Done.
        ```json
        {"status":"completed","summary":"A one-line note.",
         "claims":[{"text":"v1 is released.","evidence":["e1"],"confidence":0.9},{"text":"It reads files.","evidence":["e2"],"confidence":0.5}],
         "evidence":[{"id":"e1","kind":"input","locator":"rel-v1"},{"id":"e2","kind":"file","locator":"src/x.cs:1"}],
         "artifacts":[{"kind":"draft","media_type":"text/markdown","content":"I released v1."}],"open_questions":[],"confidence":0.8}
        ```
        """;

    /// <summary>An answer citing the first line of the README that <see cref="GitRepo"/> commits.</summary>
    public const string WorkerReply = """
        ```json
        {"status":"completed","summary":"The README greets.","claims":[{"text":"The README says hello.","evidence":["e1"],"confidence":0.9}],
         "evidence":[{"id":"e1","kind":"file","locator":"README.md:1"}],"artifacts":[],"open_questions":[],"confidence":0.9}
        ```
        """;

    public static Profile Profile(string workerRoot) => new("profile/v1", new OpenCodeSettings("http://127.0.0.1:1", "pw", "2.0.16"), workerRoot, "draft", "p/small",
        new Dictionary<string, ModelPrice> { ["p/small"] = new(0.1m, 0.5m, 0.01m, 0.125m) }, SupportCheck: false, Models: new Dictionary<string, string> { ["provider/small-model"] = "p/small" });

    public static Orchestrator Orchestrator(IWorkerRuntime runtime, string workerRoot, IRunLog log) =>
        new(Profile(workerRoot), runtime, "2.0.16", Repo.Root, log, new Dictionary<string, int>());

    public static RunRequest DraftRequest(bool interactive = false) => new("request/v1", "Draft a note.", new RequestContext(interactive, "draft"),
        [new CallerInput("rel-v1", "signal", "Released v1.")], [PromptBlock.Create("generator/note", "0.1.0", "Write one sentence.")]);

    public static RunRequest CheapRequest(RepositoryRef repo, bool interactive = false) =>
        new("request/v1", "What does the README say?", new RequestContext(interactive, "cheap", Repository: repo));

    /// <summary>A completed result with one claim, as a worker returns it.</summary>
    public static ResultContract Completed() =>
        new("result/v1", "t", "n", new string('0', 32), new PromptChain([], new AsSent("2.0.16", "build", "p/m", "2026-09-26")), ResultStatus.Completed,
            "The README greets.", [new Claim("The README says hello.", ["e1"], 0.9)], [new Evidence("e1", EvidenceKind.File, "README.md:1")], [], [], 0.9,
            new Usage(0, 0, 0, 0, 0.01m));

    /// <summary>A checkout under the worker root with one committed README.</summary>
    public static RepositoryRef GitRepo(string workerRoot)
    {
        var path = Directory.CreateDirectory(Path.Combine(workerRoot, "repo")).FullName;
        File.WriteAllText(Path.Combine(path, "README.md"), "hello\n");
        Git(path, "init", "-q");
        Git(path, "add", "README.md");
        Git(path, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "init");
        return new RepositoryRef(path, Git(path, "rev-parse", "HEAD").Trim());
    }

    /// <summary>Gives the repository an <c>origin</c> remote; nothing is fetched from it.</summary>
    public static void SetOrigin(RepositoryRef repo, string url) => Git(repo.Path, "remote", "add", "origin", url);

    /// <summary>Commits the named files; returns the repository at the new commit.</summary>
    public static RepositoryRef Commit(RepositoryRef repo, params string[] files)
    {
        Git(repo.Path, ["add", "-f", "--", .. files]);
        Git(repo.Path, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "add");
        return repo with { Commit = Git(repo.Path, "rev-parse", "HEAD").Trim() };
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', args)} failed");
    }
}
