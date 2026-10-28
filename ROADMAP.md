# Roadmap

Where chargehand is going, as stages of what is true when each is done. Before 1.0 a finished goal bumps the minor
version; the [changelog](CHANGELOG.md) says what shipped. Legend: ✅ done · ▶ in progress or next · · later.

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

## Stage 3 — One prompt, whole result · ✅ (0.5–0.7)

- · **0.5** (superseded) `/chargehand:change <goal>` in Claude Code takes one prompt to a reviewed change. Its code
  shipped in 0.4.0 and there is no 0.5.0, and the command still ships. Its usage bar (10 real `/ch` tasks, 7 merged) was
  never met. The goal is closed, superseded by GitHub issue intake (0.9.0): labelling an issue starts a driven batch
  that ends in a pull request.
- ✅ **0.6** Runs use your MCP services and memory: any MCP memory server, several at once, retaining only claims whose
  citations resolved, and a preset can give workers read-only tools from an MCP service (released in 0.6.0).
- ✅ **0.7** Workers write branches that build and pass their tests in a sandbox: the `code` preset, the sandbox and the
  verifier (ADR 0035), released in 0.8.0 (there is no 0.7.0). Its usage bar, five real issues taken to a merged change,
  was met by the first real driven batches (0.8.x).
- ✅ **Driven sessions** (ADR 0039, pulled forward from after 1.0; wired in 0.9.0): a list of tasks becomes parallel
  headless Claude Code sessions, one container each, and a draft pull request per task that chargehand checked, tested
  in a fresh container and pushed itself. Built, run end to end on a real TypeScript-on-Node repository, deployed on a
  VPS (runner, socket proxy, release images) and enabled there. The model credential stays out of the session
  containers (a gateway, 0.8.5). Several runners, a Mac among them, from 0.9.2. [Guide](docs/guide/driven.md).

## Governed driven sessions · ✅ (0.9.0; ADR 0045, extends Stage 3)

True when done: a driven session stops on spend and progress, not on a turn count, never loses its work, and a batch
can chain tasks, share the box fairly and leave a reviewed branch. All twelve steps (one pull request each) shipped in
0.9.0.

1. ✅ **Continuation and salvage** (ADR 0044, 0.9.0): commit the work at a stop, continue in a fresh session, open a
   `[partial]` draft pull request when continuations run out.
2. ✅ **Live spend and rate limits** (0.9.0): cache tokens priced per message into the task's spend; usage-limit results
   and gateway 429s are `rate_limited` and the scheduler pauses new starts (kept across a restart from 0.9.6).
3. ✅ **Governor v1** (0.9.0): the governor in the session driver with the stuck-signal rules, per-class budgets and
   `governor.jsonl` as a task artifact, in observe mode by default; `enforce` exists and stays off until the learning
   data shows its false-stop rate. Budgets from past spend (the 80th percentile) also shipped in 0.9.0.
4. ✅ **Handoff** (0.9.0): on every stop the driver writes `handoff.json` and `progress.md` from the diff, the commit
   log and the session's tool calls, and a fresh session continues from them.
5. ✅ **Driven limits move to the profile** (0.9.0): `driven.limits` overrides the preset's, and `task.json` skips
   unknown members, so a new host does not kill an older session image.
6. ✅ **Admission by free memory and load** (0.9.0; the measured working set from 0.9.1): the runner serves
   `GET /host`; the scheduler admits a task only when its memory is free; sessions get swap equal to memory.
7. ✅ **A cancelled batch reports `cancelled`** (0.9.0), with its owner record on resume.
8. ✅ **A question gets an answer** (ADR 0045, 0.9.0): `tasks[].answers` and `continue_from` start a task from another
   task's kept work, on the same branch; a `question` run event; the output volume kept for 7 days.
9. ✅ **Task dependencies, then planning** (ADR 0049, 0.9.0): `depends_on`, and a cheap planning step (off by default)
   that sizes tasks and splits large ones into a chain.
10. ✅ **Shared services** (ADR 0048, 0.9.0): the environment note in the prompt, package caches per repository, declared
    test services on the batch network, and a JDK and Gradle image by ecosystem. Not built: the code index pinned to the
    base commit (decided in the ADR); the Android SDK is not in the image.
11. ✅ **Integrator and CI fixer** (ADR 0046, 0.9.0) behind a GitHub App credential: update the branch, merge by policy
    through required checks and `--auto`, and a short session that fixes a small CI failure (at most 3 attempts). A
    repository the maintainer lists (the one that deploys his servers) always asks. It runs against GitHub; 0.15.0
    made it refuse a merge without a required check and push docs-only or CI-gated untested changes, and the App's
    installation is looked up per repository owner (0.9.2).
