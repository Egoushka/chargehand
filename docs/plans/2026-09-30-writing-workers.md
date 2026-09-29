# Writing workers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A run under the `code` preset ends with a git branch that builds and passes the repository's tests in a
sandbox, or with a failed result that says why and still carries the branch.

**Architecture:** The worker keeps no shell (ADR 0006). A node kind with `writes: true` runs in a per-run clone;
after the worker answers, chargehand runs the repository's test command through a sandbox interface (`sandbox-exec`
on macOS, `bwrap` on Linux), feeds failures back in the same session for at most 2 rounds, then commits the branch with
hooks disabled and returns it as artifacts. Nothing is pushed or merged.

**Tech Stack:** .NET 10 / xUnit 2.9 (tests), `System.Diagnostics.Process`, git CLI, YAML presets, Markdown prompts, bash.

**Spec:** `docs/specs/2026-09-30-writing-workers-design.md`; decision record `docs/adr/0035-sandboxed-writing-workers.md`.

## Global Constraints

- The repository is public: never commit IP addresses, hostnames, absolute home paths, employer or project names, keys,
  tracker URLs or prompts from real runs.
- Done for every task: `scripts/check.sh` exits 0 and its last test line reads `Passed!  - Failed:     0`. Run dotnet
  outside the agent sandbox (`dangerouslyDisableSandbox`): inside it `dotnet restore` hangs for minutes and exits 1.
- Conventional Commits; never `--no-verify`; never force-push. PR title ends with the tracker key `(CHARGEHAND-<n>)`.
- Published schemas change additively only (`SchemaCompatTests`): new optional properties and new enum values.
- One version source: `Directory.Build.props`.
- No shell for workers: no preset in this plan allows `shell`.
- Code is environment-agnostic; personal paths live in gitignored `profiles/local.*`.
- Public text says "citations checked", not "claims verified".

## Review Focus

1. A repository with no recognisable test command: the run completes with the open question "no test command found"
   and confidence at most 0.5; it does not fail and does not claim the change was tested. Pinned in Task 6 (`NoCommand`).
2. The worker changes nothing: nothing to commit means a `failed` result with `error.action`, not an empty branch.
   Pinned in Task 4 (`Commit_of_an_unchanged_tree_is_null`) and Task 6.
3. A verification command that hangs or floods output: killed at the timeout, output tail bounded to 8 KiB, the run
   fails `verification_failed`. Pinned in Task 3 (`A_hanging_command_is_killed`, `Output_is_tailed`).
4. A repository whose build needs the network: the failure output reaches the worker and the artifact says
   `network: false`; the guide names `sandbox.network`. Pinned in Task 6 (`Verification_artifact_records_network`).
5. A repository with a commit hook or a `core.hooksPath`: chargehand's commit runs none. Pinned in Task 4
   (`Commit_runs_no_repository_hook`).
6. A worker edit to the test command's own script: allowed, listed in the artifact's `changed_verification_paths`.
   Pinned in Task 6 (`Changed_build_files_are_listed`).

---

### Task 1: Contracts and preset schema (additive)

**Files:**
- Modify: `src/Chargehand.Contracts/ResultContract.cs` (`ErrorCode`: add `SandboxUnavailable`, `VerificationFailed` at the end)
- Modify: `schemas/result/v1/result.schema.json` (error code enum: add `sandbox_unavailable`, `verification_failed`)
- Modify: `schemas/preset/v1/preset.schema.json` (`node_kind`: optional `writes` boolean and `verify` object)
- Modify: `src/Chargehand/Config/Preset.cs` (`NodeKind`: `Writes`, `Verify`)
- Modify: `schemas/request/v1/*.json` (`context.verify`: optional array of strings, `minItems` 1)
- Modify: `src/Chargehand.Contracts/` request record that carries `RequestContext` (add `IReadOnlyList<string>? Verify = null`)
- Test: `tests/Chargehand.Tests/WritingContractsTests.cs`

