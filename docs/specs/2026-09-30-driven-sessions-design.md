# Driven writing sessions: a list of tasks in, draft pull requests out

- Status: draft for the maintainer's review. Decisions 1 to 4 below were taken by the maintainer and are recorded, not reopened; every other decision is the proposer's.
- Date: 2026-09-30

## Goal

A caller hands `orchestrate` a list of tasks. For each task chargehand starts one container, runs headless Claude Code in it through the same steps as the `change` skill (research, write, test, review, at most 2 fix rounds), pushes a branch `chargehand/<run>` and opens a **draft** pull request. It reports one result per task. It never merges and never enables auto-merge.

Done when: (1) a batch of three tasks on a sample repository runs with `max_parallel: 2` and ends with three draft pull requests and one batch result listing each task's state, cost and link; (2) a container with no route out cannot reach a host outside the allowlist, shown by a test that tries an IP and a name; (3) the push credential is absent from the container's environment, filesystem and logs, shown by a test that looks; (4) killing a run stops its container within 10 seconds and leaves the branch unpushed unless verification had already passed; (5) the existing `code` preset still passes its tests unchanged.

## Where it stands

Evidence: `path:line` or command output at `9e123cf` (0.8.0), checked 2026-09-30.

1. **The writing path has no shell.** ADR 0035 keeps workers shell-less: chargehand runs the verifier in `sandbox-exec` or `bwrap`, commits a branch in a per-run clone and pushes nothing. `presets/code.yaml` is the only writing preset.
2. **The `change` skill already is the flow we want.** `plugins/chargehand/skills/change/SKILL.md` runs research (an `orchestrate` call on the `default` preset), write, test, commit, review (`review` preset), at most 2 fix rounds and a report. It assumes a person: step 1 stops on a dirty tree, step 2 asks the user every question, step 10 hands a local branch back. Claude Code expands `/skill-name` in print mode and loads a plugin with `--plugin-dir` (Claude Code headless documentation, read 2026-09-30).
3. **The server already runs in a container and mounts no Docker socket.** The VPS deployment of `chargehand serve` (image pinned by digest) has a 768 MiB memory limit, `no-new-privileges`, a read-only bind of the repositories, volumes for the run log and worker clones, and the Claude subscription token in its environment (`hz inspect`, names only). The `Dockerfile` installs git, node and the Claude Code CLI, not dotnet, python or a Docker client.
4. **The server runs one run at a time.** `RunService` holds a `SemaphoreSlim(1, 1)` and refuses an 11th unfinished run (`src/Chargehand.Server/RunService.cs:13-41`; `docs/guide/server.md`, "Runs and the run log"). `GraphRunner` bounds nodes inside a run, not runs.
5. **There is no cancel.** The HTTP routes are `POST /v1/runs`, `GET /v1/runs/{id}`, `GET /v1/runs/{id}/events` and `/v1/mcp` (`ChargehandServer.cs:84-128`); a cancelled MCP call stops waiting and the run goes on (`docs/guide/mcp.md`). There is no run list.
6. **Claude Code authentication in chargehand today** has three modes, one per profile: API key (`--bare`), subscription token from `claude setup-token`, or the CLI's own login (ADR 0020 and its addendum). Inherited credential variables are removed from the child environment. `--bare` never reads OAuth.
7. **`result/v1` and `run-status/v1` are open where we need them.** `artifacts[].kind` is a free string (`schemas/result/v1/result.schema.json`); `error.code` is an enum that grows additively (ADR 0022, ADR 0035 added two); `run-status/v1` has `additionalProperties: false`, so new fields need a schema change. `request/v1` has `additionalProperties: false` at the top level and in `context`. `SchemaCompatTests` allows only additions.
8. **Docker exists on this Mac.** `docker version` answers with client and server 29.4.0 (OrbStack, 8 CPUs, about 6 GB). A container on a `--internal` network could not open a TCP connection to a public IP and could not resolve a public name; the same image on the default network reached the IP (probe run 2026-09-30 with an existing local image).
9. **The VPS engine (read-only check, 2026-09-30).** Docker 29.6.2, security options `apparmor` and `seccomp` only (so **not rootless**), cgroup v2, 16 CPUs, 31 GiB in total with about 9.7 GiB available while roughly 120 containers run. The repository clone that the server reads has an `origin` remote over HTTPS and is owned by another user than the reader's default, so git needs a `safe.directory` exception to read it as root. Whether `--internal` behaves there as on the Mac was not tested (no container was started on the VPS).
10. **Claude Code accepts a substituted credential and a base URL (local check, Claude Code 2.1.283, 2026-09-30).** With `ANTHROPIC_BASE_URL` set to a local listener that logged headers only: in API-key mode a dummy `ANTHROPIC_API_KEY` was sent as `x-api-key` to `/v1/messages`; in subscription mode a dummy `CLAUDE_CODE_OAUTH_TOKEN` was sent as `Authorization: Bearer` to the same path (two requests, one retry after the listener's 401). So a gateway can see, and replace, the credential in both modes. Not tested: a response forwarded with the real credential swapped in (needs a real credential, which this check did not use), whether subscription mode also contacts a token-refresh host that the listener could not see, and streamed usage counting.

## Decisions

Rows 1 to 4 are the maintainer's.

| # | Question | Decision |
|---|---|---|
| 1 | Isolation | One container per run, on the VPS and on any Mac that has Docker. No shell outside a container. Reverses ADR 0035's shell-less writer for the new path; the `code` preset and its sandbox stay as they are. |
| 2 | Handover | chargehand pushes `chargehand/<run>` and opens a draft pull request. It never merges and never enables auto-merge. The credential pushes only non-default branches (a per-repository deploy key or a fine-grained token) and lives outside the container except at push time. |
| 3 | Inside the session | Headless Claude Code (`claude -p`) runs the `change` skill's steps. The skill is reused, not rebuilt. A second runtime (OpenCode) is a later goal behind the same interface. |
| 4 | Trigger | The caller calls `orchestrate` with a list of tasks (tracker item ids or free-text goals). chargehand runs N under a concurrency cap and a spend cap and reports one result per task. Picking work from the tracker's cycle is out of scope (roadmap: after 1.0). |
| 5 | Where the request goes | An additive optional `driven` object on `request/v1` (decision 14). A request with `driven` ignores `context.preset` for the session and uses the `driven` preset (decision 6). |
| 6 | Preset | A new preset `driven` (a `preset/v1` file) carries the model, `max_usd` per task, the wall-clock limit, the no-progress limit and the allowlist defaults. It is the only place those numbers live. `code` is untouched. |
| 7 | Who starts containers | A runner behind an `IContainerEngine` interface. On the Mac the server process calls the `docker` CLI. On the VPS the server never holds the Docker socket: a separate **runner service** does, and exposes a narrow API (decision 8). |
| 8 | The runner's API | `start` (image from an allowlist by digest, a workspace id, an env map, limits), `signal`, `logs`, `inspect`, `remove`, `kill-all`. It builds the `docker run` argument list itself from a fixed template; a caller cannot pass a flag, a mount, a network or a capability. See "Starting containers". |
| 9 | Credential for the model | A per-run token, not the real credential, goes into the container. An egress gateway (decision 10) exchanges it for the real credential and counts the run's spend. Spike first (Task 1); if Claude Code will not run against a substituted credential, fall back to the real credential in the container's environment, recorded as `credential_delivery: env` on the result. See "Model credential". |
| 10 | Network | The run container sits on an `--internal` Docker network with no route out. One egress container is on that network and on the outside network and forwards only to an allowlist (see "Network policy"). |
| 11 | Push | The container ends by writing a git bundle of `chargehand/<run>` to an output volume. **chargehand, outside the container,** fetches the bundle into a fresh bare repository it created, pushes from there with hooks off, and opens the draft pull request through the GitHub API. The container never holds the push credential, and the remote is never contacted from inside it. |
| 12 | Who checks the tests | chargehand, not the session. After the session it runs the repository's verification command (ADR 0035's `VerifyCommand`) in a second, fresh container on the bundle's commit and records that exit code. The session's own test runs are recorded separately and labelled as the session's claim. |
| 13 | Claims | The session's last message must be a `driven-report` JSON (summary, claims, each with `file` or `diff` evidence at the branch commit). chargehand builds the result from it and runs the existing resolver and `SupportJudge` on it (ADR 0009, ADR 0036). A session that returns no parseable report ends `failed` with `invalid_result`. |
| 14 | Contract changes | All additive: `request/v1.driven`; `run-status/v1` new events and optional fields; new `error.code` values; new artifact kinds; a new `run-summary/v1` list schema; one new HTTP route family. See "Contract changes". |
| 15 | Concurrency | `driven.max_parallel` (default 2, hard ceiling 4 until measured) per batch and `driven.max_parallel_total` (default 4) across batches. The server's one-at-a-time gate applies to batches, not to tasks inside one (Task 7 changes the gate). |
| 16 | Spend | Two ceilings, because a subscription has no dollar figure. **Tokens:** `driven.max_tokens_total` per batch and `max_tokens` per task in the preset, counted from the stream's `usage` (input plus output; cache reads counted at their own line), enforced in both modes. **Dollars:** `driven.max_usd_total` per batch and the preset's `max_usd` per task, enforced only in API-key mode. Default mode is the subscription, so the token ceiling and the time and turn limits are the ones that bind. A batch starts no new task if its cap would be exceeded by the next task's cap. See "Failure modes".
| 17 | Nested chargehand calls | The skill's research and review steps call `orchestrate` from inside the container. They go to the chargehand server over the internal network with a **run-scoped token** that permits only the `default` and `review` presets on the run's pinned repository and commit, and counts against the task's cap. The server tags those child runs with `parent_run_id`. |
| 18 | Repository content is untrusted | The session loads the repository's `CLAUDE.md`, hooks and `.mcp.json` (print mode shows no trust dialog). The container is the boundary, not the session's permissions: `--permission-mode dontAsk` with an explicit `--allowedTools` list. A repository cannot choose its image (decision 19). |
| 19 | Image and toolchains | A profile allowlist of images by digest. A repository may pick one by name from `.chargehand/session.json` (`image`, `setup`, `verify`); a name not on the allowlist is refused. Default: one image with dotnet, node, python, git and Claude Code. See "The image". |
| 20 | GUI | Every state the GUI needs is a read from the run store and the run list (decision 14). The GUI is out of scope; nothing in it requires a new server concept. |

