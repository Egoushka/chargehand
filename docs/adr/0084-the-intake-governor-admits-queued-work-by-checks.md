# 0084. The intake governor admits queued work by checks, not by a count

- Status: accepted
- Date: 2026-10-27

## Context

ADR 0081 removed the daily count of starts and made a trigger that cannot start wait in a durable queue (`IntakeState.Waiting`, beside the run log)
with a reason: `max_concurrent`, `admission` or `checkout`. ADR 0083 made admission count the budget of running work. What is still missing:

- **Review backlog.** Nothing stops chargehand from opening pull requests faster than the owner reads them. Evidence: Egoushka/chargehand#529
  (27 issues labelled at once); the owner's review, not money, was the limit.
- **Subscription headroom.** Subscription-billed runs skip the dollar caps (ADR 0082) and so have no bound but a rate-limit window that
  a running task reports after it hit it (`DrivenRun.RateLimitPauses`, shown on the console). Nothing looks at it before a start.
- **Runaway protection.** The removed count was also a crude breaker. With no count, a loop that starts and fails batches has no brake but money.

The first design of this feature (a desk spec of 2026-10-26) predates ADRs 0081 to 0083. Where it disagrees with main, main wins:

| Spec said | Main has | Here |
|---|---|---|
| Queue for GitHub, Plane, review rounds, CI fixer and console | Plane intake is gone (ADR 0067). Review rounds and CI fixes keep their own pending lists. A console start is a person's run, admitted by the account and refused with `budget_exceeded`, not queued | One queue: the existing `Waiting` list of GitHub intake. Rounds and CI fixes are not queued by it and are exempt from the backlog check (they shorten it) |
| Money check uses #575, #580, #438 | #580 and #575 are merged (ADR 0082, 0083); the forecast of #438 is not used for reservations (ADR 0083) | Money stays in admission, unchanged |
| Subscription headroom uses #441 (forecast of windows) | #441 is open; the only signal is an observed rate-limit pause | The check reads observed pauses; #441 replaces the signal later |
| "Governor" | #436, #437, #530 use the word for the session governor (checkpoint and ask instead of stopping) | This one is called the intake governor in code and docs |
| Ordering by priority label (#576) | open | Out of scope; waiting triggers keep the order they were seen |

## Options

1. Keep the checks scattered: each source calls its own. A fourth check would be a fourth copy.
2. One `IntakeGovernor` in `Chargehand.Automation`: an ordered list of checks, each returns a hold (a reason and a detail) or none. Intake asks it before a
   driven start, and a hold becomes a `Waiting` entry.
3. Move the queue into the server (`RunService`) so every source, including persons' runs, queues. Reaches console starts too, but a person asked for
   the run now and a queue would hide a refusal they should see.

## Decision

Option 2.

**Checks, in order.** Money and box (memory admission, container count) stay in the server's admission (ADR 0076, 0045, 0083): the starter refuses and intake
records `admission`. The governor adds two checks that run in intake before the starter is called:

1. **Review backlog.** Per repository, the number of open pull requests chargehand opened that wait for a review. When it reaches
   `automation.github.max_review_backlog` (**default 3**), a new driven start of any issue or comment trigger waits with reason `review_backlog`
   ("3 pull requests await review"). A pull request waits for review while it is open, its head branch is `chargehand/*` in the repository itself, and no allowed author has submitted a review on it (an approval, a change request or a review comment; a plain conversation comment does not count). A
   merge or a close frees a place at the next poll. Review rounds, answers to a task's questions and CI fixes work on an open pull request and are never held by it. `0` turns the check off.
2. **Subscription headroom.** A driven start, while a task of a running batch has reported a rate-limit pause that has not reset, is a
   *hit*. The intake cannot tell which credential a start will use, and a rate limit is a rate limit, so every start counts. By default a hit **warns**: one server log line per pause and a banner on the console overview ("subscription limit reported by a running task, resets at ...");
   the start goes on. `automation.github.subscription_block: true` turns the warning into a hold with reason `subscription` until the pause resets.
   **Default false**: the signal is a report after the fact, not a forecast, and a wrong block idles the owner's main tool. After a week of data the owner decides
   whether to turn it on, and #441 replaces the signal with a forecast.

**Safety nets** replace the removed count. Both pause *intake starts* only; running batches, rounds and CI fixes go on.

- **Start-rate breaker.** More than `automation.github.max_starts_per_hour` (default 30) driven starts in the last hour pauses new starts (reason `breaker`)
  and warns once in the server log; each held trigger gets one comment. It clears when the hour's count falls under the limit. It bounds a loop, not spend; the account bounds spend. The default is above what four slots start in a busy hour (27 labelled issues must still all start); a push notification through `automation.notify` is a follow-up.
- **Failure breaker.** 3 failed batches in a row in one repository pause that repository's starts (reason `breaker`), with one warning in the server log and one comment on each held trigger. It clears when a task of that repository succeeds, or after `automation.github.breaker_cooldown_minutes` (default 120).
- The existing limit of 3 rounds per pull request stays.

**Visible reason.** A waiting trigger keeps its reason and detail in `Waiting` and on `/v1/intake`; none is marked handled. Reasons are checked in this order and the
first that holds is the one shown: `breaker`, `review_backlog`, `subscription`, `max_concurrent`, then the starter's `admission` (money, box). Queue and breaker
counters live in the intake state file, so a restart keeps the queue and starts nothing twice (the `Starting` list of ADR 0081 is unchanged).

**Settings** are additive keys of `automation.github` in profile schema v1.

## Consequences

- With seven or more labelled issues and three unreviewed pull requests, new work waits however much money is left. The owner clears it by reviewing.
- A repository on `merge` integration merges its own pull requests once checks pass, so its backlog is mostly empty.
- Counting reviews needs one more GitHub read per watched repository per poll (open pull requests are already listed for reviews; reviews per chargehand pull request are new).
- The word "governor" now means two things in the repository until #436 and #437 are renamed or merged into this.

## Reopen if

The backlog default of 3 idles the owner (the queue is long and the owner is waiting for work) or lets pull requests pile up unread; the observed rate-limit signal
proves too late to be useful (blocks come after the window is spent) before #441 lands; or a second kind of queued work (a person's runs) needs the same checks.