12. ✅ **Learning loop** (ADR 0045, 0.9.0): per task, size estimate, budget, spend, stop reason and governor decisions
    in a record next to the run log; the human outcome of each pull request read back through the forge;
    `chargehand learn report` and `GET /v1/learn`, with a console screen from 0.9.3; budgets per size class from past
    spend; a retro after each batch, off by default. Not built: filing the retro to the maintainer's tracker inbox
    (`IRetroSink` is the hook). Real-run data stays in the maintainer's datasets; `evals/` keeps synthetic examples only.

Beyond the twelve, shipped since: work starts on its own from GitHub issues and comments (0.9.0), Plane work items, a
schedule and every open pull request (0.10.0), and the App's repositories are discovered (0.15.0); local delivery
(0.11.0); commits that name the run that wrote them (0.13.0); the operator console, now the one web UI (0.9.3, 0.13.0).

## Stage 4 — Answers you can prove · ✅ (0.8)

- ✅ **0.8** Every claim is checked for support against its cited text, and results are signed so anyone can verify them
  offline (released in 0.8.0).

## Stage 5 — People find it · (0.9–1.0)

- · **0.9** A public verification benchmark, a demo and listings. The harness is built (ADR 0052, CHARGEHAND-206); no
  real run has been published. The 0.9 version number itself went to governed driven sessions.
- · **1.0** `result/v1` and the extension API are declared stable. Not started; it follows the doors below (ADR 0086).

## Doors in, doors out · ▶ (0.20–0.22, ahead of 1.0; ADR 0086)

Stage 6's first three tasks, moved ahead of 1.0: chargehand becomes reachable from where work starts and drives more
than two agents, through one steerable resource and the agent client protocol (ACP).

- ▶ **0.20** Conversations and ACP ([Cycle A spec](docs/specs/2026-10-28-cycle-a-conversations-and-acp-design.md)): a
  conversation you can stream, message, approve and cancel (`/v1/conversations`); a .NET ACP client library; research
  workers on Codex and Gemini CLI through ACP beside Claude Code; a clients guide for MCP hosts and a VS Code agent
  plugin. The spike (Stage 6 Task 1) is done (2026-10-27).
- · **0.21** chargehand inside the editor: chargehand as an ACP agent (Zed, JetBrains, Neovim, Emacs), checked
  citations as clickable locations, and an MCP App that shows a result in Claude, VS Code, ChatGPT and Cursor.
- · **0.22** Trackers: an A2A server for Jira, Gemini Enterprise and Teams.

## Measure, then cut · · (after Cycle A; ADR 0087)

Whole configurations get numbers, and offline model calls get cheaper only where measured
([Cycle B spec](docs/specs/2026-10-28-cycle-b-measure-then-cut-design.md)). Releases stay held until the cycle ends.

- · **Replay benchmark**: `chargehand bench` replays 20 merged pull requests as tasks over arms (runtime, model, MCP set,
  preset) and reports pass rate, a human-fix proxy, dollars per solved task and p95 wall time beside the real-work baseline.
- · **Batch and cache for offline calls**, built only if the judges are at least 20% of a Prompt CI run's cost.
- · **Calibrated judges**: 100 or more hand labels per judge, Cohen's kappa, and a Prompt CI check that fails when a judge
  change drops it.

## Stage 6 — A client of your own · (after 1.0)

chargehand becomes the engine under a client the maintainer uses every day instead of Claude Code's own interface, and
can extend. The client replaces the interface, not the agent: it talks to Claude Code, OpenCode and others through the
agent client protocol. [Spec](docs/specs/2026-10-02-a-client-of-your-own-design.md).

- ▶ **1.1** Sessions you can steer: moved ahead of 1.0 as 0.20 (above), where the resource is named a conversation
  because "session" means a driven session.
- · **1.2** A web client served by `chargehand serve`, usable from a phone: list, live transcript, cost, steering.
- · **1.3** What no vendor client has: the model picked for each message from your scored runs (with its reason and
  cost, one click to override), prompts improved before sending by a
  prompt enhancer you connect (your prompts stay in your own tool, which learns from chargehand's feedback), every claim in the transcript shows its checked citation and support verdict, a
  session ends in a `result/v1`, memory recall in the client. A second runtime with no client change, and client
  features as extensions you can add yourself.
- · **1.4** Unattended pickup: work taken from the tracker by a session with no person in it, as a mode of the same
  session resource.

## After that

A native shell for the client if a browser tab proves not enough, more runtimes. Not planned: an own agent loop, model
gateway, memory store, a public plugin marketplace (extensions you write yourself are planned, 1.3); multi-user accounts; a hosted service.
