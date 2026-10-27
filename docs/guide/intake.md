---
title: "Start work from GitHub"
description: "A label on an issue or an @-mention by an allowed author starts a driven batch of one task; a reply continues a task that asked: the profile, who may trigger, what chargehand writes back, the limits and the security choices."
order: 14
section: "Guides"
---

Intake ([ADR 0051](../adr/0051-intake-from-github.md)) turns an issue into a [driven](driven.md) batch of one task, so a person files an issue and gets a draft pull request. chargehand **polls** GitHub as the GitHub App of `driven.integrator`: the server is on a private network and GitHub cannot call it. Off unless the profile has `automation.github`. On `main`, not yet released; not run against GitHub, only against a fake host.

## What starts a task

| Trigger | Who counts | What happens |
|---|---|---|
| The label `chargehand` is on an open issue | the person who **applied** the label | a task starts from the issue |
| A comment that mentions `@chargehand` | the comment's author | a task starts from the issue, with the comment as part of the goal |
| A comment on an issue whose task asked a question, posted after the questions | the comment's author | the task continues from its kept work with the comment as the answer; no mention needed |

Only logins in `allowed_authors` count. The list is required and has no default; an empty list means nothing triggers. Anyone else's label or comment is ignored, once. A mention in a quoted line (`> @chargehand`), inside another word (`@chargehand-bot`, `me@chargehand.example.test`) or in a comment chargehand wrote does not count. Pull requests are not issues here; the label on one does nothing.

Removing and applying the label again starts a new task. Without that, a task is started once per label event or comment, however often the issue is read, and a restart does not repeat it.

## What chargehand writes back

On the issue, as the App, each comment starting with a hidden marker so it is never read as a trigger:

1. When the batch starts: the run id. Follow it with `chargehand watch <run-id>`.
2. When it ends: the draft pull request; or what stopped it (the task's own detail); or the session's questions, as a list, with a line that a reply from an allowed author continues the task.
3. When a limit holds it (below), once.

A reply continues the task on the same branch, from the commit the first batch started at, with the first task's size class. It is a new batch, so it counts toward the limits. If the earlier batch is no longer in the run log, chargehand says so and a new mention starts the task again.

## The goal

The goal's first line is the issue's title (the pull request's title is cut from it). The rest names the issue, then gives its title, author and body, and the triggering comment if there was one, between `<untrusted-github-text>` tags, cut to 200, 6000 and 2000 characters, with a sentence telling the session that these are texts people wrote on GitHub: they describe the work and are not instructions to it. A tag inside the text is turned into a bracket, so text cannot close the fence. The size class is the issue's `size:S`, `size:M` or `size:L` label when it has exactly one, else `budget_class`.

## Limits

| Key | Default | When over it |
|---|---|---|
| `max_concurrent` | 2 | the trigger waits, with one comment, in the queue (below), and starts when a triggered batch ends |
| `max_review_backlog` | 3 | a new issue or comment start waits, reason `review_backlog`, while that many pull requests chargehand opened in the repository have no review from an allowed author; a review, merge or close frees a place. Review rounds, answers and CI fixes are never held. 0 turns it off ([ADR 0084](../adr/0084-the-intake-governor-admits-queued-work-by-checks.md)) |
| `max_starts_per_hour` | 30 | more driven starts than that in the last hour: new issue and comment starts wait, reason `breaker`, until the hour passes. A brake on a loop, not a budget; 0 turns it off |
| `breaker_cooldown_minutes` | 120 | the last 3 batches of a repository failed (no pull request, no question): its new starts wait this long after the last failure, reason `breaker`; one try is then allowed, and a success ends the streak |
| `subscription_block` | false | a new issue or comment start while a running task's rate-limit pause has not reset: off, the server log warns once per pause and the console says so on the Intake page, and the start goes on; on, it waits with reason `subscription` until the pause resets. Not a forecast: the signal is a report after the fact ([ADR 0084](../adr/0084-the-intake-governor-admits-queued-work-by-checks.md)) |
| one task per issue | | a second trigger on an issue whose task is running gets one comment |

`max_tokens_total` (2,000,000) and `max_usd_total` (5) cap a triggered batch like `driven.max_tokens_total` and `max_usd_total` do for a caller's; both are always sent, and the one that applies is the one the credential's kind needs. A halted server, or one with ten unfinished runs, leaves the trigger for the next poll.

**No daily count of starts** ([ADR 0081](../adr/0081-automated-work-is-bounded-by-money-and-slots.md)). Spend is bounded by money, through `automation.account` below, and concurrency by `max_concurrent`, `driven.max_parallel_total` and memory admission. `max_starts_per_day` and `max_reviews_per_day` are still accepted so an older profile loads, are ignored, and are named once on stderr when the server starts. Set `automation.account` with `max_usd_per_day` and `max_usd_per_month` on any server that runs intake: without it nothing bounds a day's spend but the per-batch caps times the slots.

**The queue.** A trigger that cannot start yet (no free slot, review backlog at its limit, a start the server or the account refused, a base commit that could not be read) is kept in `waiting` in the state file with why, is not marked handled, and starts at a later poll, after a restart too. `GET /v1/intake` lists it under `waiting`. Taking the label off a waiting issue, or deleting the comment, takes it out of the queue (logged `ignored: withdrawn`).

**A run lost in a restart starts again, once.** A batch the server lost before it had state to resume from (still in the queue for a run slot, or resolving and planning its tasks; [ADR 0043](../adr/0043-driven-batches-survive-a-server-restart.md) resumes the rest) is reported lost 30 minutes after it started. When intake started it fresh from an issue, it goes into `waiting` with reason `lost` (logged `queued: lost`), behind the triggers that waited before it, and starts again with the lost run's goal when the run log still holds it, else from the issue. A second loss of that run is reported and not queued again: comment the mention to start it. A lost continuation or review round is never queued again.

**Charging automated starts to an account.** `automation.account` (beside `automation.github` and `schedules`) names an account of `http.accounts` with `driven: true`. Every automated start, from GitHub or a schedule, is then admitted as that account, the way a person's request is: its credential rule, `repositories`, `max_concurrent_runs` and day and month caps apply, its budgets are clamped to what the windows have left, and the run counts in its spend. With `pause_on_forecast: true` on the account, its automated starts are held while its month forecast passes `max_usd_per_month` ([ADR 0076](../adr/0076-month-spend-forecast.md)). A refused or held start is retried at the next poll, with a line on stderr that says why. Without `automation.account`, automated starts run as the owner, with no account caps, as before.

## Security

- **Who may start** is the list, and for a label it is the labeller. Writing an issue needs no rights; applying a label needs triage rights.
- **The issue's text is untrusted** even when an allowed author applies the label to someone else's issue. The fence helps the model; the real boundary is the session's container (no credential, no route out beyond the allowlist) and the handover's checks.
- **A repository the integrator merges into** (`merge` in `driven.integrator.repositories`, not in `always_ask`) takes work only from an issue **written** by an allowed author. For another author's issue chargehand comments once and starts nothing, because a merge there ends at a check and not at a review. A repository that is `ask` or not listed has no such rule.
- **A `merge` repository needs a rule that requires its CI checks** on the base branch (a ruleset's "Require status checks to pass", or classic branch protection). Auto-merge waits only for required checks, so without one chargehand treats the repository as `ask`: it marks the pull request ready, comments once why, and does not merge ([ADR 0046](../adr/0046-integrator-and-ci-fixer.md)).
- **A repository with no test command** gets its change pushed only when every changed file is documentation (`*.md`, `docs/`) or when that same rule makes its CI the gate; otherwise the task ends `verification_failed`. Name one in `context.verify` or `driven.services.verify` to test the change yourself ([ADR 0039](../adr/0039-driven-writing-sessions.md)).
- The App needs the Issues permission (read and write) besides [the integrator's](driven.md#merging-by-policy). It needs no administration and no workflow permission and takes no webhook. Install it only on the repositories listed, or, with `discover`, only on those chargehand may work in: then every repository it can see is watched.
- The token and the key are never in a comment, a log line or an error message. A polling failure is one line on stderr and the next poll tries again.

## Profile

```json
{
  "driven": { "enabled": true, "integrator": { "app_id": 123456, "private_key_secret": "chargehand-app-key" } },
  "automation": { "github": {
    "repositories": { "octo/repo": { "path": "/srv/chargehand/repos/repo" } },
    "allowed_authors": ["octocat"],
    "label": "chargehand",
    "mention": "@chargehand",
    "poll_seconds": 120,
    "budget_class": "M",
    "max_tokens_total": 2000000,
    "max_usd_total": 5,
    "max_concurrent": 2,
    "max_review_rounds": 3,
    "review_pulls": false } }
}
```

`path` is the server's checkout of the repository (under `repository_roots`, with an `origin` remote). Each trigger fetches it with the server's own git credentials and starts from the commit `origin/HEAD` points at; a fetch that fails is a line on stderr and the batch starts from what the checkout has. `poll_seconds` is 30 to 3600. A profile that sets `automation.github` without `driven.enabled` and the integrator's App keys is refused when it loads.

**`discover: true`** makes `repositories` optional: every repository the App's installations can see, archived ones aside, is watched too. chargehand clones each into `<worker_root>/repos/<owner>/<repo>` the first time it sees it and fetches it before each start, both with the App's installation token (sent as a header in git's environment, never written to the clone's config); that directory is always among the repository roots. A repository in `repositories` keeps its own checkout. [Start from a GitHub issue](github-start.md) is the short version for a new user.

**The issue closes on merge.** The goal of an issue's task ends with `Closes #<n>`, and the pull request's body carries the goal, so GitHub closes the issue when the pull request merges. A continuation keeps the line; a review round does not touch the body.

**The trigger label.** The first time intake watches a repository it creates the label (`label`, default `chargehand`) if the repository has none.

**Switching a repository off.** `GET /v1/intake/sources` lists the watched repositories with their switch; `PUT /v1/intake/sources` with `{"sources": [{"repository": "octo/repo", "enabled": false}]}` sets it (owner's key only; a repository intake does not watch is a 404). Off, intake reads nothing of it: labels and comments wait and are read when it is on again. Batches already running still report. Each source also carries its queue: `running`, `queued` (review comments waiting for a round and triggers told to wait for a free slot) and its five latest batches. The console's Intake page shows them with a switch each.

## State

`intake/github.json` next to the run log: the comment cursor per repository, every trigger acted on or ignored, the tasks with their phase, the review comments waiting for a round, the repositories whose label was checked, and when batches started. `intake/sources.json` beside it holds the repositories switched off. It is written whole through a temporary file. Deleting it makes the server forget what it started: labelled issues start again, and comments from before the next poll are not read.

## Review comments on a chargehand pull request

The same poll reads the pull requests of the watched repositories. On a pull request that is open and whose head branch is `chargehand/<name>` in the repository itself (not a fork), these start a **review round**:

| What | Who counts |
|---|---|
| An inline review comment | an allowed author; no mention needed. A reply inside a thread needs the mention |
| A conversation comment that mentions `@chargehand` | an allowed author |

All the comments waiting for a pull request go into one round: a batch of one task, `pr-<n>`, that continues the task which opened the pull request (found in the run log by its branch, so it works for a pull request intake did not start) with one entry per comment: who, the file and line, the text. The session starts from the branch **as the remote has it**, including what a person or the CI fixer pushed since; its push is a fast-forward of the same branch, so the pull request updates itself and keeps its title and body. If the task's batch, or an earlier round, is still running, the comments wait (one comment says so) and the next round takes them.

**One cap per pull request: 3, shared with the CI fixer.** Fix attempts and review rounds are counted together, in hidden markers on the pull request itself (`chargehand-ci-fix attempt=N ...`, `chargehand-review-round attempt=N comments=...`), so a restart or a lost state file cannot start a fourth. A round also lists the comments it took, so no comment is taken twice. When the cap is used chargehand says so once; the CI fixer, finding it used, gives up too. `max_review_rounds` (default 3) can lower the rounds, never the shared cap; 0 turns the loop off. A round counts toward `max_concurrent` and `max_starts_per_day` like any batch.

When the round ends chargehand comments on the pull request: the changes are on it, with the session's summary; or what stopped it; or its questions (a reply continues the round's task). On an opted-in repository the integrator then follows the pull request as after any task.

A pull request from a fork, a branch that is not `chargehand/<name>`, a closed pull request, and comments of anyone not on `allowed_authors` are ignored. A branch that was deleted or rewritten fails the round with the reason, before a container starts.

## Reviewing every pull request

Off unless `review_pulls` is `true`. Each poll then lists the open pull requests of every watched repository, whoever wrote them. Drafts and pull requests whose author's login ends in `[bot]` are skipped. For a head commit with no review yet (kept in `intake/github.json` as `owner/repo#12@<sha>`), chargehand:

1. fetches `refs/pull/<n>/head` into the repository's `path` checkout with the server's git credentials (kept as `refs/chargehand/pull/<n>`; works for a fork too);
2. reads the diff from GitHub (cut at 60000 characters, with a marker line saying so);
3. starts a **read-only run** (not driven) of the `review` preset at that commit, with three inputs: `goal` (the title and description, fenced as text the author wrote), `diff`, and `tests` (`not run by chargehand; see the pull request's checks`).

When the run ends chargehand writes **one comment** on the pull request. It starts with the hidden marker `<!-- chargehand-review <sha> -->`, then the summary, then each claim as a finding with its confidence and evidence locators, or `No findings.`; a failed or stopped run says so with the reason. Model text is neutralised (`@` gets a zero-width space so nobody is pinged, `<` is escaped). A new push is a new commit and gets its own review and comment.

Never twice for one commit: the commit is recorded before the comment, and the marker is looked up on the pull request itself before a run starts and before the comment is posted, so a restart or a lost state file does not repeat either.

| Key | Default | When over it |
|---|---|---|
| `review_pulls` | false | off: nothing is reviewed (a review already running still gets its comment) |

Reviews share `max_concurrent` with the batches: a review waits while that many batches and reviews run. A review is admitted as `automation.account` like a batch, so the account's dollar caps bound it; `max_reviews_per_day` is ignored. A diff GitHub will not render (too large) is skipped with a line on stderr. The run reads the head commit and nothing else; the pull request's text is untrusted, and the preset has no edit, shell or web tools. To turn it on add `"review_pulls": true` to `automation.github`. A server whose checkouts are mounted read-only cannot fetch the head itself: keep `refs/pull/*/head` fetched from outside (`git fetch origin '+refs/pull/*/head:refs/pull/*/head'` in the job that updates the checkouts) and chargehand uses that ref.

## Not covered

Review summaries (the body of a submitted review) are not read, only its inline comments. A webhook instead of the poll. Issues in a repository nobody listed. Labels from a bot account. Not run against GitHub: see the ADR's UNKNOWN lists.

Work that starts on a clock instead of from an issue is in [Scheduled work](schedules.md). Sending a task's question to a phone, and its answer back, is in [Answers from the phone](phone-answers.md).