**Interfaces:**
- Produces: `ErrorCode.SandboxUnavailable`, `ErrorCode.VerificationFailed`; `NodeKind.Writes` (bool, default false);
  `NodeKind.Verify` (`VerifySettings?`: `TimeoutSeconds` default 600, `MaxFixRounds` default 2); `RequestContext.Verify`
  (`IReadOnlyList<string>?`).

- [ ] **Step 1: Write the failing test.** A preset YAML with `writes: true` and `verify: { timeout_seconds: 30,
  max_fix_rounds: 1 }` loads through `Preset.Load` with those values; a preset without them gets `Writes == false` and
  `Verify == null`; a request JSON with `context.verify: ["dotnet","test"]` validates and round-trips; the two error
  codes serialise as `sandbox_unavailable` and `verification_failed` and validate against the result schema.
  Use `PresetRoot` (tests/Chargehand.Tests/PresetRoot.cs) to build the temp preset root as other preset tests do.
- [ ] **Step 2: Run it and see it fail** (`dotnet test --filter WritingContractsTests`): unknown property `writes`.
- [ ] **Step 3: Implement** the fields above. `VerifySettings(int TimeoutSeconds = 600, int MaxFixRounds = 2)` with
  snake_case names like the neighbouring records.
- [ ] **Step 4: Run** `scripts/check.sh`; `SchemaCompatTests` must pass (only additions).
- [ ] **Step 5: Commit** `feat(contracts): writes and verify on node kinds, verify on the request, two error codes`.

### Task 2: The sandbox

**Files:**
- Create: `src/Chargehand/Sandbox/ISandbox.cs`, `SeatbeltSandbox.cs`, `BubblewrapSandbox.cs`, `NoSandbox.cs`, `SandboxSelector.cs`
- Modify: `src/Chargehand/Config/Profile.cs` (`Sandbox: SandboxSettings?`), `profiles/profile.schema.json` (`sandbox`)
- Modify: `profiles/example.json` (a commented `sandbox` example with placeholders)
- Test: `tests/Chargehand.Tests/SandboxTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record SandboxSpec(IReadOnlyList<string> Argv, string WorkDirectory, TimeSpan Timeout, bool Network,
    IReadOnlyList<string> PassEnv);
public sealed record SandboxResult(int ExitCode, string OutputTail, bool TimedOut, TimeSpan Duration);
public interface ISandbox
{
    string Kind { get; } // "seatbelt" | "bubblewrap" | "none"
    Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken ct);
}
public sealed record SandboxSettings(string Kind = "auto", bool Network = false, IReadOnlyList<string>? Env = null);
public static class SandboxSelector
{
    // Throws ChargehandException(ErrorCode.SandboxUnavailable, message, action) when kind is auto and nothing fits.
    public static ISandbox Select(SandboxSettings? settings, Func<string, bool> onPath, bool isMacOs, bool isLinux);
}
```

  Output tail: last 8192 UTF-8 bytes of combined stdout and stderr (decode with replacement).
  Seatbelt profile (`SeatbeltSandbox.Profile(spec, home, tempDir)` internal, pure): `(version 1) (deny default)`, allow
  `process-exec*`, `process-fork`, `signal (target self)`, `sysctl-read`, `mach-lookup`, `file-read*` everywhere then
  `(deny file-read* (subpath "<home>/.ssh") ...)` for each credential location in spec decision 7, `file-write*` only for
  `(subpath "<workdir>")` and `(subpath "<tempDir>")` plus `/dev/null`, and `(allow network*)` only when `spec.Network`.
  Bubblewrap argv (`BubblewrapSandbox.Args(spec, home, tempDir)` internal, pure): `--ro-bind / /`, `--dev /dev`,
  `--proc /proc`, `--tmpfs <home>` (hides credentials), `--bind <workdir> <workdir>`, `--bind <tempDir> <tempDir>`,
  `--unshare-all` plus `--share-net` only when `spec.Network`, `--die-with-parent`, `--chdir <workdir>`, `--clearenv`
  with `--setenv` for the allowlist, then `--`, then the argv.

