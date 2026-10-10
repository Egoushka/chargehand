# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The contract package
(`Chargehand.Contracts`) is versioned separately, by schema major.

## [Unreleased]

## [0.8.6] - 2026-10-10

The driven session image keeps the .NET CLI home and the NuGet cache on its volume.

### Changed

- The session image keeps the .NET CLI home and the NuGet package cache on the volume (ADR 0039): `DOTNET_CLI_HOME=/work/.dotnet`, `NUGET_PACKAGES=/work/.nuget/packages`, so a restore of a real solution does not fill the tmpfs at `/home/session`. The image already had the .NET 10 SDK; its size does not change. The driven guide gains a `dotnet test` verification example and the NuGet hosts a restore needs in `driven.network.allow` (`api.nuget.org`, `globalcdn.nuget.org`).

## [0.8.5] - 2026-10-10

Claude Code as an observer of driven sessions, and the model credential out of the session containers. `chargehand watch` and `cancel`, the `/chargehand:run` skill and a run id after 10 s let a Claude Code session hand a prompt to a driven batch and follow it. On the subscription the real token now stays in the batch's egress container (`token_exchange`); an API key can go through an operator's gateway (`model_url`). Additive; `driven.enabled` is still false by default.

### Added

- The model credential exchange (ADR 0039 decision 9, CHARGEHAND-148): on the subscription, the real token goes only to the batch's egress container, whose model endpoint accepts a per-task token (bound to the run, its token cap and an expiry) and swaps in the real one toward `api.anthropic.com`. Sessions get the token and the endpoint as `ANTHROPIC_BASE_URL`; the batch result records `credential_delivery: token_exchange`. The runner service carries the credential to the egress container's env file and refuses any other upstream host, so it works on the deployed path. Measured through a runner and a socket proxy: one task to a draft pull request, no real token in any sampled session environment. An API key is unchanged (`environment`, or `gateway_key` with `model_url`).
- `scripts/driven-e2e.sh` can run through a gateway (`CHARGEHAND_E2E_MODEL_URL`, with `CHARGEHAND_E2E_MODEL_KEY` the key the gateway issued): it sets `driven.network.model_url` and `claude_code.base_url`, and a case checks the batch result says `gateway_key`. One task through a Bifrost key ended in a draft pull request for 0.17 USD (CHARGEHAND-161).
- Driven batches are traced like orchestrator runs (ADR 0042, CHARGEHAND-161): a `chargehand.driven.run` span, a `chargehand.driven.task` span per task (state, error code, branch, pull-request URL, model, Claude Code session id, turns, wall time) and under it the session as one `chargehand.driven.session` generation. The batch's and every task's `result/v1` carry its trace id, which until now was random and pointed at nothing. With `telemetry.usage_on_spans` the session span carries usage and cost from the profile's price table, model by model from the stream's `modelUsage`; the table may key a model under any provider. The session outcome gains the session id, the model, cache-write tokens and per-model usage.
- `driven.network.otlp_url` (ADR 0042, CHARGEHAND-161): each session's Claude Code exports its own OpenTelemetry logs and metrics to an OTLP/HTTP collector through a forward on the batch's egress container, tagged `chargehand.run_id`, `chargehand.task_id`, `chargehand.task_run_id` and `chargehand.trace_id`. No prompts, no headers, no traces. An `https` URL, credentials or a query, or a port another forward uses is refused before anything is created. Unset, nothing changes.
- `driven.network.model_url` (ADR 0039, CHARGEHAND-161): driven sessions reach the model through an Anthropic-compatible gateway with a key the gateway issued (a budget, a model list, revocable) instead of the real credential in the container's environment. An `http` gateway is reached through a second forward on the batch's egress container, an `https` one through the proxy; the session gets `ANTHROPIC_BASE_URL`, the batch result records `credential_delivery: gateway_key`, and the key is scanned for like any model credential. It needs `claude_code.api_key_secret`; a subscription token, a URL with credentials or a query, or a port equal to `mcp_forward`'s is refused before anything is created. Unset, nothing changes.
- `/chargehand:run <prompt>`, a second skill in the Claude Code plugin (ADR 0039, CHARGEHAND-159): it hands the prompt to a one-task driven batch on the connected server and observes it with `chargehand watch` under the Monitor tool, relaying the milestones up to the draft pull request, with `chargehand cancel` to stop it. Its `allowed-tools` pre-approve only `orchestrate`, `Monitor`, `chargehand watch|show|cancel` and two read-only git commands, and the skill forbids the session from doing the task itself. Driven sessions being off on the server is reported, not worked around.
- A request with a `driven` block over MCP no longer blocks until the batch ends (ADR 0029, ADR 0039, CHARGEHAND-160): without `Prefer: wait` it waits 10 s and then answers with the run id, the way `POST /v1/runs` does. A client such as Claude Code cannot set the header per call, so a batch started from it held the session for hours. Other requests still wait for their result.
- `chargehand watch <run-id>` and `chargehand cancel <run-id>` (ADR 0039, CHARGEHAND-158): a client of a running server. `watch` tails `GET /v1/runs/{id}/events` and prints one stdout line per event until the result, so a Claude Code session can run it under its Monitor tool and relay a driven run's progress; the exit code is the run's (0 completed, 1 failed, denied or lost, 3 needs input, 2 a wrong key or unknown run). Session progress is thinned to one line per 30 s per task unless the stage changes, and a dropped connection resumes without repeating lines. The server comes from `--url` or `CHARGEHAND_URL`, the key from `CHARGEHAND_API_KEY`, either missing from the profile's `http` block. `cancel` posts to `/v1/runs/{id}/cancel`.
- `scripts/driven-e2e.sh` can run a batch through a real `chargehand runner` (`CHARGEHAND_E2E_RUNNER=runner`) and behind a `docker-socket-proxy` container with the deployment's flags (`proxy`), the way a VPS runs it (CHARGEHAND-157). The default `direct` is unchanged. A case asserts the proxy served the batch, and the runner's key joins the credential scan. Every earlier run used the server's own docker engine, so the runner path, where the first VPS batch found four defects, was untested.
- A driven batch shows its tasks while they run (ADR 0039): the batch run publishes `container_started`, `session_progress` (tokens, turns and the `change` skill's step), `verify_finished`, `pushed`, `pr_opened` and `task_finished`, each with its `task_id`, so `GET /v1/runs/{batch}` and its events say more than `started`. Until now only `accepted`, `started` and `run_finished` were published, and a task's own run is unknown until it ends. `run-status/v1` gains two optional fields, `tokens` and `stage` (additive); the session driver writes turns and stage into `session-usage.json` beside the tokens.
- A driven task's result carries a `changes` artifact (ADR 0039): each changed path with its added and removed line counts, and the unified diff cut at a line within 32 KiB, from chargehand's own clone of the bundle. Until now the changed paths only reached the pull request body as a count, so a client had to fetch the branch to see the change. A diff that looks like it carries a secret is left out; the paths and counts stay. The handover's git output cap now keeps a prefix instead of skipping a read that did not fit.
- `max_parallel_nodes` in the profile (1 or 2, default 2) bounds how many worker nodes of one run execute at once; 1 means one `claude -p` worker on the host, for a machine short on memory.

### Changed

- The first deployment of driven sessions on a VPS is recorded (ADR 0039, the driven guide, the roadmap; CHARGEHAND-156): a draft pull request on the fourth attempt, the four defects each attempt found, and what is still open (the credential gateway, the kept per-task output volume).

### Fixed

- `chargehand runner` says which argument it rejected (ADR 0039, CHARGEHAND-152): the reason, with the value, on its own line before the usage (the usage names every flag, so alone it did not say which one was wrong, and the first deploy restart-looped on a tagged image reference). A valid command line with `--source-roots`, `--outside-networks` or `--forwards` empty now prints a note for each, since each starts fine and then silently refuses work (no workspace, no network join, no callback).
- Cancelling a task whose container had already ended no longer fails (ADR 0039, CHARGEHAND-157): `docker kill` answered "is not running" (or "No such container"), the engine threw, and the cancel ended `container_unavailable` instead of `cancelled`. A signal to a container that is gone or ended is now a no-op; any other `kill` failure still throws. Found by the e2e script's new runner mode; the runner uses the same engine.
- The server guide says that `repository_roots` must also cover `<worker_root>/.checkouts` for driven sessions (CHARGEHAND-155): a session's research and review calls name its own checkout, and without that root the server refuses them (`repository_not_allowed`), found on the first deploy.
- A run under `serve` is its own trace again: it started inside the HTTP request's unrecorded activity, so the parent-based sampler dropped it and every span below, and no served run reached Langfuse.

## [0.8.4] - 2026-10-05

The second fix from the first driven batches on a Linux host: a batch through the runner could not call the server back. Additive; `driven.enabled` is still false by default.

### Fixed

- A driven batch through the runner could not call the server back (ADR 0039, CHARGEHAND-154): the runner's egress request had no field for the forward, so the egress container started without it and a session's research and review call was refused (`ECONNREFUSED`; it stopped and asked, rightly, instead of skipping them). The request now carries the forwards and the runner starts only those listed in its new `--forwards` option, like `--outside-networks`, so a server cannot point an egress container at any other host. An egress with no forwards is unchanged.

## [0.8.3] - 2026-10-05

Two fixes found by the first real driven batch on a Linux host (one of them hid the other) and a docs fix from the first deploy. All additive; `driven.enabled` is still false by default.

### Fixed

- The first driven batch on a Linux host failed, and said the wrong thing (ADR 0039, CHARGEHAND-153). (1) The workspace helper cloned the checkout with `-c safe.directory`, which `git clone` of a local path does not pass to its `git-upload-pack` child: on a host where the checkout belongs to another user it stopped with "detected dubious ownership" (a Mac's bind mounts hide the owner, so no earlier run saw it). The helper now sets a global `safe.directory` in its writable `/tmp`. (2) `RunnerClient.RemoveAsync` threw on the runner's 404 for a container that was never started, so any failure before the start was replaced by "no such session container" and the rest of the cleanup was skipped; a missing container is now a no-op, as with `docker rm -f`.
- The server guide says what the runner needs to start and to run a batch (CHARGEHAND-152): an image reference without a tag (a tagged one is refused, and the runner prints its usage and exits), `--source-roots` and `--outside-networks`.

## [0.8.2] - 2026-10-05

What a container deployment of driven sessions needs from the code, all additive: the session image is published with the release, the server image carries the docker CLI the runner needs, and a session calls the server back by a fixed name. This is still not the minor version that driven sessions get when they are wired: `driven.enabled` stays false in every real profile, and a batch still delivers the model credential in the container's environment until the credential exchange (CHARGEHAND-148) lands. The v0.8.1 tag did not carry these: its notes listed the entry below by mistake, and it is moved here.

### Added

- Driven sessions can be deployed with containers (ADR 0039, CHARGEHAND-151): the release publishes the session image `chargehand-session:<version>` next to the server image and prints both digests in the job summary; the server image carries the docker CLI (pinned by digest to the engine's version) so `chargehand runner` runs from it; a session calls the server back as `http://chargehand-driven:<port>` (the egress container's alias on the batch network, the same in every batch), so `http.allowed_hosts` lists one name instead of a wildcard; the server guide describes the services and the path rule for `worker_root`.

## [0.8.1] - 2026-10-05

The parts of driven writing sessions ([ADR 0039](docs/adr/0039-driven-writing-sessions.md), [guide](docs/guide/driven.md)) and the prompt-enhancer extension (ADR 0040, 0041), all additive. This is not the minor version that driven sessions get when they are wired: `driven.enabled` stays false in every real profile, a driven batch still needs its credential exchange, and the skill's measured adherence (at least 7 of 10) is not met. No schema major changes; `Chargehand.Contracts` changes are additive.

### Changed

- The session image installs Node 26.10.0 from nodejs.org, checked against its published SHA-256, in place of the distribution's Node 18 (ADR 0039): a repository that runs `.ts` sources through Node's type stripping, or asks for `engines.node >= 26`, could not pass its tests in a session or in the fresh verification run.
- The headless `change` skill and the driver prompt state the order research, write, run the tests, then review, and that a goal which cannot be met still gets a best attempt and a stop with the reason, never a stop after research alone or an edit to a test (CHARGEHAND-141). Measured: 12 of 12 sessions followed the steps on an interim wording and 12 of 12 on this one, up from 7 of 12.

### Added

- `scripts/driven-e2e.sh` runs on a real repository: `CHARGEHAND_E2E_REPO=<path of a git checkout>` clones its committed history (nothing is pushed to its own remote), with `CHARGEHAND_E2E_TASKS` and `CHARGEHAND_E2E_VERIFY` (the verification command as a JSON argument vector) required (ADR 0039, CHARGEHAND-150). Measured on a TypeScript-on-Node repository: 10 of 10 sessions followed the skill's steps and 4 of 4 passable tasks ended in a draft pull request. `/tmp` in a session and in the fresh verification run stays `noexec`; a repository whose tests run a script from `/tmp` sets `TMPDIR` under `/work` in its `context.verify` (guide and ADR).
- A run now asks the profile's `prompt_enhancer` about its request text and reports the outcome (ADR 0041): one `enhance` call before intake and one `feedback` call after the result, in `run`, `serve`, the MCP tool and `change`. The text sent to workers is always the original; a rewrite is offered to nobody and reported as not accepted. Context is the four allowed fields (repository as the folder name). No `prompt_enhancer` in the profile: no call.
- A `prompt_enhancer` extension (ADR 0040, WHET-13): a profile entry naming an `mcp_servers` server that lists `enhance` and `feedback`. `IPromptEnhancer` and `McpPromptEnhancer` carry the calls, and `GuardedPromptEnhancer` turns a late, failing or empty answer into the original prompt inside `deadline_ms`. `chargehand extensions check` verifies the two tools. No client calls it yet.
- `chargehand runs adherence` prints `adherence` and `honest_stop` apart (ADR 0039, CHARGEHAND-141): `honest_stop` counts sessions that did research, a write and a test run in order, made no review and wrote no test file; it is not part of the 7 of 10 bar.
- Driven batches see a session's token use while it runs (ADR 0039, CHARGEHAND-147): the in-container driver rewrites `session-usage.json` in the output volume as the stream grows, and the runner reads it through the existing out-volume read (a fifth allowlisted name, read only) every poll interval (5 s by default) while the task runs and reports it to the scheduler. A task that reports more than its own token or dollar cap is stopped at once (`cost_cap_reached`), and a batch stops its running tasks as soon as their reported use passes the batch cap (before: only once it was over by a whole task cap). Dollars still arrive only with the session's final result, so live dollar caps bind through the session's own `--max-budget-usd`.
- `scripts/driven-e2e.sh` can run on a Node repository (`CHARGEHAND_E2E_REPO=node`) or a task list of its own (`CHARGEHAND_E2E_TASKS`), and fails `impossible_tests_untouched` when the task that cannot pass is passed by editing a test file (ADR 0039, CHARGEHAND-141).
- A driven session is told the repository path and base commit its run token admits (`CHARGEHAND_REPOSITORY_PATH`, `CHARGEHAND_BASE_COMMIT`, ADR 0039, CHARGEHAND-141): the skill's driven section and the driver prompt use them for the research and the review call (the review at the base commit, with the diff as input) and say not to skip either step. Measured: 7 of 9 sessions followed the steps, up from 1 of 3; the ADR records the result and that the feature is not ready. The runner also removes a container whose start was cancelled in flight, by its name.
- `scripts/driven-e2e.sh` (ADR 0039, CHARGEHAND-141): an end-to-end run of a driven batch on a synthetic repository (three tasks, a local bare remote that takes only `chargehand/*`, `max_parallel` 2, a stub of the draft-pull-request endpoint, a cancel case, a scan of everything it wrote for the push token and the model key). It calls a real model and has not been run yet. It needs two switches in the CLI that exist for it alone: `CHARGEHAND_E2E_GITHUB_API` and `CHARGEHAND_E2E_LOCAL_REMOTE=1`.
- `chargehand runs adherence <stream.jsonl>...` reads stored session streams and says, per session, whether it did research, a write, a test run and a review in that order with at most 2 fix rounds, and whether at least 7 of 10 did. Nothing is measured yet.
- Driven sessions move a task and its results through the session's output volume (ADR 0039, CHARGEHAND-146): `task.json` goes in before the container starts, and the bundle, the report and the outcome come out after it ends, by a throwaway helper container with no network started from the session image. The runner service has two new authenticated routes for it (`PUT` and `GET /out/{run}/{name}`) that take only a listed image, `task.json` in and the three result files out, and the Docker CLI engine does the same locally. A batch no longer ends every task as `container_unavailable`.
- A request with a `driven` block runs as a batch (ADR 0039), through `chargehand serve` when the profile has `driven.enabled`: tasks resolve to goals, the batch gets its network and egress container, each task gets a workspace at the pinned commit and a session container, and `TaskRunner` hands the session's branch to the handover and writes the task's own `result/v1` under its own run id (parent: the batch). The batch result lists them. With `driven.enabled` false the request ends `failed` with `invalid_request`; the stdio and CLI paths refuse a driven request the same way. The profile's `driven.network` takes `outside` and `mcp_forward` (`host:port` of the server), which gives sessions a forward to the server's MCP endpoint and a run token; unset, they get neither. Not yet working end to end: the runner service cannot put a task into a session's output volume or read its bundle out, so a batch ends each task `container_unavailable` until it can (`docs/guide/driven.md`).
- The result of a driven task and of a batch (ADR 0039): a task's claims are the session's, cited against the pushed commit and put through the same resolver and support check as any result; chargehand's own test run (`verification`, in a fresh container) and the session's claim about its tests (`session-tests`) are two artifacts, never merged; the batch result is `completed` only when every task ended in a draft pull request, else `failed` with `tasks_incomplete` naming them.
- The handover of driven sessions (ADR 0039, not yet reachable from a request): chargehand fetches a session's bundle into a scratch clone of its own checkout and checks the branch itself (descends from the base, changes something, no secret-shaped text or secret-like file names in the diff, no CI configuration unless allowed, the repository's tests pass in a fresh container), then pushes only `chargehand/<run>`, never forced, with a credential that cannot push the default branch, and opens a **draft** pull request. The GitHub client has one method, create a draft; it cannot merge.
- `chargehand eval score-claims <claims.jsonl> --repo <dir> --commit <sha> [--out <verdicts.jsonl>]` (benchmark goal 0.9): claims from any source, each with `path:line` locators, get one verdict from the shipped support judge — `uncited` (no locator), `unresolved` (a locator names no file, line or range at the commit), `supported`, `partial`, `unsupported`, or `unchecked` (the judge failed twice). Claims are judged in batches of one arm, question and run, and the arm is never sent to the judge.
- A batch's egress container can carry operator-named TCP forwards (`--forward <port>=<host>:<port>`, ADR 0039), which is how a session on the internal network reaches the chargehand server for its research and review calls; the allowlist proxy still refuses every private address, and nothing a session sends can add or change a forward.
- The `change` skill has a driven mode (ADR 0039): when `CHARGEHAND_DRIVEN=1` it asks no person anything (it prints `NEEDS_INPUT:` and the questions and stops), uses the branch in `CHARGEHAND_BRANCH`, writes no report file and neither pushes nor opens a pull request, and ends with a fenced JSON report. Its steps for a person are unchanged.
- The batch scheduler of driven sessions (ADR 0039, not yet reachable from a request): at most `max_parallel` tasks at once, in order, each reserved at its whole cap so the batch never starts a task it could not afford; running tasks are stopped when the batch overshoots its cap by more than one task's cap; a rate-limited or unavailable provider stops new starts and says how to switch to an API key; a task ref becomes a goal through the profile's `driven.task_source` mapping.
- Driven sessions call chargehand back with a run-scoped token (ADR 0039): it opens only `POST /v1/runs`, one run's status and the MCP tool, only for the `default` and `review` presets on its task's repository and commit, within the task's caps; the runs it starts record the task as their parent and use their own gate, so they cannot wait for the batch that holds the main one.
- The server can list runs (`GET /v1/runs`, `run-summary/v1`), cancel a run it holds (`POST /v1/runs/{id}/cancel`, ending `failed` with `cancelled`) and be halted and resumed (`POST /v1/halt`, `POST /v1/resume`); `chargehand runs kill --all` removes driven-session containers by label without a server (ADR 0039).

### Fixed

- `scripts/egress-test-image.sh` publishes the CLI for the container engine's architecture (`linux-arm64` or `linux-x64`) instead of the host's, and sets the image's entrypoint, so the egress tests run on a Mac; the egress tests supply a `nc` stand-in (bash `/dev/tcp`) when the test image has none.
- `chargehand runs adherence` counts `node --test`, `jest`, `vitest` and `mocha` as a test run (it read them as no test).
- The plugin's MCP entry passes `--source https://api.nuget.org/v3/index.json` to `dnx`, so a machine whose NuGet config lists a private feed no longer fails with 401 and `CONNECTION_CLOSED` (CHARGEHAND-127).

## [0.8.0] - 2026-09-30

One release for two goals; there is no 0.7.0, because no commit is 0.7 only (as with the missing 0.5.0). Goal 0.7
([roadmap](ROADMAP.md)): workers can write. The `code` preset edits files in its own clone, runs the repository's tests
in a sandbox and returns a branch, a diff and a verification artifact. Goal 0.8: every claim is checked for support
against the text it cites, results can be signed with a key you make, and `chargehand verify` checks a signed result
offline; each cited text's sha256 is recorded on its evidence. Schema changes are additive, and `Chargehand.Contracts`
1.4.0-alpha is published with them. Still open: the usage bars of 0.5 and 0.7 (the maintainer's own use on real tasks);
the Linux sandbox (`bwrap`) is only tested as arguments, never run; the support judge was measured on 30 claims.

### Added

- Every claim is checked for support (goal 0.8, ADR 0036): after a node, one call on the intake model judges whether the text each claim cites supports it. `result/v1` claims gain an optional `support` (`supported`, `partial`, `unchecked`); an unsupported claim moves to `open_questions`, a partly supported one stays at half confidence and is not kept for memory; a failed check leaves the claims `unchecked` and never fails a run. Profile `support_check` (default true) turns it off. On 30 labelled claims, Haiku and Sonnet each agreed with the labels 25 times, perfectly on clearly supported and clearly unsupported claims and not on partly supported ones (`scripts/support-eval.sh`, [guide](docs/guide/support-and-signing.md)).
- Results can be signed (ADR 0036): profile `signing.key_file` or `CHARGEHAND_SIGNING_KEY_FILE`, a P-256 key you make yourself; `result/v1` gains an optional `signature` (ES256 over the RFC 8785 canonical result). `chargehand verify <result.json> --public-key <pem>` and `ResultSignature.Verify` in `Chargehand.Contracts` check it offline. Chargehand never creates or stores a key.
- Evidence without a `sha256` gets the SHA-256 of the whole cited text, taken before any cut for the judge.
- Groundwork for writing workers (goal 0.7, ADR 0035), all additive: a preset node kind may set `writes` and `verify` (`timeout_seconds`, `max_fix_rounds`), a request's `context` may carry `verify` (the test command as an argument vector), and `result/v1` error codes gain `sandbox_unavailable` and `verification_failed`. Nothing uses them yet.
- A writing node's workspace (ADR 0035): a per-run clone under `<worker_root>/.runs/<run>/<node>` of the cached checkout, on branch `chargehand/<run>/<node>`. Chargehand commits for the worker with the repository's hooks off and signing off; the source repository and the shared checkout are never written. Nothing creates one yet.
- The sandbox a writing run's tests will execute in (ADR 0035): `sandbox-exec` on macOS, `bwrap` on Linux, behind one interface; profile field `sandbox` (`kind`, `network`, `env`). A command may write only in its workspace and a private temp directory, may not read credential locations, has no network unless allowed and gets a cut environment. Nothing runs in it yet.
- The verifier for a writing node (ADR 0035): the request's `context.verify` command, else the repository's own (`dotnet test`, `npm test` with a test script, `pytest` or `unittest`, `cargo test`, `go test ./...`), run in the sandbox with a timeout and an 8 KiB output tail. Nothing calls it yet.
- The `code` preset (goal 0.7, ADR 0035): one writer that edits files in its own clone and returns a branch. Chargehand runs the repository's tests in the sandbox after each answer, sends a failure back to the worker at most twice, commits `chargehand/<run>/writer` with hooks off and returns three artifacts: `branch` (where to fetch it), `diff` and `verification` (command, sandbox, network, exit code, attempts, output tail, changed build and test paths). A run that stays red fails with `verification_failed` and keeps the branch; a run with no test command completes with the open question "No test command found". `scripts/write-e2e.sh` checks it with a real model and the real sandbox ([Writing a branch](docs/guide/writing.md)). Workers still have no shell, nothing is pushed or merged, and a writing preset is refused with `sandbox_unavailable` where no sandbox exists.

## [0.6.1] - 2026-09-29

Two fixes found by the live checks of 0.6. A worker's citation of a service tool's reply now resolves, and a memory
mapping can carry where a run's citations were checked into the retain call's metadata. The breaking change and the
migration notes of 0.6.0 are unchanged. The one schema edit is the description of the `locator` field in `result/v1`, and
`Chargehand.Contracts` 1.3.1-alpha is published with it, because the package embeds the schema text.

### Added

- Retain placeholders `{repository}`, `{commit}` (12 hex characters) and `{locators}` (joined with `; `), so a memory mapping can
  send where a run's citations were checked as metadata (`profiles/example.json` and the guide's Hindsight entry do). Hindsight
  rewrites a retained item into a sentence without the commit; the metadata and the stored document keep it. A recall
  `results.text` template can read nested fields (`{metadata.commit}`). Mappings without the new placeholders send what they sent.

### Fixed

- A worker's citation of a service tool's reply now resolves. Workers cannot see message ids, so they cited the reply text as a `session_message` locator and the claim became an open question. A `session_message` locator that quotes at least 12 characters of a tool's reply in the session now resolves, and the task text says so when the run has services. Nothing changes without services; `result/v1` changes only in the locator's description.

## [0.6.0] - 2026-09-29

Breaking: the single-object form of `memory` in the profile no longer loads. Memory is a list of MCP providers now, and a
profile that still has the object fails with `memory is a list now` and a pointer to the guide. Move the memory
service's MCP endpoint into `mcp_servers` and list the provider under `memory`; Removed below has the migration, and a
profile without `memory` needs nothing.

Goal 0.6 ([roadmap](ROADMAP.md)): runs use your MCP services and memory. Memory is an ordered list of MCP providers, each
with a declarative mapping from recall, retain and invalidate onto the server's tools; recall asks all of them and
stacks the facts with the name of the memory each came from. With `retain` on, a run stores only the claims whose
citations resolved at its pinned commit. A preset can give its workers read-only tools from an MCP service, on Claude
Code and on OpenCode, and `chargehand extensions check` tests the mappings and grants against the servers' real tool
lists. With no server listed a run behaves as before. [Memory and services](docs/guide/memory-and-services.md) walks
through the setup. The release also closes a hole in the OpenCode runtime: workers could start a checkout's own MCP
servers and call any MCP tool. Project config is now disabled on the servers chargehand starts, and MCP tools are denied
unless a preset grants them (Security; restart an OpenCode server you started earlier).

There is no 0.5.0: goal 0.5's code shipped in 0.4.0, and what remains of its done-when is the maintainer's own use of
`/chargehand:change` on real tasks, which is still open. The one schema change is additive: `preset/v1` gains the
optional `services` field, and `Chargehand.Contracts` 1.3.0-alpha is published with it.

### Security

- OpenCode workers can no longer reach MCP servers. A preset's leading `* * allow` reached the tools of any server
  registered at the location, including write-shaped ones and OpenCode's MCP resource tools, and a checkout's own
  `opencode.json` could register a server whose command OpenCode started when a session was created there. Every
  OpenCode session's rules now end with `*_* * deny`. The server chargehand starts and `scripts/opencode-serve.sh` set
  `OPENCODE_DISABLE_PROJECT_CONFIG=1` and `OPENCODE_CONFIG_PROJECT_DISABLE=1`, so a checkout's `opencode.json`,
  `.opencode`, `AGENTS.md` and project skills are no longer read (workers stop receiving the checkout's `AGENTS.md`).
  Restart a server you started earlier with the script, and set both variables on one you start another way. The Claude
  Code runtime was not affected (ADR 0034).

### Added

- `mcp_servers` in the profile (ADR 0034): MCP servers by name, over Streamable HTTP, the older SSE transport
  (`"transport": "sse"`; `auto`, the default, tries Streamable HTTP and then SSE, and means Streamable HTTP for a worker's
  runtime) or stdio, with `{secret:item}` in header and environment values. Memory and a preset's `services` both read
  them, over one connection per server.
- Memory from any MCP memory server, several at once (ADR 0034). `memory` is a list of providers; each names an
  `mcp_servers` entry and maps recall, retain and invalidate onto that server's tools: argument templates with `{query}`,
  `{namespace}`, `{max_facts}`, `{text}` and the like, and a `results` mapping that says where the facts are in the answer
  (`path`, `id`, and `text` as a field, a template such as `{date}: {summary}`, or an ordered list of these). Recall reads
  a tool's structured content when the path finds an array there and its text block otherwise. An entry without a retain
  tool is recall-only, as an archive such as Chronicle is, and `namespace` defaults to the entry's name. A mapping that
  names an unknown server or placeholder, or misses its recall tool, fails when the profile loads.
- `services` on a preset's node kind (`preset/v1`, an additive field; ADR 0034): a server from `mcp_servers` and the tool
  names workers may call, exact or with `*` globs, never a whole server. At the start of a run chargehand connects,
  lists the server's tools and grants the ones named. A server, secret or tool that does not resolve is dropped, not fatal:
  `chargehand show` prints one `service` line each, with the span tags `chargehand.service.<name>.granted` and `.issues`.
  `as_sent.tools_sha256` covers the granted tools and is unchanged when there are none. No shipped preset lists services.
- Claude Code workers get the granted services (ADR 0020 addendum): the servers go in a private `--mcp-config` file, mode 0600
  in a fresh 0700 directory under the system temp directory, removed when the turn's process exits (also on failure or
  interrupt) and never on the command line or in a log; `--allowedTools` names exactly the granted tools, and the server's
  other tools are disallowed so they do not cost tokens. A granted server the CLI reports as not connected is an issue on
  the run (`not_connected: failed` in `chargehand show`), not a silent gap.
- OpenCode workers get the granted services (ADR 0034): before a run's first session at a location chargehand registers each
  granted server there (`PUT /api/experimental/mcp/{name}`), waits until `GET /api/mcp` says `connected`, and ends the
  session's rules with `*_* * deny` and one `<server>_<tool> * allow` per granted tool, so only those tools are callable
  and a server's other tools stay refused. The registration is named for the server and a per-process keyed hash of its
  config, so runs with other credentials do not change each other's server; it is shared by every run at the location
  that uses the same config, held by count, and removed when the last run ends (completed, failed or cancelled). A
  server that does not connect (`failed`, `needs_auth`, no answer in 30 s) is dropped, removed and reported on the run as
  `not_connected: <status>`; the run goes on without it. Presets that deny `*` still cannot reach a service on OpenCode
  (workers use its `execute` tool); no shipped preset lists services.
- `chargehand extensions check [--preset <name>] [--probe <query>]` (ADR 0034): connects every `mcp_servers` entry and lists
  its tools, then checks each memory mapping (the tool exists, every argument name is a property of its input schema,
  every required argument is set) and each preset's `services` (every named tool or glob matches a listed tool). It prints
  one line per item, `ok` or the problem with `; action:` and what to do, and reads every preset in `presets/` unless
  `--preset` names one. `--probe` also runs one real recall per memory and prints how many facts came back, never the
  facts. Exit 0 when nothing is wrong, 1 on a problem, 2 for a usage error or a profile that does not load. A line never
  holds a URL, a credential or an argument value.
- The guide page [Memory and services](docs/guide/memory-and-services.md): the `mcp_servers` transports, the Hindsight-through-a-gateway
  and Chronicle entries side by side, retain, preset services and what each runtime does with them, the check command,
  the migration from the old `memory` object, and the live checks made on 2026-09-29.

### Changed

- A `memory` list in the profile is now read (ADR 0034); until now it loaded and did nothing. `run`, `serve` and `mcp` build
  one memory stack from it: each entry becomes a source that calls its server's mapped tools over the same MCP
  connections a preset's `services` use, in list order, with the entry's limits, `retain` and `retain_tags`. A profile
  copied from `profiles/example.json` now tries its memory servers when a run recalls; a server or secret that does not
  resolve skips that memory, and `chargehand show` says why.
- Recall asks every memory at once and labels each fact with the memory it came from, and the prompt header says so:
  `- [hindsight] Deploys go through GitOps.` The chain block for recalled text is named `memory/recall/<name>`, one per
  memory that contributed, instead of `memory/recall`. Each memory keeps at most 10 facts and 4000 characters, cuts a fact
  at 600 characters, shows each fact as one line and gets 10 seconds by default (`timeout_seconds`); a fact two memories
  return appears once with both names. One recall per run and retain off by default are unchanged. A memory that throws,
  times out or answers what its mapping cannot read is skipped on its own; the others still contribute.
- `chargehand show` prints one line per memory with what it recalled and retained, or why it was skipped, from a new
  optional `extensions` report in the run record. The span tags for memory are per source
  (`chargehand.memory.<name>.recalled` and `.error`) instead of `chargehand.memory.recalled` and
  `chargehand.memory.error`. `result/v1` is unchanged.
- Retain keeps less, and what it keeps says where it was checked. With `retain` on, a run used to store the request text,
  the summary and every claim. It now stores only the claims that cite at least one `file` or `commit` entry that resolved
  at the run's pinned commit, each with its locators, under a first line that names the repository and the commit:
  `Repository: github.com/example/proj, commit 0123456789ab (citations checked at this commit)`, then
  `- <claim> [src/Api/Startup.cs:41-58] (confidence 0.90)`. No longer retained: the request text, the summary, claims
  that rest only on caller inputs, URLs or the session, claims whose citations did not resolve, claims whose text the
  error-text scrubber would change, and everything from a run without a repository (a draft run has no commit, so it
  retains nothing). The repository is the `origin` URL without scheme, user information, port and `.git`, or the
  directory name when there is no `origin`. A run that retained nothing says why in `chargehand show`
  (`no commit`, `no claim qualified`). There is no confidence floor.
- A command secret source that runs longer than 15 s is killed and the next source tried; the error says when one timed out.
- The release workflow lets one run wait per tag. GitHub delivered the v0.4.1 tag push twice, so two runs waited at the
  `release` environment's gate; a concurrency group per ref lets the later run cancel the earlier one, so one approval is
  asked for. The `mcp-registry` job is unchanged.

### Removed

- The Hindsight HTTP client (`HindsightMemory`) and the single-object form of `memory` (`backend`, `url`, `namespace`,
  `api_key_secret`, `max_tokens`, `retain`; ADR 0034). A profile that still has the object fails to load with
  `memory is a list now` and a pointer to the guide page. Migration: put the service's MCP endpoint (and its API key as a
  header) in `mcp_servers`, and list the provider under `memory` with the tool mapping, which
  `profiles/example.json` and the [guide page](docs/guide/memory-and-services.md) show for Hindsight seen through an MCP gateway:

  ```json
  "mcp_servers": { "memory-gateway": { "url": "https://<your MCP endpoint for the memory service>", "headers": { "Authorization": "Bearer {secret:<api-key-item>}" } } },
  "memory": [ { "name": "hindsight", "server": "memory-gateway", "namespace": "<your bank>", "tools": { "recall": { … }, "retain": { … } } } ]
  ```

  The `retain` flag moved from the object to the entry, and a recall entry takes the bank as `{namespace}` in its
  arguments. The tool names are the ones the MCP endpoint lists (a gateway may prefix them: `chargehand extensions check`
  prints the mapping's tool names and says which are missing). `profile/v1` needs no other change.

## [0.4.1] - 2026-09-29

Fixes found in use of 0.4.0 and the first pieces of goal 0.5, and the first version the MCP Registry can list, under
the name `io.github.Egoushka/chargehand`. Intake now sees the text of caller inputs, within a cap per input and a cap
in total; a failed worker session and a Claude Code error result always carry a cause; memory that times out or
answers garbage is skipped instead of failing the run; and the private-terms hook works in a linked worktree. No schema
changed, so `Chargehand.Contracts` stays at 1.2.0-alpha.

### Added

- The release workflow lists each new version in the MCP Registry (ADR 0027). A `mcp-registry` job runs after `release`
  when `NUGET_USER` is set: it stamps a copy of `.mcp/server.json`, waits until nuget.org serves the package README with
  the ownership line, and publishes with the registry's GitHub OIDC login. It holds no write token and needs no new secret.

### Changed

- The MCP Registry name is `io.github.Egoushka/chargehand`, with the owner spelled as GitHub spells it: the registry
  matches the namespace and the README's `mcp-name` line case-sensitively. The `Chargehand` 0.4.0 package has the
  lower-case line and cannot change, so 0.4.1 is the first version the registry can list.

### Fixed

- Intake saw a caller input's id and kind but not its text, so a run whose request carried its goal, diff and test
  output as `inputs` could stop with `ask`, asking for the inputs it had been sent. Intake now reads each
  input's id, kind, size and the first 2000 characters of its text, and the prompt says when an input was cut; the
  worker still gets every input whole.
- A worker session that ended `failed` with no assistant message and no error text (OpenCode drops the session before
  any model call when the model is one its server does not declare) returned a bare "worker ended failed" with a
  generic action, and the reason was only in the server's log. The error now says the worker runtime ended the session
  before any model call, and its `action` points at the runtime's log (for OpenCode, the server's) and names the model
  the run asked for, to check against the runtime's models and the profile's `models` map (or says no model was mapped).
