# The factory cockpit: the console as the place the owner runs the agent factory from

- Status: proposed 2026-11-10, for the maintainer's review. Nothing here changes code; every increment below is its own
  pull request. Builds on ADR 0056 (the console), ADR 0085 (every inbox card is a decision), the
  [client spec](2026-10-02-a-client-of-your-own-design.md), [threads and resume cards](2026-11-08-threads-and-resume-cards.md)
  and ROADMAP stage 6. Issue #568 asked for the scope before building; this is that scope, re-read against what shipped.
- Date: 2026-11-10

## The job

chargehand runs fleets of coding agents: driven batches, research runs, conversations over ACP and intake from issues,
with quality gates (checked citations, verification, the mutation check, the governor, earned autonomy, the budget
router). One person owns it. The console's job is the factory floor: **launch and steer many agents, watch them live,
judge the quality of what they made, approve and merge, see spend and quota windows, and see whether the factory is
learning.** Everything below is measured against those six verbs.

## 1. What exists: screens and the API each reads

Routes are registered in `web/console/src/router.tsx` and listed for the rail in `web/console/src/shell/screens.ts`.
Clients live in `web/console/src/api/`; the server side is `src/Chargehand.Server/Console/*.cs` unless named.

| Screen (route) | Code | Reads | Acts |
|---|---|---|---|
| Overview (`/`) | `screens/overview/Overview.tsx`, `Panels.tsx` (Needs you, Spend today vs caps, Admission, System, Recent merges), `ActiveBatches.tsx` | `GET /v1/overview`, `/v1/inbox`, `/v1/pulls`, `/v1/system`, `/v1/runs?kind=batch` | halt, resume (`api/queries.ts`) |
| Needs you (`/needs-you`) | `screens/needs-you/NeedsYou.tsx`, `NeedsCard.tsx`, `Options.tsx` | `GET /v1/inbox` (lanes, cause groups, `approvals` count; `InboxCatalog.cs`) | each option is the server call the card carries (`api/answer.ts`); dismiss, restore |
| Runs (`/runs`) | `screens/runs/Runs.tsx`, `RunsTable.tsx` (virtualised) | `GET /v1/runs?...` | cancel one or many |
| Batch run (`/runs/$runId`) | `screens/run/RunPage.tsx` → `screens/batch/Batch.tsx`; tabs in `tabs.ts`: tasks (graph, `graph/TaskGraph.tsx`), timeline, events, governor, spend, pull requests, artifacts, request | `GET /v1/batches/{id}`, `/v1/runs/{id}/events` (SSE, `api/useRunEvents.ts`), `/v1/analytics`, `/v1/pulls` | cancel, retry, stop session, rerun checks (`api/actions.ts`); clone to Start |
| Research run (`/runs/$runId`) | `screens/research/Research.tsx` (claims, citation per claim at the commit, support verdict, confidence, open questions, signature) | `GET /v1/runs/{id}`, `/v1/runs/{id}/events` | none |
| Task (`/runs/$runId/tasks/$taskId`, or the drawer) | `screens/task/TaskView.tsx`, `TaskDrawer.tsx`; diff in `screens/code/DiffView.tsx`; verification facts from `screens/batch/artifacts.ts` | `GET /v1/batches/{id}/tasks/{task}` (`BatchTaskDetailBody` in `ConsoleBatches.cs`: verification, session tests, changes, review, kept work, checks), `.../sessions/{s}/transcript?bytes=` (polled) | answer and continue (`POST /v1/runs` with `continue_from`), cancel, retry, stop |
| Start a batch (`/new`) | `screens/start/Start.tsx`, `Composer.tsx`, `TaskEditor.tsx`, `DependencyGraph.tsx`, `Forecast.tsx`, `SingleRun.tsx` | `GET /v1/repositories`, `/v1/system`; `POST /v1/forecast` | `POST /v1/runs` |
| Analytics (`/analytics`) | `screens/analytics/Analytics.tsx`: spend and tokens over time (uPlot), pass rate, budgets vs p80, durations, governor, continuations, `CostPanel.tsx` | `GET /v1/analytics`, `/v1/cost` (dollars per merged pull request, `ConsoleCost.cs`) | none |
| Learning (`/learning`) | `screens/learning/Learning.tsx`, `model.ts` (per size class: budget, spend, stop reasons, `merged_unchanged_rate`, warning under 0.5) | `GET /v1/learn?since=` | none |
| Intake (`/intake`) | `screens/intake/Intake.tsx`, `Sources.tsx`, `ReviewPolicy.tsx` | `GET /v1/intake`, `/v1/intake/sources`, `/v1/pulls` | `PUT /v1/intake/sources`, `POST /v1/intake/reviews/include`, `.../budget` |
| Settings (`/settings`) | `screens/settings/Settings.tsx` | `GET /v1/intake/limits` | `PUT /v1/intake/limits` |
| Repositories (`/repositories`) | `Readiness.tsx`, `ConflictRadar.tsx`, `MergeTrain.tsx`, `LearnedHosts.tsx` | `GET /v1/repositories`, `/v1/pulls/conflicts`, `/v1/pulls/train`, `/v1/egress/learned` | approve, revoke a learned host |
| Accounts (`/accounts`) | `screens/accounts/Accounts.tsx` | `GET /v1/accounts`, `/v1/limits/daily-warning`, `/v1/limits/run-approval` | `PUT` of both limits |
| System (`/system`) | `screens/system/System.tsx` | `GET /v1/system` (runtime, runners, driven limits, integrator, learning window) | none |
| Shell | `shell/CommandPalette.tsx`, `hotkeys.ts`, `notifications.ts`, `SignIn.tsx` | `GET /v1/accounts/identity`, `/v1/accounts/me` | sign in, theme, density |

