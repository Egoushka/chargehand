# 0101. The issue sweep: chargehand finds the work, sorts it, does it and merges it

- Status: accepted
- Date: 2026-11-15

## Context

Intake (ADR 0051) starts a task when an allowed author applies the `chargehand` label. The label is the only way in, so the owner picks every issue by hand and the subscription sits unused between picks. A run of 20 hand-labelled issues failed mostly for set-up reasons that a person sees once and fixes (a checkout that tracks a denied file, an App that cannot push), and each failure was repeated per issue. The pool (ADR 0099) has a governor and durable claims but, apart from the MCP tool, no producer.

Three more forces, measured on that run:

1. The governor's history budget (ADR 0045) shrank: tasks stopped at `checkpoint_spend` never entered the history, and a percentile of cut-off spends pulled the next budget lower (#947).
2. On a subscription the governor's dollar figure is an estimate of tokens at list prices, not a bill. Acting on it stopped useful work that cost nothing extra.
3. GitHub auto-merge, armed at handover (ADR 0046), outlives the head it was armed for: a CI fix or a review round the App pushes later merges with no review of its own.

## Prior art

No prior-art query was run: the parts are chargehand's own (intake, the pool, the governor, the integrator), and the design composes them.

- Coverage: miss (not queried)

## Options

1. **Chargehand sweeps into its own pool.** The sweep and the sorting run in the server, the labels are a set it owns and syncs, and ready issues become pool items. One place holds the queue, the reasons and the console view. It is the producer the pool lacks.
2. **A script outside chargehand labels N issues on a schedule.** Ships in an hour, but keeps the label as the trigger, knows nothing of blockers or of what already runs, and the console can neither show nor start it.
3. **One GitHub Project board across all repositories as the tag store.** Central by design, with no label sync. But Projects v2 writes are GraphQL-only, the App needs organisation-level project scopes that a personal account does not offer, and the owner has moved away from boards.

## Decision

Option 1.

- **Labels.** One set in code (`SweepLabels`): `agent:ready`, `agent:needs-you`, `agent:blocked`, `agent:epic`, `agent:skip` and `size:S|M|L`. Each sweep creates or recolours them in every watched repository; it never deletes one. `you`, `in-progress`, `priority:*` and `chargehand` are read, not written.
- **When.** `automation.sweep.at` in `time_zone` on `days`; `POST /v1/sweep` and the console's Scan now start one too, with the schedule on or off. One sweep at a time.
- **Only trusted authors.** An issue is taken only when its author is in `allowed_authors` or is the integrator's App. Anyone can open an issue on a public repository, and its text becomes a goal with push access.
- **Filter before any model call.** Skip labels, live claims, open pool items, running intake tasks and open chargehand pull requests; a body that names an open blocker is `agent:blocked` with no call.
- **The sorting pass.** One `intake_model` call per issue, bound to a schema (verdict, size, reason), with the issue text fenced as untrusted. An answer that does not fit is needs-owner, `unsorted`. Verdicts are cached by a hash of title, body and labels, and `max_sorts_per_sweep` caps the calls.
- **Comments only on a change.** The reason is posted when the verdict changes from a known one, or on a first needs-owner, blocked or epic verdict. A first sweep of a few hundred issues would otherwise post a few hundred comments, and a ready issue's answer is its pull request.
- **Into the pool.** A ready issue is one pool item with scope `issue-<n>`, the issue number and the size; the goal ends with `Fixes #<n>.`. The pool's item gains nullable `issue` and `size` columns. Intake does not start a labelled issue that has a pool item; the sweep skips an issue intake runs.
- **Hand-offs by size.** The hand-offs a worker gets before its items count as failed scale with the size: S 3, M 6, L 12 (was 3 for all). A large issue legitimately needs more contexts.
- **Epics are split by a read-only run.** The run returns at most 10 sub-issues as a structured result and has no GitHub access; the server validates and secret-scans the list, files the sub-issues as the App, links them on the parent and labels it blocked. Its state is persisted so a restart resumes without filing twice.
- **Spend.** The governor's spend rule acts only on an API-key session; on a subscription it is logged as observe. The stuck signals, the context checkpoint, `max_turns` and `max_minutes` act on both. A subscription is bounded by `subscription_hold_percent` (the share of a window after which driven work moves to the fallback key), settable from the console, and from there by the account's dollar caps (ADR 0082). ADR 0100's percent caps are not enforced for driven batches yet; this decision does not depend on them.
- **The history budget (#947).** A task checkpointed on spend counts in the history at what it spent, and a history budget is never below the flat budget of its size class.
- **Review and merge.** Automatic review (`review_pulls`) covers pull requests of trusted authors and of the App. A finding at or above `review_block_confidence` (0.8) starts a review round; a review with none is the approval. No GitHub auto-merge is armed on this path. GitLab has no review by chargehand, so a GitLab `merge` repository keeps the forge's auto-merge as before. Each intake poll merges a chargehand pull request in a `merge` repository, squash, pinned to the head sha, only when the App's own clean review covers that head, the checks are green, and no ask, test-integrity or no-checks marker is present. An auto-merge the App armed earlier is turned off. The migration guard widens to any `migrations` folder and adds `agent:needs-you`. `max_review_backlog` counts a pull request with a clean review on its head as reviewed.
- **The pool's breaker.** Pool runs do not pass through intake's breaker, so the pool gets one per repository: 3 failed runs in a row hold its items (reason `breaker`); a start the repository's own set-up refuses holds them (reason `problem`), is not a failure, and shows once with its fix.
- **Console and events.** A Sweep page (counts per repository and verdict, running, needs-you list, holds, Scan now) and its settings (schedule on or off, times, days, per-repository switch, hold percent) as overrides over the profile. Events `chargehand.sweep.started`, `.finished` and `.problem` (ADR 0090).

## Consequences

- The owner files issues and decides needs-you items; ready work starts and merges on its own in `merge` repositories. A `merge` repository with `review_pulls` off merges nothing, and the server log says so at start.
- A merge lands up to one poll after its checks turn green, instead of the moment GitHub sees them.
- On a subscription a session is no longer stopped for dollars; a runaway is caught by the stuck signals, the turn and wall-clock limits, and the hold percent.
- The Sweep page has no merged count and no recent list: they need a merge history the sweep does not keep, and come with the console set-up work.
- Known gaps, accepted for now: a session that moves to the fallback key mid-batch keeps the observe spend rule; a failed review marks its head reviewed and leaves its merge until a new push; anyone can post a comment that looks like a marker, which only ever stops a merge; `GET /v1/sweep` reads every pool item to count the running ones.
- Not run against GitHub: checked against a fake host, like intake.

## Reopen if

- A sweep posts more than a handful of comments on issues that did not change, or sorts an unchanged issue twice.
- A subscription session runs for hours on a stuck loop that no stuck signal, turn or wall-clock limit caught.
- A pull request merges with a head that has no clean review from the App.
- ADR 0100's percent caps start to bind driven batches: the hold percent and they then need one rule.
