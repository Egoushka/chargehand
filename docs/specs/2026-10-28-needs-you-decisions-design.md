# Needs you: every card is a decision

- Status: draft for the maintainer's review; Task 1 recorded as ADR 0085. The direction (the inbox must say from the first look what to do and
  which options there are, and resolve more on its own, carefully) was stated by the maintainer on 2026-10-28. Every
  decision below is the proposer's.
- Date: 2026-10-28
- Amends: ADR 0064 and ADR 0073 (a new ADR, Task 1, records the amendment)

## Goal

A person opens Needs you and, for each card, knows in one read what happened, why, what to do, and what each option
costs and does, without opening a run. Items no person needs to act on leave the inbox by rules that are visible,
reversible and proven in a shadow mode before they act.

Done when: (1) every open card on the owner's server shows a plain sentence, a cause and at least two options with
their effect; (2) no error code appears on a card without its plain meaning; (3) one cause shows as one card; (4) each
new auto-resolve rule ran a week in shadow mode, and a person would have dismissed at least 95% of what it marked;
(5) over two weeks of use the maintainer dismisses by hand fewer than 10 items a week.

## Where it stands

Evidence: the owner's server, `GET /v1/inbox?since=60d`, read 2026-10-28, and `main` at 3cc72d43.

1. **Mostly noise, cleared by hand.** 82 open items in 60 days: 79 failures, 2 questions, 1 pull request. ADR 0064
   and 0073's rules had resolved 44 more (`superseded` 16, `infrastructure` 21, `continued` 1, `duplicates` 4,
   `pr_closed` 2). The maintainer then dismissed 71 failures older than three days in one call; 11 items remained.
2. **A card names the event, not the action.** The title is the task's goal text ("Implement Task 6 of …"), the reason
   is a code (`session_failed`), and the error the card shows can say nothing a person can use: `"the session did not
   finish: session error"` with `retryable: false` and the action `"Read the session log … and send the task again."`
3. **One cause, many cards.** In the 14-day view before the cleanup: 11 `checkout_has_secrets` (10 on one repository's
   reviews) and 11 `cost_cap_reached` (reviews stopped by their budget). ADR 0073 groups them per repository and code,
   but a review budget too small for every repository is still one card per repository.
4. **The options are Answer, Open and Dismiss.** `web-ui.md` says "the server has no route to … retry a task", yet
   `POST /v1/batches/{id}/tasks/{task}/retry` exists (`server.md`, Batch routes). The console does not offer it.
   `chargehand takeover` (CHARGEHAND-443) and the console's Take over button exist, but not on Needs you.
