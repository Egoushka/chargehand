# 0045. Governed sessions: a governor in the session driver replaces the turn limit

- Status: accepted
- Date: 2026-10-12

## Context

On 2026-10-12 the production box ran driven batches (0.8.7, then 0.8.8). A session that reached the 80-turn limit ended `failed` and delivered nothing; another made 93 tool calls, read 8.1 million cache tokens, wrote 65k output tokens and delivered nothing. [ADR 0044](0044-driven-sessions-continue-after-a-stop.md) keeps the work of a stopped session and continues it. It does not decide when a session should stop, and a turn count is a poor answer:

- **The cost controls count the wrong thing.** The per-task token cap sums per-message input and output tokens; cache comes only from the final `result` event (`SessionDriver.cs:131,305-307`). Its default is 2,000,000 (`DrivenBatch.cs:58-59`); [ADR 0039](0039-driven-writing-sessions.md) measured 50k input plus output against 3.8M with cache, so it never binds. Live dollars are a hard-coded 0 (`EngineSessionVolumes.cs:35`); `priced` means "has an API key", so the subscription reports no dollars although `total_cost_usd` is captured (`SessionDriver.cs:336`); research and review children have their own ledger (`RunToken.cs:97-132`, `TaskRunner.cs:167`).
- **Cost per turn grows with context.** The 93-call session re-read about 100k tokens per turn. Turns say little about spend.
- **Sessions probe the environment** (`which docker`, `docker info`) because the prompt does not say what the container has.
- **A stop decided on the host cannot salvage.** The host sees `{tokens, turns, stage}` once per 5 s through a helper container, and `stream.jsonl` never leaves the volume (`Containers/ContainerTemplate.cs:178,191-201`). SIGINT and SIGTERM only cancel the driver's token, so `RunAsync` throws before it bundles or writes an outcome (`Chargehand.Cli/Program.cs:123-129`).
- **Rate limits never reach the scheduler.** A usage-limit result becomes `SessionFailed` (`SessionDriver.cs:157-159`); `BatchScheduler.cs:157` pauses new starts only on `RateLimited`, which the driven path never produces, and the matching pattern misses "usage limit" (`ChargehandException.cs:26`).

Other agent systems, as researched on the same day:

