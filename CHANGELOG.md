# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The contract package
(`Chargehand.Contracts`) is versioned separately, by schema major.

## [Unreleased]

### Added

### Changed

- Over HTTP an MCP `orchestrate` call without `Prefer: wait=N` returns the run id after 50 s instead of waiting for the result: Claude Code ends a call after about 60 s, and the caller got neither the result nor the id. Collect the result with the `result` tool. Over stdio a call still waits for its result.

### Fixed


## [0.9.6] - 2026-10-14

Inbox items resolve on their own, and a person can dismiss the rest.

### Upgrade

- Dismissals are kept in `inbox/dismissed.json` beside the run log, so that directory must stay writable (it already is for the run log). Nothing else to set.
- A profile's governor probe and wait patterns now match with the linear-time regex engine: a pattern that uses a lookaround, a backreference or an atomic group is refused by the profile check. Rewrite such a pattern, or leave the setting out for the built-in ones.
- Batches that were running before the upgrade resume as before.

### Added

- The needs-you inbox drops what a later run made moot (ADR 0064): a question or failure superseded by a later task of the same account that asked the same, an infrastructure failure (`checkout_invalid`, `internal`, `lost`, ...) once a later run on the repository finished ok, and a question a batch continued or retried. Identical failures fold into the newest with a count.
- `POST /v1/inbox/dismiss` and `POST /v1/inbox/restore` store a person's dismissals in `inbox/dismissed.json`. `inbox/v1` stays: `counts` is what is open, items gain `count`, and the body gains `resolved`. The console gets **Dismiss** per card, **Dismiss all shown**, **Undo**, a count badge and the resolved line.
- A server restart keeps the batch scheduler's rate-limit pause, the placements a task used up and an operator's cancel of a task that had not started (ADR 0063).

### Fixed

- A server stop waits for its runs to write their end before it disposes, so a run no longer writes into a directory that is already gone.
- The governor no longer throws on a plain command when the host is loaded: its patterns had a 50 ms wall-clock match timeout, and now match in linear time with none.

### Changed

- Internal: task and batch state are journal events in `<batch>/tasks`, and the batch lifecycle, planner and task result builder moved into their own projects. No behaviour change beyond the above.

## [0.9.5] - 2026-10-14

Workers can reach a gateway that identifies callers by a header.

### Upgrade

- Nothing to change in a profile.

### Added

- `claude_code.custom_headers_secret` (ADR 0062): a secret item holding header lines (`Name: value`, one per line) that Claude Code workers send to `claude_code.base_url` as `ANTHROPIC_CUSTOM_HEADERS`, for a gateway that identifies callers by a virtual key header. Needs `base_url`; an inherited `ANTHROPIC_CUSTOM_HEADERS` is removed. Header values are scanned for in driven transcripts like any model credential. Driven sessions still do not use `base_url`.

## [0.9.4] - 2026-10-13

The console works behind a single-sign-on proxy.

### Fixed

- The console works behind a single-sign-on proxy on the same host (e.g. Pangolin): its API calls send same-origin cookies, so the proxy's session cookie reaches it; before, they were sent with none and the proxy redirected them to its login page ("Failed to fetch"). chargehand still authenticates only by the bearer key and ignores cookies.

## [0.9.3] - 2026-10-13

The operator console at `/console/` is complete: runs, batches with their task graph, timeline, events, governor, spend, pull requests and diffs, tasks with their transcript, research results, a batch composer, analytics, learning, intake, repositories, accounts and system. The server keeps what it shows across restarts and gains actions on one task and on a pull request's checks. Driven pull requests get short titles, and a killed workspace preparation no longer blocks later ones.

### Upgrade

- Nothing to change in a profile. `/console/` is served when `http.ui` is true, beside `/ui/`.
- Re-running a pull request's failed checks from the console needs the GitHub App's Actions permission set to write.

### Added

