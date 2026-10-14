using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Tests;

/// <summary>What a driven batch acts on in the kill-at-every-step test (ADR 0061): a container engine, a runner's volumes and a pull request host that keep their state as Docker and a forge
/// do, and number every call so a run can be killed at any one of them. A kill copies what the server has on disk at that instant (<see cref="Killed"/> is told) and then refuses every
/// later call of that process without acting, so nothing it does after its death reaches the world. The next process is <see cref="Revive"/>d.</summary>
internal sealed class KillWorld(Func<string, string> bundleFor) : IContainerEngine, IWorkspaceEngine, ISessionVolumes, IPullRequests
{
    /// <summary><see cref="Before"/>: killed as the call is made, with nothing done. <see cref="After"/>: the call took effect, and the process died before it heard back.
    /// <see cref="Made"/>: a <c>docker run</c> cut between its create and its start.</summary>
    public enum CutMode { Before, After, Made }

    public sealed record Cut(int Call, CutMode Mode)
    {
        public override string ToString() => $"{Mode.ToString().ToLowerInvariant()} call {Call}";
    }

    private enum State { Created, Running, Exited }

    private sealed class Container(string kind, string? run)
    {
        public string Kind { get; } = kind;
        public string? Run { get; } = run;
        public State State { get; set; } = State.Running;
        public int Inspections { get; set; }
        public string Logs { get; set; } = "";
    }

    private static readonly string[] NpmTest = ["npm", "test"];
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly object _gate = new();
    private readonly Dictionary<string, Container> _containers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _volumes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _networks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _taskFiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outcomes = new(StringComparer.Ordinal);
    private Cut? _cut;
    private CancellationTokenSource? _stop;
    private bool _dead;

    /// <summary>Every call this world answered or refused, in order, across processes.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The calls of the current process, in order: the <see cref="Cut.Call"/> numbers count from 1 into it.</summary>
    public List<string> ProcessCalls { get; } = [];

    /// <summary>The run ids of the session containers that started to run (a made, never started one is not in it).</summary>
    public List<string> SessionsRan { get; } = [];

    /// <summary>How many verification containers started.</summary>
    public int Verifications { get; private set; }

    /// <summary>Open pull requests: number and head branch.</summary>
    public List<(int Number, string Head)> PullRequests { get; } = [];

    /// <summary>Told when the process is killed, before the stop is signalled: copy the state directory and the run log here.</summary>
    public Action? Killed { get; set; }

    /// <summary>Whether the current process was killed.</summary>
    public bool Dead { get { lock (_gate) return _dead; } }

    public IReadOnlyCollection<string> Containers { get { lock (_gate) return [.. _containers.Keys]; } }

    public IReadOnlyCollection<string> Volumes { get { lock (_gate) return [.. _volumes.Keys]; } }

    public IReadOnlyCollection<string> Networks { get { lock (_gate) return [.. _networks]; } }

    /// <summary>A new process: calls are numbered from 1 again, and <paramref name="cut"/> (null: none) kills it.</summary>
    public void Revive(CancellationTokenSource stop, Cut? cut = null)
    {
        lock (_gate)
        {
            (_stop, _cut, _dead) = (stop, cut, false);
            ProcessCalls.Clear();
        }
    }

    /// <summary>Every session container that runs dies now without leaving an outcome; what it did in its workspace stays there.</summary>
    public void CrashSessions()
    {
        lock (_gate)
            foreach (var c in _containers.Values.Where(c => c is { Kind: "session", State: State.Running }))
                c.State = State.Exited;
    }

    /// <summary>Kills the process now, from outside any call (a push hung in a remote hook).</summary>
    public void Kill()
    {
        lock (_gate)
            _dead = true;
        Killed?.Invoke();
        _stop!.Cancel();
    }

    /// <summary>Numbers the call and kills the process when the cut is here; <paramref name="effect"/> runs unless the process is dead or the cut is before it.</summary>
    private T Act<T>(string call, Func<T> effect, Action? made = null)
    {
        Cut? here;
        lock (_gate)
        {
            Calls.Add(_dead ? $"(dead) {call}" : call);
            if (_dead)
                throw new OperationCanceledException("the process was killed");
            ProcessCalls.Add(call);
            here = _cut?.Call == ProcessCalls.Count ? _cut : null;
        }
        if (here is { Mode: CutMode.Before })
            throw Die();
        if (here is { Mode: CutMode.Made } && made is not null)
        {
            made();
            throw Die();
        }
        var result = effect();
        return here is null ? result : throw Die();
    }