- SWE-agent caps dollars and commits `git diff` on every exit ([agents.py](https://github.com/SWE-agent/SWE-agent/blob/main/sweagent/agent/agents.py)); mini-SWE-agent returns an empty submission at its cost limit ([doc](https://mini-swe-agent.com/latest/reference/agents/default/)), which is our failure.
- OpenHands detects loops by fixed rules: 4 identical action and observation pairs, 3 identical failing actions, A-B-A-B-A-B ([doc](https://docs.openhands.dev/sdk/guides/agent-stuck-detector)). Deliberate polling trips them ([issue](https://github.com/OpenHands/software-agent-sdk/issues/762)). OpenRouter steps observe, block, stop ([doc](https://openrouter.ai/docs/agent-sdk/call-model/doom-loop-detection)).
- The same task varies up to 30x in tokens, and models predict their own usage at r <= 0.39 ([paper](https://arxiv.org/abs/2604.22750)): a budget has to come from history, not from the model's estimate.
- A fresh restart offered the old diff as optional raised the resolve rate from 66.6% to 71.8%; a cold restart gave 66.8% ([FailFast](https://arxiv.org/abs/2608.03222)). Anthropic's long-running harness restarts from a progress file, the git log and a JSON task list with a `passes` flag ([post](https://www.anthropic.com/engineering/effective-harnesses-for-long-running-agents)).
- Cognition saw summary generation cost more tokens than the work ([post](https://cognition.com/blog/devin-sonnet-4-5-lessons-and-challenges)). Claude Code's `--max-budget-usd` is a client estimate that can overshoot and ignores spend restored by `--resume` ([CLI reference](https://code.claude.com/docs/en/cli-reference)).

## Options

1. Raise the limits. Cheap, but the token cap already never binds, a higher turn limit lets a stuck session run longer at a rising cost per turn, and nothing changes about delivery.
2. A governor on the host (the server polls usage and stops the container). The polls exist; but it sees no stream, reads every 5 s, and its stop kills the driver before it bundles. It cannot salvage.
3. A model judges every turn. Sees more kinds of drift, but each judgement re-reads the context, takes one of the model-gateway slots that already cost 4-minute waits (CHANGELOG 0.8.7), and can wrongly stop good work. The best published monitor saves 14.6 to 20.4% of tokens at a 5% false-positive rate ([FailFast](https://arxiv.org/abs/2608.03222)).
4. Summarize the whole history for the next session. Cuts per-turn cost in OpenHands' numbers ([blog](https://openhands.dev/blog/openhands-context-condensensation-for-more-efficient-ai-agents)), but costs a model call that a checkpoint at the cap cannot fund (the gateway answers 403 at the cap, `Egress/ModelGateway.cs:150-157`), can cost more than it saves (Cognition), and carries tool output, secrets included, into the next prompt.
5. A deterministic governor inside the session driver, with a graduated ladder, a checkpoint that writes a handoff from facts, and a fresh continuation.

## Decision

Option 5.

- **Place.** A pure `Governor` class in the driver (injected clock, no I/O; the shape of `Driven/StallDetector.cs`) reads every stream event and returns continue, warn, checkpoint, stop or ask. The driver acts, so it can still snapshot, bundle and write the outcome (ADR 0044). The gateway's cap stays as the backstop for a driver that hangs; it counts input and output only (`Egress/UsageScanner.cs:6-8`) and is taught the cache counts and set above the governor's stop.
- **Spend.** For each message the driver prices input, output, cache-creation and cache-read tokens by model, with the price table `DrivenTelemetry.PriceUsd` uses (cache reads low, output high), and writes the running total to `session-usage.json`; research and review children are added. It is a dollar equivalent, not a bill, so it works on the subscription. The governor keeps its own ledger across continuations; the `result` event's `total_cost_usd` reconciles it afterwards.
- **Budget.** Each task has a budget in that unit, taken from the history of its size class (S, M or L; the caller names it until planning exists, default M): a percentile of past spend per task. The percentile and the minimum number of past tasks per class are profile settings; below the minimum a flat default per class applies. A task may carry its own. The task outcome records class, spend and stop reason, so history accumulates.
- **Signals and ladder.** All deterministic, no model call. The first hit of a stuck signal is a warn; the same signal firing again after a profile number of turns, or a second signal, is a checkpoint.

  | Signal | Fires when | Response |
  |---|---|---|
  | Repeated call | 4 identical action and observation pairs in a row | warn, then checkpoint |
  | Repeated failure | 3 identical failing actions | warn, then checkpoint |
  | Alternation | A-B-A-B-A-B | warn, then checkpoint |
  | Failing calls | N consecutive failing tool calls | warn, then checkpoint |
  | Environment probing | more than N commands that only ask what the container has (profile list) | warn, then checkpoint |
  | No diff growth | added plus removed lines unchanged over N turns | warn, then checkpoint |
  | Context size | context above a profile size (a fresh session is cheaper per turn) | checkpoint |
  | Spend | 85% of the budget | checkpoint; at 100% stop and salvage |

  Commands that wait on purpose (a long test run, a `--watch` command; a profile allowlist) do not count toward the repeat, failure and no-growth rules while they run. `StallDetector`'s call repeat ignores the observation; the new rule needs it.
- **Checkpoint.** The driver stops the session (ADR 0044's snapshot commits uncommitted work) and writes `handoff.json` and a rendered `progress.md` to the out volume. They are built from facts, with no model call: base and head commits, the commit log, changed paths with line counts, the last failing test command and its exit status, a task list (the `change` skill's steps, plus the session's own todo list if it kept one) with a `passes` flag the driver sets from the stream (a test command's exit status, a review call's return), never from the model's word, and the session's tool calls as name and arguments with the outputs removed. The text is scanned for secrets as the handover scans a diff (`Driven/DiffScan.cs`) and truncated; a hit drops the item and logs a finding without the value. This matters because the handoff becomes the next prompt and stream redaction covers three environment variables only (`Driven/SessionCli.cs:34`).
- **Continuation.** `TaskRunner` starts a fresh session as ADR 0044 describes (same branch, `seed.bundle`, one shared ledger) with the handoff in the prompt note. The note offers the earlier commits and does not require them: the session may reset to the base commit if the handoff shows the approach failed; the previous bundle stays under the run log. `Continues` matches only `turn_limit` and `wall_clock` today; it gains the checkpoint reasons. `rate_limited`, `needs_input`, a stop at 100% of the budget and a failed session never continue; with commits they are salvaged as a partial draft pull request (ADR 0044).
- **Ask.** A question for a person ends the task `needs_input` as today. The way back (`tasks[].answers`, `continue_from`) is a separate step of the roadmap, not part of this ADR.
- **Rate limits.** A result text or a gateway 429 that names a usage or rate limit becomes `RateLimited` (retryable), not `SessionFailed`. The scheduler stops new starts until the reset (a fixed wait when none is reported). Running sessions are not killed for it, and Claude Code's retries inside a session do not count as stuck signals.
- **Turns and minutes** stay as a runaway ceiling at about three times the typical value of the size class, from the same history; `no_progress` stays. Their stop reasons keep continuing (ADR 0044).
- **Log.** Every decision, with the signal values, the rule, the action, spend, turn and time, goes to `governor.jsonl` in the out volume, comes back as a `governor-log` artifact on the task result and as a run event (additive), so a board need not read Docker volumes.
- **Settings and rollout.** Governor settings are profile keys under `driven`, not preset keys: a preset edit starts the model-backed `driven/session` gate of up to 120 minutes (`evals/cells.json:34-41`). The first release runs in `observe` mode (decisions logged, none acted on) and switches to `enforce` once the log shows false stops under 5%, the rate the early-stop research accepts.

Out of scope here, each with its own ADR: the integrator and CI fixer, planning and splitting, shared services.

## Consequences

A session ends because money or progress ran out, not because 80 messages passed, and it ends with its work kept and a log that says why. A task that was stuck restarts fresh with a handoff, which the research favours over a cold restart.

Budgets are unvalidated until history exists; the first weeks run in `observe` mode on flat defaults. Because cost varies up to 30x for one task, percentile budgets will stop some sessions that would have finished; the checkpoint turns that into a continuation instead of a loss, and `governor.jsonl` measures how often it was wrong.

A spend checkpoint at 85% leaves the continuation 15% of the budget, enough to finish small remaining steps; otherwise the task ends as a partial draft pull request. A stuck or context checkpoint leaves most of the budget to a cheaper fresh session.

The dollar equivalent follows the price table, so it drifts when prices change; the reconciliation with `total_cost_usd` shows the drift.

The governor and the handoff live in the session image: the new behaviour needs a new image digest in `driven.images`. An older image keeps the turn limit.

Two assumptions are UNKNOWN and are tested first. (1) A warn needs a way to speak to a running session; the driver starts `claude -p` and closes stdin (`SessionDriver.cs:63,111`). `--input-format stream-json` may allow it. If not, a warn is only logged and the first rung that acts is the checkpoint. (2) Whether Claude Code emits a rate-limit event on a mid-session 429 and what it carries is unconfirmed; until a spike shows it, classification relies on the result text and the gateway's 429.

New `task.json` members break an older session image, because the parser rejects unknown members (`Driven/SessionCli.cs:11-15`). The first governor change ships the lenient parse, or moves in one release with the image digest bump.

## Reopen if

False stops exceed 5% in `observe` mode; continuations after a checkpoint rarely finish (then give them a top-up, not the 15% remainder); the provider reports a usage figure that replaces the price-table estimate; a model-based monitor shows a gain worth its cost on our logs; Claude Code's own budget flag counts resumed spend correctly (then the ledger may shrink to a cross-check).