- The operator console's **Analytics** and **Learning** screens (ADR 0056, CHARGEHAND-208), at `/console/analytics` and `/console/learning`. Analytics reads `GET /v1/analytics` (`analytics/v1`) and `GET /v1/learn`: spend and tokens over time stacked by repository, size or account (the owner), the cache-read share of input, pass rate by repository and size with its 95% Wilson interval, the wall time of finished batches, each size class's p80 spend against the budget a task of that size was last held to, the governor's false-stop rate per mode against the 5% that ADR 0045 asks for, continuations, and pull requests merged by how they were merged; a range, a bucket (day, week, month) and a grouping pick what is asked. Every mark says in a hover or on keyboard focus which route and field its number is read from, and every chart has its numbers as a table. A figure the server does not record is said so: the CI fixer's attempts, and a benchmark comparison (no route serves `chargehand bench` results). Learning shows each repository and size class with its median and p80 spend, completion, checkpoint and merge rates and how its budget is chosen (`driven.governor.history`: the owner sees the profile's numbers, an account the documented defaults), the budget in force for each size class with its source (`p80 of 23 past tasks, this repository`), the retros of the latest batches (a model's advice, shown as text) and the patterns a reader would see in the table, each with its counts. Time series are drawn by [uPlot](https://github.com/leeoniya/uPlot) 1.6.32 (released March 2025; 51 KB, 22 KB gzipped; MIT), which writes no style element; small bars and whiskers are plain SVG; every chart colour is a design token. The bundle grows by 104 KB (38 KB gzipped). Playwright can run on another port (`CONSOLE_E2E_PORT`), so two checkouts running it at once do not share a server.

- The operator console's **Start a batch** screen (ADR 0056, CHARGEHAND-208), at `/console/new`. A preset, a repository from `GET /v1/repositories` with the commit filled from its HEAD, and a list of tasks: id, size, optional budget, a multi-line goal and the tasks each waits for (a dependency that would close a loop is not offered). A layered dependency graph and an estimate show each task's budget as the server will choose it (its own `budget_usd`, else p80 of past tasks of its size in the repository, else across repositories, else the flat budget) with the source of every figure, from `GET /v1/learn`. Planning, `max_parallel` and the token and dollar caps are fields; the checks mirror `schemas/request/v1` and the server's driven rules and the account's limits, and show at the field. A JSON view is the same request and round-trips with the form, keeping what the form has no control for. Templates are kept in the browser's `localStorage`; tasks can be imported from pasted text (a line each, or a JSON list); another screen can open the form filled in through the router's state (a clone of a batch). **Start batch** sends `POST /v1/runs` with `Prefer: wait=0` and opens the new run. GitHub issues are not imported: the server has no route that lists them. Tests: Vitest for the model, the checks, the estimate and the screen; Playwright with axe-core at 1280 and 375 px in both themes.
- `driven.tasks[].title` in `request/v1` (additive, CHARGEHAND-215): the title of the task's pull request, one line of at most 200 characters. The server refuses a blank title or one with a line break. A restarted batch keeps it.
- What the operator console needs and the server did not keep (CHARGEHAND-214, first change: records). A driven batch now leaves `records/<run id>/` beside the run log: `events.jsonl` (its events, so a finished batch's timeline outlives a restart; capped at 5000 events and 1500 characters a detail, a task's `session_progress` kept once in 30 seconds) and `tasks.jsonl` (every session of a task with its runner, role, why it ended, tokens and cost; the governor's whole `governor.jsonl`, which the runner now serves as an optional read of the output volume; each look at the pull request's checks while the integrator watches them; each CI fix attempt). Oldest directories go past 500 batches or 180 days idle. `batch/v1` and `batch-task/v1` gain, additively, `timeline.source`, richer `sessions`, `governor.log` and `log_total`, and `ci` (`checks`, `looks`, `attempts`, and in the task view `history`). Intake keeps the latest 200 triggers with the time, outcome, reason and login (never the text) and `intake/v1` gains `triggers`; an account key's last request time is kept at most once a minute in `accounts-use.json` and `accounts/v1` gains `keys[].last_used`. The `not_recorded` lists of those routes shrink to what is still missing ([guide](docs/guide/server.md#batch-records)).

- The operator console's Runs, Batch run, Task and Research screens (ADR 0056, CHARGEHAND-208). **Runs** lists every run in a virtual table, with filters (state, kind, repository, account, preset, date), search, saved views kept in the browser, and a bulk cancel that asks first. A **batch** has a header (state, repository at its commit, caps used, time, cancel and halt) and eight tabs: the task graph and table, a timeline of sessions in parallel slots with continuations and rate-limit pauses, live events, the governor, spend per task, pull requests, artifacts with a diff viewer, and the request, which can be cloned into Start a batch. A **task** opens as a drawer or a page, with its goal, budget, sessions, verification, test-integrity flag, diff and, when it asked, the form that answers it. A **research** result shows each claim with its citations linked to the line at the commit, open questions and the signature. Every tab says what the server does not keep. New runtime packages, exact and at least 14 days old: `@tanstack/react-virtual`, `@xyflow/react`, `elkjs` and `shiki`; the page's script grows from 724 KB to 772 KB and the graph, the diff colour and each grammar load only when needed. Tests: Vitest for the models and screens, Playwright with axe-core at 1280 and 375 px in both themes.

- Actions on pull requests and tasks for the console (CHARGEHAND-214, third change): `POST /v1/batches/{id}/tasks/{task}/cancel`, `.../retry` and `.../session/stop`, and `POST /v1/pulls/{owner}/{repo}/{number}/checks/rerun`. Cancel acts on a task of a batch this server runs (a running session is interrupted and killed, a task not yet started never starts); stop kills the session container; retry starts a new batch of the task (continuing its kept work when it has some), and a second retry returns the first; the re-run asks GitHub to run the failed required checks' jobs again (the App needs the Actions permission to write). For the owner and the batch's account; each writes an `operator` event (a new `event` value of `run-status/v1`, additive), is safe to repeat and answers `409` when the state forbids it. A task that never started (cancelled, or its dependency failed) is now final in the batch's saved state. See [Actions](docs/guide/server.md#actions-on-pull-requests-and-tasks).

### Changed

- A driven task's pull request title is short (CHARGEHAND-215). Until now it was the first 72 characters of the goal's first line, cut mid-word, so a goal written as one paragraph made a title that started a sentence and stopped. Now it is the task's `title` when the request gives one, else the first sentence of the goal (a sentence ends at a line break, or at `.`, `!` or `?` followed by white space), cut at a word to at most 72 characters, without the punctuation it ends in. The pull request body now holds the whole goal under `Goal:`, finished or `[partial]`, cut at 12000 characters.

### Fixed

- A killed workspace preparation no longer blocks later ones for the same run (CHARGEHAND-215). The helper that clones a session's workspace is named `chargehand-prep-<run>` and had the flaw the transfer helper had: a call cancelled between `docker create` and `docker start` left a created container under that name, and every later preparation for the run failed with a name conflict (`exit 125`). Both workspace helpers (from a source directory and from a bundle), the transfers and the bundle stream now share one runner: a conflict with a stopped container that carries chargehand's label removes it (`docker rm` without `-f`) and runs again, a call cancelled in flight removes the helper it may have made, and a failure of docker itself (`125`) is run again up to 4 times; a bundle stream is read once, so it removes the leftover first and is not repeated. At start the runner removes workspace helpers that are not running, as it does transfer helpers.

## [0.9.2] - 2026-10-13

Driven sessions can run on several runners, including another machine such as a Mac (arm64 session images, a launchd runner that drains on battery or lid close), the integrator finds the GitHub App's installation on each repository's owner, and a killed transfer no longer blocks a run's output. The operator console gains its design system, shell, Overview and Needs you, on new read routes.

### Upgrade

- Nothing to change for a single runner: `driven.runner` keeps working. To add a runner, see [Several runners](docs/guide/runners.md); a Mac uses `deploy/macos/`.
- `driven.integrator.installation_id` is now optional: chargehand looks up the App's installation per repository owner. Install the App on every account or organisation whose repositories it should act on.
- The arm64 session images come from a `session-arm64` job after the release job; pin the multi-platform digests from its summary.

### Added

- Several runners (ADR 0058, CHARGEHAND-212), second change: scheduling. A pool places each driven task on a runner that answers `GET /host` (3 s timeout, 5 s cache; one that does not is skipped for 15 s, doubling to 2 min), is not `draining`, is under `max_sessions` and passes the memory and load rule on its own host figures and `reserve_mb`; the most free memory times `weight` wins. A task stays on its runner for all its sessions and its handover; its runner is in the batch state, so a restarted server finds each session where it was. A task whose runner did not answer before its session started is placed again (twice at most). A runner that goes silent during a session is waited for up to 10 minutes. The network, egress container and test services of a batch are made on a runner when its first task gets there, with that runner's `forwards`; the sweep at start runs on every runner. Events carry `runner` (additive to `run-status/v1`), and the `driven-batch` rows say which runner ran a task; `runner` in the profile reports as before. The image follows the runner's platform through `image_variants`. `GET /host` may carry `state: draining`.
- Several runners (ADR 0058, CHARGEHAND-212), third change: a runner on another machine. A runner with `workspace: bundle` is sent the repository as a git bundle of the pinned commit (`git bundle create ... HEAD` from the server's checkout, once per commit) with `PUT /out/<run>/source.bundle`, streamed with its length into the run's output volume and refused above the runner's `--max-bundle-mb` (default 512); `POST /workspace/bundle` then clones the workspace from it in the same no-network helper, with the volume mounted read-only, so the runner needs no `--source-roots`. The session's result bundle comes back over `GET /out/...` as before, now refused above 256 MiB before it is copied, and the runner client times each call out itself (a control call 100 s, a transfer 30 min) so a repository-sized transfer is not cut by `HttpClient`'s 100 s. chargehand's own test run still happens in a fresh container on the session's runner, which is sent the bundle again for it. `chargehand runner` takes `--max-bundle-mb`.
- Several runners (ADR 0058, CHARGEHAND-212), fourth change: arm64 images and a Mac as a runner. The release workflow gets a `session-arm64` job (after the release job, so behind its approval) that builds `chargehand-session` and `chargehand-session-jdk` for linux/arm64 natively on an arm64 runner and turns each tag into an index of both platforms; the digests to pin are in the job summary. `chargehand runner` gains `--keep-awake on|off` (`caffeinate` while a session container runs, macOS), `--drain-on battery,lid` and `--drain-file` (`GET /host` adds `state: draining` and `state_reason`), and `--host-source auto|proc|engine` (on a machine whose engine is a virtual machine, the runner reads the engine's memory, load and processors from a throwaway container). `deploy/macos/` has the launchd login agent, its launcher (listens on the private network's address only, key from the Keychain, socket proxy on loopback) and an install script with `--dry-run`. See [Several runners](docs/guide/runners.md#a-mac-as-a-runner).
- Read routes for an operator console (CHARGEHAND-209): `GET /v1/overview`, `/v1/inbox`, `/v1/batches/{id}`, `/v1/batches/{id}/tasks/{task}`, `/v1/pulls`, `/v1/analytics`, `/v1/repositories` for the owner and for an account (an account sees its own runs, batches, tasks, pull requests and spend only), and `/v1/intake`, `/v1/accounts`, `/v1/system` for the owner alone. They read the run log, `learning.jsonl`, the saved batch state and `intake/github.json`; none writes, and a body never holds a key, a token or a secret value (the profile is shown as settings, secret items as set or not). Each body is versioned by `contract_version` (`overview/v1`, `inbox/v1`, `batch/v1`, `batch-task/v1`, `pulls/v1`, `analytics/v1`, `repositories/v1`, `intake/v1`, `accounts/v1`, `system/v1`) and lists what the server does not record in `not_recorded` ([guide](docs/guide/server.md#console-routes)).
- The operator console's design system, shell and first two screens (ADR 0056, CHARGEHAND-208). The owner-approved design (a dark and a light theme, a lime "your move" accent, system fonts) is in `web/console`: tokens generated from the design system's token file, its 18 components as React, a rail on a desktop and a tab bar on a phone, a ⌘K palette (screens, runs, tasks, halt and resume with a confirm), a `?` sheet of keys, theme and row density, toasts, and browser notifications on a secure page. **Overview** shows sessions, spend against caps, the batches running with their progress, what admission looks at, the halt switch, what needs you and recent merges; **Needs you** answers a task's question with the request the server carries, and lists pull requests and failures. A typed client now covers every console read route. Tests: Vitest for the tokens, the primitives, the shell, the notices and both screens; Playwright with axe-core at 1280 and 375 px in both themes. New runtime packages, exact and at least 14 days old: four Radix parts (tabs, menu, tooltip, toast) and cmdk for the palette; modal layers use the browser's `<dialog>` because Radix's dialog injects a style element that the page's policy refuses.

### Changed

- The server keeps the events of its last 25 finished runs (2000 each) in memory for the console's timeline, and a batch that waits on a provider usage or rate limit now says so with a `session_progress` event (`new starts are held until <time>`).
- `LearningSeries`: the learning records over time by day, week or month and by repository, account or size class (spend, tokens with cache, pass rate, continuations, the governor's false-stop proxy).

### Fixed

- A killed transfer no longer blocks every later read of a driven run's output. The helper that moves a file in or out of a session's output volume is named `chargehand-xfer-<run>`; a call cancelled between `docker create` and `docker start` (the server restarting while it polled sessions) left a created, never started container under that name, which `--rm` does not remove, and every later transfer for the run failed with a name conflict (`exit 125`, 503 from the runner) until the task gave up. Now a cancelled transfer removes the helper it may have made; a transfer that meets a stopped helper of its name removes it (`docker rm` without `-f`, so a running transfer of the same run is waited for, not killed) and runs again; the runner removes helpers that are not running when it starts; and a transfer that fails with docker's own error (`125`) is run again up to 4 times, 0.25 s longer apart each time, before the call fails. A missing file (`3`) and a failure after bytes were copied are not repeated. A task that already failed on this stays `failed` (a resumed batch returns its recorded outcome, and a task that kept no work cannot be continued); its output volume still holds the bundle until the next server start sweeps the volumes of finished batches.
- The integrator and GitHub intake use the GitHub App's installation on each repository's owner, not one `installation_id` for every repository (ADR 0046). An App is installed once per account or organisation, so a `merge` or `ask` repository on a second owner failed with `Resource not accessible by integration` and the pull request stayed a draft. `GitHubInstallationTokens` now asks GitHub which installation covers the repository (`GET /repos/{owner}/{repo}/installation`, with the App's JWT), keeps the id per owner, and looks an owner up again when GitHub stops knowing its id; an owner that installs the App later is picked up without a restart. A repository whose owner has no installation stops the integration with `the GitHub App is not installed on <owner> (or has no access to <owner>/<repo>); install it there, or set the repository to off`, and the task stays `completed` with its draft pull request. `driven.integrator.installation_id` is now optional (`app_id` and `private_key_secret` are still set together): set, it pins that installation for the account it belongs to and skips the lookup for that owner, and reaches no other owner. A profile that works today needs no change. `IInstallationTokens.GetAsync` and `IIntegrationHost.MarkReadyAsync` and `EnableAutoMergeAsync` take the owner or repository.

## [0.9.1] - 2026-10-13

More driven sessions fit on one host: admission asks for a session's measured working set (the p90 of past peaks) instead of its whole memory limit, with a memory-pressure guard, and the parallelism ceilings rise. Also the foundation of the new operator console at `/console/` and the profile half of several runners.

### Upgrade

- Deploy the server and the runner together (one image): a 0.9.0 server reads the new runner fields strictly. Batches resume across the restart as before.
- A runner that should host more than 8 chargehand containers (sessions, their egress proxies and services all count) needs `--max-containers` raised; otherwise starts past 8 get 429 and the task fails.

### Added

- An operator console at `/console/` (ADR 0056, CHARGEHAND-210), the foundation of the page that will replace `/ui/`. A Vite, React and TypeScript app in `web/console/` (TanStack Router and Query, Vitest, Playwright, Biome), built into files that the server embeds; it is served under `http.ui`, beside `/ui/`, with the same rules: files need no key, the key is pasted, kept in `sessionStorage` and sent as the bearer header, and the Content-Security-Policy allows no inline script or style and no other origin. Hashed assets are cached for a year, `index.html` is not, and a path that is not a file falls back to `index.html` for client routes. This release has the shell: sign-in, a placeholder for each planned screen, a typed client for the existing routes (with events read over `fetch`) and the runs table. A `dotnet build` without the web build still works: `/console/` then answers `503` with a page that says it is not built. The Docker image builds the console in a Node stage; CI checks, tests and builds it (a `web` job); `scripts/check.sh` runs the web steps when Node is installed and says so when it skips them. Packages are pinned exactly, installed from the lockfile without install scripts, and none is younger than 14 days. Amends ADR 0054 for the console.
- Several runners (ADR 0058, CHARGEHAND-212), first change: the profile. `driven.runners` lists runner services, each with a `name`, `url`, `api_key_secret` and optionally `max_sessions`, `platform` (`linux/amd64` or `linux/arm64`), `weight`, `reserve_mb`, `workspace` (`shared` or `bundle`) and `forwards` (the server's address as that runner's egress reaches it); `driven.runner` stays as the one-runner form and needs no change. `driven.image_variants` maps an image to the one to start on another platform. A setting that cannot work is refused naming its key; `chargehand doctor` probes and checks each runner. `chargehand serve` refuses more than one runner until the scheduling change lands. See [Several runners](docs/guide/runners.md).
- Driven sessions are admitted on their measured working set, not on their memory limit (ADR 0045, CHARGEHAND-213). The engine records each session container's peak memory while it runs (the cgroup's `memory.peak`, else the largest `docker stats` reading) and returns it as `memory_peak_mb` in the runner's inspect reply; the task's learning record and `session_progress` events carry it too (all additive). A task's admission request is the 90th percentile of the peaks of its repository's tasks in the last 60 days when there are at least 10, else of all repositories, else `driven.admission.default_request_mb`, never above `memory_mb` and never below 512 MiB; a container started in the last minute counts at its request plus 128 MiB, not at its limit. `memory_mb` remains the container's hard limit. New profile keys: `driven.admission.default_request_mb` (default 1536, at least 512) and `driven.admission.max_memory_pressure` (default 10, percent, above 0 and at most 100). The runner's `GET /host` gains `memory_pressure_avg10` (Linux PSI, `some avg10` of `/proc/pressure/memory`, left out where the host has none); new starts wait while it is above `max_memory_pressure`, and a host without PSI skips the rule. Deploy the server with, or before, the runner: a server from before this release rejects the two new fields.

### Changed

- The ceilings of `driven.max_parallel` and `driven.max_parallel_total` rise from 4 and 8 to 16 and 32, and `request/v1`'s `driven.max_parallel` from 4 to 16 (a looser bound, so existing requests stay valid). Memory admission, the pressure guard and the rate-limit pause govern how many sessions run. The runner's `--max-containers` (default 8) counts every labelled container, so raise it with `max_parallel_total`, or `POST /start` answers 429.

## [0.9.0] - 2026-10-13

Driven sessions are governed: a governor watches spend and stuck signals (observe mode by default), stopped work continues in a fresh session or becomes a partial draft PR, tasks can depend on each other and answer questions, admission looks at free memory, and an optional GitHub App integrator merges through required checks. The orchestrator core ships apart from the driven infrastructure, with accounts that bring their own model key, GitLab, intake from GitHub issues and review comments, budgets learned from past spend, a web UI, a one-file install, `init` and `doctor`, a quality benchmark harness and a review workflow for any repository.

### Added

- A web UI at `/ui/` (ADR 0054, CHARGEHAND-203), off unless `http.ui` is `true`. Plain HTML, CSS and ES modules embedded in the server, no build step, no third-party code. Sign in by pasting an API key (the owner's or an account's); it is kept in `sessionStorage` and sent as the bearer header, with no cookie and no server-side session. Every `/ui/` response carries a Content-Security-Policy that allows no inline script or style and no other origin, and run data is only ever rendered as text. Screens: the runs list (filters by state, kind, repository and, for the owner, actor; cost, tokens, branch, pull request link; refreshed every 5 seconds), a run's page (live events over `fetch`, the result with claims and citations linked to `path:line` at the commit when the repository has a web address, artifacts including the `changes` diff, a batch's task table, cancel), starting a run (goal, preset, repository, and for accounts allowed `driven`, a list of tasks), spend against the account's caps, the learning report (`GET /v1/learn`), and halt and resume for the owner. New, additive: `GET /v1/accounts/me` (name, rights, caps, spend in the UTC day and month, unfinished runs, halted, presets); `GET /v1/runs` filters `kind`, `repository`, `run_id` and, for the owner, `actor`; `run-summary/v1` gains optional `kind`, `repository`, `commit` and `tokens`; the profile gains `http.ui`. The static files are the one thing served without a key. The JavaScript is exercised by hand in a browser, not in CI.
- Budgets come from past spend (ADR 0045, CHARGEHAND-198). When a size class has at least `driven.governor.history.min_tasks` (20) finished tasks in the last `window_days` (60) days for the task's repository, or for all repositories when the repository has too few, the task's budget is the `percentile`th (80) percentile of their spend (the learning records' `usd_estimate`, nearest rank); below that the flat budget of the size class applies, and a task's own `budget_usd` always wins. A finished task is one that completed or was stopped at its budget; failed, partial-for-another-reason, question-asking and unpriced tasks are left out. The budget is chosen once when the task starts and held by all its sessions. Where it came from (`task`, `flat`, or `history` with the task count, percentile and scope) is on the learning record (`budget.source`), in `task.json`'s `governor.budget_source` and on every governor decision (`budget_source`, additive); the `governor` run event adds `(p80 of 23 past tasks)` for a history budget. Profile: `driven.governor.history` (`min_tasks`, `window_days`, `percentile`), additive. With the default `observe` mode a changed budget only changes what the log says; in `enforce` a budget from history stops tasks at it. A session image from this release is needed for the source to reach `governor.jsonl`.
- A read-only `design` preset drafts an ADR from the current code (CHARGEHAND-189). The request text is the goal; the result's summary is the draft in the shape of `docs/adr/template.md` (context, options with trade-offs, decision, consequences, reopen if) and its claims carry the file citations the draft stands on, checked at the pinned commit and then by the support check. It writes no file. Prompt CI cell `design/worker` (new preset: it gates the preset from the next change on).
- Accounts and named API keys for a self-hosted server, with caps and an actor on every run (ADR 0050, CHARGEHAND-200). `http.accounts` in the profile declares callers besides the owner: `name`, `max_usd_per_day`, `max_usd_per_month`, `max_tokens_per_day`, `max_tokens_per_month`, `max_concurrent_runs`, `driven` (default false) and `repositories`. Their keys are made with `chargehand accounts add <account> [--label]`, which prints `chk1.<id>.<secret>` once; `accounts list` and `accounts revoke <key-id>` manage them. Only a salted SHA-256 of the secret is kept, in `accounts.json` beside the run log (owner-readable only); the server finds a key by id, compares in constant time and sees a revoked key at once, with no restart. The `http.api_key_secret` key keeps working unchanged as the implicit `owner` account. Every start record, run record, `result/v1`, run event and `run-summary/v1` gains an optional `actor` (the account name; `owner` for the single key; absent for a CLI run), set before a result is signed. An account lists, reads, streams and cancels only its own runs (another's is `404`), and halt and resume are the owner's. A request that would pass a cap is refused with `429`, `Retry-After` and `error.code` `budget_exceeded` carrying `window`, `unit`, `limit`, `spent`, `remaining` and `resets_at` (UTC day and month); an admitted request has its budgets cut to what is left. `concurrency_exceeded` is `429`, `driven_not_allowed`, `repository_not_allowed` and `continue_not_allowed` are `403`. A run token carries the account of its batch. An MCP session is now tied to the account that opened it. Schemas: `actor` added to `result/v1`, `run-status/v1` and `run-summary/v1` (additive). Accounts do not isolate runners or networks; that is CHARGEHAND-202.
- A review comment on a chargehand pull request continues the same branch (ADR 0051, CHARGEHAND-184). The intake poller also reads inline review comments and conversation comments of the watched repositories: on an open pull request whose head branch is `chargehand/<name>` in the repository itself, an inline review comment of an allowed author (a reply in a thread needs the mention) or a conversation comment that mentions the bot is queued, and all the comments waiting for a pull request start one round: a batch of one task, `pr-<n>`, with `continue_from` the task that opened the pull request (found in the run log by its branch) and one answer per comment (author, file and line, text). `continue_from` now also takes a task that completed with its branch pushed: the session starts from the branch as the remote has it (fetched on the host with the push credential, checked to descend from the task's base commit), `answers` are required, the push is a fast-forward of the same branch and the pull request keeps its title and body. One cap of 3 per pull request is shared by CI fix attempts and review rounds: a round leaves the hidden marker `chargehand-review-round attempt=N comments=r<id>,c<id>` that `FixMarker.Parse` counts with the fixer's, so a comment is never taken twice and neither loop passes the cap; the CI fixer gives up when rounds used it. Profile: `automation.github.max_review_rounds` (default 3, 0 turns the loop off). The `continue_from` description in `request/v1` says so (text only). Rounds count toward `max_concurrent` and `max_starts_per_day`. A pull request from a fork, with another branch name or closed is ignored. Tested against a fake GitHub host, a fake batch starter, a fake branch seeder and real git for the seed.
- Each account brings its own model key (ADR 0053, CHARGEHAND-201). `http.accounts[].model` names the account's own credential by secret item: `api_key_secret` (an Anthropic API key, or the key a gateway issued with `base_url`). Its read-only runs (intake, workers, the support check) run on a Claude Code runtime of its own and its batches take the key as `ANTHROPIC_API_KEY` (`credential_delivery: environment`) or, with `base_url`, as a gateway key (`gateway_key`); the key is priced, so a batch needs a dollar cap and the account's dollar caps bind. A session's research and review calls use the key of the account that started the batch. No account's run sees another account's key, and the server's `driven.network.model_url` is never used for it. `oauth_token_secret` is rejected in `model`: a subscription token is the owner's. **Behaviour change:** an account with no `model` and no `allow_server_credential: true` (new, default false) is now refused with `403` `model_credential_missing`; set `allow_server_credential: true` on an account that should keep spending the server's credential. The server refuses to start with an account `model` on the OpenCode runtime. `chargehand doctor` checks each account's key item. `Orchestrator` gains an optional `runtimeFor`, and `DrivenServices.SupportCheck` becomes `SupportCheckFor`.
- Work starts from GitHub (ADR 0051, CHARGEHAND-183). A new assembly, `Chargehand.Automation` (references Driven; Core and Driven cannot reference it, `ArchitectureTests` checks), polls GitHub as the integrator's App and starts a driven batch of one task when an allowed author applies the label `chargehand` to an open issue or mentions `@chargehand` in a comment, and continues a task that asked a question when an allowed author replies. Profile: `automation.github` with `repositories` (`owner/repo` to the server's checkout), `allowed_authors` (required; an empty list means nothing triggers), `label`, `mention`, `poll_seconds` (default 120), `budget_class`, `max_tokens_total`, `max_usd_total`, `max_concurrent` (2), `max_starts_per_day` (10); it needs `driven.enabled` and the integrator's App keys, which now also need the Issues permission (read and write). The goal is the issue's title first, then its text fenced as untrusted and cut; size from a `size:S|M|L` label. chargehand comments on the issue when the batch starts and when it ends (the draft pull request, what stopped it, or the session's questions). State (cursor, handled triggers, tasks, starts) is a JSON file next to the run log, so a restart starts nothing twice. Over the concurrency cap a trigger waits; over the daily cap it is refused, each with one comment. A repository the integrator merges into takes work only from issues an allowed author wrote. Additive; off unless the profile has the section. Tested against a fake GitHub host and a fake batch starter only.
- One-file install (CHARGEHAND-204). `deploy/compose/` runs the release images with Docker Compose: `docker compose up -d` starts the server alone (Core: read-only research and review, no runner and no engine socket), and `docker compose --profile driven up -d` adds the runner behind a `docker-socket-proxy` (containers, images, volumes, networks and POST only) for driven sessions. Images come from `.env` by tag, with `pin-digests.sh <version>` to pin server and session images by digest; secrets come from `.env` (`.env.example` holds names only); there are two profile templates (`profile.core.json`, `profile.driven.json`), named volumes for the run log and repositories, healthchecks on all three services, three networks (two `internal`), and dropped capabilities. CI runs `docker compose config -q` in both modes. The walkthrough is [Install with Docker Compose](docs/guide/install.md): prerequisites, `chargehand init`, `.env`, `up`, `doctor`, a first run and a first driven batch. A one-shot `prepare` service hands the state directories to the image's user. Run on a local engine with an image built from this commit: both modes came up healthy, `chargehand doctor` passed in the container, the runner started and removed a network, a volume and an egress container through the proxy, and the proxy answered 403 for swarm, secrets, exec and info. No model call and no driven batch was run through it.
- A learning loop for driven tasks (ADR 0045, CHARGEHAND-177). Every task that ends writes a record to `learning.jsonl` next to the run log: repository, size class and whether the caller or the planning step gave it, the budget and where it came from, spend (`usd_estimate`, tokens with cache, turns, continuations), outcome (`completed`, `partial`, `failed`, `needs_input`), the last stop reason, a count of governor decisions per signal and action (and how many acted), the test-integrity flag, and the pull request with the last commit chargehand put on its branch. No goal, prompt or session text is in it. The server reads the outcome of each recent pull request back through the forge, read-only (`merged_unchanged`, `merged_edited`, `closed`, `open`) at start and every `driven.learning.pr_check_minutes`, for `driven.learning.pr_days`; it is off where no forge credential is configured (no GitHub App, no GitLab token). `chargehand learn report [--since 30d|<time>]` and `GET /v1/learn` (an account's key sees only the tasks of its own batches, ADR 0050) give a table per repository and size class: tasks, median and 80th percentile of spend, completed and partial rates, the governor checkpoint rate and how many checkpointed tasks still completed (the false-stop proxy ADR 0045 needs before `enforce`), and the share of resolved pull requests merged unchanged. A retro after each batch is off by default (`driven.learning.retro`, preset `driven.learning.retro_preset`): one read-only run over the batch's records writes a short text as the batch result's `retro` artifact. Filing it to a tracker is not built (there is no tracker write path); `IRetroSink` is the hook. Profile: `driven.learning` (`retro`, `retro_preset`, `pr_days`, `pr_check_minutes`), all additive. `Integrator.IntegrateAsync` and `FollowAsync` gain an optional callback for a branch update they make. Tested with fakes only; no real forge or batch.
- GitLab merge requests beside GitHub pull requests (CHARGEHAND-205). A remote on `gitlab.com` or a host named `gitlab.*` (or any host when `driven.forge.kind` is `gitlab`) is opened as a draft merge request through GitLab's REST API v4 with a `PRIVATE-TOKEN`, and the integrator and the CI fixer work through it: ready, comment, rebase, auto-merge (`merge_when_pipeline_succeeds`) pinned to the head commit with squash, and the failing jobs of the head pipeline with the end of their traces. GitLab's "pipeline must succeed" stands in for GitHub's required checks (the mapping and its limits are in the guide). The forge is one public interface, `IForge`, joining `IPullRequests` and `IIntegrationHost`; GitHub's behaviour is unchanged. Profile, all additive: `driven.forge` (`kind`, `base_url`, `token_secret`), nested `group/sub/project` names in `driven.integrator.repositories` and `always_ask`, and the three App keys of `driven.integrator` are now optional together (a GitLab-only profile has none). Tested against a fake GitLab host only. `IPullRequests` gains `EditAsync` (title and body; GitHub `PATCH /pulls/{n}`, GitLab `PUT /merge_requests/{iid}`), and `PullRequestRef` says when the request was reused. A task continued from a `[partial]` result (`continue_from`) that now finishes replaces the title and body of the pull request the partial one opened, dropping `[partial]`; before, the title stayed. If the edit is refused the task is unaffected and its `pr_opened` detail says so.
- A driven task whose chargehand-verified branch changes only tests, fixtures, snapshots or build files, for a goal that does not mention test, spec, build, CI, coverage, fixture or snapshot work, is flagged for test integrity (CHARGEHAND-187). The `verification` artifact gains an optional `test_integrity` member (`flagged`, `reason`, `test_paths`, `build_paths`; null when not flagged), the result summary and the task detail say so, the pull request body starts with a warning, and the integrator treats such a pull request like `ask` even on a `merge` repository: marked ready, commented on, never merged. Additive.
- `chargehand init [<repo path>] [--profile <out>] [--force]` onboards a repository. It detects dotnet, node (with the lock file kind), python, go, rust, gradle and maven from marker files, proposes `context.verify` in a fixed order (`scripts/check.sh`, other `scripts/test*`, a `Makefile` `test` target, then the ecosystems), lists the package hosts the driven egress allowlist needs minus what the driven preset allows, and writes a starter profile with secret item names only. It never overwrites a file without `--force`; it reads no `.env` file and makes no network call.
- `chargehand doctor [--profile <path>]` checks a setup, one `ok`, `warn` or `fail` line per check with the fix, and exits 1 when a check fails: the profile loads, the server answers an authenticated read (there is no separate health route), the runner answers, each `driven.images` entry is pinned by digest, each secret item the profile names resolves (present or missing, never a value), `repository_roots` exist, the egress allowlist covers the package hosts of the repositories under the roots, and a model credential is configured.
- A driven session's spend is live and counts cache. The driver reads each assistant message's input, output, cache-read and cache-creation tokens and its model, prices them with the profile's price table (cache reads at the model's cache-read rate), and writes `cache_read`, `cache_write` and `usd_estimate` to `session-usage.json`. The `session_progress` event carries them (run-status/v1 gains optional `usd_estimate`, `cache_read`, `cache_write`; additive). `usd_estimate` is a dollar equivalent, not a bill: it exists on a subscription too, `usd` keeps meaning priced dollars, and nothing is capped on it yet. It is absent when the profile has no prices or no message could be priced. `task.json` gains an optional `prices` member (omitted when the profile has none), so rebuild the session image with the host.
- `driven.limits` in the profile (ADR 0039): `max_minutes`, `no_progress_minutes`, `max_turns`, `max_tokens`, `memory_mb`, `cpus` and `pids` override the same keys of the driven preset's `driven` block, key by key. A change to `presets/driven.yaml` is gated by the Prompt CI cell `driven/session` (the owner's approval and a model-backed run of up to two hours), which is too heavy for tuning a wall clock; the preset keeps the node kind's model, permissions and budget and the egress allowlist. Values are checked against the container's bounds when the profile loads. Additive; unset, the preset applies as before.
- A driven session stopped by the turn limit or the wall clock no longer loses its work (ADR 0044). The driver commits the working tree to the task branch (`WIP: session stopped by ...`) before it bundles, and when the bundle holds a commit, chargehand starts a continuation session on the same branch from that bundle, up to the profile's new `driven.limits.max_continuations` (default 2; each session gets `max_turns` and `max_minutes` again, the token cap is the task's). A task still unfinished is handed over as a `[partial]` draft pull request (the tests are run and recorded but do not block the push; secrets still do) and stays `failed`. Needs a new session image digest in `driven.images`. Token spend per task can grow up to (1 + `max_continuations`) times one session's, within the cap.
- `driven.tasks[].max_turns` and `max_minutes` in `request/v1` set a task's limits in place of the preset's.
- The session governor (ADR 0045), in observe mode. The driver reads the stream and logs, with no model call, when a session looks stuck (a repeated call with the same output, a repeated failure, A-B alternation, failing calls in a row, environment probing, no diff growth) or costly (context size, 85% and 100% of the task's budget), on a warn-then-checkpoint ladder. Decisions go to `governor.jsonl` in the output volume, the first 50 to `session-outcome.json`, a `governor-log` artifact on the task result and a `governor` run event each (run-status/v1 gains the event; additive). Nothing acts on them yet. The budget is a dollar equivalent per size class: request/v1 gains `driven.tasks[].size` (S, M, L; default M) and `budget_usd` (additive); the profile gains `driven.governor` (`mode` `observe` or `off`, `budgets`, thresholds). `task.json` gains an optional `governor` member; it needs a session image from this release.
- Admission by free memory and load (ADR 0045). The runner serves `GET /host` (same key as its other routes): available and total memory, swap, the one-minute load average and the processor count, read from `/proc/meminfo` and `/proc/loadavg` (a field the host does not report is left out). The scheduler starts a task only when available memory minus `driven.admission.reserve_mb` (default 1024) covers the task's container memory limit, counting containers started in the last minute, and, when `driven.admission.max_load_per_cpu` is set, only while the load per processor is under it. It looks again every 5 seconds and never stops a running task for it. With nothing of this server running and the host still short, one task starts anyway and the `session_progress` event says so; a task that waits gets one `session_progress` event saying what is short. Without a runner the server reads its own `/proc`; a runner that answers 404 for `/host` (older) or a host with no `/proc` (macOS) means tasks start by count only, and the log says so once. Additive: `driven.admission` is a new optional profile key; deploy the runner for `GET /host` to take effect.
- Session containers get swap equal to their memory limit (`--memory-swap` is twice `--memory`), so a burst is slowed by swapping before the kernel kills the container.
- The governor can act (ADR 0045): `driven.governor.mode` gains `enforce` (default stays `observe`). A checkpoint stops the session like a stall, status `stalled` with reason `checkpoint_stuck`, `checkpoint_context` or `checkpoint_spend`, and the decision that stopped it is logged with `acted: true`; 100% of the budget stops it with `budget_spent`. The three checkpoint reasons continue in a fresh session on the same branch; `budget_spent` does not, and its commits go out as a `[partial]` draft pull request. Warns stay log-only.
- A stopped session leaves a handoff (ADR 0045): on every stop the driver makes, after the snapshot commit, `handoff.json` and a rendered `progress.md` (at most 16 KiB) are written to the output volume, built from git and the stream with no model call: base and head commit, commit subjects, changed paths with line counts, the last test command and its exit status, the `change` skill's steps with a `passes` flag taken from the stream, the session's own todo list, and its last 60 tool calls without outputs. Every item is redacted and scanned for secrets; an item that matches is dropped and recorded as a finding without its value. The continuation's prompt carries the progress text and says the earlier commits are offered, not required (the session may reset to the base commit); a `[partial]` pull request body carries it too. The runner may read the two new names from the volume; against an older runner that refuses them the server skips them. Needs a session image from this release.
- A reusable workflow, `.github/workflows/review.yml`, has a chargehand server review any repository's pull requests ([guide](docs/guide/review-workflow.md)). A caller adds `uses: Egoushka/chargehand/.github/workflows/review.yml@<tag>` with the server URL, the checkout's path on the server and the key as a secret. It sends the pull request's diff range over the HTTP API, waits for the result and posts or edits one comment: each finding with its support verdict and its citations linked to the cited lines at the head commit, the run id and the cost. `fail-on: high` fails the check on a finding of confidence 0.8 or more. It runs only on `pull_request`, refuses `pull_request_target`, and skips a fork's pull request with a notice (no secrets). New scripts: `review-request.sh`, `review-remote.sh`, `review-comment.sh`, `review-post.sh`, `review-gate.sh`.
- An integrator and a CI fixer act through a GitHub App (ADR 0046), for the repositories the profile lists; off unless `driven.integrator` is set, and with none nothing changes. After a task's handover opened a pull request, chargehand updates the branch if it is behind (`merge` repositories), marks the draft ready, and either enables auto-merge with squash pinned to the head commit (`merge`: GitHub merges once the required checks and rules pass; a pull request GitHub already calls mergeable gets a pinned squash merge) or leaves a comment (`ask`). A repository on `always_ask` is `ask` whatever else says, one not listed is off, and a `[partial]` pull request is never touched. When a required check fails, a short session on the same branch (seeded from the last bundle, with the failing jobs and the end of their logs in its goal) fixes it: at most 3 attempts per pull request, never twice for the same failing jobs, counted in hidden markers on the pull request, pushed through the ordinary handover. The App's private key is a secret item; the JWT is RS256 and the installation token is cached until near its expiry. Profile: `driven.integrator` with `app_id`, `installation_id`, `private_key_secret`, `repositories`, `always_ask`, `ci_fix` (additive). run-status/v1 gains the event `integrated` (additive); the task outcome's `detail` carries the integration line; the pull request body says what happens next. The calls are tested against a fake GitHub host only.
- A task that asked a question gets an answer (ADR 0045). `request/v1` gains `driven.tasks[].continue_from` (`run_id` of an earlier batch and its `task_id`) and `answers` (a list of strings); a task with `continue_from` needs no `goal` or `ref`. It starts from that task's kept bundle on the same branch, and its prompt is the earlier goal, the questions the session asked, the answers verbatim and the earlier `progress.md` when there is one. The earlier task must have ended `needs_input` (then `answers` are required) or stopped with its work kept (a `[partial]` pull request); `context.repository.commit` must be the commit it started at; anything else is refused as `invalid_request` before a container starts. Two tasks cannot continue the same one. A task that stops with a bundle now has a `kept-work` artifact (`branch`, `bundle_run`, `commits`, `base`) on its result. run-status/v1 gains the event `question`, published when a session ends `needs_input`, with the questions in `detail` (additive). A task's output volume is kept for 7 days when the task ended with kept work: the sweep at a server start no longer removes it before then. The pull request opened for the question is reused by the answering task, so its `[partial]` title stays until edited. No new session image is needed.
- Driven sessions know their environment and can share services (ADR 0048). **The environment note** needs no setting: before its first turn a session reads a short block appended to its prompt (the static prompt text is unchanged) with the image's tools and their versions, the tools it lacks, the network limit, the repository's detected ecosystems, the command chargehand will run on the branch, and the caches and services. The image build writes `/opt/chargehand/environment.json` with `chargehand session --probe <file>`, from each tool's own `--version`; an image without the file is probed once by the driver. `task.json` gains an optional `environment` member (older session images skip it). **Package caches**, **test services** and **a session image by ecosystem** are off unless the profile's new `driven.services` is set: `caches` (`nuget`, `npm`, `pip`, `gradle`, `maven`) mounts one volume per ecosystem and repository, `chargehand-cache-<ecosystem>-<12 hex>`, read-write at `/cache/<ecosystem>` in that repository's sessions and verifier, with the package manager's variable pointed at it (per repository, not shared, so one repository's session cannot choose what another builds; the volumes are not swept or size-limited); `repositories` declares up to four services per `owner/repo`, pinned images started on the batch's internal network before the first session and removed before the network, reachable as `name:port` (`CHARGEHAND_SERVICES`, `NO_PROXY`), with the data directory in memory; `images` maps an ecosystem name to a session image digest, which must also be in `driven.images`. `images/session-jdk/Dockerfile` builds the JDK, Gradle and Maven variant from the session image, and the release workflow now pushes it as `chargehand-session-jdk`; it has no Android SDK. The runner gains `POST /service` and `--service-images` (a service starts only from a listed image, on a batch network, under its ceilings), accepts `caches` in a session start (only cache-shaped volume names), and the sweep after a restart keeps the services of a resumed batch. A code index mounted read-only at the base commit is decided in the ADR and not built. Additive; the cache mounts and the runner's service route are tested with fakes, and a Postgres service, a cache volume and the JDK image were run on a local engine.
- Tasks can depend on each other, and a batch can be planned first (ADR 0049, CHARGEHAND-174). `request/v1` gains `driven.tasks[].depends_on` (ids of tasks of the same batch) and `driven.plan` (additive). The scheduler starts a task only when all its dependencies ended `completed`, whatever the list order, and a task whose dependency failed, asked a question, was cancelled or was not started is `not_started` with the reason. A dependent starts from its dependencies' work: one dependency stacks it on that branch (its own branch name, its draft pull request based on the dependency's branch, falling back to the default branch with a note if the host refuses), several are merged into one branch on the host before the session starts, and a conflict fails the task as `merge_conflict` (new error code, additive) naming the paths. The seed is the continuation's `seed.bundle`, so the session image is unchanged. Unknown ids, cycles, a self-dependency and `depends_on` with `continue_from` are refused as `invalid_request`. A task with dependencies is not touched by the integrator. The journal keeps the dependencies, so a restart in the middle of a chain carries on.
- A planning step, off by default (`driven.planning.enabled` and `preset` in the profile, or `driven.plan` on a request). Before the batch, one read-only run on the `cheap` preset through the orchestrator sizes tasks that have no `size`, splits an L task into an ordered chain (`t-1 → … → t`, the last link keeps the id) and assigns the shared numbers tasks would otherwise both take (migrations, ADR files) by reading the repository at the base commit. The caller's `size`, `budget_usd`, limits and `depends_on` always win. The plan is the batch result's `plan` artifact (what it checked, each task's size and source, chains, numbers, dropped proposals, and `not_checked`) and a `plan` run event (run-status/v1 gains it; additive); a planner that fails leaves the batch as sent. The prompt is in code, not under `prompts/`, so Prompt CI has nothing new to gate.

### Changed

- Internal: the driven infrastructure (`Driven/`, `Containers/`, `Runner/`, `Egress/`) moved from `src/Chargehand` into its own assembly, `src/Chargehand.Driven` (ADR 0047). Namespaces, the CLI, the server, the profile, the packages and the images are unchanged: no user-visible change. A test fails if the orchestrator core references the new assembly.
- A driven session that ends on a usage or rate limit (the result says "usage limit" or "rate limit", or names a 429 as an HTTP status, not a line number) is `rate_limited` and retryable, not `session_failed`. The scheduler holds new starts until the reset time the result prints (`...limit reached|<unix seconds>`), never for less than 15 minutes (the wait when no reset is printed or it is already past); running tasks go on. Two such endings in a row with no task finishing between them, or a reset more than 6 hours away, stop the batch: it ends `failed` with the retryable `rate_limited` error and the tasks not started reported `not_started`, so it can be resent after the reset. Before, the limit became `session_failed` and the scheduler never paused. The shared `ChargehandException.RateLimited` check is unchanged.
- A driven session that ends on a usage or rate limit after committing is handed over as a `[partial]` draft pull request (ADR 0045); before, its branch was not pushed. The task stays `rate_limited` and retryable.
- `usd_estimate` sums the messages it can price when one message's model has no price, and `usd_estimate_partial: true` (in `session-usage.json` and on `session_progress`) says so; it is null only when none could be priced.
- `self-review.yml` renders its comment with `scripts/review-comment.sh`, shared with the reusable workflow: the comment gains a hidden marker, the support verdict and confidence per finding, and file and diff citations linked to the cited lines; "Task" reads "Run". `self-review.sh` builds its request with `scripts/review-request.sh`, which cuts a goal over 8000 characters. Its behaviour is otherwise unchanged.

### Upgrade

- **Accounts:** an entry in `http.accounts` with no `model` block is now refused with `403 model_credential_missing`. Add `"allow_server_credential": true` to every account that should keep spending the server's model credential, or give it a `model` block with its own `api_key_secret` (ADR 0053). The owner key, CLI runs and stdio are unaffected.
- Deploy the server and the session image together for this release. The server now puts `prices` in `task.json`, and a session image built before the lenient parse (#218) rejects a `task.json` with an unknown member. From #218 on, session images ignore unknown members in `task.json` and the usage file, so later additive fields need no lockstep.
- To use `driven.services.caches` or the JDK image, deploy the runner and the server from this release first (a session start with `caches` is refused as an unknown field by an older runner), then rebuild the session image: it carries `/cache/<ecosystem>` and the environment manifest. With `driven.services` unset an older runner and image keep working; only the environment note is new, and an older image ignores the `environment` member of `task.json`.

### Fixed

- Cancelling a running driven batch (`POST /v1/runs/{id}/cancel`, `POST /v1/halt`) ends it `failed` with error code `cancelled` and the summary "cancelled by the caller: N of M tasks finished", not `tasks_incomplete`. Tasks that finished keep their outcome; a task the cancel stopped is `cancelled`, one it kept from starting is `not_started` with `cancelled` as its `error_code`. No schema change: `status` keeps its enum, as for a cancelled plain run.
- A driven session no longer dies on a `task.json` member it does not know, and the server no longer drops a session's `session-usage.json` for one: server and session image are deployed separately, so a field added on either side reached the other as a strict-parse error. Both ignore unknown members now (`session-outcome.json` and `driven-report.json` already did).

## [0.8.8] - 2026-10-12

Driven batches survive a server restart, and sessions' research and review calls stop failing under load.

### Added

- A driven batch survives a chargehand server restart (a deploy or an OOM) (ADR 0043). Its state is saved next to the run log and the run-token key persists, so on start the server resumes each unfinished batch under the same run id: it re-attaches to sessions still running, hands over ones that finished, re-runs a task whose container is gone, and schedules pending tasks. A graceful stop leaves sessions running instead of killing them. Leftover `chargehand-*` containers, networks and volumes no saved batch owns are removed at start. A handover repeated after a crash finds the draft PR it already opened.

### Fixed

- A driven session's research and review calls are no longer refused with "10 runs are unfinished": only top-level runs count toward that cap, and session calls are bounded by `driven.max_session_calls`. A waiting `orchestrate` call sends an MCP progress notification every 30 seconds (queued or running), so a client's 300-second idle timeout no longer aborts a call that waits for a slot. The tool description, the `run` and `change` skills and the session prompt say the request/v1 fields are the tool's top-level arguments; sessions had wrapped them in a `request` object and got a schema error.

## [0.8.7] - 2026-10-12

How many runs and session calls execute at once is a profile setting.

### Added

- `http.max_concurrent_runs` (default 1) sizes the gate that executed every run one at a time, so a second driven batch no longer waits for the first when it is raised; `driven.max_session_calls` (default 2) sizes the gate for driven sessions' research and review runs, which was fixed at 2 for all sessions together. Observed on the VPS with four sessions in one batch: each research call waited about four minutes for a slot. Defaults keep the old behaviour; raise them only as far as the model's rate limit allows (ADR 0011).

## [0.8.6] - 2026-10-10

The driven session image keeps the .NET CLI home and the NuGet cache on its volume.

### Changed

- The session image keeps the .NET CLI home and the NuGet package cache on the volume (ADR 0039): `DOTNET_CLI_HOME=/work/.dotnet`, `NUGET_PACKAGES=/work/.nuget/packages`, so a restore of a real solution does not fill the tmpfs at `/home/session`. The image already had the .NET 10 SDK; its size does not change. The driven guide gains a `dotnet test` verification example and the NuGet hosts a restore needs in `driven.network.allow` (`api.nuget.org`, `globalcdn.nuget.org`).

## [0.8.5] - 2026-10-10

Claude Code as an observer of driven sessions, and the model credential out of the session containers. `chargehand watch` and `cancel`, the `/chargehand:run` skill and a run id after 10 s let a Claude Code session hand a prompt to a driven batch and follow it. On the subscription the real token now stays in the batch's egress container (`token_exchange`); an API key can go through an operator's gateway (`model_url`). Additive; `driven.enabled` is still false by default.

### Added

- The model credential exchange (ADR 0039 decision 9, CHARGEHAND-148): on the subscription, the real token goes only to the batch's egress container, whose model endpoint accepts a per-task token (bound to the run, its token cap and an expiry) and swaps in the real one toward `api.anthropic.com`. Sessions get the token and the endpoint as `ANTHROPIC_BASE_URL`; the batch result records `credential_delivery: token_exchange`. The runner service carries the credential to the egress container's env file and refuses any other upstream host, so it works on the deployed path. Measured through a runner and a socket proxy: one task to a draft pull request, no real token in any sampled session environment. An API key is unchanged (`environment`, or `gateway_key` with `model_url`).
- `scripts/driven-e2e.sh` can run through a gateway (`CHARGEHAND_E2E_MODEL_URL`, with `CHARGEHAND_E2E_MODEL_KEY` the key the gateway issued): it sets `driven.network.model_url` and `claude_code.base_url`, and a case checks the batch result says `gateway_key`. One task through a Bifrost key ended in a draft pull request for 0.17 USD (CHARGEHAND-161).
- Driven batches are traced like orchestrator runs (ADR 0042, CHARGEHAND-161): a `chargehand.driven.run` span, a `chargehand.driven.task` span per task (state, error code, branch, pull-request URL, model, Claude Code session id, turns, wall time) and under it the session as one `chargehand.driven.session` generation. The batch's and every task's `result/v1` carry its trace id, which until now was random and pointed at nothing. With `telemetry.usage_on_spans` the session span carries usage and cost from the profile's price table, model by model from the stream's `modelUsage`; the table may key a model under any provider. The session outcome gains the session id, the model, cache-write tokens and per-model usage.
- `driven.network.otlp_url` (ADR 0042, CHARGEHAND-161): each session's Claude Code exports its own OpenTelemetry logs and metrics to an OTLP/HTTP collector through a forward on the batch's egress container, tagged `chargehand.run_id`, `chargehand.task_id`, `chargehand.task_run_id` and `chargehand.trace_id`. No prompts, no headers, no traces. An `https` URL, credentials or a query, or a port another forward uses is refused before anything is created. Unset, nothing changes.
- `driven.network.model_url` (ADR 0039, CHARGEHAND-161): driven sessions reach the model through an Anthropic-compatible gateway with a key the gateway issued (a budget, a model list, revocable) instead of the real credential in the container's environment. An `http` gateway is reached through a second forward on the batch's egress container, an `https` one through the proxy; the session gets `ANTHROPIC_BASE_URL`, the batch result records `credential_delivery: gateway_key`, and the key is scanned for like any model credential. It needs `claude_code.api_key_secret`; a subscription token, a URL with credentials or a query, or a port equal to `mcp_forward`'s is refused before anything is created. Unset, nothing changes.
- `/chargehand:run <prompt>`, a second skill in the Claude Code plugin (ADR 0039, CHARGEHAND-159): it hands the prompt to a one-task driven batch on the connected server and observes it with `chargehand watch` under the Monitor tool, relaying the milestones up to the draft pull request, with `chargehand cancel` to stop it. Its `allowed-tools` pre-approve only `orchestrate`, `Monitor`, `chargehand watch|show|cancel` and two read-only git commands, and the skill forbids the session from doing the task itself. Driven sessions being off on the server is reported, not worked around.
- A request with a `driven` block over MCP no longer blocks until the batch ends (ADR 0029, ADR 0039, CHARGEHAND-160): without `Prefer: wait` it waits 10 s and then answers with the run id, the way `POST /v1/runs` does. A client such as Claude Code cannot set the header per call, so a batch started from it held the session for hours. Other requests still wait for their result.
- `chargehand watch <run-id>` and `chargehand cancel <run-id>` (ADR 0039, CHARGEHAND-158): a client of a running server. `watch` tails `GET /v1/runs/{id}/events` and prints one stdout line per event until the result, so a Claude Code session can run it under its Monitor tool and relay a driven run's progress; the exit code is the run's (0 completed, 1 failed, denied or lost, 3 needs input, 2 a wrong key or unknown run). Session progress is thinned to one line per 30 s per task unless the stage changes, and a dropped connection resumes without repeating lines. The server comes from `--url` or `CHARGEHAND_URL`, the key from `CHARGEHAND_API_KEY`, either missing from the profile's `http` block. `cancel` posts to `/v1/runs/{id}/cancel`.
- `scripts/driven-e2e.sh` can run a batch through a real `chargehand runner` (`CHARGEHAND_E2E_RUNNER=runner`) and behind a `docker-socket-proxy` container with the deployment's flags (`proxy`), the way a VPS runs it (CHARGEHAND-157). The default `direct` is unchanged. A case asserts the proxy served the batch, and the runner's key joins the credential scan. Every earlier run used the server's own docker engine, so the runner path, where the first VPS batch found four defects, was untested.
- A driven batch shows its tasks while they run (ADR 0039): the batch run publishes `container_started`, `session_progress` (tokens, turns and the `change` skill's step), `verify_finished`, `pushed`, `pr_opened` and `task_finished`, each with its `task_id`, so `GET /v1/runs/{batch}` and its events say more than `started`. Until now only `accepted`, `started` and `run_finished` were published, and a task's own run is unknown until it ends. `run-status/v1` gains two optional fields, `tokens` and `stage` (additive); the session driver writes turns and stage into `session-usage.json` beside the tokens.
- A driven task's result carries a `changes` artifact (ADR 0039): each changed path with its added and removed line counts, and the unified diff cut at a line within 32 KiB, from chargehand's own clone of the bundle. Until now the changed paths only reached the pull request body as a count, so a client had to fetch the branch to see the change. A diff that looks like it carries a secret is left out; the paths and counts stay. The handover's git output cap now keeps a prefix instead of skipping a read that did not fit.
- `max_parallel_nodes` in the profile (1 or 2, default 2) bounds how many worker nodes of one run execute at once; 1 means one `claude -p` worker on the host, for a machine short on memory.

### Changed

- The first deployment of driven sessions on a VPS is recorded (ADR 0039, the driven guide, the roadmap; CHARGEHAND-156): a draft pull request on the fourth attempt, the four defects each attempt found, and what is still open (the credential gateway, the kept per-task output volume).

### Fixed

- `chargehand runner` says which argument it rejected (ADR 0039, CHARGEHAND-152): the reason, with the value, on its own line before the usage (the usage names every flag, so alone it did not say which one was wrong, and the first deploy restart-looped on a tagged image reference). A valid command line with `--source-roots`, `--outside-networks` or `--forwards` empty now prints a note for each, since each starts fine and then silently refuses work (no workspace, no network join, no callback).
- Cancelling a task whose container had already ended no longer fails (ADR 0039, CHARGEHAND-157): `docker kill` answered "is not running" (or "No such container"), the engine threw, and the cancel ended `container_unavailable` instead of `cancelled`. A signal to a container that is gone or ended is now a no-op; any other `kill` failure still throws. Found by the e2e script's new runner mode; the runner uses the same engine.
- The server guide says that `repository_roots` must also cover `<worker_root>/.checkouts` for driven sessions (CHARGEHAND-155): a session's research and review calls name its own checkout, and without that root the server refuses them (`repository_not_allowed`), found on the first deploy.
- A run under `serve` is its own trace again: it started inside the HTTP request's unrecorded activity, so the parent-based sampler dropped it and every span below, and no served run reached Langfuse.

## [0.8.4] - 2026-10-05

The second fix from the first driven batches on a Linux host: a batch through the runner could not call the server back. Additive; `driven.enabled` is still false by default.

### Fixed

- A driven batch through the runner could not call the server back (ADR 0039, CHARGEHAND-154): the runner's egress request had no field for the forward, so the egress container started without it and a session's research and review call was refused (`ECONNREFUSED`; it stopped and asked, rightly, instead of skipping them). The request now carries the forwards and the runner starts only those listed in its new `--forwards` option, like `--outside-networks`, so a server cannot point an egress container at any other host. An egress with no forwards is unchanged.

## [0.8.3] - 2026-10-05

Two fixes found by the first real driven batch on a Linux host (one of them hid the other) and a docs fix from the first deploy. All additive; `driven.enabled` is still false by default.

### Fixed

- The first driven batch on a Linux host failed, and said the wrong thing (ADR 0039, CHARGEHAND-153). (1) The workspace helper cloned the checkout with `-c safe.directory`, which `git clone` of a local path does not pass to its `git-upload-pack` child: on a host where the checkout belongs to another user it stopped with "detected dubious ownership" (a Mac's bind mounts hide the owner, so no earlier run saw it). The helper now sets a global `safe.directory` in its writable `/tmp`. (2) `RunnerClient.RemoveAsync` threw on the runner's 404 for a container that was never started, so any failure before the start was replaced by "no such session container" and the rest of the cleanup was skipped; a missing container is now a no-op, as with `docker rm -f`.
- The server guide says what the runner needs to start and to run a batch (CHARGEHAND-152): an image reference without a tag (a tagged one is refused, and the runner prints its usage and exits), `--source-roots` and `--outside-networks`.

## [0.8.2] - 2026-10-05

What a container deployment of driven sessions needs from the code, all additive: the session image is published with the release, the server image carries the docker CLI the runner needs, and a session calls the server back by a fixed name. This is still not the minor version that driven sessions get when they are wired: `driven.enabled` stays false in every real profile, and a batch still delivers the model credential in the container's environment until the credential exchange (CHARGEHAND-148) lands. The v0.8.1 tag did not carry these: its notes listed the entry below by mistake, and it is moved here.

### Added

- Driven sessions can be deployed with containers (ADR 0039, CHARGEHAND-151): the release publishes the session image `chargehand-session:<version>` next to the server image and prints both digests in the job summary; the server image carries the docker CLI (pinned by digest to the engine's version) so `chargehand runner` runs from it; a session calls the server back as `http://chargehand-driven:<port>` (the egress container's alias on the batch network, the same in every batch), so `http.allowed_hosts` lists one name instead of a wildcard; the server guide describes the services and the path rule for `worker_root`.

## [0.8.1] - 2026-10-05

The parts of driven writing sessions ([ADR 0039](docs/adr/0039-driven-writing-sessions.md), [guide](docs/guide/driven.md)) and the prompt-enhancer extension (ADR 0040, 0041), all additive. This is not the minor version that driven sessions get when they are wired: `driven.enabled` stays false in every real profile, a driven batch still needs its credential exchange, and the skill's measured adherence (at least 7 of 10) is not met. No schema major changes; `Chargehand.Contracts` changes are additive.

### Changed

- The session image installs Node 26.10.0 from nodejs.org, checked against its published SHA-256, in place of the distribution's Node 18 (ADR 0039): a repository that runs `.ts` sources through Node's type stripping, or asks for `engines.node >= 26`, could not pass its tests in a session or in the fresh verification run.
- The headless `change` skill and the driver prompt state the order research, write, run the tests, then review, and that a goal which cannot be met still gets a best attempt and a stop with the reason, never a stop after research alone or an edit to a test (CHARGEHAND-141). Measured: 12 of 12 sessions followed the steps on an interim wording and 12 of 12 on this one, up from 7 of 12.

### Added

- `scripts/driven-e2e.sh` runs on a real repository: `CHARGEHAND_E2E_REPO=<path of a git checkout>` clones its committed history (nothing is pushed to its own remote), with `CHARGEHAND_E2E_TASKS` and `CHARGEHAND_E2E_VERIFY` (the verification command as a JSON argument vector) required (ADR 0039, CHARGEHAND-150). Measured on a TypeScript-on-Node repository: 10 of 10 sessions followed the skill's steps and 4 of 4 passable tasks ended in a draft pull request. `/tmp` in a session and in the fresh verification run stays `noexec`; a repository whose tests run a script from `/tmp` sets `TMPDIR` under `/work` in its `context.verify` (guide and ADR).
- A run now asks the profile's `prompt_enhancer` about its request text and reports the outcome (ADR 0041): one `enhance` call before intake and one `feedback` call after the result, in `run`, `serve`, the MCP tool and `change`. The text sent to workers is always the original; a rewrite is offered to nobody and reported as not accepted. Context is the four allowed fields (repository as the folder name). No `prompt_enhancer` in the profile: no call.
- A `prompt_enhancer` extension (ADR 0040, WHET-13): a profile entry naming an `mcp_servers` server that lists `enhance` and `feedback`. `IPromptEnhancer` and `McpPromptEnhancer` carry the calls, and `GuardedPromptEnhancer` turns a late, failing or empty answer into the original prompt inside `deadline_ms`. `chargehand extensions check` verifies the two tools. No client calls it yet.
- `chargehand runs adherence` prints `adherence` and `honest_stop` apart (ADR 0039, CHARGEHAND-141): `honest_stop` counts sessions that did research, a write and a test run in order, made no review and wrote no test file; it is not part of the 7 of 10 bar.
- Driven batches see a session's token use while it runs (ADR 0039, CHARGEHAND-147): the in-container driver rewrites `session-usage.json` in the output volume as the stream grows, and the runner reads it through the existing out-volume read (a fifth allowlisted name, read only) every poll interval (5 s by default) while the task runs and reports it to the scheduler. A task that reports more than its own token or dollar cap is stopped at once (`cost_cap_reached`), and a batch stops its running tasks as soon as their reported use passes the batch cap (before: only once it was over by a whole task cap). Dollars still arrive only with the session's final result, so live dollar caps bind through the session's own `--max-budget-usd`.
- `scripts/driven-e2e.sh` can run on a Node repository (`CHARGEHAND_E2E_REPO=node`) or a task list of its own (`CHARGEHAND_E2E_TASKS`), and fails `impossible_tests_untouched` when the task that cannot pass is passed by editing a test file (ADR 0039, CHARGEHAND-141).
- A driven session is told the repository path and base commit its run token admits (`CHARGEHAND_REPOSITORY_PATH`, `CHARGEHAND_BASE_COMMIT`, ADR 0039, CHARGEHAND-141): the skill's driven section and the driver prompt use them for the research and the review call (the review at the base commit, with the diff as input) and say not to skip either step. Measured: 7 of 9 sessions followed the steps, up from 1 of 3; the ADR records the result and that the feature is not ready. The runner also removes a container whose start was cancelled in flight, by its name.
- `scripts/driven-e2e.sh` (ADR 0039, CHARGEHAND-141): an end-to-end run of a driven batch on a synthetic repository (three tasks, a local bare remote that takes only `chargehand/*`, `max_parallel` 2, a stub of the draft-pull-request endpoint, a cancel case, a scan of everything it wrote for the push token and the model key). It calls a real model and has not been run yet. It needs two switches in the CLI that exist for it alone: `CHARGEHAND_E2E_GITHUB_API` and `CHARGEHAND_E2E_LOCAL_REMOTE=1`.
- `chargehand runs adherence <stream.jsonl>...` reads stored session streams and says, per session, whether it did research, a write, a test run and a review in that order with at most 2 fix rounds, and whether at least 7 of 10 did. Nothing is measured yet.
- Driven sessions move a task and its results through the session's output volume (ADR 0039, CHARGEHAND-146): `task.json` goes in before the container starts, and the bundle, the report and the outcome come out after it ends, by a throwaway helper container with no network started from the session image. The runner service has two new authenticated routes for it (`PUT` and `GET /out/{run}/{name}`) that take only a listed image, `task.json` in and the three result files out, and the Docker CLI engine does the same locally. A batch no longer ends every task as `container_unavailable`.
- A request with a `driven` block runs as a batch (ADR 0039), through `chargehand serve` when the profile has `driven.enabled`: tasks resolve to goals, the batch gets its network and egress container, each task gets a workspace at the pinned commit and a session container, and `TaskRunner` hands the session's branch to the handover and writes the task's own `result/v1` under its own run id (parent: the batch). The batch result lists them. With `driven.enabled` false the request ends `failed` with `invalid_request`; the stdio and CLI paths refuse a driven request the same way. The profile's `driven.network` takes `outside` and `mcp_forward` (`host:port` of the server), which gives sessions a forward to the server's MCP endpoint and a run token; unset, they get neither. Not yet working end to end: the runner service cannot put a task into a session's output volume or read its bundle out, so a batch ends each task `container_unavailable` until it can (`docs/guide/driven.md`).
- The result of a driven task and of a batch (ADR 0039): a task's claims are the session's, cited against the pushed commit and put through the same resolver and support check as any result; chargehand's own test run (`verification`, in a fresh container) and the session's claim about its tests (`session-tests`) are two artifacts, never merged; the batch result is `completed` only when every task ended in a draft pull request, else `failed` with `tasks_incomplete` naming them.
- The handover of driven sessions (ADR 0039, not yet reachable from a request): chargehand fetches a session's bundle into a scratch clone of its own checkout and checks the branch itself (descends from the base, changes something, no secret-shaped text or secret-like file names in the diff, no CI configuration unless allowed, the repository's tests pass in a fresh container), then pushes only `chargehand/<run>`, never forced, with a credential that cannot push the default branch, and opens a **draft** pull request. The GitHub client has one method, create a draft; it cannot merge.
- `chargehand eval score-claims <claims.jsonl> --repo <dir> --commit <sha> [--out <verdicts.jsonl>]` (benchmark goal 0.9): claims from any source, each with `path:line` locators, get one verdict from the shipped support judge — `uncited` (no locator), `unresolved` (a locator names no file, line or range at the commit), `supported`, `partial`, `unsupported`, or `unchecked` (the judge failed twice). Claims are judged in batches of one arm, question and run, and the arm is never sent to the judge.
- A batch's egress container can carry operator-named TCP forwards (`--forward <port>=<host>:<port>`, ADR 0039), which is how a session on the internal network reaches the chargehand server for its research and review calls; the allowlist proxy still refuses every private address, and nothing a session sends can add or change a forward.
- The `change` skill has a driven mode (ADR 0039): when `CHARGEHAND_DRIVEN=1` it asks no person anything (it prints `NEEDS_INPUT:` and the questions and stops), uses the branch in `CHARGEHAND_BRANCH`, writes no report file and neither pushes nor opens a pull request, and ends with a fenced JSON report. Its steps for a person are unchanged.
- The batch scheduler of driven sessions (ADR 0039, not yet reachable from a request): at most `max_parallel` tasks at once, in order, each reserved at its whole cap so the batch never starts a task it could not afford; running tasks are stopped when the batch overshoots its cap by more than one task's cap; a rate-limited or unavailable provider stops new starts and says how to switch to an API key; a task ref becomes a goal through the profile's `driven.task_source` mapping.
- Driven sessions call chargehand back with a run-scoped token (ADR 0039): it opens only `POST /v1/runs`, one run's status and the MCP tool, only for the `default` and `review` presets on its task's repository and commit, within the task's caps; the runs it starts record the task as their parent and use their own gate, so they cannot wait for the batch that holds the main one.
- The server can list runs (`GET /v1/runs`, `run-summary/v1`), cancel a run it holds (`POST /v1/runs/{id}/cancel`, ending `failed` with `cancelled`) and be halted and resumed (`POST /v1/halt`, `POST /v1/resume`); `chargehand runs kill --all` removes driven-session containers by label without a server (ADR 0039).

### Fixed

- `scripts/egress-test-image.sh` publishes the CLI for the container engine's architecture (`linux-arm64` or `linux-x64`) instead of the host's, and sets the image's entrypoint, so the egress tests run on a Mac; the egress tests supply a `nc` stand-in (bash `/dev/tcp`) when the test image has none.
- `chargehand runs adherence` counts `node --test`, `jest`, `vitest` and `mocha` as a test run (it read them as no test).
- The plugin's MCP entry passes `--source https://api.nuget.org/v3/index.json` to `dnx`, so a machine whose NuGet config lists a private feed no longer fails with 401 and `CONNECTION_CLOSED` (CHARGEHAND-127).

## [0.8.0] - 2026-09-30

One release for two goals; there is no 0.7.0, because no commit is 0.7 only (as with the missing 0.5.0). Goal 0.7
([roadmap](ROADMAP.md)): workers can write. The `code` preset edits files in its own clone, runs the repository's tests
in a sandbox and returns a branch, a diff and a verification artifact. Goal 0.8: every claim is checked for support
against the text it cites, results can be signed with a key you make, and `chargehand verify` checks a signed result
offline; each cited text's sha256 is recorded on its evidence. Schema changes are additive, and `Chargehand.Contracts`
1.4.0-alpha is published with them. Still open: the usage bars of 0.5 and 0.7 (the maintainer's own use on real tasks);
the Linux sandbox (`bwrap`) is only tested as arguments, never run; the support judge was measured on 30 claims.

### Added

- Every claim is checked for support (goal 0.8, ADR 0036): after a node, one call on the intake model judges whether the text each claim cites supports it. `result/v1` claims gain an optional `support` (`supported`, `partial`, `unchecked`); an unsupported claim moves to `open_questions`, a partly supported one stays at half confidence and is not kept for memory; a failed check leaves the claims `unchecked` and never fails a run. Profile `support_check` (default true) turns it off. On 30 labelled claims, Haiku and Sonnet each agreed with the labels 25 times, perfectly on clearly supported and clearly unsupported claims and not on partly supported ones (`scripts/support-eval.sh`, [guide](docs/guide/support-and-signing.md)).
- Results can be signed (ADR 0036): profile `signing.key_file` or `CHARGEHAND_SIGNING_KEY_FILE`, a P-256 key you make yourself; `result/v1` gains an optional `signature` (ES256 over the RFC 8785 canonical result). `chargehand verify <result.json> --public-key <pem>` and `ResultSignature.Verify` in `Chargehand.Contracts` check it offline. Chargehand never creates or stores a key.
- Evidence without a `sha256` gets the SHA-256 of the whole cited text, taken before any cut for the judge.
- Groundwork for writing workers (goal 0.7, ADR 0035), all additive: a preset node kind may set `writes` and `verify` (`timeout_seconds`, `max_fix_rounds`), a request's `context` may carry `verify` (the test command as an argument vector), and `result/v1` error codes gain `sandbox_unavailable` and `verification_failed`. Nothing uses them yet.
- A writing node's workspace (ADR 0035): a per-run clone under `<worker_root>/.runs/<run>/<node>` of the cached checkout, on branch `chargehand/<run>/<node>`. Chargehand commits for the worker with the repository's hooks off and signing off; the source repository and the shared checkout are never written. Nothing creates one yet.
- The sandbox a writing run's tests will execute in (ADR 0035): `sandbox-exec` on macOS, `bwrap` on Linux, behind one interface; profile field `sandbox` (`kind`, `network`, `env`). A command may write only in its workspace and a private temp directory, may not read credential locations, has no network unless allowed and gets a cut environment. Nothing runs in it yet.
- The verifier for a writing node (ADR 0035): the request's `context.verify` command, else the repository's own (`dotnet test`, `npm test` with a test script, `pytest` or `unittest`, `cargo test`, `go test ./...`), run in the sandbox with a timeout and an 8 KiB output tail. Nothing calls it yet.
- The `code` preset (goal 0.7, ADR 0035): one writer that edits files in its own clone and returns a branch. Chargehand runs the repository's tests in the sandbox after each answer, sends a failure back to the worker at most twice, commits `chargehand/<run>/writer` with hooks off and returns three artifacts: `branch` (where to fetch it), `diff` and `verification` (command, sandbox, network, exit code, attempts, output tail, changed build and test paths). A run that stays red fails with `verification_failed` and keeps the branch; a run with no test command completes with the open question "No test command found". `scripts/write-e2e.sh` checks it with a real model and the real sandbox ([Writing a branch](docs/guide/writing.md)). Workers still have no shell, nothing is pushed or merged, and a writing preset is refused with `sandbox_unavailable` where no sandbox exists.

## [0.6.1] - 2026-09-29

Two fixes found by the live checks of 0.6. A worker's citation of a service tool's reply now resolves, and a memory
mapping can carry where a run's citations were checked into the retain call's metadata. The breaking change and the
migration notes of 0.6.0 are unchanged. The one schema edit is the description of the `locator` field in `result/v1`, and
`Chargehand.Contracts` 1.3.1-alpha is published with it, because the package embeds the schema text.

### Added

- Retain placeholders `{repository}`, `{commit}` (12 hex characters) and `{locators}` (joined with `; `), so a memory mapping can
  send where a run's citations were checked as metadata (`profiles/example.json` and the guide's Hindsight entry do). Hindsight
  rewrites a retained item into a sentence without the commit; the metadata and the stored document keep it. A recall
  `results.text` template can read nested fields (`{metadata.commit}`). Mappings without the new placeholders send what they sent.

### Fixed

- A worker's citation of a service tool's reply now resolves. Workers cannot see message ids, so they cited the reply text as a `session_message` locator and the claim became an open question. A `session_message` locator that quotes at least 12 characters of a tool's reply in the session now resolves, and the task text says so when the run has services. Nothing changes without services; `result/v1` changes only in the locator's description.

## [0.6.0] - 2026-09-29

Breaking: the single-object form of `memory` in the profile no longer loads. Memory is a list of MCP providers now, and a
profile that still has the object fails with `memory is a list now` and a pointer to the guide. Move the memory
service's MCP endpoint into `mcp_servers` and list the provider under `memory`; Removed below has the migration, and a
profile without `memory` needs nothing.

Goal 0.6 ([roadmap](ROADMAP.md)): runs use your MCP services and memory. Memory is an ordered list of MCP providers, each
with a declarative mapping from recall, retain and invalidate onto the server's tools; recall asks all of them and
stacks the facts with the name of the memory each came from. With `retain` on, a run stores only the claims whose
citations resolved at its pinned commit. A preset can give its workers read-only tools from an MCP service, on Claude
Code and on OpenCode, and `chargehand extensions check` tests the mappings and grants against the servers' real tool
lists. With no server listed a run behaves as before. [Memory and services](docs/guide/memory-and-services.md) walks
through the setup. The release also closes a hole in the OpenCode runtime: workers could start a checkout's own MCP
servers and call any MCP tool. Project config is now disabled on the servers chargehand starts, and MCP tools are denied
unless a preset grants them (Security; restart an OpenCode server you started earlier).

There is no 0.5.0: goal 0.5's code shipped in 0.4.0, and what remains of its done-when is the maintainer's own use of
`/chargehand:change` on real tasks, which is still open. The one schema change is additive: `preset/v1` gains the
optional `services` field, and `Chargehand.Contracts` 1.3.0-alpha is published with it.

### Security

- OpenCode workers can no longer reach MCP servers. A preset's leading `* * allow` reached the tools of any server
  registered at the location, including write-shaped ones and OpenCode's MCP resource tools, and a checkout's own
  `opencode.json` could register a server whose command OpenCode started when a session was created there. Every
  OpenCode session's rules now end with `*_* * deny`. The server chargehand starts and `scripts/opencode-serve.sh` set
  `OPENCODE_DISABLE_PROJECT_CONFIG=1` and `OPENCODE_CONFIG_PROJECT_DISABLE=1`, so a checkout's `opencode.json`,
  `.opencode`, `AGENTS.md` and project skills are no longer read (workers stop receiving the checkout's `AGENTS.md`).
  Restart a server you started earlier with the script, and set both variables on one you start another way. The Claude
  Code runtime was not affected (ADR 0034).

### Added

- `mcp_servers` in the profile (ADR 0034): MCP servers by name, over Streamable HTTP, the older SSE transport
  (`"transport": "sse"`; `auto`, the default, tries Streamable HTTP and then SSE, and means Streamable HTTP for a worker's
  runtime) or stdio, with `{secret:item}` in header and environment values. Memory and a preset's `services` both read
  them, over one connection per server.
- Memory from any MCP memory server, several at once (ADR 0034). `memory` is a list of providers; each names an
  `mcp_servers` entry and maps recall, retain and invalidate onto that server's tools: argument templates with `{query}`,
  `{namespace}`, `{max_facts}`, `{text}` and the like, and a `results` mapping that says where the facts are in the answer
  (`path`, `id`, and `text` as a field, a template such as `{date}: {summary}`, or an ordered list of these). Recall reads
  a tool's structured content when the path finds an array there and its text block otherwise. An entry without a retain
  tool is recall-only, as an archive such as Chronicle is, and `namespace` defaults to the entry's name. A mapping that
  names an unknown server or placeholder, or misses its recall tool, fails when the profile loads.
- `services` on a preset's node kind (`preset/v1`, an additive field; ADR 0034): a server from `mcp_servers` and the tool
  names workers may call, exact or with `*` globs, never a whole server. At the start of a run chargehand connects,
  lists the server's tools and grants the ones named. A server, secret or tool that does not resolve is dropped, not fatal:
  `chargehand show` prints one `service` line each, with the span tags `chargehand.service.<name>.granted` and `.issues`.
  `as_sent.tools_sha256` covers the granted tools and is unchanged when there are none. No shipped preset lists services.
- Claude Code workers get the granted services (ADR 0020 addendum): the servers go in a private `--mcp-config` file, mode 0600
  in a fresh 0700 directory under the system temp directory, removed when the turn's process exits (also on failure or
  interrupt) and never on the command line or in a log; `--allowedTools` names exactly the granted tools, and the server's
  other tools are disallowed so they do not cost tokens. A granted server the CLI reports as not connected is an issue on
  the run (`not_connected: failed` in `chargehand show`), not a silent gap.
- OpenCode workers get the granted services (ADR 0034): before a run's first session at a location chargehand registers each
  granted server there (`PUT /api/experimental/mcp/{name}`), waits until `GET /api/mcp` says `connected`, and ends the
  session's rules with `*_* * deny` and one `<server>_<tool> * allow` per granted tool, so only those tools are callable
  and a server's other tools stay refused. The registration is named for the server and a per-process keyed hash of its
  config, so runs with other credentials do not change each other's server; it is shared by every run at the location
  that uses the same config, held by count, and removed when the last run ends (completed, failed or cancelled). A
  server that does not connect (`failed`, `needs_auth`, no answer in 30 s) is dropped, removed and reported on the run as
  `not_connected: <status>`; the run goes on without it. Presets that deny `*` still cannot reach a service on OpenCode
  (workers use its `execute` tool); no shipped preset lists services.
- `chargehand extensions check [--preset <name>] [--probe <query>]` (ADR 0034): connects every `mcp_servers` entry and lists
  its tools, then checks each memory mapping (the tool exists, every argument name is a property of its input schema,
  every required argument is set) and each preset's `services` (every named tool or glob matches a listed tool). It prints
  one line per item, `ok` or the problem with `; action:` and what to do, and reads every preset in `presets/` unless
  `--preset` names one. `--probe` also runs one real recall per memory and prints how many facts came back, never the
  facts. Exit 0 when nothing is wrong, 1 on a problem, 2 for a usage error or a profile that does not load. A line never
  holds a URL, a credential or an argument value.
- The guide page [Memory and services](docs/guide/memory-and-services.md): the `mcp_servers` transports, the Hindsight-through-a-gateway
  and Chronicle entries side by side, retain, preset services and what each runtime does with them, the check command,
  the migration from the old `memory` object, and the live checks made on 2026-09-29.

### Changed

- A `memory` list in the profile is now read (ADR 0034); until now it loaded and did nothing. `run`, `serve` and `mcp` build
  one memory stack from it: each entry becomes a source that calls its server's mapped tools over the same MCP
  connections a preset's `services` use, in list order, with the entry's limits, `retain` and `retain_tags`. A profile
  copied from `profiles/example.json` now tries its memory servers when a run recalls; a server or secret that does not
  resolve skips that memory, and `chargehand show` says why.
- Recall asks every memory at once and labels each fact with the memory it came from, and the prompt header says so:
  `- [hindsight] Deploys go through GitOps.` The chain block for recalled text is named `memory/recall/<name>`, one per
  memory that contributed, instead of `memory/recall`. Each memory keeps at most 10 facts and 4000 characters, cuts a fact
  at 600 characters, shows each fact as one line and gets 10 seconds by default (`timeout_seconds`); a fact two memories
  return appears once with both names. One recall per run and retain off by default are unchanged. A memory that throws,
  times out or answers what its mapping cannot read is skipped on its own; the others still contribute.
- `chargehand show` prints one line per memory with what it recalled and retained, or why it was skipped, from a new
  optional `extensions` report in the run record. The span tags for memory are per source
  (`chargehand.memory.<name>.recalled` and `.error`) instead of `chargehand.memory.recalled` and
  `chargehand.memory.error`. `result/v1` is unchanged.
- Retain keeps less, and what it keeps says where it was checked. With `retain` on, a run used to store the request text,
  the summary and every claim. It now stores only the claims that cite at least one `file` or `commit` entry that resolved
  at the run's pinned commit, each with its locators, under a first line that names the repository and the commit:
  `Repository: github.com/example/proj, commit 0123456789ab (citations checked at this commit)`, then
  `- <claim> [src/Api/Startup.cs:41-58] (confidence 0.90)`. No longer retained: the request text, the summary, claims
  that rest only on caller inputs, URLs or the session, claims whose citations did not resolve, claims whose text the
  error-text scrubber would change, and everything from a run without a repository (a draft run has no commit, so it
  retains nothing). The repository is the `origin` URL without scheme, user information, port and `.git`, or the
  directory name when there is no `origin`. A run that retained nothing says why in `chargehand show`
  (`no commit`, `no claim qualified`). There is no confidence floor.
- A command secret source that runs longer than 15 s is killed and the next source tried; the error says when one timed out.
- The release workflow lets one run wait per tag. GitHub delivered the v0.4.1 tag push twice, so two runs waited at the
  `release` environment's gate; a concurrency group per ref lets the later run cancel the earlier one, so one approval is
  asked for. The `mcp-registry` job is unchanged.

### Removed

- The Hindsight HTTP client (`HindsightMemory`) and the single-object form of `memory` (`backend`, `url`, `namespace`,
  `api_key_secret`, `max_tokens`, `retain`; ADR 0034). A profile that still has the object fails to load with
  `memory is a list now` and a pointer to the guide page. Migration: put the service's MCP endpoint (and its API key as a
  header) in `mcp_servers`, and list the provider under `memory` with the tool mapping, which
  `profiles/example.json` and the [guide page](docs/guide/memory-and-services.md) show for Hindsight seen through an MCP gateway:

  ```json
  "mcp_servers": { "memory-gateway": { "url": "https://<your MCP endpoint for the memory service>", "headers": { "Authorization": "Bearer {secret:<api-key-item>}" } } },
  "memory": [ { "name": "hindsight", "server": "memory-gateway", "namespace": "<your bank>", "tools": { "recall": { … }, "retain": { … } } } ]
  ```

  The `retain` flag moved from the object to the entry, and a recall entry takes the bank as `{namespace}` in its
  arguments. The tool names are the ones the MCP endpoint lists (a gateway may prefix them: `chargehand extensions check`
  prints the mapping's tool names and says which are missing). `profile/v1` needs no other change.

## [0.4.1] - 2026-09-29

Fixes found in use of 0.4.0 and the first pieces of goal 0.5, and the first version the MCP Registry can list, under
the name `io.github.Egoushka/chargehand`. Intake now sees the text of caller inputs, within a cap per input and a cap
in total; a failed worker session and a Claude Code error result always carry a cause; memory that times out or
answers garbage is skipped instead of failing the run; and the private-terms hook works in a linked worktree. No schema
changed, so `Chargehand.Contracts` stays at 1.2.0-alpha.

### Added

- The release workflow lists each new version in the MCP Registry (ADR 0027). A `mcp-registry` job runs after `release`
  when `NUGET_USER` is set: it stamps a copy of `.mcp/server.json`, waits until nuget.org serves the package README with
  the ownership line, and publishes with the registry's GitHub OIDC login. It holds no write token and needs no new secret.

### Changed

- The MCP Registry name is `io.github.Egoushka/chargehand`, with the owner spelled as GitHub spells it: the registry
  matches the namespace and the README's `mcp-name` line case-sensitively. The `Chargehand` 0.4.0 package has the
  lower-case line and cannot change, so 0.4.1 is the first version the registry can list.

### Fixed

- Intake saw a caller input's id and kind but not its text, so a run whose request carried its goal, diff and test
  output as `inputs` could stop with `ask`, asking for the inputs it had been sent. Intake now reads each
  input's id, kind, size and the first 2000 characters of its text, and the prompt says when an input was cut; the
  worker still gets every input whole.
- A worker session that ended `failed` with no assistant message and no error text (OpenCode drops the session before
  any model call when the model is one its server does not declare) returned a bare "worker ended failed" with a
  generic action, and the reason was only in the server's log. The error now says the worker runtime ended the session
  before any model call, and its `action` points at the runtime's log (for OpenCode, the server's) and names the model
  the run asked for, to check against the runtime's models and the profile's `models` map (or says no model was mapped).
- A Claude Code turn that ended in an error result with no `result` text (`error_max_turns`, `error_during_execution` and
  `error_max_budget_usd` carry `errors` instead) failed with no reason and was reported in OpenCode's terms, as a session
  ended before any model call. The error now names the result's subtype, turn count and `errors`.
- A request with many `inputs` still grew intake's prompt without bound, up to the request size limit. Intake now reads
  at most 8000 characters of input text in all, in request order; an input after that is listed with its id, kind and
  size and a note that its text is left out here, and the worker still gets every input whole.
- The `.private-terms` hooks work in a linked worktree. The list is gitignored, so a worktree never had a copy and every
  commit there aborted with "`.private-terms` is missing"; the check now falls back to the main worktree's list. A
  worktree's own file still wins, and with no list in either place the commit is still blocked.
- Memory failed open only on `HttpRequestException`. A provider timing out (`TaskCanceledException`, which `HttpClient`
  raises when its own timeout elapses) ended the run with an exception and no result, and a `JsonException` or
  `IOException` from a provider failed it. Recall and retain now skip the provider on any exception except the caller's
  own cancellation, and the run goes on without it.

## [0.4.0] - 2026-09-29

Goal 0.4 ([roadmap](ROADMAP.md)): chargehand runs with nothing configured. No profile file is needed, the worker
runtime is named or found on the machine, credentials come from environment variables or the CLI's own login, and the
orchestrator ships as a `dnx` tool that speaks MCP over stdio. The release now publishes that tool, `Chargehand`, to
nuget.org next to `Chargehand.Contracts`. The first pieces of goal 0.5 (the review preset, the Claude Code plugin and
the change skill) are on main and in this tag; the plugin's server needs the published package.

### Added

- `chargehand mcp` serves the `orchestrate` tool over stdio, so an MCP client starts it itself; its child processes get
  a closed stdin, and a caller receives the run id before its own timeout can fire.
- The orchestrator packs as a `dnx` tool, `Chargehand`, with an MCP server manifest (`.mcp/server.json`, ADR 0027),
  packed and started in CI by a smoke test. Prompts and presets are read from the install, and the run log goes to a
  per-user default when no profile names one.
- Runtime detection without a profile block: Claude Code connects on its own (and on the CLI's own login when no
  credential variable is set), and an own OpenCode server is started when the profile has no `opencode` block. OpenCode
  is pinned at 2.0.18.
- CI fails on a breaking change to a published schema since the latest release tag (additive changes only).
- The read-only `review` preset, the `chargehand` Claude Code plugin and its marketplace entry, and the change skill
  (`/chargehand:change`, one prompt to a reviewed change), with an end-to-end check and a user guide under `docs/guide`.
  A workflow that has chargehand review its own pull requests exists and stays off until `SELF_REVIEW=true`.
- ADR 0033, the CI policy: one required `ci-gate` check, prose-only pull requests skip the build jobs, CodeQL and
  Scorecard run weekly and on demand instead of on every pull request or push, and auto-merge is on.

### Changed

- Profile is fully optional (ADR 0026): `Profile.Load` defaults an absent file rather than throwing; `worker_root`,
  `default_preset`, `intake_model` and `prices` each have a default (a fixed directory outside `$HOME`, `cheap`,
  unset, and empty). `secret_store` is replaced by an ordered `secrets` list (env, then command templates as argv
  arrays, first success wins) — **a profile still carrying `"secret_store": "keychain"` needs a one-line migration**
  to `"secrets": [{"env": true}, {"command": ["security", "find-generic-password", "-s", "{item}", "-w"]}]` (see
  `profiles/example.json`).
- The worker runtime is chosen by a `RuntimeSelector`: a profile's `runtime` field or `CHARGEHAND_RUNTIME` wins,
  then the profile's only runtime block (`opencode` or `claude_code`), so an existing profile keeps its runtime
  (ADR 0032); otherwise `PATH` is probed for a known agent CLI. No agent CLI found is `runtime_unavailable`; more than one found
  is a new `runtime_ambiguous` error naming every CLI seen — no silent priority order.
- `IPriceTable.PriceUsd` returns `decimal?`: an unpriced model's cost is unknown, not a silent `$0`. The run's USD
  cap cannot fire on an unpriced model; the per-node-kind token budgets remain the real guardrail. `result/v1`'s
  `usage.usd` may now be `null` for the same reason.
- `ClaudeCodeWorkerRuntime` and `OpenCodeWorkerRuntime` accept an unset model (`NodeSpec.Model`, `GenerateAsync`) and
  fall back to the runtime's own default instead of requiring one.

### Fixed

- Every `result/v1` error now names a concrete `action`, and `repository_not_allowed` says how to fix it; a run with no
  `repository_roots` allows the directory it was launched in.
- With no profile, or a `models` map that does not name a preset's placeholder model (`provider/worker-model`,
  `provider/small-model`), the placeholder reached the agent CLI and every run failed. An unmapped placeholder is now
  unset, so the runtime uses its own default model, labelled `auto` in the prompt chain as for intake.
  Real `provider/model` ids in a preset still pass through.
- A call the runtime reports no model for (Claude Code's compactions; every call on the runtime's default model) cost
  a silent `$0`. It is now priced at the node's model, and with no model on either its `usage.usd` is `null`.
- A worker that ended `failed` returned "worker ended failed" and nothing else: the provider's reason was in the
  session messages and never reached the result. The message now carries it (`worker ended failed: There's an issue
  with the selected model …`), scrubbed and cut at 300 characters, and every node failure — failed, rate limited,
  over budget, past its deadline, no valid result — carries an `action` saying what to change or where to look
  (`chargehand show <run id>`).
- A worker that wrote `status: failed` in its own result block (a change request on a read-only preset, which runs as
  an answer when intake's action is not in the preset) returned a failed result with no `error`, against ADR 0022. It
  now carries `internal` with "the worker reported failed: <summary>" and an action.
- The release workflow published only `Chargehand.Contracts` to nuget.org, so the `Chargehand` dnx tool package the README,
  the plugin and the MCP Registry manifest name was never available. It now packs and pushes the tool too, each package
  only when its version is new there.
- Prompt CI blocked every pull request that adds a preset with its eval cell as uncovered: the gate reads cells from
  main, which does not have the new cell yet. A file the base lacks now passes when a cell in the change's own
  `evals/cells.json` names it (`--change-cells-file`); tolerances and existing files stay on main's cells. With
  `--allow-uncovered`, the status said "no prompt or preset change"; it now names the files that passed without a cell.
- Prompt CI crashed with an unhandled 404 when an eval cell's Langfuse dataset did not exist yet (a new cell, before
  its first `eval push`). A missing dataset now has no items, so the gate blocks with "0 items; the gate needs at
  least 8" instead.

### Security

- The `.private-terms` denylist fails closed. A malformed pattern made `git grep` exit 128, which the `pre-commit`
  hook read as "no match" and let the commit through; the commit is now blocked with the error. The `commit-msg` hook
  checks the message against the same list, except your own `Signed-off-by` and the diff `commit -v` adds
  (`scripts/check-private-terms.sh`).
- Prompt CI evaluates the commit its run was approved for (the event's head, passed as `HEAD`) instead of reading the
  pull request's head when the gate job starts, so a push between an approval and the job no longer runs
  unreviewed prompts on the eval runner.
- `Scrub` no longer redacts ordinary words that merely contain a key-like substring (`task-spec`, `disk-cache`,
  a stray "user:") — the `sk-` pattern needed a left boundary. It now also catches shapes 0.2.2 missed: LiteLLM's
  "Key Hash (Token) =", compound identifiers (`team_member`, `user_id`, `organization`, `user_api_key_alias`),
  camelCase `apiKey`, `Authorization: Basic`, and spend figures in scientific notation. Fixes an ordering bug where
  a `token: Bearer <secret>` message redacted the word "Bearer" instead of the secret.

## [0.3.0] - 2026-09-28

Goal 0.3 ([roadmap](ROADMAP.md)) closed: every change to this repository is now tracked on a project board,
released through an automated pipeline, and checked by CI before it merges. Delivered across 0.2.1–0.3.0: gateway
error redaction (0.2.2), the CHANGELOG-to-GitHub-Release pipeline with dormant nuget.org publishing (0.2.2),
`scripts/check.sh` and the schema-change guard hook (0.2.2), OpenSSF Scorecard, dependency review, CodeQL and a
dormant SonarQube job (0.2.2), stricter analyzers (0.2.2), and this release's own repository hygiene (topics,
stale-branch cleanup). This bump closes the goal and moves the roadmap to 0.4.

## [0.2.2] - 2026-09-28

### Security

- A failed result no longer repeats the model gateway's error text verbatim: keys, key aliases, bearer tokens and
  spend figures are redacted before a message reaches `result/v1` or the run log. A refusal over a spend budget says
  what to do in `error.action`.

### Added

- `ROADMAP.md`; `scripts/check.sh`, the one check before a push; a Claude Code hook that asks before an edit to a
  published schema major.
- OpenSSF Scorecard, dependency review on pull requests, CodeQL, and a SonarQube Cloud job that runs once the project
  is connected; badges in the README.

### Changed

- A tag `v<Version>` also creates the GitHub Release, with the version's section of this changelog as its notes, and
  publishes `Chargehand.Contracts` to nuget.org when its version is new there. Publishing waits for an approval in the
  `release` environment; a tag whose version has no section here fails before anything is published, and CI fails a
  version bump that comes without one.
- The Prompt CI runner ADR is now ADR 0025 (two ADRs had number 0022); ADRs 0024 and 0025 are accepted.
  `TRADEMARK.md` says how the name may be used, and contributions are signed off (DCO).
- The build runs the .NET analyzers at `10.0-recommended`. Parsing and formatting no longer depend on the machine's
  locale: a hand score such as `0.8` parses where the decimal separator is a comma.

## [0.2.1] - 2026-09-28

### Fixed

- A worker clone of a repository on another mount (a container's repository mount and its work volume) failed with
  `checkout_invalid`: `git clone --local` cannot hard-link across mount points. The clone now copies when linking
  fails.

## [0.2.0] - 2026-09-28

### The server runs on a private network

`chargehand serve` can bind a private-network address: `http.listen` sets it and `http.allowed_hosts` names the host
clients use; without allowed hosts it refuses to start beyond loopback. A `Dockerfile` builds the server with the
Claude Code runtime, and a tag `v<Version>` publishes `ghcr.io/<owner>/chargehand:<Version>`. See ADR 0024.

### Prompt CI runs on its own

A pull request that changes `prompts/` or `presets/` is gated on a self-hosted runner, not by a manual run of
`scripts/prompt-ci.sh`. A fork's pull request or a preset change waits for an approval in the `prompt-ci-review`
environment. The runner holds one eval profile per runtime and a default; a `prompt-ci:<runtime>` label picks another
when someone with write access adds it. The manual run still works. See ADR 0025.

### Added

- `result/v1` has an optional `error` on failed results (ADR 0022): a fixed `code`, the `message`, `retryable`, and
  an `action` when there is something to do, such as the command that starts the OpenCode server. Clients branch on
  the code instead of parsing `summary`. The schema stays v1; `Chargehand.Contracts` is 1.2.0-alpha with the
  `ResultError` record and the `ErrorCode` enum.

### Changed

- Preset blocks `default` 0.5.0, `cheap`, `thorough` and `strict` 0.4.0 say "read the files that answer the task"
  instead of "read every file the task needs". Worker prompt 0.3.0 keeps the completeness rules. In the phase 3
  benchmark the old wording cost 28% more than a plain session through extra reading; the new one costs the same
  and stays more complete (docs/benchmarks.md).
- Prompt CI scores a worker item with a fact checklist (new optional `facts` and `wrong` in an eval item's
  `expected`) by the share of its reference facts the answer states, less 0.25 per known false statement it
  repeats, instead of by its claim count. A fact judge, one generate call on the intake model with its prompt in the
  trusted build, reads the answer and names the facts it states (ADR 0019).
- Worker prompt 0.3.0 asks for the whole task, one claim per item and a citation for every file relied on, and
  no longer caps the summary at 120 words. Preset blocks `default` 0.4.0, `cheap`, `thorough` and `strict` 0.3.0
  say "read every file the task needs" instead of "prefer the smallest set of files". The phase 3 blind verdict
  failed on completeness in 3 of 3 pairs at equal exploration: the worker read files it then left out, and merged
  several services into one claim.
- A worker interrupted at its token budget (`budget.max_input_tokens`) gets one turn to answer from what it has
  read, with room for three calls at the last call's context on top of what it spent; the result lists the stop in
  `open_questions`. It used to fail with nothing. The USD cap still ends a node without that turn (ADR 0010).
- A request's repository may sit anywhere under the profile's new `repository_roots` (default: `worker_root`; `/`
  allows any). The worker reads a clone of it at the pinned commit under `worker_root`, reused per source and commit,
  so the source's uncommitted and ignored files never reach it and the worker stays outside the home. A short commit
  hash is enough. A checkout that tracks a file the preset denies reading is still refused (ADR 0023).
- `chargehand run` prints a failed `result/v1` with `error` when it cannot connect to the runtime (server not
  running, binary missing, another version), instead of ending with an unhandled exception.
- The eval gate retries an arm whose result has code `rate_limited`, instead of matching "rate limit" in its summary.

### Prompt CI calibration

`cheap/worker`'s items pinned a checkout that tracks encrypted env files the `cheap` preset denies reading, so since
workers refuse such a checkout (0.1.0) every arm of its A/A failed at $0. The items now pin a commit of that checkout
without those files; every reference file is unchanged. The A/A there, the first since workers lost the shell, 12
items: quality -0.046 (t -0.63), cost +13% (t 1.82), pass, $0.11; 5 items differ, by 0.75, 0.27, 0.25, 0.20 and 0.02.
Claims per item: 6.25 against 5.97 in the runs that seeded `reference_claims`, 20 of 24 arms within their range, so
`reference_claims` stays.

## [0.1.0] - 2026-09-27

First usable release. chargehand turns a request into a Task Spec, runs it on one or more coding-agent sessions, and
returns `result/v1` with evidence behind every claim. You call it from the CLI, over HTTP, or as an MCP tool. It
covers roadmap phases 3 to 5 (v0, v1, v2). Benchmark and exit-check numbers live in
[docs/benchmarks.md](docs/benchmarks.md).

### Added

#### Runs and actions

- `chargehand run` reads `request/v1`, runs intake to get a Task Spec, and returns `result/v1`.
- Intake picks one action. `answer` runs one worker session. `split` runs 2–4 read-only subtasks as a task graph.
  `deny` returns status `denied` with a reason and an unblock condition. `ask` returns `needs_input` with questions.
  `improve` returns `needs_input` with an improved request and an inline diff artifact. When the preset does not allow
  the chosen action, the run falls back to `answer`; the run log records both.
- Split runs (ADR 0017) execute at most 2 nodes at once and pass upstream contracts to dependents; a failed node stops
  only its dependents. Later nodes fork the first node's session before its first message, so they read its system
  prefix from cache. Node contracts merge deterministically, with evidence ids prefixed by the node id.
- The worker node fixes its instruction entries before the first prompt. A watcher rejects permission requests and
  interrupts above the run cap. Each node gets a 15-minute deadline and one repair turn each for the result schema
  and for evidence.
- Per-node budgets: the watcher interrupts a node above `budget.max_input_tokens` and compacts it mid-turn above
  `compaction.trigger_tokens` (new optional `preset/v1` field).
- The evidence resolver checks claims against git at the pinned commit, session messages, caller inputs, URLs seen,
  and the session diff. Claims that do not resolve move to `open_questions`.
- `request/v1` gains optional `context.approved` to run past a preset's approval thresholds.

#### Presets

- `default`, plus `cheap` 0.1.0, `thorough` 0.1.0 and `strict` 0.1.0, all read-only. `strict` asks for approval above
  risk `low` or an estimate above $0.50.
- `draft` 0.1.0 (node kind `draft`) writes a draft for a program caller from the caller's own inputs, with no
  repository and no tools, and returns it as an inline artifact. New optional `preset/v1` field `checkout: false`.

#### Callable interface (ADR 0018)

- `chargehand serve` hosts HTTP and MCP on 127.0.0.1. Every route requires a bearer key from the secret store
  (profile `http`), and bodies cap at 1 MB.
- `POST /v1/runs` takes `request/v1`. If the run finishes within `Prefer: wait=N` (default 10 s, at most 60) you get
  `200` with `result/v1`, otherwise `202` with `run-status/v1`.
- `GET /v1/runs/{id}` answers `202` while running, `200` when finished, `410` when the owning process died, `404`
  when unknown. `GET /v1/runs/{id}/events` streams `accepted`, `started`, `intake`, `node_started`, `node_finished`
  and `run_finished`.
- Runs outlive the request and execute one at a time. The server holds at most 10 unfinished runs, then answers 429.
- MCP at `/v1/mcp` (Streamable HTTP, MCP 2026-07-28 with hybrid sessions, C# SDK 2.2.0) exposes tool `orchestrate`,
  with `inputSchema` `request/v1` and `outputSchema` `result/v1`. Long runs become tasks through the tasks extension.
  An `ask` becomes `input_required`, and the answers resend the request as a child run.
- The run log's new `start` record (request, trace id, owning pid, parent run) makes it the store the CLI and the
  server share. `show` prints runs that are still running or were lost. Concurrent appends take a lock file, and
  readers skip a half-written last line.
- `Chargehand.Contracts` 1.1.0-alpha adds schema `run-status/v1` and `RunStatus`; `PromptBlock.Create` hashes a caller
  block by request/v1's rule.
- `samples/ContentEngineCall` builds the content engine's call (ADR 0014) from `Chargehand.Contracts` alone.

#### Worker runtimes

- OpenCode V2 adapter pinned to 2.0.16: a hand-written client and `IWorkerRuntime` implementation, with a retry for
  the `Model unavailable` race while a location boots. `scripts/opencode-serve.sh` starts the orchestrator's own
  server; `profiles/opencode.example.json` configures it.
- Claude Code adapter (ADR 0020, `Chargehand.ClaudeCode`) runs `claude -p` with stream-json, one process per turn,
  `--bare` and `dontAsk`, and translates preset rules to `--tools`, `--allowedTools` and `--disallowedTools`. Profile
  `claude_code` selects it instead of `opencode`, which becomes optional. It takes `version`, `binary`, and exactly
  one of `api_key_secret` (per-token API billing) or `oauth_token_secret` (a `claude setup-token` subscription
  token). Optional `base_url` routes workers through an Anthropic-compatible gateway.

#### Prompts, evals and routing

- Prompt registry in `prompts/` with SemVer front matter and normalised sha256. Every call records its
  `prompt_chain`; `chargehand prompts sync` mirrors blocks into Langfuse prompt management.
- Prompt CI (ADR 0019): `chargehand eval seed|push|gate` with cells `cheap/worker`, `draft/draft` and `intake` in
  `evals/cells.json`, items in Langfuse datasets, and deterministic scores. It runs base and change in pairs and gates
  on quality tolerance T (0.10) and cost tolerance C (+15%, or +30% for `cheap/worker`), with a declared-trade
  override. Results land as Langfuse dataset runs and scores.
- `scripts/prompt-ci.sh <pr>` runs Prompt CI on the owner's machine and posts commit status `prompt-ci`.
  `.github/workflows/prompt-ci.yml` marks pull requests that change no prompt or preset.
- `chargehand routes` prints a routing report per preset, node kind and model (runs, score, tokens, cache rate, cost,
  latency) and only suggests changes. `chargehand score` records a hand score for a run.

#### Memory

- `IMemoryProvider` (recall, retain, invalidate; scope is backend plus namespace) with an adapter for a self-hosted
  Hindsight service (HTTP API 0.10.0). With profile `memory` set, a run recalls facts once and appends them to each
  node's prompt as unverified context (chain block `memory/recall`, source `runtime`). A failed recall leaves the run
  without them. `retain` stores completed runs and stays off by default.

#### Observability

- JSONL run log with tokens, cache rate, own-table cost and latency per call. `chargehand show` summarises a run;
  `chargehand reconcile` joins calls to exported gateway spend rows by model, token counts and time.
- `chargehand cache <run>` reports cache reads, writes and hit rate per call. Per node, it names the first instruction
  entry, prompt block or as-sent field that broke a prefix that should have been shared, plus a checklist for breakers
  outside the prompt chain. Call records carry the fork parent and hashed instruction entries.
- OTLP traces (run → intake → node → call) carry `chargehand.prompt_chain` and the OpenCode session id.
- `telemetry.usage_on_spans` (ADR 0021) puts Langfuse usage and cost on call spans that no gateway records, such as
  Claude Code on a subscription. Off by default; with a LiteLLM gateway, ADR 0012 still applies.

### Changed

- Intake prompt 0.2.0 says when to split. 0.3.0 asks only when a required fact is missing and the caller's inputs do
  not supply it; 0.2.0 asked a program caller for facts it had already sent (1 of 2 A/A runs).
- Presets `default` 0.4.0, `cheap` 0.2.0 and `thorough` 0.2.0 drop approval thresholds. Intake's estimate is
  uncalibrated (ADR 0005): in the phase 4 benchmark it guessed up to $0.35 for runs that cost about $0.01 and stopped
  one with `needs_input`. `strict` keeps its thresholds.
- Preset `default` 0.2.0 allows `rg` (with `--pre` denied). Worker prompt 0.2.0 makes the final message the JSON block
  only.
- Prompt CI scores a worker as grounding times completeness. Completeness is claims kept over the item's
  `reference_claims` (new optional field in an eval item's `expected`), capped at 1; `eval seed` proposes the count
  from the seeding run. Grounding alone passed #5, which cited the same code in fewer, wider claims.
- `cheap/worker` drops the phase 3 reference question (now 12 items). At `cheap`'s 400k-token node budget it failed
  in about half its runs, and one flip moved an A/A's mean by up to 0.08.
- The orchestrator computes an inline artifact's sha256 itself and caps its content at 64 KiB.

### Fixed

- A run that throws (bad checkout, unknown preset, no valid Task Spec) ends with a failed `result/v1` and a run record
  instead of an exception.
- Caller inputs appear in the task text as `- id "<id>" (<kind>): <text>`. The old `[id]` form led workers to cite
  `[id]`, which matched no input. A failed `input` reference now lists the ids that exist, and intake sees each
  input's id and kind.
- OpenCode's stateless generate (intake) retries one 503, as ADR 0011 prescribes for the masking proxy.
- Prompt CI retries an arm that hits a rate limit (after 15, 30 and 60 s) and stops the gate if the limit persists,
  instead of scoring the item 0.
- Prompt CI stops the gate (status `error`) when a worker or draft arm fails with zero usage, meaning a refusal before
  any model call. Such arms used to score 0 on both sides and pass as no change. Intake arms report no usage and stay
  exempt.

### Security

- `chargehand serve` requires its bearer key on loopback too, since any local process can reach the port. It accepts
  only a loopback Host header (against DNS rebinding) and sends no CORS headers.
- Prompt CI never builds or runs a pull request's code. The runner uses the trusted checkout's build, and the pull
  request contributes only `prompts/` and `presets/`. Those still steer a worker, and a preset can grant tools, so a
  fork's pull request or any preset change runs only after the owner reads the diff (`--reviewed`). A symbolic link
  among them stops the run. GitHub holds no model, gateway, tracing or tailnet key; evals use their own OpenCode
  server and a spend-capped gateway key limited to the small model.
- Presets `default` 0.5.0, `cheap` 0.3.0, `thorough` 0.3.0 and `strict` 0.2.0 remove the shell tool from workers, and
  their prompt blocks point to the `grep` and `glob` tools instead. OpenCode matches shell rules against each
  command's source text, so the `git grep*`, `git log*` and `git show*` allows let a worker execute programs
  (`git grep -O`) and write files (`--output`), and quoting or naming a file slipped past the `rg` denies.
- A run fails before any worker session starts when its checkout holds a file the preset denies reading (`*.env`,
  `*.env.*`), ignored files included. OpenCode's `grep` tool checks permission against its pattern rather than the
  path it searches, and it reads ignored files named by path or `include` glob, so `read` rules alone could not keep
  those files out.
- Preset `default` 0.3.0 denies reading `*.env` and `*.env.*`, and denies `rg --no-ignore` and `rg -u`. The session
  ruleset's leading allow had overridden OpenCode's own ask-before-reading-`.env` rule, so a worker could read secret
  files without asking.

## [0.1.0-alpha] - 2026-09-26 (not tagged)

### Added

- Repository bootstrap: license, contribution guide, security policy, CI with secret scanning,
  Conventional Commits check, ADR template.
- JSON Schemas `task-spec/v1`, `result/v1` and `preset/v1` with fixtures and schema tests.
- Architecture decision records 0000–0016.
- Generated OpenCode API map and a contract test against the checked-in spec.
- Solution skeleton: CLI entry point, intake, `IWorkerRuntime` port, OpenCode adapter interface,
  contract validator, evidence resolver, prompt registry, telemetry and run log interfaces.

[Unreleased]: https://github.com/Egoushka/chargehand/compare/v0.9.6...HEAD
[0.9.6]: https://github.com/Egoushka/chargehand/compare/v0.9.5...v0.9.6
[0.9.5]: https://github.com/Egoushka/chargehand/compare/v0.9.4...v0.9.5
[0.9.4]: https://github.com/Egoushka/chargehand/compare/v0.9.3...v0.9.4
[0.9.3]: https://github.com/Egoushka/chargehand/compare/v0.9.2...v0.9.3
[0.9.2]: https://github.com/Egoushka/chargehand/compare/v0.9.1...v0.9.2
[0.9.1]: https://github.com/Egoushka/chargehand/compare/v0.9.0...v0.9.1
[0.9.0]: https://github.com/Egoushka/chargehand/compare/v0.8.8...v0.9.0
[0.8.8]: https://github.com/Egoushka/chargehand/compare/v0.8.7...v0.8.8
[0.8.7]: https://github.com/Egoushka/chargehand/compare/v0.8.6...v0.8.7
[0.8.6]: https://github.com/Egoushka/chargehand/compare/v0.8.5...v0.8.6
[0.8.5]: https://github.com/Egoushka/chargehand/compare/v0.8.4...v0.8.5
[0.8.4]: https://github.com/Egoushka/chargehand/compare/v0.8.3...v0.8.4
[0.8.3]: https://github.com/Egoushka/chargehand/compare/v0.8.2...v0.8.3
[0.8.2]: https://github.com/Egoushka/chargehand/compare/v0.8.1...v0.8.2
[0.8.1]: https://github.com/Egoushka/chargehand/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/Egoushka/chargehand/compare/v0.6.1...v0.8.0
[0.6.1]: https://github.com/Egoushka/chargehand/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/Egoushka/chargehand/compare/v0.4.1...v0.6.0
[0.4.1]: https://github.com/Egoushka/chargehand/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/Egoushka/chargehand/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Egoushka/chargehand/compare/v0.2.2...v0.3.0
[0.2.2]: https://github.com/Egoushka/chargehand/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/Egoushka/chargehand/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/Egoushka/chargehand/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Egoushka/chargehand/releases/tag/v0.1.0
