# 0092. A run that may spend over a threshold waits for the owner

- Status: accepted
- Date: 2026-11-04

## Context

ADR 0091 made the daily limit a warning: it tells the owner a day runs expensive and stops nothing. The other half of the
owner's request (2026-11-03) is that one expensive run does not start without them. Today a run whose `budget_usd` is large starts as soon as admission lets it, and the owner sees the
bill afterwards. The inbox already holds the things that wait for a person (ADR 0064, 0073, 0085), so the question belongs
there. This is the approval slice of the intake governor's spend limits (ADR 0084); the monthly cap and an account's
`max_usd_per_day` are not part of it.

## Prior art

Not searched: the shape is the existing inbox card with options (ADR 0085) plus the one-field editable setting of ADR 0091.

## Options

1. Refuse an expensive run with a 4xx and tell the caller to raise the budget. The owner is not asked; the caller has no way
   to say "yes, this one".
2. Hold the run in the server, show it in the inbox as a card with Approve and Decline, and start it when the owner approves.
3. Make the preset's `approved` flag (`RequestContext.Approved`) carry it. That flag is the caller's own claim about a plan, not
   the owner's decision, so it cannot be the gate.

## Decision

Option 2. A **per-run approval threshold**, default 5 USD, server-wide, kept in `limits/run-approval.json` beside the run log
and effective at once. `PUT /v1/limits/run-approval` takes `{"usd": 8}` (above 0) or `{"usd": null}` for never ask; `GET` reads
it with `default_usd` and how many requests wait. Both are owner-only. The Accounts screen has the field beside the daily
warning.

**What is asked about.** Only priced spend: a run whose runtime credential is `api_key` (ADR 0082), or a driven batch with an
account credential or a server API key. A subscription run never asks. The figure is the most the run may spend:

- a run: its `budget_usd`; with none stated there is nothing to compare, so it does not ask;
- a driven batch: `max_usd_total`, else the sum of the tasks' `budget_usd` when every task has one, else the sum of the
  forecast `usd_p90` per task. A forecast counts only when it rests on finished tasks like it; the flat size-class budget
  (`basis: flat`) is already capped by the preset and asks nothing.

A figure above the threshold holds the run; at or below it, or with the threshold off, the run starts as before.

**Where it sits.** After admission, on `POST /v1/runs`, the MCP `orchestrate` tool and the intake and schedule starter, so an
account's budget cut is already applied and the amount is what would run. The approval is for the request as it came:
its fingerprint is taken before admission, with the repository commit blanked. A child run started with a run token is exempt
(its parent was asked, and `RunScope` bounds it). The console's retry and answer-and-continue are ungated: they re-run work the
owner already started or is answering.

**What the caller sees.** A held run answers `409 approval_required` (the body says the figure and the threshold); the same
request sent again is the same question, not a second card. After the owner declines, the next send is refused once with
`403 approval_declined`, and a send after that asks again. An automated start gets the refusal as its start failure and tries
again on its next poll, finding the same card.

**The inbox.** One card per held request, kind `approval`, lane `decide`, owner-only (an account never sees or answers one),
with `approve` and `decline` (`POST /v1/approvals/{id}/approve|decline`, 404 once decided). There is no Dismiss: a dismissed
card would hide a request that is still held. The counts carry `approvals`, and the console adds a Spend tab and a notice kind.

**One approval, one start.** An approval pays for one start of the same request and is used up by it; a second start asks
again. A decision not used within 24 hours (a starting value) lapses. Requests and the threshold survive a restart.

The stdio host never holds a run: there is no console to answer in, so its threshold is off.

## Consequences

- An expensive automated start waits in the inbox until the owner acts; it is not lost, and it does not pile up duplicates.
- A run with no `budget_usd` is never asked about, even if it is expensive. The preset's own cap bounds it; a caller that
  wants it asked about states a budget.
- A forecast with no history asks nothing, so a new repository's first batch starts on its flat budget. This is deliberate and
  conservative in the direction of not blocking work; the daily warning (ADR 0091) still shows the spend.
- The fingerprint ignores the commit, so an approved request still starts if the repository moved on. It does not ignore the
  actor or any other field of the request.
- A server restart loses nothing; the held requests are in the file, written whole through a temporary file.

## Reopen if

Owners approve nearly everything without reading (then raise the default or ask only for forecasts), or a held start the owner
never answers blocks a schedule for days (then add a lapse for pending requests, which today never expire).