5. **The rules are not visible.** `resolved` gives counts only; a person cannot see which item a rule took or undo it.
6. **No owner.** An item names a repository and an account, not who should act (the maintainer runs one desk per area).

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | What a card says | Four parts, always in this order: **what** (one sentence: "A review of pull request #649 was refused"), **why** (the cause in plain words: "the checkout holds files that look like secrets, so the review preset will not read it"), **options** (two or three buttons, each with its effect and, when it spends, its estimate: "Retry, ≈ $0.40"), **owner** (the repository, and the desk or person the profile maps it to, decision 7). The task's goal moves under the card, collapsed. |
| 2 | Where the words come from | A catalog in code, one entry per error code in `docs/guide/reference.md`'s catalog: `what` and `why` templates filled from the item (repository, pull request, budget, spent), and the options that apply. A test fails when a code the server can emit has no entry. The catalog is the source for the card, the morning report and `chargehand inbox`, so the words are the same everywhere. |
| 3 | The vague codes | Only `session_failed` and `session_stalled` carry no cause in their code. For those, after the task ends, a cheap model reads the session log's last 200 lines and writes one sentence of cause, at most 160 characters, stored on the learning record as `cause` with its model and cost. Off by default (`inbox.explain`), a dollar cap per day, and the card says "explained by a model". It never changes what a rule does: no rule reads `cause`. |
| 4 | Three lanes | **Decide**: questions, pull requests waiting for a person, and Fix cards that need a policy choice. **Fix**: failures, one card per cause (decision 5). **Handled**: what a rule resolved or retried in the window, newest first, each with its rule, its reason and Undo. Decide is first and is the count in the badge. Handled is not counted. |
| 5 | One cause, one card | Group open failures by code, and by repository only where the cause is the repository's: `checkout_has_secrets`, `verification_failed`, `push_rejected`, `merge_conflict` group per repository; `cost_cap_reached` on reviews groups across repositories by preset, because the cause is the preset's budget. A group gains `cause_key`, additively; 0073's per-repository groups stay in `groups` for older clients. |
| 6 | The options | Each option is a server call that already exists or that Task 4 adds, never a link to a manual step. **Retry** (`POST /v1/batches/{id}/tasks/{task}/retry`, with the estimate from the task's past spend); **Retry with a bigger budget** (same, `max_usd` × 2, capped by the account); **Take over** (shows the `chargehand takeover` command and copies it); **Answer** (as today); **Exclude repository from reviews** and **Raise the review budget to $X** (Task 4: a profile override the server keeps in its state file, shown in Intake, undoable); **Snooze 7 days**; **Dismiss**. A card shows only options that can succeed: no Retry on a task still running, no Retry for `checkout_has_secrets`. |
| 7 | Owner | `inbox.owners` in the profile maps a repository glob to a name (a desk, a person); a card shows it, and the morning report and `notify_url` group by it. No owner configured: the account's name. |
| 8 | Auto-resolve: which rules | Five new rules, each in `InboxRules`, each with a reason string on the item: **(a) source gone**: the issue a task came from was closed, or the pull request a failure is about was merged or closed, including pull requests chargehand did not open (read through the forge, cached 10 minutes); **(b) auto-retry**: an infrastructure failure (0064's list) is retried once by the server, and is shown only if the retry fails; the retry is a normal batch with its caps, at most `inbox.auto_retry.max_usd_per_day` (default $2), and never for a task that asked a question; **(c) budget card**: `cost_cap_reached` reviews become one Decide card proposing a review budget at the 80th percentile of completed reviews' spend in 30 days; the individual failures move to Handled; **(d) stale**: a failure with no newer run on its repository and task for 7 days moves to Handled as `stale`; **(e) superseded by a merge**: a failed task whose issue has a merged pull request from any author since. Questions and pull requests waiting for a person are never auto-resolved. |
| 9 | Auto-resolve: how carefully | Every new rule ships in `shadow` mode: it marks items `would_resolve: <rule>` and changes nothing; the card shows a small "would be resolved: <reason>" line. After 7 days, `chargehand inbox rules report` shows per rule how many items it marked and how many a person then dismissed, restored or acted on. A rule moves to `on` only by a profile change after its precision is at least 0.95 on at least 10 items. A restore of an item a rule resolved counts against that rule and is listed in the report. |
| 10 | What is not built | Free-form model triage of every item (decision 3 is the only model call), an inbox per account in a separate store, notifications per item (the existing notices stay), and any rule that closes a pull request or an issue. |

## Tasks (each one pull request)

1. **ADR** amending 0064 and 0073 with decisions 4, 5, 8 and 9.
2. **Catalog** (decision 2): `InboxCatalog` with an entry per emitted code; `inbox/v1` items gain `what`, `why`,
   `options` (each `{id, label, effect, estimate_usd?, call}`), additively. Test: every code in the reference table
   has an entry; each option's `call` names a route that exists.
3. **Cards and lanes** in the console (decisions 1 and 4): the four-part card, Decide, Fix, Handled, the badge counts
   Decide only. Fix `web-ui.md` where it says there is no retry route.
4. **Options that change policy** (decision 6): exclude a repository from reviews and set a review budget, kept in the
   server's state, shown in Intake, with undo. Retry and Retry bigger on the card.
5. **Cause groups** (decision 5) and **owners** (decision 7).
6. **Shadow mode and the rules report** (decision 9), with rule (d) first: it needs no forge call.
7. **Rules (a), (c), (e)** in shadow mode.
8. **Auto-retry (b)** in shadow mode, with its daily cap.
9. **Model cause** (decision 3), off by default.
10. **Switch on**: after a week of shadow data, a profile change per rule that met the bar, with the report in the PR.

## Acceptance checks

Run against a fake inbox in tests, and against the owner's server where it says so.

- `InboxCatalogTests`: for each code in `docs/guide/reference.md`'s error table, an entry with non-empty `what` and
  `why`; a test lists the codes the server can emit and fails on any without an entry.
- `inbox/v1` contract: an older client that ignores `what`, `why`, `options`, `lane` and `would_resolve` still parses
  the body; `SchemaCompatTests` stay green.
- A `session_failed` item with no model cause shows "the session ended with an error; its log has the details" and the
  options Retry, Take over, Dismiss, never a bare code.
- A `checkout_has_secrets` group shows no Retry, and shows Exclude repository from reviews and Dismiss.
- Five `cost_cap_reached` reviews on three repositories show as one Decide card with a proposed budget; accepting it
  sets the budget, and the five move to Handled with reason `budget_raised`.
- A rule in `shadow` changes no `counts`; a rule `on` moves its items to Handled; Undo on a Handled item puts it back and
  the rules report counts a restore against that rule.
- Auto-retry stops for the day at its cap and says so on the next card.
- No rule resolves a `question` or a `pull_request` item (a test over every rule).
- The console at 375 and 1280 px, dark and light, passes axe-core; a card's options are 44 px targets.
- On the owner's server after Task 10: the Decide badge at most 10 for a week, read daily from `GET /v1/inbox`.

## Risks

- **A rule hides a real item.** Mitigation: shadow first, the precision bar, Handled with Undo, and the report.
- **Auto-retry spends without asking.** Mitigation: infrastructure codes only, once per task, a daily dollar cap, the
  account's caps, and the retry is a normal batch in Runs.
- **The catalog goes stale** when a code is added. Mitigation: the test that fails on a code with no entry.
- **The model cause is wrong.** Mitigation: labelled as a model's, never read by a rule, off by default.
- **A forge call per item** for rules (a) and (e). Mitigation: a 10-minute cache per pull request and issue, filled in
  the background; `GET /v1/inbox` reads the cache and never waits on the forge. This is the first inbox state not
  derived from the run log on each call (0064 stored only dismissals); the ADR (Task 1) records it.

## Out of scope

Closing issues or pull requests from the inbox, an inbox of its own per person, and a mobile app.