- A Claude Code turn that ended in an error result with no `result` text (`error_max_turns`, `error_during_execution` and
  `error_max_budget_usd` carry `errors` instead) failed with no reason and was reported in OpenCode's terms, as a session
  ended before any model call. The error now names the result's subtype, turn count and `errors`.
- A request with many `inputs` still grew intake's prompt without bound, up to the request size limit. Intake now reads
  at most 8000 characters of input text in all, in request order; an input after that is listed with its id, kind and
  size and a note that its text is left out here, and the worker still gets every input whole.
- The `.private-terms` hooks work in a linked worktree. The list is gitignored, so a worktree never had a copy and every
  commit there aborted with "`.private-terms` is missing"; the check now falls back to the main worktree's list. A
  worktree's own file still wins, and with no list in either place the commit is still blocked.
- Memory failed open only on `HttpRequestException`. A provider timing out (`TaskCanceledException`, which `HttpClient`
  raises when its own timeout elapses) ended the run with an exception and no result, and a `JsonException` or
  `IOException` from a provider failed it. Recall and retain now skip the provider on any exception except the caller's
  own cancellation, and the run goes on without it.

## [0.4.0] - 2026-09-29

Goal 0.4 ([roadmap](ROADMAP.md)): chargehand runs with nothing configured. No profile file is needed, the worker
runtime is named or found on the machine, credentials come from environment variables or the CLI's own login, and the
orchestrator ships as a `dnx` tool that speaks MCP over stdio. The release now publishes that tool, `Chargehand`, to
nuget.org next to `Chargehand.Contracts`. The first pieces of goal 0.5 (the review preset, the Claude Code plugin and
the change skill) are on main and in this tag; the plugin's server needs the published package.

