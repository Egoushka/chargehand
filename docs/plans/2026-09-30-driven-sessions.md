# Driven writing sessions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A caller hands `orchestrate` a list of tasks; chargehand runs each in its own container with headless Claude Code
following the `change` skill, verifies the result itself, pushes `chargehand/<run>` and opens a draft pull request, and reports
one result per task. It never merges.

**Architecture:** A runner service holds the Docker socket and builds every container from one fixed template (non-root,
read-only root, no capabilities, no socket). Run containers sit on an `--internal` network and reach an allowlist through an
egress proxy. The session ends by writing a git bundle; chargehand, outside the container, runs the tests in a fresh container,
scans the diff, pushes from a repository it created, and opens the draft pull request. The `code` preset and its sandbox are
untouched.

**Tech Stack:** .NET 10 / xUnit 2.9, `System.Diagnostics.Process` (`docker`, git), ASP.NET Core minimal APIs (runner, routes),
a Dockerfile, YAML presets, JSON Schema, bash.

**Spec:** `docs/specs/2026-09-30-driven-sessions-design.md`; decision record `docs/adr/0039-driven-writing-sessions.md`.
Builds on `docs/plans/2026-09-30-writing-workers.md` (the verifier, `RunWorkspace`, the artifact shapes).

## Global Constraints

- The repository is public: never commit IP addresses, hostnames, absolute home paths, employer or project names, keys,
  tracker URLs or prompts from real runs. Fixtures and test repositories are synthetic.
- Done for every task: `scripts/check.sh` exits 0 and its last test line reads `Passed!  - Failed:     0`. Run dotnet
  outside the agent sandbox (`dangerouslyDisableSandbox`): inside it `dotnet restore` hangs for minutes and exits 1.
- Conventional Commits; never `--no-verify`; never force-push. PR title ends with the tracker key `(CHARGEHAND-<n>)`; each task
  below gets its own item in the tracker before it starts. Do not enable auto-merge on a pull request that adds code paths which
  open pull requests on other repositories until the maintainer has read Task 9.
- Published schemas change additively only (`SchemaCompatTests`): new optional properties, new enum values, new files. If a task
  of `docs/plans/2026-09-30-stability-1.0.md` has merged, update its snapshot (route table, tool schema, enum parity) in the same PR.
- Tests that need Docker use `[DockerFact]` (skipped with a stated reason when `docker info` fails) and must run, not skip, on the
  maintainer's Mac before the task is called done.
- The `code` preset, `ISandbox` and its implementations are not modified except to add, never to change behaviour; their tests
  run in every `scripts/check.sh`.
- No credential in a command line, an image layer, an artifact, a log, a span or an exception message. Tests assert this with a
  unique canary value.
- No merge, no auto-merge, no non-draft pull request, anywhere in the code (Task 9 has a test that greps the GitHub client's
  calls for it).
- One version source: `Directory.Build.props`. Public text says "citations checked", not "claims verified".

## Review Focus

1. **The runner cannot be talked into a dangerous container.** No request field becomes a flag, a mount, a network, a capability or
   an image not on the allowlist. Pinned in Task 3 (`Template_has_no_passable_flag`) and Task 5 (`Runner_rejects_unlisted_image_and_extra_fields`).
2. **A process that ignores the proxy still cannot get out.** The run container has no route, not merely a proxy setting. Pinned in
   Task 4 (`Direct_ip_and_public_dns_fail_on_the_internal_network`).
3. **The push credential is never in the container.** Not in its environment, mounts, `docker inspect` or the stored logs; the
   push uses a repository chargehand made, hooks off. Pinned in Task 9 (`Credential_canary_absent_from_container_and_logs`,
   `Hostile_workspace_config_does_not_run_on_handover`).
4. **Nothing is pushed unless chargehand's own verification passed and the diff is non-empty and clean.** A session that says its
   tests pass is not believed. Pinned in Task 9 (`Session_claim_does_not_override_red_verification`, `Empty_diff_is_not_pushed`,
   `Secret_in_diff_blocks_push`).
5. **Cancel stops the container and pushes nothing.** With the server down, `kill-all` still works by label. Pinned in Task 7
   (`Cancel_stops_within_ten_seconds_and_pushes_nothing`) and Task 5 (`Kill_all_by_label`).