- [ ] **Step 1: Write failing tests.**

```csharp
public class SandboxTests
{
    [Fact]
    public void Auto_on_a_machine_with_no_sandbox_refuses_with_an_action()
    {
        var e = Assert.Throws<ChargehandException>(() => SandboxSelector.Select(null, _ => false, isMacOs: false, isLinux: true));
        Assert.Equal(ErrorCode.SandboxUnavailable, e.Error.Code);
        Assert.Contains("sandbox.kind", e.Error.Action);
    }

    [Fact]
    public void None_is_chosen_only_when_asked_for()
    {
        Assert.Equal("none", SandboxSelector.Select(new SandboxSettings("none"), _ => false, false, true).Kind);
        Assert.Equal("bubblewrap", SandboxSelector.Select(null, n => n == "bwrap", false, true).Kind);
        Assert.Equal("seatbelt", SandboxSelector.Select(null, n => n == "sandbox-exec", true, false).Kind);
    }

    [Fact]
    public void Seatbelt_profile_denies_network_and_credentials_and_writes_only_the_workspace()
    {
        var p = SeatbeltSandbox.Profile(new SandboxSpec(["true"], "/w/run", TimeSpan.FromSeconds(5), false, []), "/h", "/t");
        Assert.Contains("(deny default)", p);
        Assert.DoesNotContain("(allow network", p);
        Assert.Contains("(deny file-read* (subpath \"/h/.ssh\"))", p);
        Assert.Contains("(allow file-write* (subpath \"/w/run\") (subpath \"/t\")", p);
    }

    [Fact]
    public void Bubblewrap_shares_the_network_only_on_request()
    {
        var off = BubblewrapSandbox.Args(new SandboxSpec(["true"], "/w", TimeSpan.FromSeconds(5), false, []), "/h", "/t");
        var on = BubblewrapSandbox.Args(new SandboxSpec(["true"], "/w", TimeSpan.FromSeconds(5), true, []), "/h", "/t");
        Assert.DoesNotContain("--share-net", off);
        Assert.Contains("--share-net", on);
        Assert.Contains("--clearenv", off);
    }

    [MacOsFact]
    public async Task Seatbelt_blocks_a_write_outside_the_workspace_and_a_read_of_a_credential_directory()
    {
        using var dir = new TempDir();
        var home = Directory.CreateDirectory(Path.Combine(dir.Path, "home")).FullName;
        Directory.CreateDirectory(Path.Combine(home, ".ssh"));
        File.WriteAllText(Path.Combine(home, ".ssh", "id"), "secret");
        var work = Directory.CreateDirectory(Path.Combine(dir.Path, "work")).FullName;
        var outside = Path.Combine(dir.Path, "outside.txt");
        var sandbox = new SeatbeltSandbox(home);
        var write = await sandbox.RunAsync(new SandboxSpec(["/bin/sh", "-c", $"echo x > '{outside}'"], work, TimeSpan.FromSeconds(20), false, []), default);
        var read = await sandbox.RunAsync(new SandboxSpec(["/bin/cat", Path.Combine(home, ".ssh", "id")], work, TimeSpan.FromSeconds(20), false, []), default);
        var inside = await sandbox.RunAsync(new SandboxSpec(["/bin/sh", "-c", "echo ok > inside.txt"], work, TimeSpan.FromSeconds(20), false, []), default);
        Assert.NotEqual(0, write.ExitCode);
        Assert.False(File.Exists(outside));
        Assert.NotEqual(0, read.ExitCode);
        Assert.DoesNotContain("secret", read.OutputTail);
        Assert.Equal(0, inside.ExitCode);
    }
}
```

  Add the attribute next to the tests: `sealed class MacOsFactAttribute : FactAttribute { public MacOsFactAttribute() { if (!OperatingSystem.IsMacOS()) Skip = "macOS only"; } }`.