### Added

- `chargehand mcp` serves the `orchestrate` tool over stdio, so an MCP client starts it itself; its child processes get
  a closed stdin, and a caller receives the run id before its own timeout can fire.
- The orchestrator packs as a `dnx` tool, `Chargehand`, with an MCP server manifest (`.mcp/server.json`, ADR 0027),
  packed and started in CI by a smoke test. Prompts and presets are read from the install, and the run log goes to a
  per-user default when no profile names one.
- Runtime detection without a profile block: Claude Code connects on its own (and on the CLI's own login when no
  credential variable is set), and an own OpenCode server is started when the profile has no `opencode` block. OpenCode
  is pinned at 2.0.18.
- CI fails on a breaking change to a published schema since the latest release tag (additive changes only).
- The read-only `review` preset, the `chargehand` Claude Code plugin and its marketplace entry, and the change skill
  (`/chargehand:change`, one prompt to a reviewed change), with an end-to-end check and a user guide under `docs/guide`.
  A workflow that has chargehand review its own pull requests exists and stays off until `SELF_REVIEW=true`.
- ADR 0033, the CI policy: one required `ci-gate` check, prose-only pull requests skip the build jobs, CodeQL and
  Scorecard run weekly and on demand instead of on every pull request or push, and auto-merge is on.

