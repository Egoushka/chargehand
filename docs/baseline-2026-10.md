# Baseline, October 2026: where driven tasks spend time, where quality is lost, who can use chargehand

Measured on 2026-10-23 before any change of the next phase, so later changes have a number to move. Sources: the
production server's run records (`records/<run>/events.jsonl`), its learning log (`learning.jsonl`), its run log, the
GitHub state of every pull request the tasks opened, and a walk through the README on a clean machine. The repositories
behind the driven tasks are private except chargehand itself, so they are named by what they are, not by name. A
*driven task* is one goal run as a Claude Code session in a container that ends in a pull request
([ADR 0039](adr/0039-driven-writing-sessions.md)).

Sample: the last 30 driven tasks that started a session (2026-10-19 to 2026-10-22), and for context all 62 since
2026-10-14. 34 more tasks never started a session (29 because a task they depended on failed); they are left out of the
time tables. The server ran 0.17.0 throughout, so the fix that verifies chargehand's own branches with
`scripts/check.sh` (0.18.0) is not in this data.

## 1. Where the time goes

Each phase is measured from run events: batch start, session container start, the session's progress events (each
carries the stage the `change` skill reports: research, write, test, review), the verifier's own "tests passed in N s",
push, pull request, integration and task end. A task with continuations
([ADR 0044](adr/0044-driven-sessions-continue-after-a-stop.md)) counts all its sessions.

| phase | tasks with it | median min | p80 min | sum h | share of summed task time |
|---|---:|---:|---:|---:|---:|
| waiting for a parallel slot or a task it depends on | 30 | 0.0 | 0.0 | 0.0 | 0% |
| start: planning, clone, checkout, memory admission, container start, slot wait | 30 | 0.4 | 3.5 | 3.9 | 17% |
| **session: model turns plus the session's own restores, builds and tests** | 30 | **28.4** | **52.8** | **17.3** | **76%** |
| of which stage research | 25 | 6.0 | 8.5 | 2.8 | 12% |
| of which stage write | 27 | 4.1 | 10.5 | 3.0 | 13% |
| of which stage test | 18 | 6.4 | 29.7 | 4.6 | 20% |
| of which stage review | 25 | 8.6 | 21.1 | 5.3 | 23% |
| of which no stage reported yet | 30 | 2.1 | 4.0 | 1.4 | 6% |
| handover before the verifier: bundle, diff scan, container start | 21 | 0.6 | 0.7 | 0.3 | 1% |
| verifier in a fresh container: restore, build, test | 21 | 1.4 | 4.6 | 0.7 | 3% |
| push and open the pull request | 16 | 0.0 | 0.0 | 0.0 | 0% |
| integration: update the branch, mark ready, enable auto-merge | 15 | 1.7 | 3.8 | 0.6 | 2% |
| CI watch while the task holds its slot | 15 | 0.0 | 0.0 | 0.0 | 0% |
| **batch start to task end** | 30 | **44.0** | | **22.9** | 100% |

The start row's 3.9 h is almost all slot wait: 9 tasks of one batch shared 4 parallel slots, and a 10th waited in a
batch of 4 with 3 slots. The 17 tasks that waited for neither a slot nor memory paid a median of 18 s before their
container ran; the 7 that waited for memory admission, a median of 82 s.

Beyond the task, measured from GitHub:

| after the task | n | median | range |
|---|---:|---:|---:|
| pull request open to merged, when chargehand's integrator merged it | 2 | 2 min | -1 to 5 min |
| pull request open to merged, when a person merged it | 6 | 94 min | 15 to 359 min |
| pull request blocked by a failing required check, nobody acting (still open on 2026-10-23) | 4 | | |
| a fix on chargehand's own `main` to its release | 38 commits | 5.4 h | p80 12.0 h |
| a release to the production server (Renovate pin, merged by the owner) | 6 releases | 52 min | 14 to 227 min |

The hypotheses in the phase prompt, tested:

