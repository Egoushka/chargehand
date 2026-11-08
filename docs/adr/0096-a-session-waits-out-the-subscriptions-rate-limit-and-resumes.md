# 0096. A session waits out the subscription's rate limit and resumes

- Status: accepted
- Date: 2026-11-08

## Context

Issue #731: an overnight batch on a subscription meets its 5-hour window. Today the limit ends the run:

- **Driven session.** The result's `api_error_status` is 429 or its text names a usage limit, `SessionDriver.Conclude` maps it to
  `SessionStatus.RateLimited`, and the task ends with a `[partial]` pull request (ADR 0045). The gateway fallback (ADR 0088) takes the
  limit first; once the batch's allowance is spent the 429 reaches the session.
- **Worker turn.** `ClaudeCodeWorkerRuntime.Fell` reruns the turn on the API key under `api_fallback_daily_usd` (#632). With the
  fallback unset, closed or capped, the turn's `Idle(Failed)` carries the limit text and the node ends `RateLimited`.

In both cases the provider said when the limit lifts, the session's whole context is on disk (`--resume <session_id>`), and nothing is
spent while waiting. The scheduler already pauses starts to that reset (`BatchMachine.Limit`, ADR 0045). What is missing is the run
that hit the limit carrying on after it.

## Prior art

The peer project named in the issue resumes at the reset time. No prior-art query was run: the design is the issue's own sentence.

- Coverage: miss (not queried)

## Options

1. **Keep ending.** The turn in flight and the partial branch are paid for twice by a later batch.
2. **Wait at the batch.** The scheduler holds the ended task and starts a continuation (ADR 0044) after the reset. A new session starts
   cold, and the `[partial]` handover and the continuation rules change.
3. **Wait in the run and resume the same session.** The driver or the worker runtime sleeps to the reset and resumes with `--resume`.
   The context is kept and no scheduler rule changes. The container (or the worker's slot) is held while it waits.

## Decision

Option 3, on by default, constants and no profile key. The fallback is tried first, as before; waiting replaces *ending*, not the
fallback, so the daily API cap is untouched.

1. **What waits.** A result with `is_error` whose `api_error_status` is 429 or whose text `UsageLimit.Matches`, and that
   `GatewayRefusal.IsBudget` does not match. A budget refusal is never waited out; neither is any other error.
2. **Until when.** `GatewayRefusal.ResetsAt`; with none, or one already past, `UsageLimit.DefaultBackoff` (15 minutes). The run
   resumes `QuotaWait.Margin` (30 seconds) after it.
3. **How long, how often.** One wait is at most `QuotaWait.Longest` (6 hours, the scheduler's pause) and at most `SessionTask.QuotaWaitMinutes`
   when set (0: never). A run waits at most `QuotaWait.MostWaits` (4) times. A reset further away, or a fifth limit, ends the run
   `rate_limited` exactly as today.
4. **Driven session.** The wait counts as session time, so a driven session waits only when the reset falls inside what is left of its
   `max_minutes` wall clock; the host's kill deadline is then never crossed and no host change is needed. A session that must wait
   longer sets a larger `max_minutes`. `SessionDriver` waits after any Claude Code process it started (the first, a check-gate round, the
   commit and report turns), then resumes with `--resume <session_id>` and `QuotaWait.ResumePrompt`; with no session id yet (the limit hit
   before the first message) it runs the start command again. The no-progress timer is touched after the wait. The stream gets one
   `system` line `quota_wait` (resume time, reason).
5. **Worker turn.** When the fallback does not take the limit, `ClaudeCodeWorkerRuntime` drops the failed `Idle`, sleeps to the resume
   time and runs the same prompt again with `--resume`; the node sees one longer turn. `InterruptAsync` ends the sleep and the turn.
6. **The scheduler is unchanged.** A run that waited and finished is a normal completion. A run still limited after its waits ends
   `rate_limited` and the batch pauses as before.

## Consequences

- A batch holds its slots through a short window instead of ending its tasks. With the pool full of waiting tasks the scheduler starts
  nothing new, which is the pause it wants anyway.
- A 5-hour window outlasts a typical `max_minutes`; such a driven session still ends `rate_limited` until its caller gives it room.
  Extending the host's deadline by a declared wait (so the wait stops counting as session time) is the follow-up.
- The first call after a wait pays a cache write, as after any idle gap.

### Acceptance checks

Each is a test that must pass (`scripts/check.sh`):

- `QuotaWaitTests.A_limit_with_a_reset_waits_to_the_reset_plus_the_margin`
- `QuotaWaitTests.A_limit_with_no_reset_or_a_past_one_waits_the_default_backoff`
- `QuotaWaitTests.A_budget_refusal_or_any_other_error_is_never_waited_out`
- `QuotaWaitTests.A_reset_past_the_room_or_the_longest_wait_and_a_fifth_limit_are_not_waited_for`
- `SessionDriverQuotaTests.A_session_that_hits_the_limit_waits_then_resumes_the_same_session_and_completes`
- `SessionDriverQuotaTests.A_limit_before_the_first_message_runs_the_start_command_again`
- `SessionDriverQuotaTests.A_budget_refusal_ends_the_session_rate_limited_without_waiting`
- `SessionDriverQuotaTests.A_reset_past_the_wall_clock_ends_the_session_rate_limited_as_before`
- `SessionDriverQuotaTests.A_session_still_limited_after_its_waits_ends_rate_limited`
- `ClaudeCodeQuotaWaitTests.A_limited_turn_waits_for_the_reset_then_resumes_the_session_and_succeeds`
- `ClaudeCodeQuotaWaitTests.A_budget_refusal_ends_the_turn_without_waiting`
- `ClaudeCodeQuotaWaitTests.An_interrupt_ends_the_wait_and_the_turn`

Not run: a live run on a real subscription limit (needs an exhausted window); the first one after merge records whether
`claude --resume` accepts a session that ended on a 429.

## Reopen if

- `claude --resume` refuses a session that ended on a 429: the resumed turn must then start from the handoff file (ADR 0044).
- Held containers starve a batch of slots that tasks without a limit could have used: the wait moves to option 2.