- [ ] **Step 2: Run** `dotnet test --filter SandboxTests`; expect compile failure (types missing).
- [ ] **Step 3: Implement** the four sandbox classes. `SeatbeltSandbox`/`BubblewrapSandbox` share a private
  `ProcessRunner.RunAsync(fileName, args, env, workDirectory, timeout, ct)` (kill the process tree at timeout, cap the
  captured output to a rolling 8 KiB tail). The private temp directory is `Directory.CreateTempSubdirectory("chargehand-sbx-")`,
  removed after the run. The environment is built from `PATH`, `LANG`, `LC_ALL`, `TERM`, `TMPDIR=<tempDir>`,
  `HOME=<tempDir>` plus the names in `spec.PassEnv`. `NoSandbox` runs the argv directly with the same environment and
  timeout logic.
- [ ] **Step 4: Run** the tests; on this machine the macOS test must run (not skip) and pass.
- [ ] **Step 5: Commit** `feat(sandbox): sandbox-exec and bwrap behind one interface, refused when absent`.

### Task 3: The verifier

**Files:**
- Create: `src/Chargehand/Verification/VerifyCommand.cs`, `src/Chargehand/Verification/Verifier.cs`
- Test: `tests/Chargehand.Tests/VerifierTests.cs`

**Interfaces:**
- Consumes: `ISandbox`, `SandboxSpec`, `SandboxResult` (Task 2).
- Produces:

```csharp
public sealed record VerifyPlan(IReadOnlyList<string> Argv, string Source); // Source: "request" | "detected"
public static class VerifyCommand
{
    public static VerifyPlan? Resolve(IReadOnlyList<string>? requested, string directory);
}
public sealed record VerifyOutcome(VerifyPlan Plan, int ExitCode, string OutputTail, bool TimedOut, TimeSpan Duration)
{
    public bool Passed => ExitCode == 0 && !TimedOut;
}
public sealed class Verifier(ISandbox sandbox, bool network, IReadOnlyList<string> passEnv)
{
    public Task<VerifyOutcome> RunAsync(VerifyPlan plan, string directory, TimeSpan timeout, CancellationToken ct);
}
```

  Detection order (first match): `*.sln` or `*.slnx` or `*.csproj` → `dotnet test`; `package.json` with `scripts.test`
  → `npm test`; `pyproject.toml` or `pytest.ini` or `tests/` with `*.py` → `python3 -m pytest` if `pytest.ini` or a
  `[tool.pytest` section exists, otherwise `python3 -m unittest`; `Cargo.toml` → `cargo test`; `go.mod` →
  `go test ./...`; none → `null`.

- [ ] **Step 1: Write failing tests** for: each detection rule on a temp directory with the marker file; the request's
  argv wins and reports `Source == "request"`; `Passed` for exit 0; `A_hanging_command_is_killed` (a `NoSandbox`, argv
  `["/bin/sh","-c","sleep 30"]`, timeout 1 s → `TimedOut` and elapsed under 10 s); `Output_is_tailed` (argv printing
  20000 bytes → `OutputTail` at most 8192 bytes and ends with the last bytes printed).
- [ ] **Step 2: Run** and see failures.
- [ ] **Step 3: Implement** `VerifyCommand.Resolve` and `Verifier` (a thin call to `sandbox.RunAsync`).
- [ ] **Step 4: Run** the tests, then `scripts/check.sh`.
- [ ] **Step 5: Commit** `feat(verify): resolve the repository's test command and run it in the sandbox`.

### Task 4: The run workspace

**Files:**
- Create: `src/Chargehand/Workspace/RunWorkspace.cs`
- Test: `tests/Chargehand.Tests/RunWorkspaceTests.cs`

**Interfaces:**
- Consumes: the cached checkout path and commit that `Orchestrator.Checkout` returns (`Orchestrator.cs:357`).
- Produces:

