# The issue sweep: chargehand finds the work, sorts it, does it and merges it

- Status: accepted 2026-11-14 (owner approved the design, adding the schedule's on/off and days from the console; owner's answers in conversation: process everything; sweep at 08:00, 15:00 and 20:00 plus a
  console button; epics are split first; triage and backlog go through the sorting pass; chargehand reviews and merges
  its own green pull requests; the subscription hold is set from the console). Builds on ADR 0099 (the task pool),
  ADR 0051 (intake from GitHub), ADR 0045 (the governor), ADR 0046 (the integrator), ADR 0097 (earned autonomy),
  ADR 0100 (subscription caps).
- Date: 2026-11-14

## The job

The owner wants chargehand to deliver issues, not wait to be told about each one. Today a run starts only when the owner
adds the `chargehand` label by hand, issue by issue, repository by repository. Labelling 20 issues on 2026-11-14 showed
what that costs. Most runs failed on configuration: a budget learned from history had shrunk far below the flat budget (#947), a
repository was refused over a public `.env`-named file, and a checkout pointed at a renamed repository. Nothing told the
owner until he looked. The owner's words: "burn tokens", "process everything", "use chargehand as a subagent system".

Success is that the owner opens the console and sees what chargehand found, what it is doing, what it merged and the
few things that need him. He no longer finds and labels the work himself.

## Goals

1. Three times a day (08:00, 15:00, 20:00, profile time zone) and on a console button, read every open issue of every
   watched repository.
2. Sort each one with a cheap model pass: ready for an agent, needs the owner, blocked, or an epic to split. Write the
   verdict back as labels from one central label set.
3. Queue every ready issue in the task pool; the pool's governor starts the work.
4. Review chargehand's own pull requests automatically; merge the clean green ones where the repository allows it.
5. Spend: a task on the subscription is bounded by the subscription's own caps and the stuck signals, not by a dollar
   budget. Dollar budgets stay for paid credentials.

## Non-goals

- The console's broader redesign and its settings pages. That is project 2 (below); this project adds only the Sweep
  page and three settings it needs.
- Wheelhouse wiring. That is project 3.
- Issues in repositories that are not in `automation.github.repositories`.

## Anti-goals (a failure even if it works)

- Work on an issue the owner kept for himself (`you`) or one blocked by an open issue.
- A merge of a pull request that adds a schema migration. The owner merges those himself.
- A sweep that re-sorts unchanged issues each time and burns the cheap model on nothing.
- The same issue started twice (by the sweep and by a label, or by two sweeps).
- Silent failure: a configuration error (refused checkout, no push access) must show on the Sweep page as a repository
  problem, once. It must not appear as N failed runs that trip the per-repository breaker.

## Constraints

- The server polls GitHub as the App; it cannot receive webhooks.
- The profile is the owner's GitOps file and stays the default. Console settings are overrides in the server's state,
  in the same pattern as `PUT /v1/intake/limits` (`ConsoleLimits`).
- The repository is public: issue text is untrusted input to the sorting model and to the worker. The sorting model's
  output is only a verdict, a size and a reason, validated against a schema. It never chooses tools or commands.
- The `chargehand` label keeps working as it does today: a manual "do this one now".

## Approaches

1. **Chargehand sweeps into its own pool (picked).** The sweep and the sorting run in the server. The labels are a set
   the server owns and syncs. Ready issues become pool items. One place holds the queue, the reasons and the console.
   It uses the pool built on 2026-11-12, whose missing producer is exactly this.
2. **A workspace script labels N issues on a schedule.** It ships in an hour but keeps the label as the trigger. It has
   no sense of blockers or of what is already running, and it lives outside chargehand, so the console cannot show it
   or start it.
3. **One GitHub Project board across all repositories as the central tag store.** It is central by design, with no label
   sync. But Projects v2 is GraphQL-only for writes, the App needs organisation-level project scopes on a personal
   account, and the owner moved away from boards.

## Design

### 1. Labels: one set, owned by chargehand

A table in code (`SweepLabels`), with name, colour and description:

| Label | Meaning | Written by |
|---|---|---|
| `agent:ready` | the sorting pass says an agent can do it now | sweep |
| `agent:needs-you` | needs the owner (a decision, an account, a secret, physical access) | sweep |
| `agent:blocked` | waits on an open issue or a named event | sweep |
| `agent:epic` | too big for one task; split first | sweep |
| `agent:skip` | the owner says never touch it | owner |
| `size:S`, `size:M`, `size:L` | the sorting pass's estimate (intake already reads these) | sweep |

The existing `you`, `in-progress`, `priority:*` and `chargehand` labels keep their meaning and are read, not written.
At the start of each sweep the server creates any label from the set that a watched repository lacks. It updates a
label's colour or description when they differ, and never deletes a label.

### 2. The sweep

- **When:** `automation.sweep.at` (default `["08:00","15:00","20:00"]`), in `automation.sweep.time_zone` (default UTC). The console button `Scan now` and `POST /v1/sweep` (owner key) also start one. One sweep at a time: a
  request during a sweep returns the running sweep's id.
- **Read:** every open issue (not pull requests) of every watched repository, paged, with labels, body, `updated_at`
  and the timeline's cross-references.
- **Only trusted authors:** the sweep takes an issue only when its author is in `allowed_authors` or is the
  integrator's App (the sub-issues of a split). Any other author's issue is skipped before any model call: on a public
  repository anyone can open an issue, and its text would become a worker's goal with push access. The owner runs such
  an issue by applying the `chargehand` label himself, as before.
- **Filter before any model call:** skip `you`, `in-progress`, `agent:skip`, an issue with a live claim ref (as intake
  does), and an issue that already has an open pool item, a running task or an open chargehand pull request.
  - **Blockers:** an issue is `agent:blocked` without a model call when its body names `#n` after "blocked by",
    "depends on" or "after #n" (case-insensitive, same repository or `owner/repo#n`) and that issue or PR is still open.
- **Cache:** the sorting verdict is kept per issue with a hash of title, body and labels. An unchanged issue is not sorted
  again; a changed one is.

### 3. The sorting pass

One call per issue to `intake_model` (Haiku), with a structured output: `verdict` (`ready` | `needs_owner` | `blocked`
| `epic`), `size` (`S` | `M` | `L`), `reason` (one sentence, at most 200 characters). The input is the issue's title,
body (cut at 8 KiB) and labels, plus the repository's `CLAUDE.md` first 2 KiB as context. The issue text is fenced as
untrusted. An output that fails the schema counts as `needs_owner` with reason `unsorted`. The labels are written
(removing the other `agent:` verdict labels). The reason is posted as one comment, with a hidden marker, only when the
verdict changes, so a re-sort does not spam the issue.

### 4. Into the pool

Each `ready` issue becomes a pool item (the pool's item gains two nullable columns, `issue` and `size`, by a
`user_version` step, and the run starter passes the size to the task): source `sweep`, `repository`, `area` `issue-<n>` (one scope per issue, so issues
run in parallel up to the pool's checks), the title, the body, and `priority` from `priority:urgent` 2, `priority:high`
1, otherwise 0. The item carries the issue number, so the worker's pull request says `Fixes #n`. The item's size label
sets the governor's size class. An `epic` becomes one pool item whose goal is to split it. The worker has no GitHub
write access: it returns a list of sub-issues (title, body, at most 10) as a structured result. The server validates and
secret-scans the list, then files the sub-issues as the App and links them in a task list on the parent. No pull request
is opened. The next sweep sorts the sub-issues.

The manual `chargehand` label and the sweep must not both start one issue. Intake checks the pool for an item on that
issue, and the sweep checks intake's state for a running task.

### 5. Spend

- The governor's spend rule (checkpoint at 85%, stop at 100%) applies when the session's credential is an API key.
  On the subscription it is logged (observe), not acted on. The stuck signals, the context checkpoint, `max_turns` and
  `max_minutes` still act on both.
- Fix #947: a task stopped at `checkpoint_spend` counts in the history at what it spent, and a history budget is never
  below the flat budget of its size class.
- The subscription is bounded by `subscription_hold_percent`, the share of a window after which driven work moves to the
  API key. From there the account's dollar caps (ADR 0082) bound it. ADR 0100's percent caps are not enforced for
  driven batches yet, and this project does not depend on them. The hold becomes a console setting (section 7).
- The pool's `worker_tokens` ceiling stays: it bounds one context, not spend, and a worker hands over with a resume card.
  The hand-off limit after which items count as failed (`PoolRunStarter`, 3 today) scales by size: S 3, M 6, L 12
  (builder's call made in review).

### 6. Review and merge

- Automatic review (`review_pulls`) covers only pull requests whose author is in `allowed_authors` or is chargehand's
  own App; anyone else's pull request is reviewed only on an allowed author's `@chargehand review`, as today.
- `review_pulls` also covers pull requests opened by chargehand's own App. Today `PullReviews` skips every `[bot]` author,
  so the App's login is let through by name.
- A blocking finding is one with confidence at or above `automation.github.review_block_confidence` (default 0.8). No
  schema change: findings already carry a confidence. A blocking finding starts a review round on the same branch, with
  the finding as the answer, up to `max_review_rounds`. A review with no blocking finding is the approval.
- The gate applies to every chargehand pull request, label-started ones too. Today the integrator turns on GitHub
  auto-merge at handover (`Integrator`). It now marks the pull request ready and enables auto-merge only after a clean
  review on the head, in a repository whose mode is `merge` (or earned under ADR 0097), with green checks.
- Migrations: reuse the existing guard (`MigrationPaths`, which switches the pull request to "ask" with a marker comment).
  Widen its patterns to `**/Migrations/**`, `**/migrations/**` and `db/migrations/*.sql`, and add the
  `agent:needs-you` label in the same step.
- `max_review_backlog` counts a pull request as reviewed when chargehand's clean review is on its head. Otherwise the
  backlog never drains, since the owner no longer reviews code.

### 7. Console

A **Sweep** page:

```
Sweep                      last 15:00 (4 min)  next 20:00   [Scan now]
 repo            ready  running  merged  needs you  blocked  epic  problem
 owner/app          3        2       5          1        0     0   -
 owner/infra        0        0       0          0        0     0   checkout refused: tracks a denied file (fix: public_files)
Needs you (4)       issue, reason, link
Recent              merged / failed / split, with the run link
```

Settings (overrides, profile is the default; each shows profile, override, effective, as Limits does): the sweep's
schedule (on or off as a whole, the times, and the days of the week it runs, so the owner can turn it off for days he
does not want it; `automation.sweep.days`, default every day), sweep on/off per repository, and
`subscription_hold_percent`. `Scan now` works when the schedule is off. A repository problem (checkout refused, App cannot push,
breaker open) shows once in its row, with the fix in one line. The sweep holds that repository's items with that reason
instead of starting runs that fail. Pool runs do not pass through intake's breaker (`IntakeBreakers`), so the pool
gets the same per-repository rule: 3 failed runs in a row hold that repository's items with reason `breaker`, and a
repository problem holds them with reason `problem` without counting as a failure.

### 8. Events

The sweep emits `sweep.started`, `sweep.finished` (counts per verdict) and `sweep.problem` to the event log (ADR 0090), so
project 3 (wheelhouse) can show them without polling the console.

## Acceptance checks (machine-run)

1. With a fake GitHub host holding 10 issues across 2 repositories (one `you`, one blocked by an open issue, one blocked
   by a closed one, one epic, one `agent:skip`, five plain), a sweep calls the model 6 times, writes the expected labels,
   and enqueues 5 items plus 1 split item. A second sweep with nothing changed makes 0 model calls and enqueues nothing.
2. A label missing in a repository is created on the next sweep; a label with another colour is updated; an extra label
   is untouched.
3. `POST /v1/sweep` during a sweep returns the running sweep's id; a non-owner key gets 403.
   With the schedule off, or today not in its days, no sweep starts at a scheduled time, and `POST /v1/sweep` still runs one.
4. An issue with the `chargehand` label and an open pool item starts exactly one task.
5. A subscription session past 100% of its dollar budget is not stopped; an API-key session is.
6. History budget: with past spends that would give a p80 under the flat M budget, the budget is the flat M budget.
7. A green chargehand pull request with a clean review in a `merge` repository is merged. The same with a blocking finding
   starts one review round. One adding `src/X/Migrations/1_add.cs` is not merged and gets `agent:needs-you`.
8. A repository whose checkout is refused shows one `sweep.problem` and no failed batch, and the pool's breaker does not
   count it.
9. A chargehand pull request has GitHub auto-merge enabled only after a clean review on its head.
10. An epic's split result with 3 sub-issues files 3 issues and edits the parent once; a result whose body holds a
    credential files nothing.
11. `scripts/check.sh` exits 0.

## Later projects (agreed order)

2. **The console as the place to set everything up.** Settings for accounts, caps, repositories, the integrator and the
   sweep, with a cleaner, user-first layout. Every look change goes through the owner's UI design gate (before/after
   screenshots).
3. **Wheelhouse wiring.** Chargehand emits `chargehand.run.*` events. A wheelhouse session hands a goal to the pool and
   waits on it like a child session. Wheelhouse shows the Sweep counts and the "needs you" list.

## Left to the builder

The module split (a `Sweep` piece in `Chargehand.Automation` beside `GitHubIntake` is the expected home), the state file
format for the verdict cache, the colours of the labels, the exact prompt text, and paging and rate-limit handling against
the GitHub API.
