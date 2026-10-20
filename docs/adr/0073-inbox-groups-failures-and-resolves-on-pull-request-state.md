# 0073. The inbox groups repeated failures and resolves them on the pull request's state

- Status: accepted; amends 0064
- Date: 2026-10-20

## Context

ADR 0064's rules took the owner's inbox from 38 open items to fewer, but on 2026-10-20 `GET /v1/inbox` again returned
39: 36 failures and 3 questions. Repeats dominated: 10 `checkout_has_secrets` failures on one repository's pull request
reviews, 6 `internal` on the same repository's reviews, 7 `verification_failed`, 3 `session_failed`, several five to
eight days old. 0064 folds only failures with the same text, and each review names its own pull request, so none
folded. None of its rules looked at the pull request a failure was about, or at a retry by another account.

## Options

1. Group in the console only. Other readers of `/v1/inbox` (the morning report, an account's client) would not get it,
   and the console cannot dismiss items beyond the `limit` it fetched.
2. Group in the route, additively: a new `groups` list beside `items`, each item still listed. Older clients ignore it.
3. Replace grouped items with one item. Breaks `inbox/v1` for clients that read `items`.

## Decision

Option 2, and three more resolve rules in `InboxRules`.

- **Groups.** Open failures (after 0064's folding) with the same `repository` and code (`reason`), two or more, form a
  group: `{id, kind, repository, reason, count, at, items}`. `count` sums the items' `count`; `items` are ids, newest
  first; `id` is `group:<code>:<newest item id>`, so it is new whenever a newer failure joins. `POST
  /v1/inbox/dismiss` with a group id dismisses every failure it stands for (kept in `InboxDismissals` with the group id
  as `shown`); `restore` with it brings them back. A second dismiss of the same id is `unchanged`.
- **Pull request closed** (`resolved.pr_closed`). A failed task whose own pull request (its learning record) was merged
  or closed, and a review run (`Review pull request owner/name#N.`, ADR 0051) whose pull request is one chargehand
  opened and was merged or closed.
- **Same pull request head** (`superseded`). A failed task with a pull request, or a failed review, is superseded when
  a later review of that pull request at the same commit completed, or a later task on it left that commit as its head
  and completed. Any account: the commit is the same content.
- **Same task again** (`superseded`). A failed task is superseded when a batch of any account continued it
  (`continue_from`) or retried it (`Retry of <batch>/<task>.`) and the task completed or opened a pull request there.
  0064's same-goal rule still needs the same account; a retry names the task itself, so it does not.

The console shows a group as one card: the count, the code, the repository, the newest failure's title and error, the
runs behind **Show N runs**, **Open newest** and **Dismiss all N**. "Dismiss all shown" sends group ids for groups.

## Consequences

- The 10 `checkout_has_secrets` reviews read as one card; one click dismisses them. They resolve on their own only once
  a review of the same head completes: the server does not know the state of a pull request chargehand did not open
  (said in `not_recorded`). Stopping them at the source is the repository's preset, not this change.
- A group id includes the newest item id, so an Undo restores exactly what was dismissed, and a later failure of the same
  kind is a new group.
- With a `limit`, a group can list ids that are not in `items`; the console says how many lie beyond it.
- Each dismiss still rebuilds the inbox over the whole log, as 0064 says.

## Reopen if

Groups by repository and code hide failures that need different actions, or people ask to group questions too.
