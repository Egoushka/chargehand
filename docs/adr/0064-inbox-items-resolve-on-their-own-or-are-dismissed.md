# 0064. Inbox items resolve on their own, or a person dismisses them

- Status: accepted
- Date: 2026-10-14

## Context

The console's "needs you" inbox (`GET /v1/inbox`, CHARGEHAND-209, ADR 0056) is derived from the run log and the
learning records on every call, over a 14-day window. An item leaves only when what it names changes: a batch continues
a question, a pull request is merged or closed. A failure never leaves. On the owner's server on 2026-10-14 the inbox
held 38 items, of which about 31 were stale:

- 17 failures of probe runs with `checkout_invalid` from one day, a fault since fixed; later runs on that repository
  succeeded.
- 2 failures with `internal`, the same task text twice.
- 6 batches `lost` to server restarts; later batches on the same repositories ran.
- 5 questions, mostly a driven session saying chargehand was unreachable or full; two are the same task id in two
  batches, the older replaced by the newer.

An inbox that is mostly noise is not read, and the real items in it are missed.

## Options

1. Store an acknowledged flag per item. Every stale item needs a click, and the 17 repeats need 17.
2. Derive more: rules over the run log that resolve what a later run made moot, fold repeats, and store only what no
   rule can know (a person's dismissal).
3. Shorten the window. Hides real items as fast as stale ones.

## Decision

Option 2. The rules run in `InboxRules` (Server, `Console/`) on every call, nothing stored. "Later" means a run that
started after the item's run (or the item's batch) started.

- **Superseded.** A question: a later batch of the same account on the same repository asks a task with the same id
  the same goal (or ref), whatever became of it. A failed task: such a later task completed or opened a pull request,
  or a later run of its own with the same text completed. A failed run of its own: a later one with the same text on
  the same repository completed. A failed or lost batch: a later batch in which each of its tasks did so.
- **Continued.** A question a batch answered (`continue_from`, as before) or retried (the batch `POST
  .../retry` starts, text `Retry of <batch>/<task>.`). The retry route refuses a task that waits for an answer, so in
  practice this is the answer.
- **Infrastructure.** A failure whose code says chargehand, its host or its setup failed, not the task, is resolved by
  any later run on the same repository, of any kind and any account, that finished ok (completed, or a batch with a
  task that did). The codes, from the catalog in [reference](../guide/reference.md): `lost`, `internal`,
  `checkout_invalid` (the path, commit or clone: the server's checkout, as the snapshot's fixed fault was),
  `repository_not_allowed` (the profile's `repository_roots`), `runtime_unavailable`, `runtime_version_mismatch`,
  `runtime_ambiguous`, `provider_unavailable`, `rate_limited`, `sandbox_unavailable`, `container_unavailable`,
  `credential_unavailable`. Left out on purpose: `checkout_has_secrets` (the repository's content against the preset;
  a later run with another preset fixes nothing), `cost_cap_reached`, `deadline_exceeded`, `invalid_result`,
  `verification_failed`, `session_failed`, `session_stalled`, `push_rejected`, `pr_failed`, `merge_conflict`,
  `invalid_request`, `intake_failed`: each is about the task, its budget or its output.
- **Duplicates.** Open failures with the same subject, code, repository and full text fold into the newest, which
  carries `count`.
- **Dismissed.** `POST /v1/inbox/dismiss` and `POST /v1/inbox/restore`, body `{"ids": [...]}`, for the owner and for an
  account on the items its own inbox shows (as the console's actions are scoped). Dismissing a folded item dismisses
  every failure it stands for; restoring it brings them back. Kept by item id in `inbox/dismissed.json` beside the run
  log, written whole through a temporary file, as `accounts-use.json` and intake's state are.

`inbox/v1` stays: `counts` is what is still open, items gain `count` (default 1), and the body gains `resolved`
(`superseded`, `infrastructure`, `continued`, `dismissed`, `duplicates`), counted in items before folding. The console
shows a Dismiss button per card, "Dismiss all shown" for the filter in view, an Undo on the notice, `×N` on a folded
card and the resolved counts under the title.

Where the owner's asked-for rules and the data differ, this is the closest:

- A task id alone does not supersede: ids such as `t1` repeat across unrelated batches, so the later task must also be
  asked the same goal. Two batches that sent the same task again match; one that reworded its goal does not.
- Superseding needs the same account: another account's batch says nothing about this one's question. The
  infrastructure rule takes any account, because the fault it waits out is the server's.
- The ids go in the body, not the path: a pull request's id holds its URL, which has slashes.
- Not the step journal (ADR 0061): it belongs to driven batches (its flows are batch ids, `OpenBatches` owns the file,
  and it is absent while driven sessions are off), and a JSON file in `batches/` would be read as a legacy batch
  snapshot.

## Consequences

- The snapshot above should drop from 38 to at most 11 open items without a click (an estimate from its counts, not
  a measurement): the 25 `checkout_invalid`, `internal` and `lost` failures resolve once a later run on their
  repository finished ok, the older of the two questions with one task id is superseded, and identical failures left
  open show once. The questions about chargehand being unreachable are for Dismiss.
- A rule can resolve an item a person still meant to look at (a failure on a repository that later worked for an
  unrelated run). The `resolved` counts and `not_recorded` say so; the run is still in Runs.
- An owner's dismissal hides an account's item from the account too. A new failure of the same kind is a new item,
  even after its kind was dismissed.
- `dismissed.json` only grows (an entry per dismissed item, about 100 bytes). Each call to dismiss rebuilds the whole
  inbox over the whole log, as `GET` does.

## Reopen if

People dismiss what a rule could have caught (a reason that repeats); a rule hides an item someone needed; or
`dismissed.json` grows past a few thousand entries.
