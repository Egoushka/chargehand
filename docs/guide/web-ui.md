---
title: "Web UI"
description: "A page at /ui/ that lists runs, streams a run's events, shows results with linked citations, starts and cancels runs and shows spend against caps: how to turn it on, what each screen does, and the security trade-off of keeping the key in the tab."
order: 5
section: "Guides"
---

The server can serve a small web page at `/ui/` ([ADR 0054](../adr/0054-web-ui.md)). It is for watching and for the few actions you would otherwise do with `curl`. It calls the routes in [the server guide](server.md) and nothing else, so what an account may see and do there is what it may do over HTTP.

## Turn it on

It is off unless the profile asks:

```json
{ "http": { "port": 4300, "api_key_secret": "chargehand-api-key", "ui": true } }
```

Restart `chargehand serve`; its start line then ends `UI /ui/`. Open `http://127.0.0.1:4300/ui/` (the same `allowed_hosts` rules apply as for the API). The page's files are public and load without a key; every call the page makes carries one.

## Sign in

Paste an API key: the owner's `http.api_key_secret` or an account's `chk1.<id>.<secret>` ([accounts](server.md#accounts-and-named-keys)). The page checks it with `GET /v1/accounts/me`. A wrong key shows an error and nothing is stored. **Sign out** removes the key. There is no server-side session.

For everyday watching, sign in with an account key. The owner's key can also halt the server.

### Where the key is kept, and what that costs

The key is kept in `sessionStorage`: it lives in that tab, is gone when the tab closes, and is never sent anywhere but the server that served the page, as `Authorization: Bearer ...`. It is not a cookie, so nothing sends it by itself and there is nothing for a forged request to ride on. It is not `localStorage`, so it does not outlive the tab or reach other tabs.

The trade-off is that a script running in the page could read it. The page is built so none can: every response under `/ui/` carries `Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`, which allows no inline script, no `eval` and no other origin; results, claims, branch names and diffs reach the page only as text (the code never builds markup from a string); a link appears only for an `http` or `https` URL; and there is no third-party code and no build step. Over plain HTTP on a private network the key crosses the network in clear, as it does for any API caller ([ADR 0024](../adr/0024-server-on-a-private-network-and-release-images.md)): put TLS in front when that matters.

## Screens

**Runs** (`#/`) lists runs, newest first, 100 at most, refreshed every 5 seconds (untick Auto-refresh to stop it). Columns: start time, run id, state, kind (`single`, or `batch` for a request with a `driven` block), preset, repository, the account (owner only), cost, tokens, branch and pull request link. Cost is the result's priced `usd`; a subscription run has none and shows a dash. Filters: state, kind, repository (exact path) and, for the owner, actor. A batch's research and review calls are runs of their own; they are hidden unless you tick "Show session calls". Above the list is **Spend**.

**Spend** shows the account's spend in the UTC day and month against its caps (`max_usd_per_day`, `max_usd_per_month`, `max_tokens_per_day`, `max_tokens_per_month`), when each window resets, and its unfinished runs against `max_concurrent_runs`. A cap that is not set says "no cap". The figures come from `GET /v1/accounts/me`, the same spend a new request is checked against, so a refusal with `budget_exceeded` matches what the page shows. The owner has no caps; its spend is that of runs recorded as `owner`.

**Run** (`#/runs/<id>`) shows the run's state and, while it is unfinished, the **live events**: `accepted`, `started`, `intake`, `node_started`, `node_finished`, and for a batch `container_started`, `session_progress` (stage, turns, tokens and `usd_estimate`, the session's dollar equivalent so far), `pushed`, `pr_opened`, `task_finished`, `governor`, `integrated` and the rest. A batch also gets a live table of its tasks. The page reads the stream with `fetch`, because the browser's own server-sent-events client cannot send a bearer header. When the run finishes, the result replaces the stream. A run that another process holds, or a server that restarted, is polled every few seconds instead.

The result shows the summary, the error and what to do about it, confidence, usage, and:

- **Claims** with their support verdict and confidence. Each citation is a link to `path:line` at the commit when the repository has a web address: taken from a pull request link in the result (`github.com/.../pull/N`, GitLab `.../-/merge_requests/N`), or typed once into the box the page offers and kept in this browser's `localStorage` (it is not a secret). A `file` citation uses the evidence's `commit`, else the run's commit; a `url` citation links to the URL; the rest are shown as text. With no web address, citations are text.
- **Tasks** for a batch: state, tokens, cost, branch, pull request link, error code and detail; each task id links to the task's own run.
- **Artifacts**, each collapsed: `changes` (changed paths with their line counts and the diff), `verification`, `plan`, `pull-request`, `branch`, `review`, `session-log` and `governor-log` (references, with their hash), and any other kind as inline text.

**Cancel run** asks, then calls `POST /v1/runs/{id}/cancel`; the run ends `failed` with `cancelled`. It answers `409` for a run another process holds.

**Start a run** (`#/new`) sends `POST /v1/runs` with `Prefer: wait=0` and goes to the new run's page. Fields: goal (`text`), preset (the presets the server has), repository path and commit (`context.repository`; most presets need both), and for accounts allowed `driven`, "Run as a batch" with a list of tasks (id, goal, size, `depends_on`), which sets preset `driven`. A refusal (`400` bad request, `403` driven or repository not allowed, `429` cap or concurrency) is shown with the server's message.

**Learning** (`#/learn`) shows `GET /v1/learn`: per repository and size class, finished tasks, completion and partial rates, the median and 80th-percentile spend, the share that hit a checkpoint, pull requests and the share merged unchanged ([the learning loop](driven.md#the-learning-loop)). Choose 7, 30 or 90 days or all time. An account sees only its own batches.

**Halt server** and **Resume server** appear for the owner in the header and call `POST /v1/halt` and `/v1/resume`; a halted server shows a banner to everyone.

## What the server adds for it

- `GET /v1/accounts/me`: the caller's `name`, `owner`, `driven`, `repositories`, `limits`, `spend.day` and `spend.month` (`usd`, `tokens`, `resets_at`), `unfinished_runs`, `halted` and `presets`. A run token gets `403`, as on every route it does not open.
- `GET /v1/runs` accepts `kind=single|batch`, `repository=<exact path>`, `run_id=` and, for the owner, `actor=`; an account always gets its own runs.
- `run-summary/v1` gains optional `kind`, `repository`, `commit` and `tokens` (additive).

## Not tested in CI

The tests cover what a .NET test can see: the files are served only when `http.ui` is on, without a key, with the policy and the other headers; every other route still needs a key; the embedded files are the folder's; no file names another origin, builds markup from a string, carries inline script or style, or calls `fetch` outside `api.js`; `/v1/accounts/me` and the new list filters. The JavaScript itself was exercised by hand in a browser against a server with seeded runs; nothing runs it automatically.
