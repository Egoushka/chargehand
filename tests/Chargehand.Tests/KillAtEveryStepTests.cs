using System.Diagnostics;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Runtimes.ClaudeCode.Api;
using Chargehand.Workspace;

namespace Chargehand.Tests;

/// <summary>ADR 0061's exit test: a driven task runs end to end through <see cref="DrivenRun"/>'s own wiring (the real <see cref="Handover"/>, <see cref="ContainerVerifier"/> and journaled
/// steps, a real git remote), and the server is killed once at every call it makes to the engine, the runner's volumes and the pull request host: before the call, after it, and for a
/// <c>docker run</c> between its create and its start; and twice inside the push. A kill keeps what was on disk at that instant; a restarted server opens it, sweeps and resumes the batch.
/// Whatever the cut, the task ends with one run id, one branch, one pull request, one verification and one outcome, and leaves nothing a run without a kill does not leave. The cut points
/// are the calls of an uncut run, so a new side effect is cut too; the steps in flight at each cut are read from the journal the kill left.</summary>
public sealed class KillAtEveryStepTests
{
    private const string Image = "ghcr.io/example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Batch = "run-batch-k";

    /// <summary>The steps of a first session's task; each must be in flight at one cut at least, or the test no longer reaches it.</summary>
    private static readonly string[] Steps = ["session/t1/<run>", "prep/<run>", "xfer/<run>/task", "prep/<run>/verify", "handover/<run>/verify", "handover/<run>/push", "handover/<run>/pr"];

