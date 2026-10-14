using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Tasks.Api;
using Chargehand.Modules.Tasks.Core;

namespace Chargehand.Modules.Tasks.Tests;

/// <summary>C4 slice S3: the restart decision as a table, one row per path through <c>TaskRunner.Prepare.cs</c>. Each row is the saved entry, what the shell would find out if asked, the facts
/// asked for in order, and how the task is taken up. A row marked "pins current behaviour" is one the C4 brief (section 3) flags; a later decision can flip it here. No fakes, no clock, no file system.</summary>
public class TaskResumeTests
{
    private const string Run = "run-2";

    /// <summary>What the shell finds out when asked.</summary>
    private sealed record World(bool StartCut = false, string? CutSeed = null, ContainerStatus Status = ContainerStatus.Running, bool HasOutcome = false, string? GoneSeed = null);

    private sealed record Row(TaskEntry? Entry, World World, ResumeStep[] Asked, TakeUp Up);

    private static readonly TaskOutcome Completed = new("t1", TaskState.Completed, Run, "pull request opened");
    private static readonly TaskOutcome Cancelled = new("t1", TaskState.Cancelled, Run, "cancelled");

    private static TaskEntry Entry(TaskPhase phase, int continuation = 0, string? container = null, TaskOutcome? outcome = null, string? runId = Run) =>
        new("t1", null, "goal", phase, runId, "chargehand/run-1", container, Outcome: outcome, Continuation: continuation,
            Runs: continuation == 0 ? [runId ?? "run-1"] : ["run-1", Run]);

    private static readonly AskStartCut Cut = new(Run);
    private static readonly InspectContainer Inspect = new($"chargehand-{Run}");
    private static readonly AskOutcome Outcome = new(Run);
    private static readonly RemoveRun Remove = new(Run, null);
    private static readonly TakeUp Fresh = new(ResumeKind.Fresh);
    private static readonly TakeUp Attach = new(ResumeKind.Attach);

