# Roadmap

Where chargehand is going, as stages of what is true when each is done. Before 1.0 a finished goal bumps the minor
version; the [changelog](CHANGELOG.md) says what shipped. Legend: ✅ done · ▶ next · · later.

## Stage 1 — Answers questions about code, with checked citations · ✅ (0.1–0.2)

Intake, one or more read-only workers on OpenCode or Claude Code, `result/v1` with every citation resolved against the
pinned commit, presets with token and USD budgets, CLI, HTTP and MCP, Prompt CI, cache and routing reports, a server
image.

## Stage 2 — Easy to install, trust and follow · ✅ (0.3–0.4)

- ✅ **0.3** Every change is tracked, released and checked: GitHub Releases with notes, `Chargehand.Contracts` on
  nuget.org, OpenSSF Scorecard, CodeQL, dependency review, stricter analyzers.
- ✅ **0.4** It runs with nothing configured: no profile file needed, the runtime picked by name or found on the machine,
  secrets from environment variables, extensions by category, the `Chargehand` tool package on nuget.org, and its
  MCP Registry listing (from 0.4.1: the 0.4.0 package's README could not match the registry's name).

## Stage 3 — One prompt, whole result · ▶ (0.5–0.7)

- ▶ **0.5** `/chargehand:change <goal>` in Claude Code takes one prompt to a reviewed change. Its code shipped in 0.4.0
  and there is no 0.5.0; what is open is its usage bar, the maintainer's own use of the command on real tasks.
- ✅ **0.6** Runs use your MCP services and memory: any MCP memory server, several at once, retaining only claims whose
  citations resolved, and a preset can give workers read-only tools from an MCP service (released in 0.6.0).
- ✅ **0.7** Workers write branches that build and pass their tests in a sandbox: the `code` preset, the sandbox and the
  verifier (ADR 0035), released in 0.8.0 (there is no 0.7.0). Its usage bar, five real issues taken to a merged change,
  is not met yet.
- ▶ **Driven sessions** (ADR 0039, pulled forward from after 1.0): a list of tasks becomes parallel headless Claude Code
  sessions, one container each, and a draft pull request per task that chargehand checked, tested in a fresh container
  and pushed itself. Built, and run end to end on a real TypeScript-on-Node repository: 10 of 10 sessions followed the
  skill's steps, and 4 of 4 passable tasks reached a draft pull request (on a local remote). Deployed on a VPS (runner,
  socket proxy, release images) and run there once: after four attempts, one test-only task on a private repository ended
  in a draft pull request. Open is the substituted credential (a gateway; until then the model credential is in the
  container's environment). It gets a minor version when it is wired; until then `driven.enabled` stays false in every
  real profile.
  [Guide](docs/guide/driven.md).

## Governed driven sessions · ▶ (ADR 0045, extends Stage 3)

True when done: a driven session stops on spend and progress, not on a turn count, never loses its work, and a batch
can chain tasks, share the box fairly and leave a reviewed branch. It takes a minor version when the first step ships.
Twelve steps, one pull request each, in this order. "Needs" names what a step waits for. **Maintainer** marks a step that
needs the maintainer outside the code (a credential or a change on the server); every release also needs the
maintainer's approval and a new session-image digest.

1. ▶ **Continuation and salvage** (ADR 0044, in review): commit the work at a stop, continue in a fresh session,
   open a `[partial]` draft pull request when continuations run out. Needs: nothing.
2. · **Live spend and rate limits**: price cache tokens per message and add research and review calls to the task's
   spend; teach the model gateway the cache counts; classify usage-limit results and gateway 429s as `RateLimited` so
   the scheduler pauses new starts. Needs: 1.
3. · **Governor v1**: the pure governor in the session driver with the stuck-signal rules, per-class budgets and
   `governor.jsonl` returned as a task artifact, first in observe-only mode. Needs: 2. Ships after the lenient
   `task.json` parse from step 5, or with an image digest bump.
4. · **In-driver handoff and checkpoint**: at 85% of the budget the driver writes a machine-checkable handoff
   (`handoff.json`, `progress.md`) from the diff, the commit log and the session's own tool calls, scans it for
   secrets, and a fresh session continues from it. Needs: 1, 3.
5. · **Driven limits move to the profile**: out of `presets/driven.yaml`, so a limit change no longer starts the
   model-backed gate of up to two hours; and `task.json` skips unknown members, so a new host does not kill an older
   session image. Needs: nothing. Land the `task.json` half before 3.
6. ▶ **Admission by free memory and load**: the runner serves `GET /host`; the scheduler admits a task only when its
   memory limit is free; sessions get swap equal to memory. Needs: nothing in code; the runner redeploys.
7. · **A cancelled batch reports `cancelled`**: new status values (additive) and an owner record on resume. Needs:
   nothing.
8. ▶ **A question gets an answer** (ADR 0045): `tasks[].answers` and `continue_from` start a task from another task's
   kept work, on the same branch; a `question` run event; the output volume kept for 7 days. Needs: 1.
9. · **Task dependencies, then planning**: `depends_on` with stacked branches or a merge between tasks; then a cheap
   planning step that sizes tasks (S, M, L), splits large ones into a chain and assigns shared numbers. Needs: 3.
10. ▶ **Shared services** (ADR 0048, in review): the environment note in the prompt, package caches per repository,
    declared test services on the batch network, and a JDK and Gradle image chosen by ecosystem are built and tested
    with fakes (the Postgres service, the cache volumes and the JDK image were also run on a local engine); the
    code index pinned to the base commit is decided and not built, and the Android SDK is not in the image. Needs: 6,
    and a runner ADR. **Maintainer** approves the server change and makes it: a runner and server from this release,
    the new image digests, `--service-images`, the `driven.services` profile keys.
11. ▶ **Integrator and CI fixer** (ADR 0046, in review; built and tested against a fake GitHub host, not yet run against GitHub) behind a GitHub App credential: update the branch, merge by policy through required
    checks and `--auto`, and a short session that fixes a small CI failure (at most 3 attempts, one per failing-job
    set). Supersedes ADR 0039 decision 2 and ADR 0015 for repositories that opt in; a repository the maintainer lists
    (the one that deploys his servers) always asks. Needs: 3 and a new ADR. **Maintainer** creates the GitHub App and
    installs its key; any credential change on the server is his.
12. · **Learning loop**: per task, size estimate, budget, spend, stop reason and governor decisions; the human outcome
    of each pull request (merged unchanged, edited, closed); a retro filed to the maintainer's tracker inbox. Needs: 3,
    9. Real-run data stays in the maintainer's datasets; `evals/` keeps synthetic examples only.

## Stage 4 — Answers you can prove · ✅ (0.8)

- ✅ **0.8** Every claim is checked for support against its cited text, and results are signed so anyone can verify them
  offline (released in 0.8.0).

## Stage 5 — People find it · (0.9–1.0)

- · **0.9** A public verification benchmark, a demo and listings.
- · **1.0** `result/v1` and the extension API are declared stable.

## After 1.0

Work picked from the tracker without anyone watching (driven sessions take the list a person gives), a second session
runtime behind the same interface, a chat client that approves and denies runs, more agent
runtimes through ACP. Not planned: an own agent loop, model gateway, memory store or plugin marketplace.