    private static string G(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "user.name=t", "-c", "user.email=t@example.test", .. args])
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {output}");
        return output;
    }

    /// <summary>One batch of one task in its own world, state directory and remote, run by a series of server processes; each one a kill ends leaves a copy of its disk for the next.</summary>
    private sealed class Scenario : IDisposable
    {
        private readonly TempDir _dir = new();
        private readonly Lab _lab;
        private readonly Profile _profile;
        private readonly DrivenServices _services;
        private int _lives;
        private string? _frozen;

        public KillWorld World { get; }

        public string Remote => Path.Combine(_dir.Path, "remote.git");

        /// <summary>The state directory and run log the next process opens.</summary>
        private string Disk { get; set; }

        private static string State(string disk) => Path.Combine(disk, "batches");

        private static string Log(string disk) => Path.Combine(disk, "log.jsonl");

        public Scenario(Lab lab)
        {
            _lab = lab;
            Disk = Path.Combine(_dir.Path, "life-0");
            World = new KillWorld(BundleFor) { Killed = Freeze };
            G(_dir.Path, "init", "-q", "--bare", "-b", "main", Remote);
            _profile = Runs.Profile(_dir.Path) with
            {
                ClaudeCode = new ClaudeCodeSettings("2.1.283", OauthTokenSecret: "claude-code-oauth-token"),
                Driven = new DrivenSettings(true, MaxParallel: 1, Images: [Image], PushSecret: "secret-push"),
            };
            _services = new DrivenServices(World, World, World, _ => World, null,
                (_, _, _) => Task.FromResult(new DrivenCheckout(lab.Source, lab.Base, $"file://{Remote}", "main")), new DrivenRunTests.NoEvidenceProblems(), name => $"value-of-{name}",
                Poll: TimeSpan.FromMilliseconds(1));
        }

        /// <summary>The branch a session of <paramref name="run"/> made: the lab's one commit on the base under <c>chargehand/&lt;run&gt;</c>, the same file however often it is asked for.</summary>
        private string BundleFor(string run)
        {
            var bundle = Path.Combine(_dir.Path, "bundles", $"{run}.bundle");
            if (!File.Exists(bundle))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
                File.WriteAllBytes(bundle, _lab.BundleOf($"chargehand/{run}"));
            }
            return bundle;
        }

        /// <summary>What a killed server leaves: its state directory and run log as they are at this instant. The journal's shared-memory index is rebuilt by the next opener.</summary>
        private void Freeze()
        {
            _frozen = Path.Combine(_dir.Path, $"life-{++_lives}");
            Directory.CreateDirectory(State(_frozen));
            foreach (var file in Directory.Exists(State(Disk)) ? Directory.GetFiles(State(Disk)) : [])
                if (!file.EndsWith("-shm", StringComparison.Ordinal))
                    File.Copy(file, Path.Combine(State(_frozen), Path.GetFileName(file)));
            if (File.Exists(Log(Disk)))
                File.Copy(Log(Disk), Log(_frozen));
        }

        private RunRequest Request() =>
            new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", _lab.Base)),
                Driven: new RequestDriven([new DrivenTask("t1", Goal: "Add a retry")], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

        /// <summary>A server process: the first sends the batch; a later one sweeps, as a server does at its start, and resumes what the last one left unfinished. Killed at
        /// <paramref name="cut"/> or by <paramref name="killer"/>, the next process opens what this one left on disk then.</summary>
        /// <returns>Whether this process was killed, and the batch's result when it was not.</returns>
        public async Task<(bool Killed, ResultContract? Result)> ProcessAsync(KillWorld.Cut? cut = null, Func<KillWorld, Task>? killer = null)
        {
            using var stop = new CancellationTokenSource();
            World.Revive(stop, cut);
            var killing = killer?.Invoke(World);
            ResultContract? result = null;
            try
            {
                using var run = new DrivenRun(_profile, new JsonlRunLog(Log(Disk)), Repo.Root, _services, State(Disk));
                if (_lives == 0 && !File.Exists(Log(Disk)))
                    result = await run.RunAsync(Request(), Batch, g => $"tok-{g.RunId}", stop.Token, null, () => false);
                else
                {
                    var unfinished = await run.UnfinishedAsync(stop.Token);
                    await run.SweepOrphansAsync(unfinished, stop.Token);
                    if (unfinished.SingleOrDefault() is { } resumed)
                        result = await run.RunAsync(resumed.Request, resumed.RunId, g => $"tok-{g.RunId}", stop.Token, null, () => false, resumed);
                }
            }
            catch (OperationCanceledException) when (World.Dead)
            {
            }
            finally
            {
                if (killing is not null)
                    await killing;
            }
            if (!World.Dead)
                return (false, result);
            Disk = _frozen!;
            return (true, null);
        }

        /// <summary>The steps in flight on the disk the next process opens.</summary>
        public async Task<IReadOnlyList<string>> InFlightAsync()
        {
            await using var journal = await DrivenJournal.OpenAsync(State(Disk));
            return [.. (await journal.UnfinishedAsync(Batch)).Select(k => k.Name)];
        }

        public Task<RunLogData> LogAsync() => new JsonlRunLog(Log(Disk)).ReadAllAsync(default);

        public IReadOnlyList<string> Branches() => [.. G(Remote, "for-each-ref", "--format=%(refname)", "refs/heads").Split('\n', StringSplitOptions.RemoveEmptyEntries)];

        public void Dispose() => _dir.Dispose();
    }

    /// <summary>A source repository with a test command, and <c>file://</c> remotes accepted (as <c>scripts/driven-e2e.sh</c> does) while it lasts.</summary>
    private sealed class Lab : IDisposable
    {
        private readonly TempDir _dir = new();
        private readonly string? _localRemote = Environment.GetEnvironmentVariable("CHARGEHAND_E2E_LOCAL_REMOTE");

        public string Source => Path.Combine(_dir.Path, "source");

        public string Base { get; }

        private string Template => Path.Combine(_dir.Path, "template.bundle");

        /// <summary>The session's bundle with its one ref named <paramref name="branch"/>: a bundle's header is text before its pack, and the pack does not name the ref.</summary>
        public byte[] BundleOf(string branch)
        {
            var bytes = File.ReadAllBytes(Template);
            var end = bytes.AsSpan().IndexOf("\n\n"u8) + 2;
            var header = System.Text.Encoding.ASCII.GetString(bytes, 0, end).Replace("refs/heads/chargehand/template", $"refs/heads/{branch}", StringComparison.Ordinal);
            return [.. System.Text.Encoding.ASCII.GetBytes(header), .. bytes.AsSpan(end)];
        }

        public Lab()
        {
            Directory.CreateDirectory(Source);
            G(Source, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(Source, "README.md"), "hello\n");
            File.WriteAllText(Path.Combine(Source, "package.json"), """{"scripts":{"test":"echo ok"}}""");
            G(Source, "add", "-A");
            G(Source, "commit", "-q", "-m", "base");
            Base = G(Source, "rev-parse", "HEAD").Trim();
            var work = Path.Combine(_dir.Path, "work");
            G(_dir.Path, "clone", "-q", "--template=", Source, work);
            G(work, "checkout", "-q", "-b", "chargehand/template", Base);
            File.WriteAllText(Path.Combine(work, "retry.txt"), "retry twice\n");
            G(work, "add", "-A");
            G(work, "commit", "-q", "-m", "feat: retry");
            G(work, "bundle", "create", Template, "chargehand/template", "--not", "--remotes");
            Environment.SetEnvironmentVariable("CHARGEHAND_E2E_LOCAL_REMOTE", "1");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CHARGEHAND_E2E_LOCAL_REMOTE", _localRemote);
            _dir.Dispose();
        }
    }

    /// <summary>A hook of the remote that marks it was reached and does not end until the process is killed: in <c>pre-receive</c> the branch is not on the remote yet, in <c>post-receive</c> it is.</summary>
    private static Func<KillWorld, Task> HangIn(Scenario scenario, string hook) => async world =>
    {
        var reached = Path.Combine(scenario.Remote, "reached");
        var path = Path.Combine(scenario.Remote, "hooks", hook);
        await File.WriteAllTextAsync(path, $"#!/bin/sh\ncat > /dev/null\n: > '{reached}'\nwhile [ -e '{path}' ]; do sleep 0.05; done\nexit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var watch = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!File.Exists(reached) && await watch.WaitForNextTickAsync(limit.Token))
        {
        }
        world.Kill();
        File.Delete(path);
    };

    private static string Normal(string text, string? run) => run is null ? text : text.Replace(run, "<run>", StringComparison.Ordinal);

    /// <summary>One way to kill a process: the call it dies at, or a killer from outside any call.</summary>
    private sealed record Kill(string Label, KillWorld.Cut? Cut, Func<Scenario, Func<KillWorld, Task>>? Killer = null);

    /// <summary>A kill before, after and (for a <c>docker run</c>) inside each of <paramref name="calls"/>.</summary>
    private static IEnumerable<Kill> At(IReadOnlyList<string> calls)
    {
        for (var i = 1; i <= calls.Count; i++)
        {
            yield return new($"{calls[i - 1]}: before", new(i, KillWorld.CutMode.Before));
            yield return new($"{calls[i - 1]}: after", new(i, KillWorld.CutMode.After));
            if (calls[i - 1].StartsWith("start ", StringComparison.Ordinal))
                yield return new($"{calls[i - 1]}: made, not started", new(i, KillWorld.CutMode.Made));
        }
    }

    private sealed record Clean(IReadOnlyCollection<string> Volumes, IReadOnlyCollection<string> Networks);

    /// <summary>The world after the last process, as messages for what does not hold. <paramref name="sessions"/>: the session containers that should have run.</summary>
    private static async Task<List<string>> CheckAsync(Scenario s, ResultContract? result, Clean clean, IReadOnlyList<string> inFlight, int sessions)
    {
        List<string> wrong = [];
        var run = s.World.SessionsRan.FirstOrDefault();
        var log = await s.LogAsync();
        void Expect(bool holds, string what)
        {
            if (!holds)
                wrong.Add(what);
        }

        Expect(result?.Status == ResultStatus.Completed, $"the batch ended {result?.Status.ToString() ?? "unresumed"}: {result?.Summary}");
        var tasks = result is null ? [] : JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks").EnumerateArray().ToList();
        Expect(tasks.Count == 1 && tasks[0].GetProperty("status").GetString() == "completed" && tasks[0].GetProperty("pr_url").GetString() == "https://example.test/o/r/pull/1",
            $"the task did not end completed with pull request 1: {(tasks.Count == 1 ? tasks[0].ToString() : "no task")}");
        Expect(s.World.SessionsRan.Count == sessions && s.World.SessionsRan.Distinct().Count() == 1, $"session containers that ran: {string.Join(", ", s.World.SessionsRan)}");
        Expect(s.World.Containers.Count == 0, $"containers left: {string.Join(", ", s.World.Containers)}");
        Expect(s.World.Volumes.Select(v => Normal(v, run)).Order().SequenceEqual(clean.Volumes.Order()), $"volumes left: {string.Join(", ", s.World.Volumes.Select(v => Normal(v, run)))}");
        Expect(s.World.Networks.Order().SequenceEqual(clean.Networks.Order()), $"networks left: {string.Join(", ", s.World.Networks)}");
        var taskRuns = log.Runs.Where(r => r.RunId != Batch).Select(r => r.RunId).ToList();
        Expect(taskRuns.Count == 1 && taskRuns[0] == run, $"task run records: {string.Join(", ", taskRuns)}");
        Expect(log.Runs.Count(r => r.RunId == Batch) == 1, "the batch has not exactly one run record");
        Expect(s.Branches().SequenceEqual([$"refs/heads/chargehand/{run}"]), $"branches on the remote: {string.Join(", ", s.Branches())}");
        Expect(s.World.PullRequests.SequenceEqual([(1, $"chargehand/{run}")]), $"pull requests: {string.Join(", ", s.World.PullRequests)}");
        // A verification the kill cut short runs again: its verdict was never recorded.
        var verifyCut = inFlight.Any(k => k.StartsWith("handover/", StringComparison.Ordinal) && k.EndsWith("/verify", StringComparison.Ordinal));
        Expect(s.World.Verifications == 1 || verifyCut && s.World.Verifications == 2, $"verifications: {s.World.Verifications}");
        Expect((await s.InFlightAsync()).Count == 0, "steps are left in the journal");
        return wrong;
    }

    /// <summary>The calls of the process after <paramref name="before"/> when nothing kills it, and what the batch leaves then.</summary>
    private static async Task<(List<string> Calls, Clean Clean)> ProbeAsync(Lab lab, Func<Scenario, Task> before)
    {
        using var probe = new Scenario(lab);
        await before(probe);
        var (killed, result) = await probe.ProcessAsync();
        Assert.False(killed);
        Assert.Equal(ResultStatus.Completed, result!.Status);
        var run = probe.World.SessionsRan[0];
        return ([.. probe.World.ProcessCalls], new([.. probe.World.Volumes.Select(v => Normal(v, run))], probe.World.Networks));
    }

    /// <summary>Each of <paramref name="kills"/> in a scenario of its own, in parallel: <paramref name="before"/> brings it to where the kill goes, then the kill, then one restart.</summary>
    private static async Task<List<string>> KillEachAsync(Lab lab, IReadOnlyList<Kill> kills, Func<Scenario, Task> before, Clean clean, int sessions, ISet<string> covered)
    {
        var wrong = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(kills, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, async (kill, _) =>
        {
            using var s = new Scenario(lab);
            await before(s);
            var (killed, _) = await s.ProcessAsync(kill.Cut, kill.Killer?.Invoke(s));
            if (!killed)
            {
                wrong.Add($"{kill.Label}: the run ended before the cut");
                return;
            }
            var inFlight = await s.InFlightAsync();
            var (again, result) = await s.ProcessAsync();
            var run = s.World.SessionsRan.FirstOrDefault();
            lock (covered)
                covered.UnionWith(inFlight.Select(k => Normal(k, run)));
            foreach (var what in again ? ["the restarted server was killed"] : await CheckAsync(s, result, clean, inFlight, sessions))
                wrong.Add($"{kill.Label} [in flight: {string.Join(", ", inFlight.Select(k => Normal(k, run)))}]: {what}");
        });
        return [.. wrong.Order(StringComparer.Ordinal)];
    }

    private static Task Nothing(Scenario _) => Task.CompletedTask;

    [Fact]
    public async Task A_server_killed_at_any_step_of_a_driven_task_and_restarted_ends_with_one_run_branch_pull_request_verification_and_outcome()
    {
        using var lab = new Lab();
        var (calls, clean) = await ProbeAsync(lab, Nothing);
        List<Kill> kills = [.. At(calls), new("push: before the remote took the branch", null, s => HangIn(s, "pre-receive")),
            new("push: after the remote took the branch", null, s => HangIn(s, "post-receive"))];
        HashSet<string> covered = new(StringComparer.Ordinal);

        var wrong = await KillEachAsync(lab, kills, Nothing, clean, sessions: 1, covered);

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {kills.Count} kills went wrong:\n{string.Join('\n', wrong)}\n\nThe calls of a run without a kill:\n{string.Join('\n', calls)}");
        Assert.Empty(Steps.Except(covered));
    }

    [Fact]
    public async Task A_server_killed_at_any_step_after_a_session_died_without_its_outcome_starts_it_again_once_on_a_fresh_workspace()
    {
        using var lab = new Lab();
        var started = (await ProbeAsync(lab, Nothing)).Calls.FindIndex(c => c.StartsWith("start session ", StringComparison.Ordinal)) + 1;
        // The first server is killed just after docker started the session; the session then dies without an outcome and leaves its work volume as it was.
        async Task Died(Scenario s)
        {
            Assert.True((await s.ProcessAsync(new(started, KillWorld.CutMode.After))).Killed);
            s.World.CrashSessions();
        }
        var (calls, clean) = await ProbeAsync(lab, Died);
        // Up to the new session's start: from there on the calls are those of the test above.
        List<Kill> kills = [.. At(calls[..(calls.FindIndex(c => c.StartsWith("start session ", StringComparison.Ordinal)) + 1)])];
        HashSet<string> covered = new(StringComparer.Ordinal);

        var wrong = await KillEachAsync(lab, kills, Died, clean, sessions: 2, covered);

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {kills.Count} kills went wrong:\n{string.Join('\n', wrong)}\n\nThe calls of the restarted server without a kill:\n{string.Join('\n', calls)}");
        Assert.Contains("session/t1/<run>", covered);
    }
}
