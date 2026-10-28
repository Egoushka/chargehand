# 0085. Every inbox card is a decision, and new resolve rules prove themselves in shadow first

- Status: accepted; amends 0064 and 0073
- Date: 2026-10-28

## Context

ADRs 0064 and 0073 made the inbox derive what left it and group repeats, yet on 2026-10-28 the owner's server held 82
open items in 60 days, 79 of them failures, and 71 were dismissed by hand in one call. The rules had resolved 44 more.
What stayed was hard to act on: a card is titled by the task's goal, its reason is a bare code, its only actions are
Answer, Open and Dismiss, and one cause still makes many cards (11 `checkout_has_secrets`, 10 of them on one
repository's reviews; 11 `cost_cap_reached`, reviews stopped by one preset's budget, spread over repositories). The
rules that resolve items show counts only: a person cannot see which item a rule took, or undo it. The spec
`docs/specs/2026-10-28-needs-you-decisions-design.md` has the full evidence and the plan; this ADR records the parts
that change 0064 and 0073.

## Options

1. Keep the rules as they are and add more of them, acting at once. Each new rule is a guess about which items nobody
   needs; a wrong one hides real work, and nothing would tell.
2. Ask a model to triage every item. Costly per call, not reproducible, and a rule nobody can read.
3. Sort the inbox into lanes by what a person must do, group failures by cause, and let a new rule act only after it
   ran in a shadow mode long enough to measure how often a person agreed.

## Decision

Option 3.

- **Lanes.** Every open item is in one lane. **Decide**: questions, pull requests waiting for a person, and failure
  cards whose fix is a policy choice (exclude a repository from reviews, a review budget). **Fix**: other failures, one
  card per cause. **Handled**: what a rule resolved or retried in the window, newest first, each with its rule, its
  reason and Undo. The console's badge counts Decide only; Handled is not counted. An item's lane is additive in
  `inbox/v1` (`lane`), as are the words a card shows (`what`, `why`, `options`), which come from one catalog in code,
  an entry per error code, so the console, the morning report and `chargehand inbox` say the same.
- **Cause groups** (amends 0073). Open failures group by code, and by repository only where the cause is the
  repository's: `checkout_has_secrets`, `verification_failed`, `push_rejected` and `merge_conflict` group per
  repository; `cost_cap_reached` on reviews groups across repositories by preset, because the cause is the preset's
  budget. A group gains `cause_key`, additively. 0073's per-repository groups stay in `groups` for older clients, with
  their ids and their dismiss and restore unchanged.
- **New rules** (amends 0064), each in `InboxRules`, each with a reason on the item: (a) *source gone*, the issue a task
  came from was closed, or the pull request a failure is about was merged or closed, including pull requests chargehand
  did not open; (b) *auto-retry*, an infrastructure failure (0064's codes) is retried once by the server as a normal
  batch with its caps, at most `inbox.auto_retry.max_usd_per_day`, never for a task that asked a question, and shown
  only if the retry fails; (c) *budget card*, `cost_cap_reached` reviews become one Decide card proposing a review
  budget at the 80th percentile of completed reviews' spend in 30 days, and the individual failures move to Handled;
  (d) *stale*, a failure with no newer run on its repository and task for 7 days moves to Handled; (e) *superseded by a
  merge*, a failed task whose issue has a merged pull request from any author since. No rule resolves a question or a
  pull request waiting for a person, and no rule closes a pull request or an issue.
- **Shadow first.** Every new rule ships in `shadow` mode: it marks items `would_resolve: <rule>` and changes nothing,
  not `counts`, not the lane. After 7 days `chargehand inbox rules report` shows per rule how many items it marked and
  how many a person then dismissed, restored or acted on. A rule moves to `on` only by a profile change, after its
  precision (dismissed over marked) is at least 0.95 on at least 10 items. Restoring an item a rule resolved counts
  against that rule and is listed in the report. 0064's and 0073's rules stay on; they gain Undo through Handled.
- **Stored state.** 0064 stored only dismissals; everything else was derived from the run log on every call. Two kinds
  of state now join it, both beside the run log and written whole through a temporary file: the policy choices made
  from a card (the excluded repositories and the review budget, each undoable), and a cache of forge state for rules
  (a) and (e), one entry per pull request and issue, refreshed in the background every 10 minutes. `GET /v1/inbox`
  reads the cache and never waits on the forge.

## Consequences

- A person reads a card's sentence, cause and options without opening a run, and acts from the card. Options are
  server calls (retry, retry with a bigger budget, the policy choices, answer, snooze, dismiss), or a command shown and
  copied (take over); a card offers only what can succeed.
- One preset's too-small review budget is one card, not one per repository.
- A rule that would hide real work is caught in shadow before it acts; the price is a week of shadow data per rule
  before the inbox gets quieter from it.
- The inbox is no longer fully derived: a lost state file forgets the policy choices (the repositories come back into
  reviews) and empties the forge cache until it refills. Both are small and rebuilt or re-chosen by hand.
- Auto-retry spends without a person asking. It is bounded by the infrastructure codes, once per task, a daily dollar
  cap and the account's caps, and each retry is a normal batch in Runs.

## Implementation notes

- The review policy is kept in `intake/sources.json` beside the switches. A review budget set there replaces the
  `review` preset's per-run cap through a server-internal flag (`budgetIsCap`: intake's starter, the run service, the
  orchestrator). It is not a field of `request/v1`, so no client can lift a cap by asking. The account's own caps still
  clamp it at admission.
- "Retry with a bigger budget" for a task is not offered. A driven task's dollar cap is the smaller of the preset's
  `max_usd` and the batch's `max_usd_total`, and its token cap is the preset's, so raising the batch total lifts
  neither. It needs a per-task override in the driven path and is left for its own change.

## Reopen if

A rule at `on` is restored more than 1 in 20 times; Decide holds more than 10 items for a week on the owner's server;
or the forge cache's calls hit the forge's rate limit.