### Changed

- Profile is fully optional (ADR 0026): `Profile.Load` defaults an absent file rather than throwing; `worker_root`,
  `default_preset`, `intake_model` and `prices` each have a default (a fixed directory outside `$HOME`, `cheap`,
  unset, and empty). `secret_store` is replaced by an ordered `secrets` list (env, then command templates as argv
  arrays, first success wins) — **a profile still carrying `"secret_store": "keychain"` needs a one-line migration**
  to `"secrets": [{"env": true}, {"command": ["security", "find-generic-password", "-s", "{item}", "-w"]}]` (see
  `profiles/example.json`).
- The worker runtime is chosen by a `RuntimeSelector`: a profile's `runtime` field or `CHARGEHAND_RUNTIME` wins,
  then the profile's only runtime block (`opencode` or `claude_code`), so an existing profile keeps its runtime
  (ADR 0032); otherwise `PATH` is probed for a known agent CLI. No agent CLI found is `runtime_unavailable`; more than one found
  is a new `runtime_ambiguous` error naming every CLI seen — no silent priority order.
- `IPriceTable.PriceUsd` returns `decimal?`: an unpriced model's cost is unknown, not a silent `$0`. The run's USD
  cap cannot fire on an unpriced model; the per-node-kind token budgets remain the real guardrail. `result/v1`'s
  `usage.usd` may now be `null` for the same reason.
