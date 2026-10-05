# 0039. Driven writing sessions

- Status: proposed; decisions 1 to 4 were taken by the maintainer, the rest are the proposer's
- Date: 2026-09-30

## Context

The `code` preset (ADR 0035) writes a branch that passes its tests, but a worker there has no shell and chargehand never pushes. The
`change` skill (`plugins/chargehand/skills/change/SKILL.md`) does the whole job (research, write, test, review, at most 2 fix rounds)
inside a person's Claude Code session. The next step is to run that flow without the person: several tasks at once, each in its own
isolated session, ending in a draft pull request. The design spec (`docs/specs/2026-09-30-driven-sessions-design.md`, "Where it
stands") records the evidence: the server runs in a container with no Docker socket, runs one run at a time, has no cancel and no run
list, and Docker is present on the Mac.

## Options

1. Extend the sandboxed, shell-less writer (ADR 0035 option 2) to several tasks and a push. Keeps ADR 0006 true; but the agent that
   works is the read-only-shell one, feedback stays a turn slower, and the skill's flow would be rebuilt inside chargehand.
2. A full agent session with a shell, inside a sandbox on the host (`sandbox-exec`, `bwrap`). No daemon; but one mechanism per
   operating system, no limits on memory or processes, and the network rule is per-platform.
3. A container per run, headless Claude Code inside it running the skill. One mechanism on both hosts; needs Docker, an image, and a
   way for a containerised server to start containers.

## Decision

Option 3. The maintainer's four decisions, recorded:

1. **Isolation.** One container per run, on the VPS and on a Mac with Docker. No shell runs outside a container.
2. **Handover.** chargehand pushes `chargehand/<run>` and opens a draft pull request. It never merges and never enables auto-merge.
   The credential can push only non-default branches (a per-repository deploy key or a fine-grained token) and is outside the
   container except at push time.
3. **Inside the session.** Headless Claude Code (`claude -p`) runs the `change` skill's steps, so the skill is reused. OpenCode as a
   second runtime is a later goal behind the same interface.
4. **Trigger.** The caller calls `orchestrate` with a list of tasks (tracker item ids or free-text goals); chargehand runs N in
   parallel under a concurrency cap and a spend cap and reports one result per task. Picking work from the tracker's cycle is not in
   scope (roadmap: after 1.0).

What the proposer added, each with its reason in the spec: the server never holds the Docker socket, a **runner service** with a fixed
container template does (a socket mount is root on the host; a path-filtering proxy cannot see the request body); the run container sits on
an `--internal` network and reaches only an allowlist through an egress proxy; the model credential is a per-run token exchanged by the
proxy, falling back to the real credential in the environment, recorded, if a spike shows Claude Code cannot run that way; the session
ends by writing a git bundle and **chargehand** pushes from outside after running the tests itself in a fresh container; the session's
claims go through the existing resolver and support judge; every contract change is additive.

### What this supersedes

- **ADR 0035, decision 1 and option 3's rejection, for driven sessions.** ADR 0035 chose a shell-less worker with chargehand running
  the verifier in `sandbox-exec` or `bwrap` because a sandbox around a process that holds the model's credentials and network was the
  worse trade. This ADR accepts that trade inside a container by removing the two things that made it bad: the container has no route
  out except an allowlist, and it holds no push credential (and, if the spike succeeds, no real model credential). ADR 0035's other
  decisions stand: commits made with hooks off, the verifier's command resolution, the artifact shapes, the admission that a worker can
  edit the tests.
- **The `code` preset keeps working, unchanged.** It stays shell-less, pushes nothing, and uses the platform sandbox. Its tests are the
  regression guard (no task of the plan changes it; every task's `scripts/check.sh` runs them). The platform sandbox
  remains the verifier for `code`; driven sessions verify in a container.
- **ADR 0006's "no `autonomous` preset until sandboxing is designed"** was already relaxed by ADR 0035 for shell-less writers. A driven
  session is the first preset whose worker has a shell; it is permitted only through the `driven` preset, only inside a container, and
  the profile must opt in (`driven.enabled`).
- **ADR 0015's "a person merges" stands, and is now enforced by the credential:** a key that cannot push the default branch, and code
  that opens only draft pull requests.
- **ADR 0023's source-is-read-only rule stands:** the session works in a clone that the runner makes from the read-only source.

### Spike result (2026-10-04)

Measured with `scripts/driven-credential-spike.sh` on Claude Code 2.1.283 (the version pinned in `images/session/Dockerfile`; run on the Mac
with an empty config directory, not inside the image). **No gateway exists yet:** a local listener on loopback swapped the credential, which
shows only that a swap is feasible, not that a gateway is built, scoped or safe. Two trivial `claude -p` prompts on the subscription token,
the token read from the Keychain into the listener's memory only; header values and bodies were never logged, and a scan of every file the
run wrote found no token.

| Question | Answer |
|---|---|
| Subscription mode: which header does a dummy `CLAUDE_CODE_OAUTH_TOKEN` travel in? | `Authorization: Bearer <dummy>` to `ANTHROPIC_BASE_URL`, with `anthropic-beta` carrying the OAuth flag. No `x-api-key`. |
| Does it contact a refresh or auth host? | No. The only other destination was `api.anthropic.com:443`, and only without `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` (see below). No `platform.claude.com`. |
| Does a forwarded response with the real token swapped in succeed, streaming included? | Yes. Status 200 as server-sent events, `claude -p` finished with `is_error: false`, in both runs. |
| API-key mode | **Not tested.** No API key was available. The 2026-09-30 half result (dummy sent as `x-api-key`) stands; acceptance of a forwarded response is UNKNOWN. |
| Hosts contacted with `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` | None other than the base URL, observed through an `HTTPS_PROXY` that logged and refused every CONNECT. Without the variable the process asked for `api.anthropic.com:443` six times through the proxy (refused, the run still succeeded). |
| Can a gateway count usage? | Yes. `message_start` carries integer `input_tokens`, `output_tokens` and the two cache token counts; `message_delta` carries integer `output_tokens`, in the streamed response. |

Not tested: API-key mode; a refresh when the token is near expiry or revoked; long sessions, tool calls and many requests in parallel;
other Claude Code versions; running inside the session image (same version as the host, but the container path was not exercised); a
gateway that checks a run token, counts or refuses; hosts that ignore the proxy variables (only traffic that honours `HTTPS_PROXY` or the
base URL was observed, the later egress test on an internal network covers the rest). Decision 9's preferred design stands for subscription
mode. If a later run fails, decision 9 falls back to `credential_delivery: env` for that mode and Task 4 drops the credential exchange.

The VPS engine is Docker 29.6.2, not rootless,
so the runner holds a root-equivalent socket there; that is the security cost of decision "runner service" and is why the runner's
template and review focus are the most scrutinised code in the plan.

### Amendments while building (2026-09-30)

The spec's "As built" section lists them. In short: the server's MCP endpoint is reached through operator-named forwards on the egress container, because the allowlist proxy refuses private addresses (correctly); workspaces and the fresh verification run are built by a no-network helper from chargehand's read-only checkout; a change to CI configuration and a repository with no test command are not pushed unless allowed; the handover never runs git in the session's workspace. No decision above is reopened.

### Adherence measured on 9 sessions (2026-10-04): 7 of 9, below the plan's 10-session bar, not ready

Measured with `scripts/driven-e2e.sh` on a synthetic Python repository, session image built from this checkout, Claude Code on the maintainer's subscription token, three tasks (`easy`, `unskip`, `impossible`) per run, three runs: 9 sessions, each judged by `chargehand runs adherence` (research, write, test, review in that order, at most 2 fix rounds). The plan asks for 10 sessions (five tasks, twice) and at least 7 of them; 9 sessions of one small repository do not meet that, so **the feature is not called ready**, and the status stays proposed.

- Before any fix (earlier run, 3 sessions): 1 of 3. Two causes, both found in the stored streams. (1) The session could not make the research and review calls: its token admits only chargehand's own checkout path and the base commit, the session saw `/work` and `HEAD`, and every call was refused ("limited to the repository of its task", then "limited to the commit of its task"). One session read the path out of its own run token to get through; one went on without research and review; one stopped. (2) The skill's review step names `HEAD`, which the token refuses, so even a session with the right path could not review.
- Fix: the runner passes `CHARGEHAND_REPOSITORY_PATH` and `CHARGEHAND_BASE_COMMIT` into the container, and the skill's driven section and the driver prompt tell the session to use them for both calls (the review still gets `git diff <base>..HEAD` as input) and not to skip research or review when a call is refused.
- After the fix: run 1 2 of 3, run 2 2 of 3, run 3 3 of 3, so **7 of 9**. The two sessions that broke are `impossible` (contradictory goal): in runs 1 and 2 it did research and then stopped without writing, which is arguably the right answer to that goal; in run 3 it wrote a test-detecting hack that passes (and so opened a draft pull request, which the script's `one_task_failed` case reports as a failure). The six `easy` and `unskip` sessions all followed the steps. This says little about real goals: tasks are tiny and the sample is one repository, so the rate is a floor for nothing.
- Tokens: the nine sessions used 50,443 input plus output tokens (3.83 million counting cache reads and writes), as the sessions' own stream totals report. The cancel sessions (one per run) were not measured.
- Cancel: `cancel_leaves_no_container` failed once and passed in the later runs. The cause found: the script's wait for "the task is running" matched the batch's egress container, which exists earlier, so a cancel could land during setup. The script now waits for the task's own container. A start cancelled in flight could also leave the container docker had made (it was removed only by the id a finished start returned); the runner now removes it by its fixed name too. Whether that second path was the one hit is not known.

### Adherence measured on 12 more sessions, two toolchains (2026-10-04): 7 of 12, not met

Four more runs of `scripts/driven-e2e.sh` with the same session image and subscription token as above, now on two synthetic repositories: a Node project (`node --test`, runs 1 and 2) and the Python one (runs 3 and 4), three tasks each (`easy`, `unskip`, `impossible`), 12 sessions in all, judged by `chargehand runs adherence`. This set does not include the 9 sessions above (their streams were not kept).

| | followed | sessions |
|---|---|---|
| all | 7 | 12 |
| `easy` + `unskip` | 7 | 8 |
| `impossible` | 0 | 4 |
| Node repository | 4 | 6 |
| Python repository | 3 | 6 |

The plan's bar (at least 7 of 10, or 70%) is **not met** on all sessions (58%); it is met by the two tasks that can pass (88%), and a task that cannot pass is where it fails. Causes in the streams: in 3 of 4 `impossible` sessions the model did research and then stopped without writing (no write, no test, no review), in the fourth it researched, ran the tests and reviewed an empty diff (no write); one `easy` session (Python) committed and reviewed before it ran the tests. No `impossible` session passed, so none was caught editing a test (the script now fails that case if it ever does). Tokens: 76,842 input plus output tokens over the 12 sessions (5.68 million counting cache reads and writes); per task type, `easy` 26,940, `unskip` 23,212, `impossible` 26,690 (input plus output, 4 sessions each). Four batches ran; the cancel sessions were not measured. The sample is still tiny repositories with tiny goals.

- Found and fixed: `chargehand runs adherence` did not recognise `node --test` (or `jest`, `vitest`, `mocha`) as a test run, so the first Node run read 0 of 3 with "no test" on sessions that had run it; re-reading the kept streams with the fix gives the figures above.
- Open: in run 1 `cancel_leaves_no_container` failed once (a batch's egress container, `Exited (127)`, was still listed 20 seconds after the cancel result); it passed in runs 2 to 4 and 6 earlier runs. Cause not found (the run's log was not kept); the script now prints the leftover's last log lines.

### Adherence after the order was made explicit, 24 sessions in two sets (2026-10-04): 24 of 24, bar met on these tasks

Change: the skill's driven section and the driver prompt now say the order is research, write, run the tests, then review; that a goal which cannot be met still gets a best attempt (write and test) and then a stop with the reason, never a stop after research alone and never an edit to a test; and step 5 must run before step 7. The report now has two numbers. `adherence` is the plan's metric, unchanged. `honest_stop` counts sessions that did research, a write and a test run, in that order, made no review and wrote no test file; it is shown next to adherence and is not counted in the 7 of 10 bar.

Measured with a rebuilt session image (set A, 12 sessions, image `c564df2b...`): the skill carried the instruction as a bullet in its driven section. That wording is not what is committed: a test keeps the driven section to the steps that need a person, so the same instruction now sits in steps 4 and 7 of the skill, with the driver prompt unchanged. Set B below measures the committed wording. four runs of `scripts/driven-e2e.sh`, alternating the Node repository (runs 1, 3) and the Python one (runs 2, 4), three tasks each, 12 sessions, same subscription token, no rate limiting or auth refusal, no secret found in streams or logs.

| | followed | sessions |
|---|---|---|
| all | 12 | 12 |
| `easy` + `unskip` | 8 | 8 |
| `impossible` | 4 | 4 |
| `honest_stop` | 0 | 12 |

Every session reviewed once, fixed once and reviewed again (2 reviews). On `impossible` all four wrote an attempt, ran the tests (exit 1), reviewed, and finished with the failing test result and the reason; none edited a test (`impossible_tests_untouched` held, `one_task_failed` ok). So the earlier misses came from the model stopping after research; `honest_stop` stays at 0 because every stopped session now reviews. Tokens: 74,206 input plus output (about 6.4 million with cache). `cancel_leaves_no_container` passed in all four runs (containers gone 0 s after the cancel result); the earlier failure did not recur and its cause is still unknown.

Set A meets the plan's bar (12 of 12, at least 7 of 10) on these two tiny repositories and three trivial goals; it does not show the same rate on real repositories. The feature stays not ready: `driven.enabled` stays false until the credential design (CHARGEHAND-130) and a run on a real repository.

**Set B, the committed wording (steps 4 and 7 of the skill, driver prompt as in set A), image `sha256:63e936d43a6cc27e2a2fd89c73adfb5f9415deb7e8ae3a3ce0e3384d015b72a5`.** Four more runs, node, python, node, python, 12 sessions, same token. 12 of 12 followed (`easy` + `unskip` 8 of 8, `impossible` 4 of 4), `honest_stop` 0 of 12, every session reviewed twice (one fix round), no test file edited (`one_task_failed` ok in all four runs), `cancel_leaves_no_container` passed in all four, no rate limiting, auth refusal or secret. Tokens 72,442 input plus output (about 6.6 million with cache). Together 24 of 24 on the two wordings; the same caveat on tiny repositories applies.

### On a real repository, 10 sessions (2026-10-04): 10 of 10 followed, 4 of 4 passable tasks reached a draft pull request

Measured with `scripts/driven-e2e.sh` on a clone of a TypeScript-on-Node repository (Node 26, `node --test`, biome, tsc), session image with Node 26.10.0, Claude Code 2.1.283 on the subscription token. Five tasks written from the repository's own code (a validation and its test, a duplicate check in a config parser, a refactor with a missing unit test, a missing test alone, and one whose goal contradicts an existing test), two batches of five, 10 sessions, each judged by `chargehand runs adherence`.

| | followed | sessions |
|---|---|---|
| batch 1 | 5 | 5 |
| batch 2 | 5 | 5 |
| all | 10 | 10 |

Every session reviewed twice (one fix round). `honest_stop` 0 of 10. Tokens: 50,528 and 51,920 input plus output in the two batches.

- **Batch 1: 0 of 4 passable tasks reached a pull request.** Chargehand's own fresh-container test run failed all five with exit 1. Cause, from the stored output: the repository's tests write a script into `/tmp` and run it; `/tmp` is a `noexec` tmpfs (Docker's default), so 3 of 217 tests failed with `spawn claude EACCES`. Reproduced under the verifier's exact flags, and with `TMPDIR` set under `/work` on a volume: 216 of 216. The template was left as it is (see Consequences); the repository's `context.verify` now sets `TMPDIR`.
- **Batch 2, with that verify command: 4 of 4 passable tasks ended in a draft pull request** on the local remote (every PR draft, base `main`, head `chargehand/<run>`): each changed one source file and its test, or the test alone (14 to 24 added lines), and chargehand's own run of the repository's check passed. The task that cannot pass ended `verification_failed` with the assertion `12 !== 8`; no test file was edited. The default branch did not move; the cancel case left no container and opened no pull request; neither credential appeared in any file the run wrote.
- **Not shown:** one repository, five goals written by an agent, a local remote and a stub of the pull-request endpoint (no pull request exists on GitHub). The first deployment's credential can move the default branch (Consequences).

### First deployment on a VPS (2026-10-05): a draft pull request on the fourth attempt

Deployed with the release images by digest: the server, a runner (the same image, `chargehand runner`) holding no raw socket but reaching the engine through a socket proxy on a private internal network, and the session image; a private target repository checked out under `repository_roots`; `driven.enabled` true for the smoke only. One task (test-only, on the pinned base commit) went through the runner and ended `completed`: a draft pull request, one file (+27), chargehand's own fresh-container run of the repository's check exited 0 in 42 s, 9,560 tokens; the default branch did not move; the stored result carries the `verification` artifact.

Four attempts, one defect each, none reachable from a Mac or from the fakes: (1) the workspace helper (uid 10001) cloning a checkout owned by another user: `git clone` of a local path runs `git-upload-pack` as a child that checks ownership itself and ignores `-c safe.directory` and `GIT_CONFIG_*`, so the helper now sets a global config in its tmpfs; and `RunnerClient.RemoveAsync` threw on the runner's 404 for a container that never started, replacing that error (0.8.3); (2) `RunnerEgress` had no field for the forward, so through a runner the egress container had none and a session's call to the server was refused; the request carries forwards now and the runner starts only those in `--forwards` (0.8.4); (3) the host's ratcheted policy rejects a new raw socket mount, so the maintainer chose a socket proxy in front of the runner (it cannot see request bodies and so does not stop a privileged or bind-mounting create; the runner's API is still the gate); (4) `repository_roots` must cover `<worker_root>/.checkouts`, which a session names in its research and review calls.

What the attempts also showed: a session that cannot make its research or review call stops and asks instead of skipping them (twice, as the skill says); a failure before a container starts used to be reported as the cleanup's error. Not changed by this: the model credential is still delivered in the container's environment until the credential gateway exists, so `driven.enabled` goes back to false after the smoke. Open: the per-task output volume is kept and nothing removes it.

## Consequences

- A shell exists again, in a container, running repository content and model output. The boundary is the container, its network
  policy and the absence of credentials, not the session's permission rules. Anything the session can read, it can write into the
  branch: the push scan is a backstop, and the deployment's real protection is a key that is scoped, low-limit and revocable.
- The allowlist limits where data goes, not what: the proxy does not inspect TLS. A package registry on the list can still receive
  data in a URL.
- The runner is new security-sensitive code: a small program holding the Docker socket, reviewed as such (plan Task 5's review focus).
- The server's one-run-at-a-time gate must change for batches (plan Task 7).
- **The subscription is the default model credential (maintainer's decision, 2026-09-30), with an API key as the manual fallback.**
  Dollars are not measurable in that mode, so batches are bounded by a token ceiling counted from the stream, plus turns and time; the
  dollar ceiling binds only in API-key mode. Parallel sessions share one plan's limit. Whether unattended parallel server-hosted use is
  within the plan's terms is **unknown** and accepted as a risk by the maintainer; the risk is an action by the provider on the
  account. A rate limit or sign-out ends tasks as retryable and stops the batch; nothing switches to an API key on its own.
  Because the subscription token is long-lived and covers the whole plan, keeping it out of the container (the per-run token design)
  matters more here than for a limited API key; if the spike shows it cannot be kept out, the fallback delivery puts a year-long
  credential where hostile repository code runs, and the maintainer should then reconsider before enabling it.
- `/tmp` in a session and in the fresh verification run is `noexec` (Docker's default for `--tmpfs`, kept on purpose: a hostile repository cannot run a binary it dropped there). A repository whose tests run a script they write to `/tmp` fails with `EACCES` (measured, 2026-10-04: 3 of 217 tests on a TypeScript-on-Node repository, all five tasks of a batch ended `verification_failed`). Decision (maintainer): no `exec` on `/tmp`; the repository's `context.verify` sets `TMPDIR` to a directory under `/work`, which a volume provides.
- **The push credential of the first deployment can move the default branch (maintainer's decision, 2026-10-04).** It is a fine-grained token of the owner's account, limited to one repository. A repository ruleset that blocks updates of the default branch does not bind it: the owner's admin role is the ruleset's bypass, and a probe on a throwaway branch under a copy of the ruleset showed the token updating a protected branch (HTTP 200). What holds `main` is chargehand's code (the handover pushes one ref, `chargehand/<run>`, never forced, and opens only draft pull requests) and the ruleset against the owner's own mistakes, not the credential. The maintainer accepts this for one repository. A separate account or GitHub App with write but not admin would make the ruleset bind the credential; reopen if more repositories are added.
- The image is another thing to pin, scan and keep at the profile's Claude Code version.
- A session whose model ignores the skill's steps produces a branch that chargehand's own verification and review still judge; the
  skill is followed by instruction, not enforced, so the plan measures adherence (Task 12).
- New error codes, artifact kinds, events, a list schema and three routes are additive; `Chargehand.Contracts` needs a new alpha
  version when released, and the snapshots of the 1.0 stability plan must include them.
- Docker is required for this feature on any host. A Mac without it gets `container_unavailable` and the `code` preset still works.

## Reopen if

The credential spike shows the real credential must live in the container; the push scan finds a real secret in a branch; Docker
cannot be kept on the VPS or the runner cannot be made narrow enough to review; a batch of four cannot run in the VPS's memory;
session adherence to the skill is too low to be useful (Task 12's measured rate); OpenCode's session becomes wanted before 1.0.
