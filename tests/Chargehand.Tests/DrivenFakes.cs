using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>The fakes a driven task runs on, shared by <c>Chargehand.Tests</c> and <c>Chargehand.Modules.Tasks.Tests</c> (C4 brief, S7): the runner's engine, a session's output volume, and the
/// handover. Nothing starts a container or reaches a network.</summary>
internal static class DrivenFakes
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string Report = """{"summary":"Did the task.","claims":[],"tests":{"command":"npm test","exit_code":0}}""";

    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal sealed class FakeEngine : IContainerEngine, IWorkspaceEngine, IServiceEngine
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _stopped = [];

        public List<string> Calls { get; } = [];
        public List<ContainerSpec> Started { get; } = [];
        public List<EgressSpec> Egresses { get; } = [];
        public bool HoldRunning { get; set; }
        /// <summary>Each container answers running to this many inspections before it exits.</summary>
        public int RunFor { get; set; }
        private readonly Dictionary<string, int> _inspections = [];
        /// <summary>The start call is cancelled after docker made the container, as a cancel in flight does.</summary>
        public bool CancelOnStart { get; set; }
        /// <summary>The nth container started (1-based) stays running until <see cref="Release"/>; 0: none.</summary>
        public int HoldNth { get; set; }
        private readonly HashSet<string> _held = [];
        /// <summary>Called with the number of the start (1-based) once it is recorded; what it returns is thrown.</summary>
        public Func<int, Exception?>? OnStart { get; set; }

        /// <summary>The peak memory (MiB) every container reports; null: the engine cannot measure it.</summary>
        public int? PeakMb { get; set; }
        /// <summary>The peak a container reports, by its id; overrides <see cref="PeakMb"/> where it answers.</summary>
        public Func<string, int?>? PeakOf { get; set; }

        private int? Peak(string id) => PeakOf?.Invoke(id) ?? PeakMb;

        /// <summary>Containers docker no longer knows.</summary>
        public HashSet<string> Gone { get; } = [];

        /// <summary>Containers docker made and never started, by name: what a start cut between its create and its start leaves. Removing one forgets it.</summary>
        public HashSet<string> Created { get; } = [];
        public EngineResources Resources { get; set; } = new([], [], []);

        public void Release() { lock (_gate) _held.Clear(); }

        private void Record(string call)
        {
            lock (_gate)
                Calls.Add(call);
        }

        public List<ServiceSpec> Services { get; } = [];

        /// <summary>What a service's start throws; null: it starts.</summary>
        public Func<ServiceSpec, Exception?>? OnService { get; set; }

        public Task<string> StartServiceAsync(ServiceSpec spec, CancellationToken ct)
        {
            if (OnService?.Invoke(spec) is { } failure)
                throw failure;
            lock (_gate)
                Services.Add(spec);
            Record($"service {spec.Name}");
            return Task.FromResult($"cid-svc-{spec.Name}");
        }

        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct)
        {
            lock (_gate)
                Started.Add(spec);
            Record($"start {spec.RunId}");
            lock (_gate)
                if (Started.Count == HoldNth)
                    _held.Add($"cid-{spec.RunId}");
            if (CancelOnStart)
                throw new OperationCanceledException();
            int number;
            lock (_gate)
                number = Started.Count;
            if (OnStart?.Invoke(number) is { } failure)
                throw failure;
            return Task.FromResult($"cid-{spec.RunId}");
        }

        public Task SignalAsync(string id, string signal, CancellationToken ct)
        {
            Record($"signal {id} {signal}");
            lock (_gate)
                _stopped.Add(id);
            return Task.CompletedTask;
        }

        /// <summary>The next this many inspections find the runner silent (ADR 0058); -1: always.</summary>
        public int SilentInspections { get; set; }

        public Task<ContainerState> InspectAsync(string id, CancellationToken ct)
        {
            bool running;
            lock (_gate)
            {
                if (SilentInspections != 0 && !id.StartsWith("chargehand-egress", StringComparison.Ordinal))
                {
                    if (SilentInspections > 0)
                        SilentInspections--;
                    return Task.FromException<ContainerState>(new Chargehand.Modules.Runners.Api.RunnerUnreachableException("the runner could not be reached: no route to host"));
                }
                if (Gone.Contains(id))
                    return Task.FromResult(new ContainerState(ContainerStatus.Missing, null, false));
                if (Created.Contains(id))
                    return Task.FromResult(new ContainerState(ContainerStatus.Created, null, false));
                if (_held.Contains(id) && !_stopped.Contains(id))
                    return Task.FromResult(new ContainerState(ContainerStatus.Running, null, false, Peak(id)));
                _inspections[id] = _inspections.GetValueOrDefault(id) + 1;
                running = (HoldRunning || _inspections[id] <= RunFor) && !_stopped.Contains(id);
            }
            return Task.FromResult(running ? new ContainerState(ContainerStatus.Running, null, false, Peak(id)) : new ContainerState(ContainerStatus.Exited, ExitCodeOf?.Invoke(id) ?? 0, false, Peak(id)));
        }

        /// <summary>The exit code a container ends with, by its id; null: 0.</summary>
        public Func<string, int>? ExitCodeOf { get; set; }

        public Task RemoveAsync(string id, CancellationToken ct)
        {
            Record($"rm {id}");
            lock (_gate)
                Created.Remove(id);
            return Task.CompletedTask;
        }
        public Task KillAllAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OwnsAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct) => OnList is null ? Task.FromResult(Resources.Containers.Count) : throw OnList;
        public Task<EngineResources> ListAsync(CancellationToken ct)
        {
            Record("list");
            return OnList is null ? Task.FromResult(Resources) : throw OnList;
        }

        /// <summary>The engine fails when listed.</summary>
        public Exception? OnList { get; set; }
        /// <summary>The end of a container's log, by its id; null: empty.</summary>
        public Func<string, string>? LogsOf { get; set; }

        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) => Task.FromResult(LogsOf?.Invoke(id) ?? "");
        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) { Record($"volume {name}"); return Task.CompletedTask; }
        public Task RemoveVolumeAsync(string name, CancellationToken ct) { Record($"volume-rm {name}"); return Task.CompletedTask; }
        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) { Record($"network {name}"); return Task.CompletedTask; }
        public Task RemoveNetworkAsync(string name, CancellationToken ct) { Record($"network-rm {name}"); return Task.CompletedTask; }
        public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) { lock (_gate) Egresses.Add(spec); Record($"egress {spec.BatchId} [{string.Join(' ', spec.Forwards ?? [])}]"); return Task.FromResult("egress-1"); }
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) { Record($"connect {container} {network}"); return Task.CompletedTask; }
        public Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct) { Record($"prepare {spec.RunId} {spec.Branch} {spec.Commit}"); return Task.CompletedTask; }
    }

    /// <summary>Plays the session: the output volume holds an outcome for the task's goal, and a bundle and report when it completed.</summary>
    internal sealed class FakeVolumes(Func<string, SessionStatus> script, Func<string, string, SessionOutcome?>? outcomes = null) : ISessionVolumes
    {
        private readonly Dictionary<string, string> _goals = [];

        /// <summary>Every task file written, in order.</summary>
        public List<SessionTask> Tasks { get; } = [];

        /// <summary>Every seed bundle written: the run it went to and the file it came from.</summary>
        public List<(string RunId, string Path)> Seeds { get; } = [];

        /// <summary>What the session's running tally says while it works; null: it has written none.</summary>
        public TaskUsage? Usage { get; set; }

        public int UsageReads;

        /// <summary>The transcript each session left, by run id; a run not in it has none.</summary>
        public Dictionary<string, string> Transcripts { get; } = [];

        /// <summary>What reading a transcript throws (a runner that refuses or does not answer); null: it reads.</summary>
        public Exception? TranscriptFails { get; set; }

        /// <summary>The windows asked for, in order.</summary>
        public List<(string RunId, int MaxBytes)> TranscriptReads { get; } = [];

        public Task<byte[]?> ReadTranscriptTailAsync(string runId, int maxBytes, CancellationToken ct)
        {
            lock (TranscriptReads)
                TranscriptReads.Add((runId, maxBytes));
            if (TranscriptFails is not null)
                throw TranscriptFails;
            if (!Transcripts.TryGetValue(runId, out var text))
                return Task.FromResult<byte[]?>(null);
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            return Task.FromResult<byte[]?>(bytes.Length <= maxBytes ? bytes : bytes[^maxBytes..]);
        }

        /// <summary>The session image is newer than the server: its outcome and report carry a member the server does not know.</summary>
        public bool NewerSession { get; set; }

        /// <summary>Runs whose output volume holds nothing.</summary>
        public HashSet<string> NoOutput { get; } = [];

        /// <summary>The <c>progress.md</c> a session leaves, from its run id and goal; null: it leaves none.</summary>
        public Func<string, string, string?>? Progress { get; set; }

        /// <summary>The <c>governor.jsonl</c> a session leaves, from its run id and goal; null: it leaves none.</summary>
        public Func<string, string, string?>? GovernorLog { get; set; }

        public Task<TaskUsage?> ReadUsageAsync(string runId, CancellationToken ct)
        {
            Interlocked.Increment(ref UsageReads);
            return Task.FromResult(Usage);
        }

        public Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct)
        {
            lock (_goals)
            {
                _goals[runId] = task.Goal;
                Tasks.Add(task);
            }
            return Task.CompletedTask;
        }

        public Task WriteSeedAsync(string runId, string bundlePath, CancellationToken ct)
        {
            lock (_goals)
                Seeds.Add((runId, bundlePath));
            return Task.CompletedTask;
        }

        /// <summary>The run ids whose output was fetched, in order.</summary>
        public List<string> Fetched { get; } = [];

        public Task FetchOutputAsync(string runId, string directory, CancellationToken ct)
        {
            string goal;
            lock (_goals)
            {
                Fetched.Add(runId);
                goal = _goals[runId];
            }
            if (NoOutput.Contains(runId))
                return Task.CompletedTask;
            var status = script(goal);
            var outcome = outcomes?.Invoke(runId, goal) ?? new SessionOutcome(status, status == SessionStatus.Failed ? "session_error" : "", 9, 1000, 200, 4000, null, 0, [], status == SessionStatus.Completed,
                CacheWriteTokens: 300, SessionId: $"cc-session-{runId}", Model: "claude-sonnet-5-5",
                ModelUsage: new Dictionary<string, TokenCounts> { ["claude-sonnet-5-5"] = new(1000, 200, 0, 4000, 300) });
            var newer = NewerSession ? "\"added_later\":1," : "";
            File.WriteAllText(Path.Combine(directory, "session-outcome.json"),
                "{" + newer + JsonSerializer.Serialize(outcome, Snake)[1..]);
            if (outcome.Status == SessionStatus.Completed)
                File.WriteAllText(Path.Combine(directory, "driven-report.json"), "{" + newer + Report[1..]);
            if (outcome.BundleWritten)
                File.WriteAllText(Path.Combine(directory, "chargehand.bundle"), "bundle");
            if (Progress?.Invoke(runId, goal) is { } progress)
                File.WriteAllText(Path.Combine(directory, "progress.md"), progress);
            if (GovernorLog?.Invoke(runId, goal) is { } governorLog)
                File.WriteAllText(Path.Combine(directory, "governor.jsonl"), governorLog);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeHandover : IBranchHandover
    {
        public List<HandoverInput> Inputs { get; } = [];

        /// <summary>For each handover, whether the bundle it was told to read was on disk.</summary>
        public List<bool> BundleSeen { get; } = [];

        /// <summary>The commit the nth handover (1-based) pushed; null: always <c>Commit</c>.</summary>
        public Func<int, string>? CommitFor { get; set; }

        /// <summary>Flagged on every handover when set (ADR 0046).</summary>
        public TestIntegrityFlag? Integrity { get; set; }

        /// <summary>Called with the number of the handover (1-based) once it pushed and opened its pull request, before it returns.</summary>
        public Action<int>? OnPushed { get; set; }

        public Task<HandoverOutcome> RunAsync(HandoverInput input, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Inputs)
                BundleSeen.Add(File.Exists(Path.Combine(input.OutDirectory, "chargehand.bundle")));
            int number;
            lock (Inputs)
            {
                Inputs.Add(input);
                number = Inputs.Count;
            }
            input.Report?.Invoke(new HandoverEvent(RunEventKind.VerifyFinished, "tests passed"));
            input.Report?.Invoke(new HandoverEvent(RunEventKind.Pushed, $"pushed {input.Branch}"));
            input.Report?.Invoke(new HandoverEvent(RunEventKind.PrOpened, "draft pull request opened", $"https://example.test/o/r/pull/{number}"));
            OnPushed?.Invoke(number);
            return Task.FromResult(new HandoverOutcome(HandoverStatus.Pushed, "", null, input.Branch, CommitFor?.Invoke(number) ?? Commit, new PullRequestRef($"https://example.test/o/r/pull/{number}", number),
                new VerificationRecord(["npm", "test"], "detected", 0, "ok", false, TimeSpan.FromSeconds(3), true), ["a.txt"], [], [], TestIntegrity: Integrity));
        }
    }
}