- `ClaudeCodeWorkerRuntime` and `OpenCodeWorkerRuntime` accept an unset model (`NodeSpec.Model`, `GenerateAsync`) and
  fall back to the runtime's own default instead of requiring one.

### Fixed

- Every `result/v1` error now names a concrete `action`, and `repository_not_allowed` says how to fix it; a run with no
  `repository_roots` allows the directory it was launched in.
- With no profile, or a `models` map that does not name a preset's placeholder model (`provider/worker-model`,
  `provider/small-model`), the placeholder reached the agent CLI and every run failed. An unmapped placeholder is now
  unset, so the runtime uses its own default model, labelled `auto` in the prompt chain as for intake.
  Real `provider/model` ids in a preset still pass through.
- A call the runtime reports no model for (Claude Code's compactions; every call on the runtime's default model) cost
  a silent `$0`. It is now priced at the node's model, and with no model on either its `usage.usd` is `null`.
- A worker that ended `failed` returned "worker ended failed" and nothing else: the provider's reason was in the
  session messages and never reached the result. The message now carries it (`worker ended failed: There's an issue
  with the selected model …`), scrubbed and cut at 300 characters, and every node failure — failed, rate limited,
  over budget, past its deadline, no valid result — carries an `action` saying what to change or where to look
  (`chargehand show <run id>`).