| hypothesis | verdict | evidence |
|---|---|---|
| The human and release chain dominates wall time | PARTIALLY CONFIRMED | Where a person merges, the wait after the task (median 94 min) is twice the task (44 min). Where the integrator merges, it is 2 min. A fix to chargehand itself reaches production after a median 5.4 h plus 52 min. Inside the task, the session is 76%. |
| Cold restores are paid twice (session and verifier) | CONFIRMED, small | The production profile sets no `driven.services.caches`, so every session and every verifier restores cold ([ADR 0048](adr/0048-shared-services-for-driven-sessions.md) built the caches; they are off). The verifier is 3% of task time (chargehand: 253 to 303 s, of which restore is UNKNOWN). The session's own restores are inside the test stage (20%) and cannot be separated without transcripts: UNKNOWN. |
| Stalled sessions burn the most time (CHARGEHAND-261) | CONFIRMED | 13 of 30 tasks needed a continuation. They used 63% of all session time (median 42 min of session against 16 min for one-session tasks) and 2 of the 13 merged, against 6 of 17. |
| The intake model call is pure latency for callers that send a full spec (CHARGEHAND-274) | CONFIRMED, small | Driven batches skip intake. On 455 read-only runs since 2026-10-08 the intake call took a median 9.2 s (p80 15 s) of a median 247 s run: 5.8% of summed run time. |

Two findings the prompt did not name:

- **The CI watch dies on its first network error, so the CI fixer almost never runs.** Of 11 tasks that reached the
  watch ([ADR 0046](adr/0046-integrator-and-ci-fixer.md)), 10 ended it within 4 minutes with "the pull request's state
  could not be reached: An error occurred while sending the request". One poll failing ends the watch; there is no
  retry. The fixer started once in 62 tasks. Four chargehand pull requests now sit blocked on failing tests that a fixer
  attempt is designed for.
- **Labelled issues that wait for a slot are forgotten, then refused.** On 2026-10-22, 27 issues of chargehand
  were labelled at 12:23. The server logged "waiting: max_concurrent" for the waiting ones on every poll until 14:07.
  From 14:10 it never looked at them again: from 15:23 it polled every 2 minutes with no triggered batch running and
  started nothing. The owner labelled them again at 19:35 and 21:21; three and one started, and the rest were refused
  by `max_starts_per_day` (10 in any 24 hours) and marked handled, so a third label is needed. Why the waiting issues
  stopped being read after 14:10 is UNKNOWN (the deferred path in `GitHubIntake.LabelTriggersAsync` does not mark them
  seen; the server restarted at 14:10). When measured, 18 of the 27 had never started.

## 2. Where quality is lost

How the last 30 started tasks ended (pull request state read from GitHub on 2026-10-23):

| end | tasks |
|---|---:|
| merged without edits | 5 |
| merged after a person's edits | 3 |
| pull request open, blocked by a failing required check | 4 |
| pull request open, waiting for a person (auto-merge not allowed on the repository) | 1 |
| partial draft pull request (stopped by the turn or time limit) | 2 |
| pull request closed without merging | 1 |
| no pull request: the verifier's command was not in the image (exit 127) | 5 |
| no pull request: the change edits CI configuration, which chargehand refuses to push | 3 |
| no pull request: the session stopped to ask a question | 2 |
| no pull request: the push was rejected (a network timeout, a conflicting base) | 2 |
| no pull request: no test command was found | 1 |
| no pull request: the session printed no usable report | 1 |
| **merged** | **8 of 30 (27%)** |

The categories the prompt asked for, over the same 30:

| category | count | what was seen |
|---|---:|---|
| unverified: verified green by a command that tests nothing | 4 | chargehand's verifier command was `dotnet format && dotnet build`, set in the production profile. All four pull requests failed `dotnet test` (3) or the web tests (1) in CI. `scripts/check.sh` would have caught three of them; the fourth only with Node in the image. |
| unverified: verification could not run | 6 | One infrastructure repository's command is `make ...`; the session image has no `make`, so every verifier exited 127 (5 failed, 1 partial). The driven path counts that as a failure, so nothing unverified was pushed, but each session's work was lost. The benchmark scored the same case as a pass until CHARGEHAND-248. |
| stalled (needed a continuation) | 13 | See section 1; 2 of 13 merged. |
| junk in the branch | 5 of 26 pull requests | `WIP: session stopped by turn_limit after 81 turns` commits from the salvage snapshot reached the branch; one of them fails the repository's commit-message check. One branch committed NuGet scratch files and untracked them in a later commit. A file-name scan of the final changed paths found no temporary files. |
| work thrown away at the handover | 3 | The session edited `.github/workflows/`; the handover refused to push after the whole session had run (5 of 62 overall). |
| human-edited after review | 3 of 8 merged | The learning log's `merged_edited`: the head at merge differs from the last commit chargehand pushed. All three (two pull requests) were the owner merging the base branch in to resolve conflicts; none changed the code chargehand wrote. |
| pull request title or commits break the repository's own rules | 4 of 4 on chargehand | Titles were plain sentences; the repository's `title` check requires Conventional Commits. |