## Starting containers

**The question:** the server is itself a container on the VPS (Where it stands, 3), so how does it start run containers?

| Option | Security cost | Verdict |
|---|---|---|
| Mount the Docker socket into the server | Anyone who controls the server process controls the host as root: a container with `--privileged` and the root filesystem mounted is one API call away. The server takes requests from MCP clients and feeds repository text to models. | Rejected. |
| A socket proxy that filters API paths | Filters by endpoint, not by body: it cannot stop `POST /containers/create` with `Privileged: true` or a host mount. | Rejected as the only control. |
| **A runner service with a narrow API** (chosen) | The socket is held by one small program whose only job is to fill a fixed template. Compromise of the server yields "start or kill session containers from allowlisted images", not host root. The runner's own code becomes the thing to review. | Chosen. |
| Rootless Docker or Podman for the runner | Root in the daemon is a user, not root on the host. Stronger, extra setup, and on a Mac it is the VM's job anyway. | The VPS engine is **not rootless** (Where it stands, 9). The design works without it; moving the runner to a rootless daemon is a later hardening, recorded in the ADR's reopen list. |

The runner listens on the private network only, behind its own bearer key (ADR 0024's pattern), and labels every container `chargehand.run=<id>` so `kill-all` and cleanup work without the server. Its fixed template, per container:

- image by digest from the allowlist; non-root user; `--read-only` root filesystem with a tmpfs for `/tmp`; `--cap-drop ALL`; `--security-opt no-new-privileges`; `--pids-limit`, `--memory` and `--cpus` from the preset; no device, no `--privileged`, no host PID, IPC or network mode;
- mounts: a per-run named volume at `/work` (read-write) and one at `/out` (read-write; the bundle and logs), nothing else, and no Docker socket;
- network: the per-batch `--internal` network only (decision 10);
- environment: only the names `start` was given, from a fixed set (`ANTHROPIC_BASE_URL`, the run token, `HTTPS_PROXY`, `CHARGEHAND_MCP_URL`, `CHARGEHAND_RUN_TOKEN`, `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`, toolchain proxies).

On the Mac the server calls `docker` itself through the same template (the launchd agent is not a container, so there is no nesting problem). Both paths build their argument list in one class, so the template is tested once.

The workspace volume is filled before the session starts by the runner's helper: `git clone --local --no-hardlinks` from the read-only source at the pinned commit (ADR 0023) into the volume, on a new branch `chargehand/<run>`. The source is never written.

## Model credential

**The question:** how does Claude Code authenticate in a container, at what cost and under what terms, and how does the credential get there without landing in an image or a log?

What is known (Claude Code authentication documentation, read 2026-09-30):

- A subscription token from `claude setup-token` is one year long, "for CI pipelines and scripts", needs a Pro, Max, Team or Enterprise plan, and is read from `CLAUDE_CODE_OAUTH_TOKEN`. It can only make model requests.
- `ANTHROPIC_API_KEY` is used without a prompt in `-p` mode and is billed per token. `--bare`, the documented recommendation for scripted calls, reads no OAuth credential, so subscription tokens exclude `--bare`.
- Precedence: a cloud provider, then `ANTHROPIC_AUTH_TOKEN`, then `ANTHROPIC_API_KEY`, then `apiKeyHelper`, then `CLAUDE_CODE_OAUTH_TOKEN`, then a stored login. ADR 0020 already strips the ones a profile did not choose.
- `claude -p --output-format json` reports `total_cost_usd`, a client-side estimate that can differ from the bill; `--max-budget-usd` caps one process.

| | API key | Subscription token |
|---|---|---|
| Cost | per token; `driven.max_usd_total` is a real ceiling | the owner's plan limits; dollars are 0 in chargehand's price table, so the dollar cap cannot stop a run and only turns and the wall clock can |
| Parallelism | limited by the key's rate tier | N sessions draw on one plan's limit; 4 in parallel may exhaust it for interactive use (**UNKNOWN**: the plan's actual headroom, not measured) |
| Terms | standard API terms | whether running several unattended sessions of a subscription, from a server, is within the plan's terms is **UNKNOWN**; the documentation recommends the token for scripts but this design has not read the consumer terms for it. The maintainer has chosen it anyway (decision below); the risk is an account action by the provider, not a technical failure. |
| In the container | needs `ANTHROPIC_API_KEY` (or the gateway) | needs the token (or the gateway) |

**Decision (maintainer, 2026-09-30):** all Claude sessions go through the **subscription** for now, and the profile switches to an API key if the subscription stops working. The switch is a profile edit (`claude_code.oauth_token_secret` to `api_key_secret`), never automatic: a silent fallback would start spending money nobody approved. When a task fails with `rate_limited` or an authentication error in subscription mode, the batch starts no further tasks, reports the failed ones as retryable, and says in `error.action` that switching to an API key is the fallback. The terms question above is accepted by the maintainer as a risk, not answered; the guide says so.

**Delivery.** The risk is not the image or the log; it is that the container is a place where a prompt-injected session, a hostile test or a repository hook runs, and all of them can `cat /proc/self/environ` and write it into a file that then rides the branch to a public remote. So:

1. **Preferred:** the container holds a random per-run token only. `ANTHROPIC_BASE_URL` points at the egress gateway on the internal network. The gateway maps the token to the real credential, adds it to the upstream request, refuses after the token's cap or expiry, and counts usage from the `usage` block of each response. A leaked token is useless outside the run and outside the internal network. This needs Claude Code to run against a substituted credential with a base URL (ADR 0020 already supports `claude_code.base_url`; a masking proxy's direct route is the existing example) and, for the subscription token, the gateway to pass an OAuth bearer it did not mint. **UNKNOWN until Task 1's spike**, for both modes.
2. **Fallback, recorded:** the real credential in the container environment (`credential_delivery: env` on the batch artifact). Then the push scanner (Failure modes) and a short-lived, low-limit key are the only mitigations.
3. The credential reaches the runner in one `start` call over the private network, from the server's secret sources (ADR 0026). It is never a command-line argument, never in an image layer, never in the run log or a span; the runner and the session's stream reader replace its value in any text they keep (the pattern of ADR 0020's MCP-config addendum).

## Network policy

**The question:** which destinations, and how is it enforced on each host?

**Allowlist (default, one place: the `driven` preset):**

| Destination | Why |
|---|---|
| the model API host (`api.anthropic.com`), or the gateway | model calls; the documented required host |
| the chargehand server's MCP address on the internal network | the skill's research and review calls (decision 17) |
| package registries the repository's toolchain needs: `registry.npmjs.org`, `api.nuget.org`, `pypi.org`, `files.pythonhosted.org`, and the profile's additions | restore and install during setup and tests |
| a git remote | **not needed**: the repository arrives as a mounted clone and the push happens outside (decision 11). A repository with git-hosted dependencies adds its host to the profile allowlist. |

Anything else is refused and logged with the run id. The documented optional telemetry hosts are not on the list; `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` is set.

**Enforcement (same on the VPS and the Mac):**

1. The run container is attached to an `--internal` network. It has no route out, so a process that ignores proxy variables fails anyway. Checked on this Mac: no TCP to a public IP, no public DNS (Where it stands, 8). Not checked on the VPS engine; the integration test in Task 4 runs on both.
2. An egress container (a small CONNECT-only forward proxy, part of this repository so it is tested) is attached to the internal network and to an outside network. The run container gets `HTTPS_PROXY` and `HTTP_PROXY`; Claude Code honours them and does not support SOCKS (network configuration documentation). The proxy allows a CONNECT only to `host:443` on the list, resolves names itself, and refuses an IP literal or a private address range, so the allowlist cannot be used to reach the host's private network.
3. The proxy sees host names, not content (no TLS interception), so the allowlist limits where data can go, not what. `registry.npmjs.org` can still receive data through a package name in a URL; this is stated in the guide and in the ADR, not hidden.
4. One egress container per batch, not per run, so its logs say which run asked. The network is removed when the batch ends.

## The image

**What it holds:** the .NET 10 SDK, Node 22, Python 3 with `pip` and `venv`, git, the Claude Code CLI pinned to the version in the profile (the adapter refuses a mismatch, ADR 0020), and the `chargehand` plugin directory at the server's version so the skill's text is the one under review. No Docker client, no cloud CLI, no secret. The size is **UNKNOWN** (to be measured in Task 6; a fat single image is likely a few GiB).

**How a repository selects toolchains.** Detection (the marker files of ADR 0035's `VerifyCommand`) chooses `setup` and `verify` defaults; the repository can override both and name an image in `.chargehand/session.json`. The image name must be on the profile allowlist; the file is read from the pinned commit, not from the working tree the session edits. A repository needing another toolchain (Java, Go, Rust) needs a profile-allowlisted image; the design does not build images on demand.

**Why not one image per toolchain first:** variants save pull time but multiply the things to pin, scan and keep at the same Claude Code version. Start fat; split when the measured pull time matters.

## Running the skill headless

The container's command is a small driver (`chargehand-session`, in this repository) that runs, in `/work`:

```
claude -p "/chargehand:change <goal>" --plugin-dir <plugin> --permission-mode dontAsk \
  --allowedTools <list> --max-budget-usd <task cap> --output-format stream-json --verbose
```

with `--append-system-prompt` saying: no person is available; where the skill would ask the user, stop and print `NEEDS_INPUT:` followed by the questions; name the branch `chargehand/<run>`; finish with the `driven-report` JSON. The driver tails the stream, enforces the no-progress limit, sends SIGINT then SIGTERM on a signal from the runner, then makes the bundle.

Three things in the skill do not fit a container and change, additively, in Task 11: step 1's "stop on a dirty tree" is satisfied by the fresh clone; step 2 and 7's questions become `NEEDS_INPUT`; step 3's branch name is taken from the environment (`CHARGEHAND_BRANCH`) when set. The skill's interactive behaviour for a person is unchanged. **Risk:** whether a model following the skill text plus an appended system prompt keeps to it over a long session is not measured; Task 12 measures it.

## Contract changes (all additive)

**`request/v1`:** optional `driven`:

```json
{ "tasks": [ { "id": "t1", "ref": "CHARGEHAND-12" }, { "id": "t2", "goal": "Add a retry to the fetch client" } ],
  "max_parallel": 2, "max_tokens_total": 4000000, "max_usd_total": 6.0, "draft_pr": true }
```

`tasks` has 1 to 20 items; each has a unique `id` within the batch and at least one of `ref` (a tracker item id, resolved through a profile mapping in the style of ADR 0034, else `invalid_request` with the action to configure it) or `goal`. `text` stays required and describes the batch. `max_tokens_total` is required in subscription mode; `max_usd_total` is required in API-key mode; `draft_pr` accepts only `true`; the field exists so a future non-draft mode is a visible change, not a silent one. `context.repository` is required with `driven`, and task ids must be unique; both are checked in code (`DrivenRules`) when the request is accepted, not in the schema, because `SchemaCompatTests` treats a new cross-field rule on a published schema as breaking.

**`result/v1`** (the batch result; each task also has its own `result/v1` in the run store under its own run id):

- artifact `driven-batch` (`application/json`): per task `{id, run_id, status, branch, pr_url, pr_number, usd, credential_delivery, error_code}`;
- per task, in that task's result: artifacts `branch` (as ADR 0035), `pull-request` (`{url, number, draft: true, head, base}`), `verification` (as ADR 0035, now with `sandbox: "container"` and the exit code chargehand measured), `session-tests` (the session's own test runs, labelled `claimed_by: session`), `review` (run ids of the nested review calls and their `status`, `claims` count), `session-log` (uri and sha256 of the stored stream, never inline);
- `usage` is the batch's total.
- New `error.code` values: `container_unavailable`, `credential_unavailable`, `session_failed`, `session_stalled`, `push_rejected`, `pr_failed`, `cancelled`, `tasks_incomplete`. A batch is `completed` only when every task is; otherwise `failed` with `tasks_incomplete` (`retryable: false`, `action` naming the tasks), and the artifact says which.

**`run-status/v1`:** new `event` values `container_started`, `session_progress`, `verify_finished`, `pushed`, `pr_opened`, `task_finished`; new optional fields `task_id`, `branch`, `pr_url`, `usd_total`, `turns`. `status` keeps its enum; a cancelled run ends `failed` with code `cancelled`.

**`run-summary/v1` (new file, new schema):** one row per run for lists: `run_id`, `parent_run_id`, `task_ref`, `preset`, `status`, `started_at`, `finished_at`, `usd`, `branch`, `pr_url`, `error_code`.

**HTTP (new routes, existing ones unchanged):** `GET /v1/runs?status=&since=&limit=` (rows of `run-summary/v1`), `POST /v1/runs/{id}/cancel`, `POST /v1/halt` (stop starting tasks, cancel all, until `POST /v1/resume`). The MCP tool gets no new argument beyond `driven`.

These land before the 1.0 freeze in `docs/plans/2026-09-30-stability-1.0.md`: its Tasks 2 to 4 snapshot the route table and the schemas, so if any of them merges first, the matching task here updates the snapshot in the same pull request.

## Failure modes

| Failure | What happens |
|---|---|
| **The container dies** (OOM, engine restart, host reboot) | The runner's `inspect` reports exit; the session ends `failed` with `session_failed`, the output volume is kept for 7 days for inspection. A task is not retried automatically: a half-done session's side effects are unknown. The caller may resend. A server restart marks unfinished tasks `lost` as runs are today, and the runner's `kill-all` on start removes orphan containers of the previous process. |
| **The push is rejected** (protected branch, revoked key, non-fast-forward) | `failed` with `push_rejected`, the bundle kept, the error text (credential redacted) in `error.message`. The credential cannot push a default branch, so a misconfigured target cannot merge by accident. A branch name collision is impossible by construction (`<run>` is unique). |
| **The PR cannot be opened** after a push | `failed` with `pr_failed`; the branch exists and is named in the result. |
| **The session loops** | The driver tracks the stream: no new tool call and no output for `no_progress_minutes` (default 10), or the same tool call with the same input 5 times in a row, or more than `max_turns` (default 80) assistant messages ends the session with `session_stalled`; work so far is bundled, verified, and pushed only if verification passes and the diff is not empty. A hard wall clock (`max_minutes`, default 45) sits above all three. |
| **A cap is hit** | The token ceiling (both modes) or the dollar ceiling (API-key mode) ends the process: the driver stops the session and, in API-key mode, the gateway also refuses the run token. The task ends `cost_cap_reached`, branch kept unpushed unless verified. In the batch: no new task starts if its cap would exceed the batch cap; running tasks are stopped when the total is exceeded by more than one task's cap. |
| **The subscription is rate-limited or signed out** | A task ends `rate_limited` (retryable) or `provider_unavailable`; the batch starts no further task and lists the rest as not started. The `action` names the switch to an API key. There is no automatic fallback. Parallel sessions share one plan's limit, which is why `max_parallel` defaults to 2. |
| **The session claims tests pass and they do not** | chargehand's own verification run (decision 12) decides; the `session-tests` artifact is labelled as the session's claim. A red verification means no push and `verification_failed`, as in ADR 0035. |
| **A secret ends up in the diff** | A scan of the diff (gitleaks, as the repository's own hook uses) and of the model credential's literal value runs before the push; a hit fails the task with `session_failed` and pushes nothing. This is a backstop, not a guarantee. |
| **Kill switch** | `POST /v1/runs/{id}/cancel` signals the container (SIGINT, 5 s, then SIGTERM, then remove). `POST /v1/halt` does it for every run and refuses new ones until resumed. `chargehand runs kill --all` and the runner's `kill-all` work with the server down, by label. A profile key `driven.enabled: false` refuses `driven` requests at intake. Cancel never pushes. |
| **The runner or Docker is down** | `container_unavailable` at intake, nothing created, `retryable: true`. |
| **The gateway or model API is down mid-run** | Claude Code retries; past the no-progress limit the session stalls out as above. |

## Claims, tests and review evidence

- **Citations.** The session's `driven-report` claims cite `file` and `diff` evidence at the branch commit; the existing resolver and `SupportJudge` check them after the run, so each claim in the task result has `support`. The nested research call's claims are checked by the server when they are made. A claim that cites nothing resolvable moves to `open_questions`, as everywhere (ADR 0009).
- **Tests.** The result carries two records and never merges them: `verification` (chargehand ran the command in a fresh container on the bundle's commit: argv, image digest, network false unless the profile allowed it, exit code, output tail, changed build and test paths, as ADR 0035) and `session-tests` (what the session said it ran).
- **Review.** The nested `review` runs are separate runs with `parent_run_id`; the task result lists their ids, statuses and claim counts so a reader can open each. Unresolved findings after the last fix round appear as `open_questions`.
- **Limits, stated.** The session can still edit the tests or the build script so the command passes (ADR 0035, decision 12); the `verification` artifact lists those paths. A green draft pull request is evidence that a command passed in a container, not that the change is right. Public text says "citations checked", not "claims verified".

## In a later GUI

A GUI needs: a list, a detail view and two buttons. The list is `GET /v1/runs` (state, cost, branch, pull request link, parent, error code). The detail is the existing `GET /v1/runs/{id}` plus the `session-log` artifact and the nested runs filtered by `parent_run_id`. Live state is the existing events stream with the new events. The buttons are cancel and halt. The pull request link is `pr_url`. Nothing here requires the GUI to know about containers.

## Components

| Unit | Responsibility |
|---|---|
| `Driven/DrivenRequest`, `DrivenBatch` | Parse and validate `driven`, resolve `ref` to a goal, apply caps. |
| `Driven/BatchScheduler` | Decisions 15 and 16: parallelism, spend, halt. |
| `Containers/IContainerEngine`, `DockerCliEngine`, `RunnerClient` | Start, signal, inspect, remove, `kill-all`. |
| `Containers/ContainerTemplate` | The one place the `docker run` argument list is built. |
| `Runner/` (`chargehand runner`) | The narrow HTTP API in front of the socket; image allowlist. |
| `Egress/AllowlistProxy` | CONNECT-only forward proxy, and the credential exchange when decision 9 works. |
| `Driven/SessionDriver` (`chargehand-session`) | Run `claude -p`, tail the stream, enforce limits, write the bundle and `driven-report`. |
| `Driven/Handover` | Decision 11: fetch the bundle into a fresh repository, scan, push, open the draft pull request. |
| `Driven/DrivenResult` | Build the per-task and batch `result/v1`, support-check the claims. |
| `Server` additions | Cancel, halt, run list, run-scoped token, `parent_run_id`. |
| `images/session/Dockerfile`, `presets/driven.yaml` | The image and the preset. |

## Testing

- Unit: the container template (every flag asserted, none passable), the scheduler under caps, the allowlist matcher (IP literals, private ranges, ports, case), request validation, result assembly.
- Integration (need Docker; skipped without it, run on the Mac and in CI where Docker exists): a container on the internal network cannot reach a public IP or resolve a public name; it reaches the stub allowed host through the proxy; the credential literal is absent from `docker inspect`, the image history and the stored logs; cancel stops a container within 10 s.
- Handover: a bundle from a hostile workspace (a repository with `core.fsmonitor`, hooks and a `.gitattributes` filter set in its `.git/config`) is fetched and pushed without running any of them, against a local bare remote that refuses the default branch.
- End to end (`scripts/driven-e2e.sh`, a real model, run by hand or on the self-hosted runner): three tasks on the sample repository, one of which cannot pass.

## Out of scope

Picking work from the tracker's cycle; a second runtime (OpenCode, through the same container interface); non-draft pull requests, merging, auto-merge; a GUI; image building on demand; per-task images chosen by a repository; Windows hosts; making the allowlist content-aware; rootless operation beyond recording what the VPS engine supports.

## Open items

1. **Partly answered: a substituted credential.** Claude Code sends a dummy credential to a base URL in both modes (Where it stands, 10). Still UNKNOWN: that a forwarded response with the real credential swapped in works, and the subscription token's refresh behaviour. Task 1 finishes it with a low-limit key from the maintainer's secret source.
2. **UNKNOWN: subscription terms and headroom** for unattended, parallel, server-hosted sessions. The maintainer reads the terms; the design defaults to an API key for batches.
3. **Partly answered: the VPS engine.** 29.6.2, rootful, cgroup v2 (Where it stands, 9). Still UNKNOWN: whether `--internal` isolates there as on the Mac (Task 4's test decides), and how much memory one session with a .NET build needs; about 9.7 GiB is available now next to ~120 containers, so `max_parallel` 2 is the default and 4 stays a ceiling until measured.
4. **UNKNOWN: the fat image's size and pull time;** the VPS's memory available for N sessions (each runs Claude Code plus a build). The defaults (`max_parallel` 2, ceiling 4) are guesses until measured.
5. **UNKNOWN: whether the skill survives headless.** `chargehand runs adherence` measures it from stored streams; the 10 sessions have not been run, so no number exists. Needs the maintainer's capped key.
6. **Answered: the remote.** The VPS clone has an HTTPS `origin` (Where it stands, 9). The push credential must therefore be a fine-grained token or an ssh deploy key with the remote URL rewritten by chargehand for the push; the choice per repository goes in `driven.push_secret`. Whether every repository under the root has an `origin` was checked for the one present.
7. **The tracker mapping for `ref`:** which tool reads an item's title and body. Decided when Task 8 is written against the real server's tool list.

## As built (amendments found while building, 2026-09-30)

Recorded here so the spec matches the code. None reopens a maintainer decision.

1. **The server's MCP endpoint is reached through a forward, not the allowlist.** The allowlist proxy refuses every private address (decision 10), which is right, and the chargehand server sits on one. The batch's egress container therefore carries operator-named TCP forwards (`--forward <port>=<host>:<port>`); the session's MCP URL is the egress container's name and the forward's port. It joins the network the server sits on (`driven.network.outside`, a runner `--outside-networks` entry). Nothing a session sends can add or change a forward.
2. **Workspaces are prepared by a helper, and verification reuses it.** A no-network helper container clones chargehand's read-only checkout into a fresh volume on a new branch at the pinned commit (no hooks, `core.hooksPath=/dev/null`, a `chargehand` identity). The same helper builds the workspace of the fresh verification container: the session's container and workspace are removed, a new workspace is prepared, and only the bundle in the session's output volume is carried over (`verify-branch` fetches the branch from it and checks it out detached). The runner insists on volume names derived from the run id, and on a source path under a configured root.
3. **The image's entrypoint is the CLI**, and the runner passes the verb (`session` or `verify-branch`).
4. **Two refusals the design did not name.** A change to CI configuration is not pushed unless allowed (pushing it can run it with the repository's secrets), and a repository with no test command is not pushed unless allowed.
5. **The handover reads only what chargehand made.** It never runs git in the session's workspace. A bundle carries objects and refs, no configuration or hooks; a test keeps a hostile workspace configuration from ever running on the host.
6. **Run tokens run on their own gate of two**, because the batch holds the one-at-a-time gate while it waits for the session's research and review calls (one gate would deadlock).
7. **Measured:** the session image is 2.4 GB on disk (local build); a container on an `--internal` network could not reach a public IP or resolve a public name (OrbStack, Docker 29.4.0). The VPS engine is Docker 29.6.2, not rootless. Not measured: pull time on the VPS, memory per session, `--internal` on the VPS engine.

Built and merged or open as pull requests, in plan order: 2 (contracts), 3 (container template and engine), 4 (egress proxy and internal network, plus forwards), 5 (runner service, with workspace preparation and outside networks), 6 (session image and driver), 7 (run list, cancel, halt, run tokens, the child gate), 8 (batch scheduler, ref resolution), 9 (workspace helper, handover, container verifier), 10 (task and batch results), 11 (the skill's driven mode). Not built: 1 (the real-credential spike needs a low-limit key), 12 (a run of the end-to-end script and the adherence measurement against a real model; the script and the helper are written, wiring a request to the batch is done, the deployment is the maintainer's).