- A worker that wrote `status: failed` in its own result block (a change request on a read-only preset, which runs as
  an answer when intake's action is not in the preset) returned a failed result with no `error`, against ADR 0022. It
  now carries `internal` with "the worker reported failed: <summary>" and an action.
- The release workflow published only `Chargehand.Contracts` to nuget.org, so the `Chargehand` dnx tool package the README,
  the plugin and the MCP Registry manifest name was never available. It now packs and pushes the tool too, each package
  only when its version is new there.
- Prompt CI blocked every pull request that adds a preset with its eval cell as uncovered: the gate reads cells from
  main, which does not have the new cell yet. A file the base lacks now passes when a cell in the change's own
  `evals/cells.json` names it (`--change-cells-file`); tolerances and existing files stay on main's cells. With
  `--allow-uncovered`, the status said "no prompt or preset change"; it now names the files that passed without a cell.
- Prompt CI crashed with an unhandled 404 when an eval cell's Langfuse dataset did not exist yet (a new cell, before
  its first `eval push`). A missing dataset now has no items, so the gate blocks with "0 items; the gate needs at
  least 8" instead.

### Security

- The `.private-terms` denylist fails closed. A malformed pattern made `git grep` exit 128, which the `pre-commit`
  hook read as "no match" and let the commit through; the commit is now blocked with the error. The `commit-msg` hook
  checks the message against the same list, except your own `Signed-off-by` and the diff `commit -v` adds
  (`scripts/check-private-terms.sh`).
- Prompt CI evaluates the commit its run was approved for (the event's head, passed as `HEAD`) instead of reading the
  pull request's head when the gate job starts, so a push between an approval and the job no longer runs
  unreviewed prompts on the eval runner.
- `Scrub` no longer redacts ordinary words that merely contain a key-like substring (`task-spec`, `disk-cache`,
  a stray "user:") — the `sk-` pattern needed a left boundary. It now also catches shapes 0.2.2 missed: LiteLLM's
  "Key Hash (Token) =", compound identifiers (`team_member`, `user_id`, `organization`, `user_api_key_alias`),
  camelCase `apiKey`, `Authorization: Basic`, and spend figures in scientific notation. Fixes an ordering bug where
  a `token: Bearer <secret>` message redacted the word "Bearer" instead of the secret.

## [0.3.0] - 2026-09-28

Goal 0.3 ([roadmap](ROADMAP.md)) closed: every change to this repository is now tracked on a project board,
released through an automated pipeline, and checked by CI before it merges. Delivered across 0.2.1–0.3.0: gateway
error redaction (0.2.2), the CHANGELOG-to-GitHub-Release pipeline with dormant nuget.org publishing (0.2.2),
`scripts/check.sh` and the schema-change guard hook (0.2.2), OpenSSF Scorecard, dependency review, CodeQL and a
dormant SonarQube job (0.2.2), stricter analyzers (0.2.2), and this release's own repository hygiene (topics,
stale-branch cleanup). This bump closes the goal and moves the roadmap to 0.4.

## [0.2.2] - 2026-09-28

### Security

- A failed result no longer repeats the model gateway's error text verbatim: keys, key aliases, bearer tokens and
  spend figures are redacted before a message reaches `result/v1` or the run log. A refusal over a spend budget says
  what to do in `error.action`.

### Added

- `ROADMAP.md`; `scripts/check.sh`, the one check before a push; a Claude Code hook that asks before an edit to a
  published schema major.
- OpenSSF Scorecard, dependency review on pull requests, CodeQL, and a SonarQube Cloud job that runs once the project
  is connected; badges in the README.

### Changed

- A tag `v<Version>` also creates the GitHub Release, with the version's section of this changelog as its notes, and
  publishes `Chargehand.Contracts` to nuget.org when its version is new there. Publishing waits for an approval in the
  `release` environment; a tag whose version has no section here fails before anything is published, and CI fails a
  version bump that comes without one.
- The Prompt CI runner ADR is now ADR 0025 (two ADRs had number 0022); ADRs 0024 and 0025 are accepted.
  `TRADEMARK.md` says how the name may be used, and contributions are signed off (DCO).
- The build runs the .NET analyzers at `10.0-recommended`. Parsing and formatting no longer depend on the machine's
  locale: a hand score such as `0.8` parses where the decimal separator is a comma.

## [0.2.1] - 2026-09-28

### Fixed