```csharp
public sealed record BranchInfo(string Repository, string Branch, string Commit, string Base);
public sealed class RunWorkspace
{
    public string Directory { get; }
    public string Branch { get; }
    public string Base { get; }
    public static Task<RunWorkspace> CreateAsync(string checkout, string commit, string workerRoot, string runId, string nodeId, CancellationToken ct);
    public Task<BranchInfo?> CommitAsync(string message, CancellationToken ct);   // null when the tree is unchanged
    public Task<string> DiffAsync(CancellationToken ct);                          // git diff Base..HEAD, at most 64 KiB, truncated marker
    public Task<IReadOnlyList<string>> ChangedPathsAsync(CancellationToken ct);   // git diff --name-only Base..HEAD
}
```

  Directory: `<workerRoot>/.runs/<runId>/<nodeId>`; created with `git clone --local --no-checkout <checkout> <dir>` then
  `git checkout -b chargehand/<runId>/<nodeId> <commit>`. Commit: `git add -A`, then `git -c core.hooksPath=/dev/null -c
  user.name=chargehand -c user.email=chargehand@localhost commit -q -m <message>`; unchanged tree returns null without
  committing.

- [ ] **Step 1: Write failing tests** using `Runs.GitRepo` (tests/Chargehand.Tests/ScriptedRuntime.cs:158): create
  yields a directory on branch `chargehand/run-1/n1` with the README present and the source repository's `.git` listing
  unchanged (compare `git -C source branch --list` and `worktree list` before and after); an edit then
  `CommitAsync` returns a `BranchInfo` whose `Commit` differs from `Base`, and `git -C <source> log` still shows one
  commit; `Commit_of_an_unchanged_tree_is_null`; `Commit_runs_no_repository_hook` (write `.git/hooks/pre-commit` in the
  run clone as `exit 1` and set it executable; the commit still succeeds); `Diff_is_truncated_at_64_KiB` (a 200 KB
  added file → result length at most 65536 bytes and ends with `[diff truncated at 65536 bytes]`).
- [ ] **Step 2: Run** and see failures.
- [ ] **Step 3: Implement** with `ProcessStartInfo` git calls as `Orchestrator.Checkout` does (reuse its helper if it is
  reachable; otherwise a small private `Git.Run`).
- [ ] **Step 4: Run** the tests, then `scripts/check.sh`.
- [ ] **Step 5: Commit** `feat(workspace): a per-run clone and branch for a writing node, committed with hooks off`.

### Task 5: Verify hook in WorkerNode

**Files:**
- Modify: `src/Chargehand/Nodes/WorkerNode.cs` (`NodeRequest`: `AfterAnswer`; `Run`: call it)
- Test: `tests/Chargehand.Tests/WorkerNodeTests.cs` (add cases beside the existing ones; reuse its private `FakeRuntime`)

**Interfaces:**
- Produces: `NodeRequest.AfterAnswer` of type `Func<int, CancellationToken, Task<string?>>?` (default null): called with
  the round number (0 for the first check) after the contract assembled and resolved; returns feedback text to send as
  the next turn, or null to stop. The node loops: `Turn(feedback)` → assemble → resolve, at most `MaxRounds` (a new
  `int MaxFixRounds = 0` parameter on `NodeRequest`) times. Usage covers all turns. With `AfterAnswer` null the node's
  behaviour is unchanged.

- [ ] **Step 1: Write failing tests** with the fake runtime: (a) `AfterAnswer` returns "fix it" once then null → two
  turns submitted, the second prompt equals "fix it", the contract is the second answer; (b) always returns feedback with
  `MaxFixRounds = 2` → three turns in total (1 + 2), then stops; (c) null hook → one turn (existing behaviour).
- [ ] **Step 2: Run** and see failures.
- [ ] **Step 3: Implement** after the evidence-repair block in `Run`: a `for (var round = 0; contract is not null && outcome == IdleOutcome.Succeeded && r.AfterAnswer is not null; round++)` loop that calls the hook, breaks on null or when `round == r.MaxFixRounds`, otherwise runs the turn and re-assembles (falling back to the previous contract if the new reply fails to parse, as the evidence-repair does).
- [ ] **Step 4: Run** `scripts/check.sh`.
- [ ] **Step 5: Commit** `feat(nodes): a hook between a node's turns for the verify loop`.