    private static readonly Dictionary<string, Row> Rows = new()
    {
        ["no entry: fresh"] = new(null, new(), [], Fresh),
        ["pending: fresh"] = new(Entry(TaskPhase.Pending), new(), [], Fresh),
        ["started without a run id: fresh"] = new(Entry(TaskPhase.Started, runId: null), new(), [], Fresh),
        // pins current behaviour; C4 decision pending: a follow-up a restart cut is dropped (item 3).
        ["handed over: done with its saved outcome, the follow-up is not taken up"] = new(Entry(TaskPhase.HandedOver, outcome: Completed), new(), [], new(ResumeKind.Done, Outcome: Completed)),
        // pins current behaviour; C4 decision pending: Failed stands for a cancel too (item 2).
        ["failed (a cancel): done with its saved outcome"] = new(Entry(TaskPhase.Failed, outcome: Cancelled), new(), [], new(ResumeKind.Done, Outcome: Cancelled)),
        ["failed without an outcome: fresh"] = new(Entry(TaskPhase.Failed), new(), [], Fresh),

        ["started, first session's start cut: restart it under its run id"] = new(Entry(TaskPhase.Started), new(StartCut: true), [Cut], new(ResumeKind.RestartFirstSession)),
        ["started, continuation's start cut, its seed there: restart it under its run id"] = new(Entry(TaskPhase.Started, 1), new(StartCut: true, CutSeed: "seed.bundle"),
            [Cut, new AskCutSeed("run-1")], new(ResumeKind.RestartContinuation, "seed.bundle")),
        ["started, continuation's start cut, its seed gone, container running: attach"] = new(Entry(TaskPhase.Started, 1), new(StartCut: true),
            [Cut, new AskCutSeed("run-1"), Inspect], Attach),
        // pins current behaviour; C4 decision pending: the bundle already found gone is asked for again once the session is found gone too.
        ["started, continuation's start cut, its seed gone, session gone: the seed is asked for again, then fresh"] = new(Entry(TaskPhase.Started, 1),
            new(StartCut: true, Status: ContainerStatus.Missing), [Cut, new AskCutSeed("run-1"), Inspect, Outcome, Remove, new AskGoneSeed("run-1")], Fresh),
        ["started, continuation's start cut with one run known: handled as begun"] = new(Entry(TaskPhase.Started, 1) with { Runs = [Run] }, new(StartCut: true), [Cut, Inspect], Attach),

        ["started, container running: attach"] = new(Entry(TaskPhase.Started), new(), [Cut, Inspect], Attach),
        ["started, container exited: attach and read its outcome"] = new(Entry(TaskPhase.Started), new(Status: ContainerStatus.Exited), [Cut, Inspect], Attach),
        ["started, container created and never started: attach"] = new(Entry(TaskPhase.Started), new(Status: ContainerStatus.Created), [Cut, Inspect], Attach),
        ["started, container named in the entry: that one is inspected"] = new(Entry(TaskPhase.Started, container: "c-7"), new(), [Cut, new InspectContainer("c-7")], Attach),
        ["started, container gone, outcome kept: attach"] = new(Entry(TaskPhase.Started), new(Status: ContainerStatus.Missing, HasOutcome: true), [Cut, Inspect, Outcome], Attach),
        ["started, first session gone without an outcome: clear it, fresh"] = new(Entry(TaskPhase.Started), new(Status: ContainerStatus.Missing), [Cut, Inspect, Outcome, Remove], Fresh),
        ["started, continuation gone without an outcome, seed there: clear it, restart from the seed under a new run id"] = new(Entry(TaskPhase.Started, 1),
            new(Status: ContainerStatus.Missing, GoneSeed: "seed.bundle"), [Cut, Inspect, Outcome, Remove, new AskGoneSeed("run-1")], new(ResumeKind.RestartContinuationRenamed, "seed.bundle")),
        ["started, continuation gone without an outcome, seed gone: clear it, fresh"] = new(Entry(TaskPhase.Started, 1), new(Status: ContainerStatus.Missing),
            [Cut, Inspect, Outcome, Remove, new AskGoneSeed("run-1")], Fresh),
        ["started, gone container named in the entry: that one is removed"] = new(Entry(TaskPhase.Started, container: "c-7"), new(Status: ContainerStatus.Missing),
            [Cut, new InspectContainer("c-7"), Outcome, new RemoveRun(Run, "c-7")], Fresh),

        // pins current behaviour; C4 decision pending: the outcome read before the restart is read again (item 6; its records and events are idempotent since #323 and #325).
        ["finished, container exited: attach and read the outcome again"] = new(Entry(TaskPhase.Finished), new(Status: ContainerStatus.Exited), [Inspect], Attach),
        ["finished, container gone, outcome kept: attach"] = new(Entry(TaskPhase.Finished), new(Status: ContainerStatus.Missing, HasOutcome: true), [Inspect, Outcome], Attach),
        // pins current behaviour; C4 decision pending: a session whose outcome was read runs again from scratch once its container and outcome are gone.
        ["finished, container and outcome gone: clear it, fresh"] = new(Entry(TaskPhase.Finished), new(Status: ContainerStatus.Missing), [Inspect, Outcome, Remove], Fresh),
        ["finished continuation, container and outcome gone, seed there: restart from the seed under a new run id"] = new(Entry(TaskPhase.Finished, 1),
            new(Status: ContainerStatus.Missing, GoneSeed: "seed.bundle"), [Inspect, Outcome, Remove, new AskGoneSeed("run-1")], new(ResumeKind.RestartContinuationRenamed, "seed.bundle")),
    };

    public static TheoryData<string> Names() => new(Rows.Keys);

    [Theory]
    [MemberData(nameof(Names))]
    public void Decides(string row)
    {
        var (entry, world, asked, up) = Rows[row];
        List<ResumeStep> steps = [];
        var facts = new ResumeFacts();
        ResumeStep step;
        while ((step = TaskResume.Decide(entry, facts)) is not TakeUp)
        {
            steps.Add(step);
            Assert.True(steps.Count <= 10, $"{row}: no decision after {string.Join(", ", steps)}");
            facts = step switch
            {
                AskStartCut => facts with { StartCut = world.StartCut },
                AskCutSeed => facts with { CutSeed = new SeedLookup(world.CutSeed) },
                InspectContainer => facts with { Status = world.Status },
                AskOutcome => facts with { HasOutcome = world.HasOutcome },
                RemoveRun => facts with { Removed = true },
                AskGoneSeed => facts with { GoneSeed = new SeedLookup(world.GoneSeed) },
                _ => throw new InvalidOperationException(step.ToString()),
            };
        }

        Assert.Equal(asked, steps);
        Assert.Equal(up, step);
    }
}