- A worker clone of a repository on another mount (a container's repository mount and its work volume) failed with
  `checkout_invalid`: `git clone --local` cannot hard-link across mount points. The clone now copies when linking
  fails.

## [0.2.0] - 2026-09-28

### The server runs on a private network

`chargehand serve` can bind a private-network address: `http.listen` sets it and `http.allowed_hosts` names the host
clients use; without allowed hosts it refuses to start beyond loopback. A `Dockerfile` builds the server with the
Claude Code runtime, and a tag `v<Version>` publishes `ghcr.io/<owner>/chargehand:<Version>`. See ADR 0024.

### Prompt CI runs on its own

A pull request that changes `prompts/` or `presets/` is gated on a self-hosted runner, not by a manual run of
`scripts/prompt-ci.sh`. A fork's pull request or a preset change waits for an approval in the `prompt-ci-review`
environment. The runner holds one eval profile per runtime and a default; a `prompt-ci:<runtime>` label picks another
when someone with write access adds it. The manual run still works. See ADR 0025.

### Added

- `result/v1` has an optional `error` on failed results (ADR 0022): a fixed `code`, the `message`, `retryable`, and
  an `action` when there is something to do, such as the command that starts the OpenCode server. Clients branch on
  the code instead of parsing `summary`. The schema stays v1; `Chargehand.Contracts` is 1.2.0-alpha with the
  `ResultError` record and the `ErrorCode` enum.

### Changed

- Preset blocks `default` 0.5.0, `cheap`, `thorough` and `strict` 0.4.0 say "read the files that answer the task"
  instead of "read every file the task needs". Worker prompt 0.3.0 keeps the completeness rules. In the phase 3
  benchmark the old wording cost 28% more than a plain session through extra reading; the new one costs the same
  and stays more complete (docs/benchmarks.md).
- Prompt CI scores a worker item with a fact checklist (new optional `facts` and `wrong` in an eval item's
  `expected`) by the share of its reference facts the answer states, less 0.25 per known false statement it
  repeats, instead of by its claim count. A fact judge, one generate call on the intake model with its prompt in the
  trusted build, reads the answer and names the facts it states (ADR 0019).
- Worker prompt 0.3.0 asks for the whole task, one claim per item and a citation for every file relied on, and
  no longer caps the summary at 120 words. Preset blocks `default` 0.4.0, `cheap`, `thorough` and `strict` 0.3.0
  say "read every file the task needs" instead of "prefer the smallest set of files". The phase 3 blind verdict
  failed on completeness in 3 of 3 pairs at equal exploration: the worker read files it then left out, and merged
  several services into one claim.
- A worker interrupted at its token budget (`budget.max_input_tokens`) gets one turn to answer from what it has
  read, with room for three calls at the last call's context on top of what it spent; the result lists the stop in
  `open_questions`. It used to fail with nothing. The USD cap still ends a node without that turn (ADR 0010).
- A request's repository may sit anywhere under the profile's new `repository_roots` (default: `worker_root`; `/`
  allows any). The worker reads a clone of it at the pinned commit under `worker_root`, reused per source and commit,
  so the source's uncommitted and ignored files never reach it and the worker stays outside the home. A short commit
  hash is enough. A checkout that tracks a file the preset denies reading is still refused (ADR 0023).
- `chargehand run` prints a failed `result/v1` with `error` when it cannot connect to the runtime (server not
  running, binary missing, another version), instead of ending with an unhandled exception.
- The eval gate retries an arm whose result has code `rate_limited`, instead of matching "rate limit" in its summary.

### Prompt CI calibration

`cheap/worker`'s items pinned a checkout that tracks encrypted env files the `cheap` preset denies reading, so since
workers refuse such a checkout (0.1.0) every arm of its A/A failed at $0. The items now pin a commit of that checkout
without those files; every reference file is unchanged. The A/A there, the first since workers lost the shell, 12
items: quality -0.046 (t -0.63), cost +13% (t 1.82), pass, $0.11; 5 items differ, by 0.75, 0.27, 0.25, 0.20 and 0.02.
Claims per item: 6.25 against 5.97 in the runs that seeded `reference_claims`, 20 of 24 arms within their range, so
`reference_claims` stays.

## [0.1.0] - 2026-09-27

First usable release. chargehand turns a request into a Task Spec, runs it on one or more coding-agent sessions, and
returns `result/v1` with evidence behind every claim. You call it from the CLI, over HTTP, or as an MCP tool. It
covers roadmap phases 3 to 5 (v0, v1, v2). Benchmark and exit-check numbers live in
[docs/benchmarks.md](docs/benchmarks.md).

### Added

#### Runs and actions

- `chargehand run` reads `request/v1`, runs intake to get a Task Spec, and returns `result/v1`.
- Intake picks one action. `answer` runs one worker session. `split` runs 2–4 read-only subtasks as a task graph.
  `deny` returns status `denied` with a reason and an unblock condition. `ask` returns `needs_input` with questions.
  `improve` returns `needs_input` with an improved request and an inline diff artifact. When the preset does not allow
  the chosen action, the run falls back to `answer`; the run log records both.
- Split runs (ADR 0017) execute at most 2 nodes at once and pass upstream contracts to dependents; a failed node stops
  only its dependents. Later nodes fork the first node's session before its first message, so they read its system
  prefix from cache. Node contracts merge deterministically, with evidence ids prefixed by the node id.
- The worker node fixes its instruction entries before the first prompt. A watcher rejects permission requests and
  interrupts above the run cap. Each node gets a 15-minute deadline and one repair turn each for the result schema
  and for evidence.
- Per-node budgets: the watcher interrupts a node above `budget.max_input_tokens` and compacts it mid-turn above
  `compaction.trigger_tokens` (new optional `preset/v1` field).
- The evidence resolver checks claims against git at the pinned commit, session messages, caller inputs, URLs seen,
  and the session diff. Claims that do not resolve move to `open_questions`.
- `request/v1` gains optional `context.approved` to run past a preset's approval thresholds.

#### Presets

- `default`, plus `cheap` 0.1.0, `thorough` 0.1.0 and `strict` 0.1.0, all read-only. `strict` asks for approval above
  risk `low` or an estimate above $0.50.
- `draft` 0.1.0 (node kind `draft`) writes a draft for a program caller from the caller's own inputs, with no
  repository and no tools, and returns it as an inline artifact. New optional `preset/v1` field `checkout: false`.

#### Callable interface (ADR 0018)

- `chargehand serve` hosts HTTP and MCP on 127.0.0.1. Every route requires a bearer key from the secret store
  (profile `http`), and bodies cap at 1 MB.
- `POST /v1/runs` takes `request/v1`. If the run finishes within `Prefer: wait=N` (default 10 s, at most 60) you get
  `200` with `result/v1`, otherwise `202` with `run-status/v1`.
- `GET /v1/runs/{id}` answers `202` while running, `200` when finished, `410` when the owning process died, `404`
  when unknown. `GET /v1/runs/{id}/events` streams `accepted`, `started`, `intake`, `node_started`, `node_finished`
  and `run_finished`.
- Runs outlive the request and execute one at a time. The server holds at most 10 unfinished runs, then answers 429.
- MCP at `/v1/mcp` (Streamable HTTP, MCP 2026-07-28 with hybrid sessions, C# SDK 2.2.0) exposes tool `orchestrate`,
  with `inputSchema` `request/v1` and `outputSchema` `result/v1`. Long runs become tasks through the tasks extension.
  An `ask` becomes `input_required`, and the answers resend the request as a child run.
- The run log's new `start` record (request, trace id, owning pid, parent run) makes it the store the CLI and the
  server share. `show` prints runs that are still running or were lost. Concurrent appends take a lock file, and
  readers skip a half-written last line.
- `Chargehand.Contracts` 1.1.0-alpha adds schema `run-status/v1` and `RunStatus`; `PromptBlock.Create` hashes a caller
  block by request/v1's rule.
- `samples/ContentEngineCall` builds the content engine's call (ADR 0014) from `Chargehand.Contracts` alone.

#### Worker runtimes

- OpenCode V2 adapter pinned to 2.0.16: a hand-written client and `IWorkerRuntime` implementation, with a retry for
  the `Model unavailable` race while a location boots. `scripts/opencode-serve.sh` starts the orchestrator's own
  server; `profiles/opencode.example.json` configures it.
- Claude Code adapter (ADR 0020, `Chargehand.ClaudeCode`) runs `claude -p` with stream-json, one process per turn,
  `--bare` and `dontAsk`, and translates preset rules to `--tools`, `--allowedTools` and `--disallowedTools`. Profile
  `claude_code` selects it instead of `opencode`, which becomes optional. It takes `version`, `binary`, and exactly
  one of `api_key_secret` (per-token API billing) or `oauth_token_secret` (a `claude setup-token` subscription
  token). Optional `base_url` routes workers through an Anthropic-compatible gateway.

#### Prompts, evals and routing

- Prompt registry in `prompts/` with SemVer front matter and normalised sha256. Every call records its
  `prompt_chain`; `chargehand prompts sync` mirrors blocks into Langfuse prompt management.
- Prompt CI (ADR 0019): `chargehand eval seed|push|gate` with cells `cheap/worker`, `draft/draft` and `intake` in
  `evals/cells.json`, items in Langfuse datasets, and deterministic scores. It runs base and change in pairs and gates
  on quality tolerance T (0.10) and cost tolerance C (+15%, or +30% for `cheap/worker`), with a declared-trade
  override. Results land as Langfuse dataset runs and scores.
- `scripts/prompt-ci.sh <pr>` runs Prompt CI on the owner's machine and posts commit status `prompt-ci`.
  `.github/workflows/prompt-ci.yml` marks pull requests that change no prompt or preset.
- `chargehand routes` prints a routing report per preset, node kind and model (runs, score, tokens, cache rate, cost,
  latency) and only suggests changes. `chargehand score` records a hand score for a run.

#### Memory

- `IMemoryProvider` (recall, retain, invalidate; scope is backend plus namespace) with an adapter for a self-hosted
  Hindsight service (HTTP API 0.10.0). With profile `memory` set, a run recalls facts once and appends them to each
  node's prompt as unverified context (chain block `memory/recall`, source `runtime`). A failed recall leaves the run
  without them. `retain` stores completed runs and stays off by default.

#### Observability

- JSONL run log with tokens, cache rate, own-table cost and latency per call. `chargehand show` summarises a run;
  `chargehand reconcile` joins calls to exported gateway spend rows by model, token counts and time.
- `chargehand cache <run>` reports cache reads, writes and hit rate per call. Per node, it names the first instruction
  entry, prompt block or as-sent field that broke a prefix that should have been shared, plus a checklist for breakers
  outside the prompt chain. Call records carry the fork parent and hashed instruction entries.
- OTLP traces (run → intake → node → call) carry `chargehand.prompt_chain` and the OpenCode session id.
- `telemetry.usage_on_spans` (ADR 0021) puts Langfuse usage and cost on call spans that no gateway records, such as
  Claude Code on a subscription. Off by default; with a LiteLLM gateway, ADR 0012 still applies.

### Changed

- Intake prompt 0.2.0 says when to split. 0.3.0 asks only when a required fact is missing and the caller's inputs do
  not supply it; 0.2.0 asked a program caller for facts it had already sent (1 of 2 A/A runs).
- Presets `default` 0.4.0, `cheap` 0.2.0 and `thorough` 0.2.0 drop approval thresholds. Intake's estimate is
  uncalibrated (ADR 0005): in the phase 4 benchmark it guessed up to $0.35 for runs that cost about $0.01 and stopped
  one with `needs_input`. `strict` keeps its thresholds.
- Preset `default` 0.2.0 allows `rg` (with `--pre` denied). Worker prompt 0.2.0 makes the final message the JSON block
  only.
- Prompt CI scores a worker as grounding times completeness. Completeness is claims kept over the item's
  `reference_claims` (new optional field in an eval item's `expected`), capped at 1; `eval seed` proposes the count
  from the seeding run. Grounding alone passed #5, which cited the same code in fewer, wider claims.
- `cheap/worker` drops the phase 3 reference question (now 12 items). At `cheap`'s 400k-token node budget it failed
  in about half its runs, and one flip moved an A/A's mean by up to 0.08.
- The orchestrator computes an inline artifact's sha256 itself and caps its content at 64 KiB.

### Fixed

- A run that throws (bad checkout, unknown preset, no valid Task Spec) ends with a failed `result/v1` and a run record
  instead of an exception.
- Caller inputs appear in the task text as `- id "<id>" (<kind>): <text>`. The old `[id]` form led workers to cite
  `[id]`, which matched no input. A failed `input` reference now lists the ids that exist, and intake sees each
  input's id and kind.
- OpenCode's stateless generate (intake) retries one 503, as ADR 0011 prescribes for the masking proxy.
- Prompt CI retries an arm that hits a rate limit (after 15, 30 and 60 s) and stops the gate if the limit persists,
  instead of scoring the item 0.
- Prompt CI stops the gate (status `error`) when a worker or draft arm fails with zero usage, meaning a refusal before
  any model call. Such arms used to score 0 on both sides and pass as no change. Intake arms report no usage and stay
  exempt.

### Security

- `chargehand serve` requires its bearer key on loopback too, since any local process can reach the port. It accepts
  only a loopback Host header (against DNS rebinding) and sends no CORS headers.
- Prompt CI never builds or runs a pull request's code. The runner uses the trusted checkout's build, and the pull
  request contributes only `prompts/` and `presets/`. Those still steer a worker, and a preset can grant tools, so a
  fork's pull request or any preset change runs only after the owner reads the diff (`--reviewed`). A symbolic link
  among them stops the run. GitHub holds no model, gateway, tracing or tailnet key; evals use their own OpenCode
  server and a spend-capped gateway key limited to the small model.
- Presets `default` 0.5.0, `cheap` 0.3.0, `thorough` 0.3.0 and `strict` 0.2.0 remove the shell tool from workers, and
  their prompt blocks point to the `grep` and `glob` tools instead. OpenCode matches shell rules against each
  command's source text, so the `git grep*`, `git log*` and `git show*` allows let a worker execute programs
  (`git grep -O`) and write files (`--output`), and quoting or naming a file slipped past the `rg` denies.
- A run fails before any worker session starts when its checkout holds a file the preset denies reading (`*.env`,
  `*.env.*`), ignored files included. OpenCode's `grep` tool checks permission against its pattern rather than the
  path it searches, and it reads ignored files named by path or `include` glob, so `read` rules alone could not keep
  those files out.
- Preset `default` 0.3.0 denies reading `*.env` and `*.env.*`, and denies `rg --no-ignore` and `rg -u`. The session
  ruleset's leading allow had overridden OpenCode's own ask-before-reading-`.env` rule, so a worker could read secret
  files without asking.

## [0.1.0-alpha] - 2026-09-26 (not tagged)

### Added

- Repository bootstrap: license, contribution guide, security policy, CI with secret scanning,
  Conventional Commits check, ADR template.
- JSON Schemas `task-spec/v1`, `result/v1` and `preset/v1` with fixtures and schema tests.
- Architecture decision records 0000–0016.
- Generated OpenCode API map and a contract test against the checked-in spec.
- Solution skeleton: CLI entry point, intake, `IWorkerRuntime` port, OpenCode adapter interface,
  contract validator, evidence resolver, prompt registry, telemetry and run log interfaces.

[Unreleased]: https://github.com/Egoushka/chargehand/compare/v0.8.6...HEAD
[0.8.6]: https://github.com/Egoushka/chargehand/compare/v0.8.5...v0.8.6
[0.8.5]: https://github.com/Egoushka/chargehand/compare/v0.8.4...v0.8.5
[0.8.4]: https://github.com/Egoushka/chargehand/compare/v0.8.3...v0.8.4
[0.8.3]: https://github.com/Egoushka/chargehand/compare/v0.8.2...v0.8.3
[0.8.2]: https://github.com/Egoushka/chargehand/compare/v0.8.1...v0.8.2
[0.8.1]: https://github.com/Egoushka/chargehand/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/Egoushka/chargehand/compare/v0.6.1...v0.8.0
[0.6.1]: https://github.com/Egoushka/chargehand/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/Egoushka/chargehand/compare/v0.4.1...v0.6.0
[0.4.1]: https://github.com/Egoushka/chargehand/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/Egoushka/chargehand/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Egoushka/chargehand/compare/v0.2.2...v0.3.0
[0.2.2]: https://github.com/Egoushka/chargehand/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/Egoushka/chargehand/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/Egoushka/chargehand/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Egoushka/chargehand/releases/tag/v0.1.0