### Task 6: `code` preset and the change execution

**Files:**
- Create: `presets/code.yaml`, `prompts/core/writer.md`, `prompts/preset/code.md`
- Modify: `evals/cells.json` (one synthetic `code` cell; read the file's existing cells for the shape)
- Modify: `src/Chargehand/Orchestrator.cs` (`AnswerKind`, `Execute`: branch to `ExecuteChange` when `kind.Writes`)
- Create: `src/Chargehand/ChangeRun.cs` (the artifact and error building, kept out of `Orchestrator.cs`)
- Modify: `src/Chargehand/Nodes/WorkerNode.cs` (`WorkerFailedAction` wording for writers, only if reached)
- Test: `tests/Chargehand.Tests/ChangeRunTests.cs`

**Interfaces:**
- Consumes: Tasks 1-5.
- Produces: preset `code` (node kind `writer`, `writes: true`, `verify: { timeout_seconds: 600, max_fix_rounds: 2 }`,
  permissions: `* * allow`, `edit .git/** deny`, `edit *.env deny`, `edit *.env.* deny`, the read denies of the other
  presets, `shell * deny`, `webfetch/external_directory/question/subagent deny`; `allowed_actions: [answer, ask, deny]`);
  `ChangeRun.Artifacts(BranchInfo, string diff, VerifyOutcome? verification, SandboxKind, bool network, IReadOnlyList<string> changedVerificationPaths)`
  returning the three artifacts of spec decision 11; `Orchestrator` builds the result per decision 10.

  `changed_verification_paths`: changed paths whose file name matches `*test*`, `*spec*`, `Makefile`, `*.csproj`,
  `package.json`, `pyproject.toml`, `Cargo.toml`, `go.mod`, `*.sln*`, or that live under `scripts/`, `tests/`, `test/`,
  `.github/`; or equal the request's `verify` first argv element.

- [ ] **Step 1: Write failing tests** (scripted runtime whose reply handler writes a file into `NodeSpec.Directory`
  before answering, on `Runs.GitRepo`, sandbox `none` via the profile, verify argv `["/bin/sh","-c", ...]`):
  `Green` (the command exits 0 → `completed`, artifacts `branch`, `diff`, `verification` with `exit_code` 0, the branch
  exists in the run clone); `Red_then_fixed` (the command fails until the second worker turn writes a marker file →
  `completed`, verification `attempts` 2, the second prompt contains the failing output tail); `Red_forever` (→ `failed`,
  `error.code` `verification_failed`, `retryable` false, `error.action` set, the three artifacts still present);
  `NoCommand` (no `verify`, empty repository markers → `completed`, `open_questions` contains "no test command found",
  `confidence` at most 0.5); `Unchanged_tree` (the worker writes nothing → `failed` with an action, no `branch`
  artifact); `Verification_artifact_records_network` (`network` false); `Changed_build_files_are_listed` (the worker
  edits `scripts/check.sh`, so `changed_verification_paths` contains it); `Refuses_without_a_sandbox` (profile
  `sandbox.kind: auto` with no platform sandbox injected → `failed`, `sandbox_unavailable`; nothing cloned under `.runs`);
  `The_source_repository_is_untouched` (source `git status --porcelain` empty and branch list unchanged after each case).
- [ ] **Step 2: Run** and see failures.
- [ ] **Step 3: Implement.** In `Execute`, for `kind.Writes`: `Checkout` as today (the cached clone), then
  `RunWorkspace.CreateAsync`, `NodeSpec.Directory` = the workspace directory, `NodeRequest.RepositoryPath` = the
  workspace directory and `Commit` = the base commit (so `File` evidence reads the base). The `AfterAnswer` hook:
  resolve the plan (`VerifyCommand.Resolve(request.Context.Verify, ws.Directory)`); if null, return null; else run the
  `Verifier`; passed → null; failed → "The verification command `<argv>` exited <code> (attempt <n>). Output tail:\n<tail>\nFix the code and reply again
  with the ```json result block." After the node: commit (`chore`-free message from the node's summary, first line at
  most 72 characters), build artifacts, set status/error per decision 10, and add the `Retain` behaviour unchanged.
  The sandbox is chosen once per run by `SandboxSelector.Select(profile.Sandbox, ...)` before the checkout, so a
  refusal costs nothing. Prompts: `core/writer.md` asks for the change, tells the worker it has no shell, that the
  repository's tests are run for it afterwards and their output will come back, and to end with the same `result/v1`
  JSON block (claims cite `diff` evidence for the hunks it wrote).
- [ ] **Step 4: Run** `scripts/check.sh`; run Prompt CI's static coverage check (`dotnet run --project src/Chargehand.Cli -- eval --help`
  names the command) only if the change touches `prompts/` (it does): the new cell in `evals/cells.json` names
  `core/writer.md` and `preset/code.md`.
- [ ] **Step 5: Commit** `feat: the code preset writes a branch that passes its tests in a sandbox`.

### Task 7: End-to-end script, docs, changelog

**Files:**
- Create: `scripts/write-e2e.sh`, `.github/workflows/write-e2e.yml` (manual and self-hosted label, like `change-e2e.yml`)
- Modify: `docs/guide/capabilities.md` ("Writing nodes in worktrees, not yet" → how it works and what it does not do),
  `docs/guide/index.md`, `docs/guide/decisions.md` (ADR 0035 status accepted, once accepted), `README.md:13`,
  `ROADMAP.md` (0.7 ▶ then ✅ when released), `CHANGELOG.md` (`[Unreleased]`), `profiles/example.json`
- Modify: `docs/adr/0035-sandboxed-writing-workers.md` (Status: accepted, after the maintainer's review), amend 0006 and
  0015 status lines ("amended by 0035")

- [ ] **Step 1: Write `scripts/write-e2e.sh`** modelled on `scripts/change-e2e.sh`: build the sample repository (a
  Python module with a failing unittest), run `chargehand run --preset code` on "make the failing test pass", then check
  with `git -C <run clone> log` and `python3 -m unittest` that the branch exists and passes, and print `ok` or `FAIL`
  per case (`green`, `no-sandbox-refused`).
- [ ] **Step 2: Run it by hand** with a real model when the gateway is reachable; record the result (or that it could
  not run, and why) in the PR.
- [ ] **Step 3: Update the docs** listed above; the guide states the limits of decision 12 and the network default.
- [ ] **Step 4: Run** `scripts/check.sh`.
- [ ] **Step 5: Commit** `docs: writing workers, the write e2e and the changelog`.

## Release (not a task of this plan)

Cutting 0.7.0 (version bump, tag, GitHub Release, `Chargehand.Contracts` on nuget.org because the schemas changed,
MCP Registry listing) is outward-facing and waits for the maintainer.

## Self-review

- Spec coverage: decisions 1 (no shell: Task 6 permissions), 2 (Task 1, 6), 3-4, 9 (Task 4), 5 (Task 3), 6-7 (Task 2),
  8 (Task 5, 6), 10-11 (Task 6), 12 (Task 6 test, Task 7 docs), 13 (Task 6 permissions only), 14 (Task 6
  `allowed_actions`). Errors: Task 1. Testing section: Tasks 2-6 and 7.
- Type names used across tasks: `ISandbox`, `SandboxSpec`, `SandboxResult`, `SandboxSettings`, `SandboxSelector`,
  `VerifyPlan`, `VerifyCommand.Resolve`, `VerifyOutcome`, `Verifier`, `BranchInfo`, `RunWorkspace`,
  `NodeRequest.AfterAnswer`, `NodeRequest.MaxFixRounds`, `NodeKind.Writes`, `NodeKind.Verify`, `RequestContext.Verify`.