**Server routes no screen reads** (from `MapGet`/`MapGroup` in `src/Chargehand.Server`): `/v1/conversations` (start,
read, events, messages, approvals, end; `Conversations/ConversationRoutes.cs`; there is no list route), `/v1/events`,
`/v1/events/stream`, `/v1/threads`, `/v1/threads/{id}/card` (`Events/EventRoutes.cs`), `/v1/fleet` (`ConsoleFleet.cs`:
runs, claims and open pull requests with `blocked_by`), `/v1/intake/rank` (`ConsoleRank.cs`), `/v1/limits/public-free`
(`ConsolePublicFree.cs`). `POST /v1/approvals/{id}/approve|decline` (ADR 0092) is reached only through a card's option.

## 2. Gaps against the job

| Verb | What exists | What is missing |
|---|---|---|
| Launch and steer many | Start composes one batch for one repository with a dependency graph and a forecast. Steering is cancel, retry, stop session, answer and continue. | No launch of one task across many repositories or from a saved template. Conversations (0.20) have routes but no screen, and no list route to build one on. No fleet view: `/v1/fleet` is read by nobody. Threads and resume cards (`/v1/threads`) are not shown. |
| Watch live | A batch and a research run stream `/v1/runs/{id}/events`; the task graph updates; notifications on events. | The transcript is polled by byte window (`api/transcript.ts`), not streamed. There is no one board of everything running across runs with its blocker (`waiting_for_slot`, `rate_limit`) and age. `/v1/events/stream` is unused. |
| Judge quality | Research: every claim with its citation and support verdict (`Research.tsx`). Code: the diff, verification pass or fail with exit code, the test-integrity flag, the session's own tests kept apart (`TaskView.tsx`), checks per pull request as integrator looks (`ConsoleBatches.cs`). | **The mutation score is not a field**: it lives in the `verification_failed` reason text only (`docs/guide/driven.md`, the mutation check), so a passing score is never shown. The `review` artifact (the research and review runs a session started) is in `BatchTaskDetailBody` but no screen renders it. No one place per task sums these up; no side-by-side of two tasks on the same goal. |
| Approve and merge | Cards in Needs you are decisions with options from `InboxCatalog.cs`; a held run has Approve and Decline (ADR 0092). | A pull request waiting for a person has only Open PR: the server has no merge route (ADR 0056, amendment 1; still true in the route map). The merge train (`MergeTrain.tsx`) is read-only. Earned autonomy (`src/Modules/Chargehand.Modules.Oversight/Api/EarnedAutonomy.cs`, ADR 0097) has no route and no screen: the owner cannot see which repository earned auto-merge or why it shrank. |
| Spend and quota windows | Spend today vs caps on Overview; spend, tokens and cache share over time and dollars per merged pull request on Analytics; forecast on Start; the daily warning and the approval threshold on Accounts. | `subscription_usage` (ADR 0098) is on `GET /v1/runs` rows but `api/types.ts` does not read it: the console never shows how far into its windows a subscription was. The budget router (ADR 0095: pools, balances, today's allowance, the tier picked) has no route. A paused scheduler (`rate_limited`) shows only as `blocked_by` in the unread `/v1/fleet`. |
| Learning and merge rate | Learning shows `merged_unchanged_rate` per size class with a warning under 0.5 (`learning/model.ts`); `/v1/cost` gives dollars per merged pull request. | No trend of merge rate over time, no per-repository view, no link from a learning row to the runs behind it, and no autonomy state beside the rate it is computed from. |

## 3. Target information architecture

The rail keeps its three groups (`shell/screens.ts`); what changes is what the first screen is and what a task page
is for. Screens in **bold** are new or reworked.

```
main       **Cockpit** (/)        Needs you      Runs      **Conversations**      Start
insight    **Quality**            **Spend**      Learning
configure  Intake   Repositories  Accounts   Settings   System
```

- **Cockpit** replaces Overview's panels with a live board of `/v1/fleet` plus the open threads, spend and the decide
  count. Nothing on it is more than one click from the thing it names.
- **Quality** is a view over tasks, not runs: each task is one quality card (sketch B), filterable by verdict, with a
  compare mode for two tasks on one goal (two arms, two models, two prompts).
- **Spend** is Analytics with the windows and the router's pools folded in; Analytics' charts stay as its lower half.
- **Conversations** is the client spec's Tasks 4 and 5 (list, live transcript, send, approve, cancel) on the routes
  that exist in `ConversationRoutes.cs`, at phone width.

### Sketch A: the cockpit

```
+------------------------------------------------------------------------------------------------+
| COCKPIT                      decide 3 | fix 2        day $12.40 / $40   week 41% ▮▮▮▮░░░░ resets 2d |
+---------------------------------------------+--------------------------------------------------+
| RUNNING (6)                 age   blocker   | OPEN PULL REQUESTS (4)           state            |
| ▶ batch  repo-a  task 2/5   12m   -         | #n  repo-a  checks ✓  mutation .71  awaiting you  |
| ▶ batch  repo-b  task 1/1    4m   -         | #n  repo-b  checks ✗ (fix 2/3)      integrator    |
| ⏸ batch  repo-c  queued      -   slot       | #n  repo-c  partial draft           awaiting you  |
| ⏸ conv   repo-a             30s  rate_limit |                                                  |
| ● research repo-d            2m   -         +--------------------------------------------------+
|                                             | THREADS (open)                                   |
+---------------------------------------------+ d-xx  goal ...   next: ...   moved 2h ago by ...  |
| CLAIMS (2)  repo-a#n held by run ..  40m    | d-yy  goal ...   next: ...   moved 1d ago by ...  |
+---------------------------------------------+--------------------------------------------------+
```

### Sketch B: the quality card (one task)

```
+------------------------------------------------------------------------------------------------+
| task 3 of 5  repo-a  goal: "..."                 model: tier/strong (router: pool 2, $0.84)     |
| VERDICT  ✓ verification (exit 0, 48s)   ✓ mutation 0.71 ≥ 0.60   ✓ checks 4/4   ⚠ integrity    |
+-----------------------------+------------------------------------------------------------------+
| evidence                    | diff  12 files  +340 −88                [side by side] [unified] |
| · verification: cmd, tail   | src/.../Foo.cs                                                   |
| · session tests (apart)     |  - old                                                           |
| · mutation: survivors 7/24  |  + new                                                           |
| · review run: 9 claims,     |                                                                  |
|   8 supported, 1 refuted →  |                                                                  |
| · governor: 0 stops         |                                                                  |
+-----------------------------+------------------------------------------------------------------+
| [Approve merge]  [Retry]  [Answer]  [Compare with task 4 ▸]                                     |
+------------------------------------------------------------------------------------------------+
```

### Sketch C: spend and windows

```
+------------------------------------------------------------------------------------------------+
| SPEND                       today $12.40 / warn $40          forecast month ≈ $310 (ADR 0076)  |
| subscription  5h window  ▮▮▮▮▮▮░░ 72%  resets 1h 10m     weekly ▮▮▮▮░░░░ 41%  resets 2d 4h    |
| pools (order) 1 free-public  ▮▮▮▮▮▮▮▮ 100%   2 subscription 41%   3 api-key $18 / $60 today   |
+------------------------------------------------------------------------------------------------+
| spend over time (uPlot, existing)     | $ per merged PR by repository and preset (existing)      |
+------------------------------------------------------------------------------------------------+
```

### Sketch D: launch many

```
+------------------------------------------------------------------------------------------------+
| START            template: "bump dependency X" ▾   preset: code ▾   tier: auto (router) ▾      |
| goal  [ ...................................................................................... ] |
| repositories  [x] repo-a  [x] repo-b  [ ] repo-c   (3 ready, 1 not: no verify command)         |
| forecast  3 tasks  ≈ $2.70  slots 3/4 free   subscription weekly after: 44%                     |
|                                                                [Start 3 batches]                |
+------------------------------------------------------------------------------------------------+
```

## 4. The plan: nine increments, one pull request each

Ordered by value to the owner, whose stated focus is the quality of output. **Server** marks an increment that needs a
new route or an additive field; the rest are console-only. Every increment ends with `scripts/check.sh` green and, for
the console, a Vitest screen test in the screen's folder and the Playwright smoke test unchanged.

1. **Quality card on the task page** (sketch B). **Server:** `BatchTaskDetailBody` gains an additive `mutation`
   member (score, threshold, survivors, killed, `not_run` reason) written by the mutation check beside `verification`
   (`src/Modules/Chargehand.Modules.Handover/Api/MutationCheck.cs`); `batch-task` fixtures gain examples. Console:
   `TaskView.tsx` opens with the verdict row and renders the `review` artifact's claims with their support verdicts.
   Accept: a task with a passing score shows it; one below threshold shows the score and the reason; a task that changed
   no C# production file shows "not checked"; `SchemaCompatTests` stays green (additive).
2. **Cockpit board** (sketch A) from `/v1/fleet`: running, queued and rate-limited items with age and blocker, claims,
   open pull requests with their blocker, polled every 10 s; the Needs you and Spend tiles stay. Console-only.
   Accept: `overview.test.tsx` renders the three item kinds with `blocked_by`; `rate_limit` is a visible pause banner;
   each row links to its run, task or pull request.
3. **Windows and pools on Spend** (sketch C). Console: read `subscription_usage` and `credential` from `GET /v1/runs`
   rows into `api/types.ts`, show the latest window utilisation on the Cockpit and per run in the Runs table.
   **Server:** `GET /v1/router` (`router/v1`): pools in order with balance, today's allowance, spend since refill and the
   tier each route resolves to; owner-only; empty body with `enabled: false` when no pools block is configured.
   Accept: a run row with `subscription_usage` shows its window and fraction; the Spend screen draws the pools from a
   fixture; a profile without pools shows "router off" and no error.
4. **Merge from the card.** **Server:** `POST /v1/pulls/{owner}/{repo}/{number}/merge` through the integrator's
   policy (ADR 0046): refuses without a required check, honours `always_ask`, owner-only, `409` with the reason when
   policy refuses. Console: the `pull_request` card gains Merge beside Open PR, and the quality card's Approve merge
   calls the same route. Accept: `InboxCatalog.cs` offers `merge` only on a pull request whose checks passed; a refusal
   shows the reason on the card; the route is in the route listing `tests/Chargehand.Tests/ServerTests.cs` checks.
5. **Autonomy beside the merge rate.** **Server:** `GET /v1/learn` gains an additive `autonomy` list, one entry per
   repository from `EarnedAutonomy.Assess` (largest size class, auto-merge earned or not, streak, what would widen or
   narrow it). Console: Learning shows a row per repository with the rate, the grant and the reason, and a 30-day
   merge-rate trend from `GET /v1/learn?since=` sampled per week. Accept: `learning.test.tsx` shows a repository that
   earned auto-merge and one that lost it after a revert; the trend has one point per week in range.
6. **Conversations screen.** **Server:** `GET /v1/conversations` (list, newest first, state, cost) beside the routes in
   `ConversationRoutes.cs`. Console: list, a live transcript over `{id}/events`, send a message, answer a tool approval,
   end; usable at phone width (`shell/useIsPhone.ts`). Accept: a fake runtime's conversation streams into the screen;
   approve and decline call `{id}/approvals/{call}`; the smoke test passes at 390 px width.
7. **Open threads on the cockpit** from `/v1/threads` and `/v1/threads/{id}/card`: goal, next step, who moved it last;
   a card opens in a sheet (`shell/Sheets.tsx`). Console-only. Accept: three threads render newest activity first; a
   closed thread is absent; the card's `text` is shown as text, never as markup.
8. **Launch many** (sketch D): a task template saved in `localStorage` (not secret), a repository multi-select from
   `GET /v1/repositories` readiness, one `POST /v1/forecast` and one `POST /v1/runs` per repository, started in order,
   each result linked. Console-only. Accept: `start.test.tsx` starts three batches from one goal and shows a refused
   one (`409` admission) without stopping the others; the per-repository forecast sums on the screen.
9. **Compare two tasks** on the Quality screen: two quality cards side by side with their diffs, the stronger verdict
   marked; entered from a batch (two tasks) or from Runs (two batches on one request). Console-only. Accept: the compare
   view renders from two `GET /v1/batches/{id}/tasks/{task}` fixtures and the address holds both ids.

Left out on purpose: a live transcript stream (the polled window serves until conversations stream; revisit after 6),
an editor or terminal (client spec, decision 7), and a model picker in the chat (client spec Task 7, after the router
route exists). The replay benchmark's arms (ADR 0087) are the natural feed for increment 9 once `chargehand bench` runs.

## Acceptance for the whole

Read by the maintainer after increments 1 to 4 ship: for two weeks, every merge decision on a chargehand pull request
is taken from the console (the card or the quality card), not from the forge's page, and no task's mutation score or
window utilisation has to be looked up in a log. If either still needs the log, the card is missing a fact: add it
before increment 5.