6. **The spend cap is a ceiling, not a hope.** No task starts if its cap would exceed the batch cap; a run token is refused after its
   cap. A subscription-mode run says it has no dollar figure. Pinned in Task 8 (`Batch_does_not_start_a_task_that_could_exceed_the_cap`)
   and Task 1/4 (gateway refusal).
7. **A looping session ends.** No progress, a repeated identical tool call, too many turns, and the wall clock each end it with
   `session_stalled`. Pinned in Task 6 (`Stall_detectors`).
8. **The 1.0 freeze is not broken.** Every contract change is additive and the compat test passes against the latest tag. Pinned in
   Task 2.
9. **A nested `orchestrate` call from a session cannot escape its task.** A run token is limited to presets `default` and `review`,
   the pinned repository and commit, and the task's cap. Pinned in Task 7 (`Run_token_is_scoped`).
10. **The existing `code` preset still works.** Pinned by the existing `CodePresetTests` running unchanged in every task.

---

### Task 1: Spike: can Claude Code run on a substituted credential?

Measurement, not a feature. No production code; the result is recorded in ADR 0039 (Decision, "spike result") and the spec's open item 1.

**Files:**
- Create: `scripts/driven-credential-spike.sh` (uses a throwaway local HTTP listener that logs request headers and forwards nothing;
  then a forwarding variant against the real API with a low-limit key supplied by the maintainer's secret source, never printed)
- Modify: `docs/adr/0039-driven-writing-sessions.md` (record the result), `docs/specs/2026-09-30-driven-sessions-design.md` (open item 1)

**Already answered (2026-09-30, local listener, no real credential):** in both modes Claude Code sends the dummy credential to the base URL
(`x-api-key` for the API key, `Authorization: Bearer` for the subscription token), so the first two questions below are half done: what remains
is the forwarded response with the real credential swapped in.

**Questions to answer, with output kept out of the repository:**
- With `ANTHROPIC_BASE_URL` set to a local listener and `ANTHROPIC_API_KEY` a dummy, does `claude -p` send the dummy as `x-api-key`
  and accept a forwarded response? (Decides the API-key path.)
- With `CLAUDE_CODE_OAUTH_TOKEN` a dummy and the same base URL, which header does it send (`Authorization: Bearer`?), does it try
  to reach `platform.claude.com` for a refresh, and does a forwarded response with the real token swapped in succeed? (Decides the
  subscription path.)
- Which hosts does a `-p` run contact with `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` set? (Seeds the allowlist.)
- Does the response's `usage` block arrive in a form a gateway can count, including streamed responses?

- [ ] **Step 1:** Write the script's listener first and a check that it records headers and no body content into the output file.
- [ ] **Step 2:** Run both modes by hand on the pinned CLI version; record yes/no per question and the version.
- [ ] **Step 3:** Write the result into the ADR and spec. If either mode fails, state that decision 9 falls back to
  `credential_delivery: env` for that mode, and update Task 4's scope accordingly (the proxy then needs no credential exchange).
- [ ] **Step 4:** `scripts/check.sh` (docs only). **Commit** `docs: record the credential spike for driven sessions`.

### Task 2: Contracts, preset fields, profile block (all additive)

**Files:**
- Modify: `schemas/request/v1/request.schema.json` (`driven` object), `schemas/run-status/v1/run-status.schema.json` (events, fields),
  `schemas/result/v1/result.schema.json` (eight error codes), `schemas/preset/v1/preset.schema.json` (`driven` block), `src/Chargehand.Contracts/*` (matching records and `ErrorCode` values)
- Create: `schemas/run-summary/v1/run-summary.schema.json` and `examples/`; `src/Chargehand.Contracts/RunSummary.cs`
- Modify: `src/Chargehand/Config/Profile.cs` (`Driven: DrivenSettings?`: `Enabled`, `Images`, `MaxParallel`, `MaxParallelTotal`, `Network`, `Runner`), `profiles/profile.schema.json`, `profiles/example.json`
- Create: `presets/driven.yaml` (model placeholder, `max_usd`, `max_minutes` 45, `no_progress_minutes` 10, `max_turns` 80, allowlist defaults)
- Test: `tests/Chargehand.Tests/DrivenContractsTests.cs`

**Interfaces:**
- Produces: `RequestDriven(IReadOnlyList<DrivenTask> Tasks, int? MaxParallel, decimal? MaxUsdTotal, bool DraftPr = true)`;
  `DrivenTask(string Id, string? Ref, string? Goal)`; `ErrorCode.{ContainerUnavailable, CredentialUnavailable, SessionFailed,
  SessionStalled, PushRejected, PrFailed, Cancelled, TasksIncomplete}`; `RunSummary` record; `DrivenSettings`.

- [ ] **Step 1: Write the failing tests.** A request with `driven` (two tasks, one `ref` one `goal`) validates and round-trips; a
  task with neither `ref` nor `goal`, a duplicate `id`, 0 or 21 tasks, `draft_pr: false`, or `driven` without `context.repository`
  is rejected with a specific message; each new error code serialises snake_case and validates; each new `run-status` event and
  field validates; a `run-summary/v1` example validates and round-trips; a profile with a `driven` block loads and a profile
  without it loads with `Driven == null`; `driven.yaml` loads as a preset. Use `PresetRoot` for preset fixtures.
- [ ] **Step 2: Run** `dotnet test --filter DrivenContractsTests`; expect unknown-property and missing-type failures.
- [ ] **Step 3: Implement** the fields with snake_case names like their neighbours. `draft_pr` is `{ "const": true }`.
- [ ] **Step 4: Run** `scripts/check.sh`; `SchemaCompatTests` must pass (only additions), and so must every existing `code` preset test.
- [ ] **Step 5: Commit** `feat(contracts): driven requests, run summaries and eight error codes, all additive`.

### Task 3: The container template and the Docker engine

**Files:**
- Create: `src/Chargehand/Containers/IContainerEngine.cs`, `ContainerTemplate.cs`, `DockerCliEngine.cs`, `ContainerSpec.cs`
- Test: `tests/Chargehand.Tests/ContainerTemplateTests.cs`, `tests/Chargehand.Tests/DockerEngineTests.cs` (`[DockerFact]`)

**Interfaces:**
- Produces:

```csharp
public sealed record ContainerSpec(string RunId, string ImageDigest, string WorkVolume, string OutVolume, string Network,
    IReadOnlyDictionary<string, string> Env, int MemoryMb, double Cpus, int Pids, IReadOnlyList<string> Command);
public interface IContainerEngine
{
    Task<string> StartAsync(ContainerSpec spec, CancellationToken ct);             // container id
    Task SignalAsync(string id, string signal, CancellationToken ct);
    Task<ContainerState> InspectAsync(string id, CancellationToken ct);            // running | exited(code) | missing
    Task RemoveAsync(string id, CancellationToken ct);
    Task KillAllAsync(CancellationToken ct);                                       // by label chargehand.run
    Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct);
}
public static class ContainerTemplate { public static IReadOnlyList<string> RunArgs(ContainerSpec spec); } // pure
```

- [ ] **Step 1: Write failing tests.** `Template_has_no_passable_flag`: for a spec with hostile values (an env name `--privileged`,
  a volume name `x:/host`, an image `img --cap-add=ALL`), `RunArgs` either throws `ArgumentException` or puts the value only in a
  position where Docker reads it as a value; and the argument list always contains `--read-only`, `--cap-drop ALL`,
  `--security-opt no-new-privileges`, `--user`, `--pids-limit`, `--memory`, `--cpus`, `--network <the given internal name>`,
  a label `chargehand.run=<id>`, exactly two `-v` named volumes, and never `--privileged`, `docker.sock`, `--network host`,
  `--pid`, `--ipc`, `--device`. An image that is not `name@sha256:<64 hex>` is refused. The `[DockerFact]` test starts a container
  from a local image with a trivial command, inspects it, signals it, removes it, and `KillAllAsync` removes a labelled one and
  leaves an unlabelled one.
- [ ] **Step 2: Run** and see compile failures.
- [ ] **Step 3: Implement** `RunArgs` as the only place a `docker run` line is built; `DockerCliEngine` calls `docker` through
  `ProcessStartInfo.ArgumentList` (never a shell string), with output caps. Record `docker version` and `docker info` facts
  (client and server version, rootless yes/no) in the engine test's output, not in the repository.
- [ ] **Step 4: Run** the tests, then `scripts/check.sh`.
- [ ] **Step 5: Commit** `feat(containers): a fixed run template and a Docker CLI engine`.

### Task 4: The egress proxy and the internal network

**Files:**
- Create: `src/Chargehand/Egress/AllowlistMatcher.cs`, `AllowlistProxy.cs`, `src/Chargehand/Containers/BatchNetwork.cs`
- Test: `tests/Chargehand.Tests/AllowlistMatcherTests.cs`, `tests/Chargehand.Tests/EgressTests.cs` (`[DockerFact]`)

**Interfaces:**
- Produces: `AllowlistMatcher.IsAllowed(string host, int port)`; `AllowlistProxy` (CONNECT-only listener, per-run request log: run id,
  host, allowed or refused); `BatchNetwork.CreateAsync(batchId)` returning an internal network name and the proxy address on it, and
  `RemoveAsync`. If Task 1 found the credential exchange works, `AllowlistProxy` also offers `Exchange(runToken) -> credential`,
  refusing after the token's cap or expiry; otherwise that part is omitted and this plan's Task 10 records `credential_delivery: env`.

- [ ] **Step 1: Write failing tests.** Matcher: exact and suffix entries, case, trailing dot, port other than 443 refused, IP
  literals refused, `localhost` and private ranges refused even when listed, a name that resolves (via the proxy's own resolver) to
  a private address refused. Proxy: a CONNECT to an allowed host reaches a local stub; a CONNECT to another host gets 403 and is
  logged; plain `GET http://` is refused. `[DockerFact]` `Direct_ip_and_public_dns_fail_on_the_internal_network`: a container on the
  network cannot open a TCP connection to a public IP and cannot resolve a public name; `Allowed_host_through_the_proxy_works`:
  the same container reaches the stub through `HTTPS_PROXY`; `Network_is_removed_when_the_batch_ends`.
- [ ] **Step 2: Run** and see failures.
- [ ] **Step 3: Implement.** The proxy resolves names itself and connects by address. One proxy container per batch (built from the
  server image with another entry point), attached to the internal network and to an outside network.
- [ ] **Step 4: Run** the tests on the Mac (Docker tests must run, not skip) and in CI where Docker exists, then `scripts/check.sh`.
- [ ] **Step 5: Commit** `feat(egress): a CONNECT allowlist proxy and a per-batch internal network`.

### Task 5: The runner service

**Files:**
- Create: `src/Chargehand/Runner/RunnerServer.cs`, `RunnerClient.cs`, `ImageAllowlist.cs`; CLI verb `chargehand runner`
- Modify: `src/Chargehand.Cli/Program.cs`; `docs/guide/server.md` (a "Runner" section, placeholders only)
- Test: `tests/Chargehand.Tests/RunnerTests.cs`

**Interfaces:**
- Produces: HTTP routes `POST /start`, `POST /signal`, `GET /inspect/{id}`, `POST /remove/{id}`, `POST /kill-all`, `GET /logs/{id}`; bearer key;
  `RunnerClient : IContainerEngine`. The body of `/start` is a `ContainerSpec` minus anything the template fixes; unknown fields are
  a 400.

- [ ] **Step 1: Write failing tests.** `Runner_rejects_unlisted_image_and_extra_fields` (an image off the allowlist, a body with
  `privileged: true`, `mounts`, `network: "host"`, or `caps` all get 400 and start nothing); no key is 401; `Kill_all_by_label`
  against a fake engine; `RunnerClient` round-trips against the in-process server with a fake engine; the runner refuses to start
  when its listen address is not private and `allowed_hosts` is empty (ADR 0024's rule).
- [ ] **Step 2: Run** and see failures.
- [ ] **Step 3: Implement** the runner as thin glue over `ContainerTemplate` and `DockerCliEngine`. Record in the guide whether the
  VPS engine can run rootless (read it, write what you saw; **UNKNOWN** until then).
- [ ] **Step 4: Run** `scripts/check.sh`.
- [ ] **Step 5: Commit** `feat(runner): a narrow service in front of the Docker socket`.

### Task 6: The session image and the driver

**Files:**
- Create: `images/session/Dockerfile`, `images/session/README.md`, `src/Chargehand/Driven/SessionDriver.cs`, `StreamTail.cs`, `StallDetector.cs`; CLI verb `chargehand-session` (hidden)
- Test: `tests/Chargehand.Tests/StallDetectorTests.cs`, `SessionDriverTests.cs`, `tests/Chargehand.Tests/SessionImageTests.cs` (`[DockerFact]`)

**Interfaces:**
- Produces: `StallDetector(TimeSpan noProgress, int repeatLimit, int maxTurns, TimeSpan wallClock)` fed stream-json events, returning
  `Continue | Stalled(reason)`; `SessionDriver.RunAsync(options, ct)` that starts the `claude` process, tails the stream, sends SIGINT
  then SIGTERM, writes `/out/chargehand.bundle`, `/out/stream.jsonl` (credential canary redacted), `/out/driven-report.json`.

- [ ] **Step 1: Write failing tests.** `Stall_detectors`: no event for the limit; the same tool name and input hash five times in a
  row; turn count over the maximum; wall clock. Each returns the named reason; interleaved different calls reset the repeat counter.
  Driver with a fake `claude` script: writes a report and a bundle on success; a fake that never prints is stopped with
  `session_stalled`; a fake that prints `NEEDS_INPUT:` ends `needs_input` with the questions; the canary never reaches `/out`.
  `[DockerFact]` `Image_has_the_toolchains`: `dotnet --version`, `node --version`, `python3 --version`, `git --version` and
  `claude --version` equal to the profile's pinned version, and no `docker` binary.
- [ ] **Step 2: Run** and see failures.
- [ ] **Step 3: Implement.** The Dockerfile pins the base image by digest and Claude Code by version (build argument matching
  `claude_code.version`), adds the plugin directory at the server's version, runs as a non-root user. Measure image size and pull
  time and write them into `images/session/README.md` (closes spec open item 4's image half).
- [ ] **Step 4: Run** `scripts/check.sh`; build the image on the Mac.
- [ ] **Step 5: Commit** `feat(driven): the session image and a driver with stall detection`.

### Task 7: The server: batches, run tokens, cancel, halt, run list

**Files:**
- Modify: `src/Chargehand.Server/RunService.cs` (the gate admits batches; tasks inside use their own cap), `ChargehandServer.cs` (routes), `src/Chargehand/RunLog/*` (`parent_run_id`, list query), `src/Chargehand.Cli/Program.cs` (`chargehand runs kill --all`)
- Create: `src/Chargehand.Server/RunToken.cs`
- Test: `tests/Chargehand.Tests/ServerTests.cs` (extend), `RunTokenTests.cs`, `RunListTests.cs`

**Interfaces:**
- Produces: `GET /v1/runs?status=&since=&limit=` (`run-summary/v1` rows), `POST /v1/runs/{id}/cancel`, `POST /v1/halt`, `POST /v1/resume`;
  `RunToken.Issue(runId, repository, commit, capUsd, expiry)` and `Validate(token)` (HMAC with a server-held key, never logged);
  `parent_run_id` written to the run log when a call carries a run token.

- [ ] **Step 1: Write failing tests.** `Run_token_is_scoped`: a token permits `default` and `review` on its pinned repository and
  commit and nothing else (another preset, another commit, another path are 403 or `invalid_request`), is refused after expiry, and
  counts child spend against its cap. `Cancel_stops_within_ten_seconds_and_pushes_nothing` (fake engine; asserts the signal
  sequence and that the handover was never called). Halt refuses new runs with a stated reason until resume. The list route returns
  newest first, filters by status and `since`, caps `limit`, and needs the bearer key. `chargehand runs kill --all` works with no
  server (calls the engine's `KillAllAsync`). Existing `200/202/400/401/404/410/429` tests pass unchanged.
- [ ] **Step 2: Run** and see failures.
- [ ] **Step 3: Implement.** Keep the one-at-a-time gate for non-driven runs; a driven batch holds one slot and schedules its own tasks.
- [ ] **Step 4: Run** `scripts/check.sh`.
- [ ] **Step 5: Commit** `feat(server): run tokens, cancel, halt and a run list for driven sessions`.

### Task 8: The batch scheduler and `ref` resolution

**Files:**
- Create: `src/Chargehand/Driven/DrivenBatch.cs`, `BatchScheduler.cs`, `TaskSource.cs`
- Modify: `src/Chargehand/Orchestrator.cs` (a request with `driven` routes to the batch), `src/Chargehand/Config/Profile.cs` (`driven.task_source`: a declarative tool mapping in ADR 0034's style)
- Test: `tests/Chargehand.Tests/BatchSchedulerTests.cs`, `TaskSourceTests.cs`

**Interfaces:**
- Produces: `BatchScheduler.RunAsync(batch, runTask, ct)` with `max_parallel`, `max_parallel_total`, the batch cap and halt;
  `TaskSource.ResolveAsync(ref)` returning a goal via the mapped MCP tool, or a `ChargehandException(InvalidRequest)` with the
  action to configure `driven.task_source`.

- [ ] **Step 1: Write failing tests** (a fake task runner with scripted costs and durations): never more than `max_parallel` at
  once; `Batch_does_not_start_a_task_that_could_exceed_the_cap` (cap 5, caps of 2 each, two running at 4 reported: the third does
  not start); tasks in flight when the total is exceeded by more than one cap are cancelled and end `cost_cap_reached`; halt stops
  starting tasks; results come back in task order with their own run ids; the batch is `completed` only when all are, else
  `failed` + `tasks_incomplete` naming the tasks; a `ref` with no `task_source` configured is `invalid_request` with the action; a
  `ref` whose tool returns nothing fails that task only.
- [ ] **Step 2: Run** and see failures.
- [ ] **Step 3: Implement**, deriving the `task_source` mapping from the real server's tool list (decide and record open item 7).
- [ ] **Step 4: Run** `scripts/check.sh`.
- [ ] **Step 5: Commit** `feat(driven): a batch scheduler with parallel and spend caps`.

### Task 9: Handover: verify in a fresh container, scan, push, open a draft pull request

**Files:**
- Create: `src/Chargehand/Driven/Handover.cs`, `BundleFetch.cs`, `DiffScan.cs`, `GitHubPullRequests.cs`
- Modify: `src/Chargehand/Verification/Verifier.cs` (a container-backed `ISandbox`-like runner; `ISandbox` itself unchanged)
- Test: `tests/Chargehand.Tests/HandoverTests.cs`, `HandoverDockerTests.cs` (`[DockerFact]`), `GitHubPullRequestsTests.cs`

**Interfaces:**
- Consumes: the bundle and report the driver wrote; a push credential from the secret sources (`driven.push_secret`).
- Produces: `Handover.RunAsync(task, out)` returning `Pushed(branch, commit, prUrl, number) | NotPushed(reason)`; it (1) fetches the
  bundle into a fresh bare repository it creates, `core.hooksPath=/dev/null`, (2) resolves the verification command at the pinned
  commit, (3) runs it in a fresh container on the bundle's commit, (4) scans the diff and the credential literal, (5) pushes
  `chargehand/<run>` only if verification exited 0, the diff is non-empty and the scan is clean, (6) opens a draft pull request.

- [ ] **Step 1: Write failing tests** against a local bare remote that refuses the default branch: green path pushes and records a
  draft pull request (a fake GitHub client); `Session_claim_does_not_override_red_verification`; `Empty_diff_is_not_pushed`;
  `Secret_in_diff_blocks_push`; `Hostile_workspace_config_does_not_run_on_handover` (a bundle built from a repository whose config
  sets `core.fsmonitor`, a hook and a `.gitattributes` filter that would write a marker file: the marker never appears);
  `Push_rejected_keeps_the_bundle` (code `push_rejected`, credential redacted from the message); `Pr_failure_after_push_names_the_branch`;
  `Client_never_sends_a_merge_or_auto_merge_or_non_draft` (the client class has no such method, and a request-recording fake sees
  `draft: true` only). `[DockerFact]` `Credential_canary_absent_from_container_and_logs`.
- [ ] **Step 2: Run** and see failures.
- [ ] **Step 3: Implement.** The GitHub client is the smallest one that creates a draft pull request (`POST /pulls` with `draft: true`).
- [ ] **Step 4: Run** `scripts/check.sh`.
- [ ] **Step 5: Commit** `feat(driven): verify in a fresh container, then push and open a draft pull request`.

### Task 10: Result assembly and claim checking

**Files:**
- Create: `src/Chargehand/Driven/DrivenResult.cs`, `DrivenReport.cs`
- Modify: `schemas/` (none; `driven-report` is internal and documented in the guide)
- Test: `tests/Chargehand.Tests/DrivenResultTests.cs`

**Interfaces:**
- Produces: `DrivenResult.Build(task, report, verification, handover, children)` returning the task's `result/v1` with artifacts
  `branch`, `pull-request`, `verification`, `session-tests` (labelled `claimed_by: session`), `review`, `session-log` (uri and sha256);
  the batch result with `driven-batch`; every claim's evidence resolved and support-checked through the existing resolver and `SupportJudge`.

- [ ] **Step 1: Write failing tests.** A report with one resolvable claim and one citing a missing line: the first has `support`, the
  second moves to `open_questions`; a report that is not JSON gives `failed` + `invalid_result`; `verification` and `session-tests`
  are never merged; `credential_delivery` appears on the batch artifact; `usage` sums the children; `review` lists the child run ids
  from `parent_run_id`; every artifact validates against the schema and `session-log` is never inline; the result validates and signs
  with the existing signer.
- [ ] **Step 2: Run** and see failures.
- [ ] **Step 3: Implement**, reusing `CitedText` and `SupportJudge` as they are.
- [ ] **Step 4: Run** `scripts/check.sh`.
- [ ] **Step 5: Commit** `feat(driven): task and batch results with checked claims and kept evidence`.

### Task 11: The skill runs headless

**Files:**
- Modify: `plugins/chargehand/skills/change/SKILL.md` (three additive sentences, see the spec), `plugins/chargehand/skills/change/report-template.md` only if needed
- Create: `prompts/driven/session-append.md` (the appended system prompt), a Prompt CI cell if the prompt registry covers it
- Test: `tests/Chargehand.Tests/ChangeSkillTests.cs` (extend) and the prompt-coverage check

**Interfaces:**
- The skill reads `CHARGEHAND_BRANCH` when set (branch name), treats questions as `NEEDS_INPUT:` output when `CHARGEHAND_DRIVEN=1`, and
  is otherwise unchanged for a person.

- [ ] **Step 1: Write failing tests.** The skill text still contains every step heading and the 2-fix-round rule (existing test);
  it names `CHARGEHAND_BRANCH` and `NEEDS_INPUT:` exactly once each; the appended prompt contains the report's JSON shape and the
  branch variable; the prompt registry's hash for the block is recorded.
- [ ] **Step 2: Run** and see failures.
- [ ] **Step 3: Implement** the edits and the prompt block.
- [ ] **Step 4: Run** `scripts/check.sh` and Prompt CI's static coverage check.
- [ ] **Step 5: Commit** `feat(skill): the change skill can run without a person`.

### Task 12: End to end, adherence measurement, docs, changelog

**Files:**
- Create: `scripts/driven-e2e.sh`, `docs/guide/driven.md`
- Modify: `docs/guide/index.md`, `docs/guide/reference.md` (profile block, error codes), `docs/guide/server.md`, `ROADMAP.md` (after 1.0 line notes the shipped half), `CHANGELOG.md`, `docs/adr/0039-driven-writing-sessions.md` (measured adherence, status stays `proposed`), the spec's open items

- [ ] **Step 1: Write `scripts/driven-e2e.sh`** modelled on `scripts/write-e2e.sh`: a synthetic repository with three tasks (one easy, one
  needing a test fix, one that cannot pass), a local bare remote that refuses the default branch, `max_parallel: 2`, a capped API key.
  It asserts: two draft pull requests recorded, one `failed`, the batch `tasks_incomplete`, a cancel test, no credential canary in `/out`.
- [ ] **Step 2: Run it by hand** with a real model on the Mac; record the result, the cost and, for the adherence measurement, the
  share of sessions out of at least 10 (five tasks, twice) that performed research, test, review and at most 2 fix rounds in order
  (read from the stream). If fewer than 7 of 10, say so in the ADR and do not call the feature ready.
- [ ] **Step 3: Write the guide** (what it does, the credential choice and its terms open question, the network allowlist and what it
  does not protect, the image, the runner, the kill switch, the limits of the verification artifact) and the docs edits above.
- [ ] **Step 4: Run** `scripts/check.sh`.
- [ ] **Step 5: Commit** `docs: driven sessions, the e2e and the changelog`. **Not done by any agent:** the release, the tag, and the
  decision to turn on `driven.enabled` on the VPS.

## Order and sizes

Task 1 first (it can change Task 4). Then 2; then 3, 4, 5 in order; 6 after 3; 7 after 2; 8 after 7; 9 after 3, 4 and 6; 10 after 9;
11 any time after 2; 12 last. Each task is one pull request of a few hundred lines; Task 5 and Task 9 are the largest and the
ones the maintainer should read line by line.