    private void Act(string call, Action effect) => Act(call, () =>
    {
        effect();
        return 0;
    });

    private OperationCanceledException Die()
    {
        Kill();
        return new OperationCanceledException(_stop!.Token);
    }

    public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct)
    {
        var name = $"chargehand-{spec.RunId}";
        var kind = spec.Command[0];
        return Task.FromResult(Act($"start {kind} {spec.RunId}", () =>
        {
            lock (_gate)
            {
                if (_containers.ContainsKey(name))
                    throw new ChargehandException(ErrorCode.ContainerUnavailable, $"docker run failed: the name {name} is already in use", "Remove it.");
                _containers[name] = new Container(kind, spec.RunId);
                _volumes.TryAdd(spec.WorkVolume, false);
                _volumes.TryAdd(spec.OutVolume, false);
                if (kind == "session")
                    SessionsRan.Add(spec.RunId);
                else
                    Verifications++;
            }
            return name;
        }, () =>
        {
            lock (_gate)
                _containers.TryAdd(name, new Container(kind, spec.RunId) { State = State.Created });
        }));
    }

    public Task SignalAsync(string id, string signal, CancellationToken ct)
    {
        Act($"signal {id}", () =>
        {
            lock (_gate)
                if (_containers.TryGetValue(id, out var c) && c.State == State.Running)
                    c.State = State.Exited;
        });
        return Task.CompletedTask;
    }

    /// <summary>A session runs for two inspections and leaves its outcome when it had a workspace and a task file; a verification has ended by its first and passes when the branch's bundle is there.</summary>
    public Task<ContainerState> InspectAsync(string id, CancellationToken ct) => Task.FromResult(Act($"inspect {id}", () =>
    {
        lock (_gate)
        {
            if (!_containers.TryGetValue(id, out var c))
                return new ContainerState(ContainerStatus.Missing, null, false);
            if (c.State == State.Created)
                return new ContainerState(ContainerStatus.Created, null, false);
            if (c.State == State.Running && ++c.Inspections > (c.Kind == "session" ? 2 : 0))
                End(c);
            return c.State == State.Running ? new ContainerState(ContainerStatus.Running, null, false) : new ContainerState(ContainerStatus.Exited, 0, false);
        }
    }));

    private void End(Container c)
    {
        c.State = State.Exited;
        var work = RunnerNames.Work(c.Run!);
        if (c.Kind == "session" && _volumes.GetValueOrDefault(work) && _taskFiles.Contains(c.Run!))
            _outcomes.Add(c.Run!);
        if (c.Kind == "verify-branch")
            c.Logs = _volumes.GetValueOrDefault(work) && _outcomes.Contains(c.Run!)
                ? VerifyBranchCli.Marker + JsonSerializer.Serialize(new { argv = NpmTest, exit_code = 0, output_tail = "ok", timed_out = false, duration_ms = 1000 })
                : "fatal: no workspace or no bundle";
    }

    public Task RemoveAsync(string id, CancellationToken ct)
    {
        Act($"rm {id}", () =>
        {
            lock (_gate)
                _containers.Remove(id);
        });
        return Task.CompletedTask;
    }

    public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) => Task.FromResult(Act($"logs {id}", () =>
    {
        lock (_gate)
            return _containers.TryGetValue(id, out var c) ? c.Logs : "";
    }));

    public Task CreateVolumeAsync(string name, string runId, CancellationToken ct)
    {
        Act($"volume {name}", () =>
        {
            lock (_gate)
                _volumes.TryAdd(name, false);
        });
        return Task.CompletedTask;
    }

    /// <summary>As <c>docker volume rm -f</c> whose failure the engine ignores: a volume a container (even an exited one) still uses stays.</summary>
    public Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        Act($"volume-rm {name}", () =>
        {
            lock (_gate)
                if (!_containers.Values.Any(c => c.Run is { } run && (RunnerNames.Work(run) == name || RunnerNames.Out(run) == name)))
                {
                    _volumes.Remove(name);
                    if (name.StartsWith("chargehand-out-", StringComparison.Ordinal))
                    {
                        _taskFiles.Remove(name["chargehand-out-".Length..]);
                        _outcomes.Remove(name["chargehand-out-".Length..]);
                    }
                }
        });
        return Task.CompletedTask;
    }

    /// <summary>As the workspace helper: a clone does not go into a volume that holds one already, and a failed helper removes its volume (<c>DockerCliEngine.PrepareWorkspaceAsync</c>).</summary>
    public Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct)
    {
        Act($"prepare {spec.RunId}", () =>
        {
            lock (_gate)
            {
                if (_volumes.GetValueOrDefault(spec.WorkVolume))
                {
                    _volumes.Remove(spec.WorkVolume);
                    throw new ChargehandException(ErrorCode.ContainerUnavailable, "preparing the workspace failed (exit 128): fatal: destination path '/work' already exists and is not an empty directory", "Retry.");
                }
                _volumes[spec.WorkVolume] = true;
            }
        });
        return Task.CompletedTask;
    }

    public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct)
    {
        Act($"network {name}", () =>
        {
            lock (_gate)
                _networks.Add(name);
        });
        return Task.CompletedTask;
    }

    public Task RemoveNetworkAsync(string name, CancellationToken ct)
    {
        Act($"network-rm {name}", () =>
        {
            lock (_gate)
                _networks.Remove(name);
        });
        return Task.CompletedTask;
    }

    public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) => Task.FromResult(Act($"egress {spec.BatchId}", () =>
    {
        var name = $"chargehand-egress-{spec.BatchId}";
        lock (_gate)
        {
            if (_containers.ContainsKey(name))
                throw new ChargehandException(ErrorCode.ContainerUnavailable, $"docker run of the egress proxy failed: the name {name} is already in use", "Remove it.");
            _containers[name] = new Container("egress", null);
        }
        return name;
    }));

    public Task ConnectNetworkAsync(string container, string network, CancellationToken ct)
    {
        Act($"connect {container}", () => { });
        return Task.CompletedTask;
    }

    public Task KillAllAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> OwnsAsync(string id, CancellationToken ct) => Task.FromResult(true);

    public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(Act("count", () => Containers.Count));

    public Task<EngineResources> ListAsync(CancellationToken ct) => Task.FromResult(Act("list", () => new EngineResources([.. Containers], [.. Networks], [.. Volumes])));

    public Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct)
    {
        Act($"xfer task {runId}", () =>
        {
            lock (_gate)
            {
                _volumes.TryAdd(RunnerNames.Out(runId), false);
                _taskFiles.Add(runId);
            }
        });
        return Task.CompletedTask;
    }

    public Task WriteSeedAsync(string runId, string bundlePath, CancellationToken ct)
    {
        Act($"xfer seed {runId}", () => { });
        return Task.CompletedTask;
    }

    /// <summary>The output volume's files: the outcome, the report and the branch's bundle, once the session left them.</summary>
    public Task FetchOutputAsync(string runId, string directory, CancellationToken ct)
    {
        Act($"fetch {runId}", () =>
        {
            bool done;
            lock (_gate)
                done = _outcomes.Contains(runId);
            if (!done)
                return;
            var outcome = new SessionOutcome(SessionStatus.Completed, "", 9, 1000, 200, 4000, null, 0, [], true, Commits: 1);
            File.WriteAllText(Path.Combine(directory, "session-outcome.json"), JsonSerializer.Serialize(outcome, Snake));
            File.WriteAllText(Path.Combine(directory, "driven-report.json"), """{"summary":"Did the task.","claims":[],"tests":{"command":"npm test","exit_code":0}}""");
            File.Copy(bundleFor(runId), Path.Combine(directory, "chargehand.bundle"), overwrite: true);
        });
        return Task.CompletedTask;
    }

    public Task<TaskUsage?> ReadUsageAsync(string runId, CancellationToken ct) => Task.FromResult<TaskUsage?>(null);

    public Task<byte[]?> ReadTranscriptTailAsync(string runId, int maxBytes, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    /// <summary>A host that opens one per call: it does not refuse a second pull request for the same branch, so only the handover's own lookup keeps it to one.</summary>
    public Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct) => Task.FromResult(Act($"pr create {head}", () =>
    {
        lock (_gate)
        {
            PullRequests.Add((PullRequests.Count + 1, head));
            return Ref(PullRequests.Count);
        }
    }));

    public Task EditAsync(RemoteRepository repo, int number, string title, string body, CancellationToken ct)
    {
        Act($"pr edit {number}", () => { });
        return Task.CompletedTask;
    }

    public Task<PullRequestRef?> FindByHeadAsync(RemoteRepository repo, string head, CancellationToken ct) => Task.FromResult(Act($"pr find {head}", () =>
    {
        lock (_gate)
            return PullRequests.Where(p => p.Head == head).Select(p => (PullRequestRef?)Ref(p.Number)).FirstOrDefault();
    }));

    private static PullRequestRef Ref(int number) => new($"https://example.test/o/r/pull/{number}", number);
}