**Top three causes of lost quality, by count over the 30:**

1. **The environment does not match the repository** (11 tasks, 37%): the verifier cannot run (6), runs a command
   that tests nothing (4), or finds none (1). This is the problem Step 2 of the phase targets.
2. **Stalls and continuations** (13 tasks, 63% of session time, 15% merge rate).
3. **Work refused after it was done** (5 tasks: CI configuration 3, push rejected 2), where a check before the session
   or a retry would have kept it.

## 3. Who can use it today

The README quick start, followed literally in fresh containers (`mcr.microsoft.com/dotnet/sdk:10.0`, `node:22`) on
2026-10-23 at `f32be60c`, with no homelab, no tracker, no gateway and no model credential:

| step | time | result | what the stranger sees |
|---|---:|---|---|
| clone | 3 s | ok | |
| `dotnet run --project src/Chargehand.Cli -- run < request.json`, cold | 40 s | exit 1 | `no agent CLI found on PATH (claude, opencode)`, with what to do next |
| install Claude Code at the pinned version | 54 s | ok after installing Node by hand | the README does not say how, or that Node 22 is needed (`EBADENGINE` on Node 18) |
| run again, not signed in | 7 s | exit 1 | `claude is not signed in and no Claude Code credential is set`, with what to do next |
| `dnx Chargehand@0.18.0` (the latest GitHub release) | 1 s | fails | `Version 0.18.0 of package chargehand is not found`: the release workflow is waiting for approval, so nothing was published |
| `dnx Chargehand@0.17.0 -- mcp`, no `claude` on `PATH` | 7 s | crashes | an unhandled exception and a stack trace; an MCP client sees only a closed connection |
| the same with `claude` but no credential | 4 s | crashes, exit 134 | the server never answers `initialize`, so the client lists no tools |
| `docker pull ghcr.io/egoushka/chargehand:0.18.0` (the compose default) | 1 s | not found | |
| `docker pull` of 0.17.0 on Apple silicon | 1 s | fails | `no matching manifest for linux/arm64/v8`: the images are amd64 only |
| server and session images, amd64 | 46 s, 53 s | ok | 1.6 GB and 2.1 GB on disk (408 MB and 544 MB compressed) |
| `docker compose config` | | fails | `CHARGEHAND_API_KEY is missing a value` (expected; the guide says to set it) |
| without .NET installed | | fails | the README links no .NET 10 install |

No step reached a model answer, which needs a signed-in Claude Code; that is expected and cannot be removed. Within
that limit, a stranger who has Claude Code signed in still meets: an unpublished release that every default points
at, a crash instead of a message from the MCP entry point, amd64-only images, and no install line for .NET or Node.
The core guides (README, quickstart, install, MCP) name no private resource; the driven guide assumes a GitHub App,
a model gateway, Langfuse and the maintainer's server in about ten places (`docs/guide/driven.md`), which is fine for
an advanced guide and wrong for a first run. `README.md` still says users: 1.

## What this baseline asks of the next changes

| number | now | measured by |
|---|---:|---|
| merged share of started driven tasks | 27% (8 of 30) | this note's section 2, rerun |
| tasks lost to the environment (verifier cannot run, tests nothing, none found) | 37% (11 of 30) | section 2 |
| continuations' share of session time | 63% | section 1 |
| CI watches that end on a transient error | 10 of 11 | run events, `integrated` |
| median session time per task | 28 min | section 1 |
| minutes from a clean machine with Claude Code signed in to a first answer | not reachable at 0.18.0; at 0.17.0 the MCP server lists its tools 8 s after `dnx` starts, and the time to an answer is UNKNOWN (no run with a real credential was made) | section 3 |
