# Systematic concurrency tests (Coyote)

[Coyote](https://github.com/microsoft/coyote) rewrites the IL of the assemblies listed in the csproj so it, not the thread pool, decides which task runs next. A scenario then runs over 1,000 task schedules; a failing schedule replays from its seed. The tool is pinned in `dotnet-tools.json`; the build rewrites the output directory in place (`CoyoteRewrite` target).

- `BatchSchedulerConcurrencyTests`: usage reports, caps and task ends meeting in many orders.
- `TaskRunnerCancelTests`: a cancel landing at any point of a task's run. With the fix of #308 taken out, this finds the lost pull request in the first 10 schedules (5 of 5 seeds); a plain loop of the same scenario finds it in 2 of 3,000 runs.

## What it cannot see

- Only awaits, `Task.Run`, `lock`/`Monitor`, `SemaphoreSlim` and the like are scheduling points. A bug that needs two threads inside one unlocked region (a removed `lock`, a race inside `CancellationTokenSource`) is invisible: removing the lock around `BatchSchedulerRun.Apply` made a plain loop fail in 17% to 65% of runs and Coyote find nothing.
- Code that uses a newer BCL API than Coyote 1.7.11 knows crashes the test host. Known: `ConfigureAwait(ConfigureAwaitOptions)` (`BatchSchedulerRun` uses it on the rate-limit wait, so scenarios keep the clock out of that path) and `Task.Delay(TimeSpan, TimeProvider)`.
- Static state shared between iterations fails the run ("created in a previous test iteration"): `JsonlRunLog` keeps a static lock, so scenarios use an in-memory `IRunLog`.
- Invariants decide what counts as a bug: a scheduler that turns a thrown exception into a `Failed` outcome hides it unless the scenario asserts the error code.

Telemetry is switched off in code (`WithTelemetryEnabled(false)`); keep it off.

Run: `dotnet test tests/Chargehand.Modules.Batches.CoyoteTests` (about 5 s).
