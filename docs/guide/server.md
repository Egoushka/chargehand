---
title: "Run the HTTP server"
description: "chargehand serve: the profile's http block, the bearer key and named accounts with caps, binding and allowed hosts, each route with its status codes, and the release image."
order: 4
section: "Guides"
---

`chargehand serve` hosts the HTTP interface and the MCP endpoint in one process. Runs started through either land in the same run log as CLI runs.

## Start it

`serve` needs an `http` block in the profile; without one it exits with `profile has no http settings (ADR 0018)`. The block from `profiles/example.json`:

```json
{
  "schema": "profile/v1",
  "http": { "port": 4300, "api_key_secret": "chargehand-api-key" }
}
```

Keep your `models` map in the same profile ([Quickstart](quickstart.md#write-a-profile)). Then:

```bash
export CHARGEHAND_API_KEY=<key>
dotnet run --project src/Chargehand.Cli -- serve
```

The server prints its address on stderr. It connects to the worker runtime once at startup, so a runtime that is down stops it; a runtime that goes down later fails each run with `runtime_unavailable` ([ADR 0022](../adr/0022-error-codes-in-result-v1.md)).

## The bearer key

Every route, `/v1/mcp` included, requires `Authorization: Bearer <key>`. That holds on loopback too, because any local process can reach the port. A missing or wrong key gets `401`.

The key is the secret item that `http.api_key_secret` names, resolved through the profile's `secrets` sources in order. With no `secrets` list, chargehand reads the item from the environment, upper-cased with `-` turned into `_`: `chargehand-api-key` reads `CHARGEHAND_API_KEY`. A `command` source runs an argv template instead, such as the macOS Keychain lookup in `profiles/example.json`. The `secrets` list replaces `secret_store` ([changelog 0.4.0](../../CHANGELOG.md#040---2026-09-29)).

## Accounts and named keys

The key above is the **owner**: every route, every run, no caps. To give someone else a key of their own, declare an account in the profile and make it a key ([ADR 0050](../adr/0050-accounts-and-named-api-keys.md)). This is for one self-hosted server and a handful of callers, not a hosted service.

```json
{
  "http": {
    "port": 4300,
    "api_key_secret": "chargehand-api-key",
    "accounts": [
      { "name": "alice", "max_usd_per_day": 5, "max_usd_per_month": 60, "max_tokens_per_day": 2000000, "max_concurrent_runs": 2 },
      { "name": "ci", "max_concurrent_runs": 1, "driven": true, "repositories": ["/srv/chargehand/repos/app"] }
    ]
  }
}
```

```bash
chargehand accounts add alice --label laptop    # prints the key once: chk1.<id>.<secret>
chargehand accounts list                        # accounts, caps, key ids and states; never a key
chargehand accounts revoke <key-id>             # stops working at once, no restart
```

The caller sends the key as `Authorization: Bearer chk1...` on every route, `/v1/mcp` included. chargehand stores a salted SHA-256 of the secret half in `accounts.json` beside the run log (readable by its owner only), so a lost key cannot be read back: revoke it and add another. A key whose account is gone from the profile, an unknown key and a revoked key all get `401`. Run `accounts` where the server's run log is (in the container, its `/app/runs` volume, which must persist).

| field | meaning |
|---|---|
| `name` | lowercase letters, digits, hyphens; `owner` is reserved. Recorded as `actor` |
| `max_usd_per_day`, `max_usd_per_month` | dollars the account's runs may cost in the UTC day or calendar month, by their results' `usage`. A subscription run has no price and adds none: pair it with a token cap |
| `max_tokens_per_day`, `max_tokens_per_month` | input plus output tokens in the same windows |
| `max_concurrent_runs` | unfinished runs (queued or running) at once |
| `driven` | may start a batch; default `false`, because a batch pushes branches and opens pull requests with the server's credential |
| `repositories` | absolute directories; `context.repository.path` must be one of them or below it, and every request must name a repository. Absent: any the server allows |
| `model` | the account's own model credential, see [below](#an-accounts-own-model-key): `api_key_secret` and optional `base_url` |
| `allow_server_credential` | an account with no `model` may spend the server's model credential; default `false`: its runs are refused with `model_credential_missing` |

The single `api_key_secret` keeps working unchanged as the `owner` account, with no migration.

**What an account sees.** Every run records `actor`, the account's name, on its start and run records, its `result/v1`, its events and its `run-summary/v1` (the owner's runs say `owner`, as do batches started by GitHub intake; a CLI run has none, and a record from before accounts is the owner's). An account lists, reads, streams and cancels only its own runs; another's run is `404`. `POST /v1/halt` and `/v1/resume` are the owner's (`403` otherwise). The owner sees every run. A driven session's run token carries the account of its batch, so its research and review calls belong to that account and it can read only that account's runs. A batch may `continue_from` only a run of its own account.

**Caps** are checked when a run is requested, against the account's spend in the window: finished runs by their result's usage, a run still going by its calls so far, a batch once. When a cap is reached the request is refused with `429` and a `Retry-After` for the end of the window:

```json
{ "errors": ["the daily dollar cap of $5.00 is used up ($5.20 spent, 0 remaining); it resets at 2026-10-16T00:00:00Z"],
  "error": { "code": "budget_exceeded", "window": "day", "unit": "usd", "limit": 5, "spent": 5.2, "remaining": 0, "resets_at": "2026-10-16T00:00:00+00:00" } }
```

A request that is admitted has its `budget_usd` (for a batch also `max_usd_total` and `max_tokens_total`) cut to what the tightest window has left. `concurrency_exceeded` is `429`; `driven_not_allowed`, `repository_not_allowed` and `continue_not_allowed` are `403`, in the same body. Over MCP the tool error starts with the code. A run already underway can overshoot a cap by its own budget.

### An account's own model key

By default an account would spend the server's model credential, one bill and one rate limit for everyone ([ADR 0053](../adr/0053-accounts-bring-their-own-model-key.md)). Give it its own:

```json
{ "name": "alice", "model": { "api_key_secret": "alice-anthropic-key" } }
{ "name": "ci", "driven": true, "model": { "api_key_secret": "ci-gateway-key", "base_url": "https://gateway.example.com/anthropic" } }
{ "name": "bob", "allow_server_credential": true }
```

`api_key_secret` is a secret-store item, resolved like the server's other secrets when the account's first run needs it (a change needs a restart; `chargehand doctor` checks it resolves). An API key alone goes to Anthropic; with `base_url` it is the key an Anthropic-compatible gateway issued, and goes there. Its read-only runs (intake, workers, the support check) and its batches use that key and no other: each account has a runtime of its own, so a run never sees another account's key. Spend on it is priced as API usage: a batch needs a dollar cap and the account's dollar caps bind.

An account with neither `model` nor `allow_server_credential: true` is refused at admission: `403`, `error.code` `model_credential_missing` (over MCP the tool error starts with it). The owner, the CLI and stdio always use the server's credential. **A subscription token is the owner's only**: `oauth_token_secret` is rejected in `model`, because a subscription is one person's plan, has no dollar price for a cap to bind, and lending it out spends the owner's limits. `model` needs the `claude_code` runtime; the server will not start with it on OpenCode, which keeps its provider credentials in its own server.

In a batch the key reaches the session container as `ANTHROPIC_API_KEY` (`credential_delivery: environment`), or, with `base_url`, as a gateway key (`gateway_key`) through a forward on the batch's egress container; the server's own `driven.network.model_url` is never used for an account's key. An API key is not exchanged for a per-task token (see [driven](driven.md#the-model-credential)): use a gateway key for an account whose repositories you do not trust.

**Not isolated.** Accounts share the worker root and the checkouts, the server's model credential and rate limit (those without a `model` of their own), the memory providers (a fact one account's run retained can be recalled by another), and for batches the runner, the container engine, the push credential and the package caches. Separate runners and networks per account are CHARGEHAND-202.

## Binding and allowed hosts

| field | default | meaning |
|---|---|---|
| `http.port` | `4300` | TCP port |
| `http.listen` | `127.0.0.1` | IP address to bind |
| `http.allowed_hosts` | none | host names accepted besides `localhost` and `127.0.0.1` |
| `http.api_key_secret` | required | the secret item that holds the bearer key (the `owner`) |
| `http.accounts` | none | named accounts with their own keys and caps, see above |
| `http.ui` | `false` | serve the [web UI](web-ui.md) at `/ui/` and the [operator console](web-ui.md#the-operator-console-console) at `/console/` |

The server compares each request's `Host` header with `localhost`, `127.0.0.1` and `allowed_hosts`, and answers any other host with `400`. That check guards against DNS rebinding. When `listen` is not loopback, the server refuses to start unless `allowed_hosts` names at least one host, because beyond loopback the check must name the server. Bind a private network only ([ADR 0024](../adr/0024-server-on-a-private-network-and-release-images.md)). The server sends no CORS headers and caps request bodies at 1 MB.

With no `repository_roots`, `serve` allows repositories under `worker_root` only, and `context.repository.path` is a path on the server's machine ([ADR 0028](../adr/0028-default-repository-roots.md)).

## Routes

| route | answers |
|---|---|
| `POST /v1/runs` | `200` with `result/v1` when the run finishes within the wait; `202` with `run-status/v1` when it does not; `400` with `errors` for a bad request; `429` when 10 runs are unfinished |
| `GET /v1/accounts/me` | `200` with the caller's account: `name`, `owner`, `driven`, `repositories`, `limits`, `spend.day` and `spend.month` (`usd`, `tokens`, `resets_at`, the figures its caps are checked against), `unfinished_runs`, `halted` and `presets`; for the [web UI](web-ui.md) |
| `GET /v1/runs?status=&since=&limit=` | `200` with `{"runs": [run-summary/v1, ...]}`, newest first: state, preset, cost, branch and pull request link when the result has them, and the error code of a failed run. `status` is a run state (`queued`, `running`, `lost`, `completed`, `needs_input`, `failed`, `denied`), `since` an ISO 8601 time, `limit` 1 to 500 (default 50); `400` for a bad value. Also `kind=single|batch`, `repository=<exact path>`, `run_id=` and, for the owner only, `actor=`; each row also says `kind`, `repository`, `commit` and `tokens` |
| `GET /v1/learn?since=` | `200` with what finished driven tasks taught, the table of `chargehand learn report` as JSON: `{"since", "tasks", "groups": [{"repository", "size_class", "tasks", "spend_median_usd", "spend_p80_usd", "completion_rate", "partial_rate", "checkpoint_rate", "checkpointed_completed_rate", "prs", "merged_unchanged_rate", ...}]}`; `since` is `30d` or an ISO 8601 time, `400` for another value; an empty report when driven sessions are off. Needs the API key; a run token is refused, and an account sees only the tasks of its own batches ([the learning loop](driven.md#the-learning-loop)) |
| `POST /v1/runs/{id}/cancel` | `202` when this server holds the run and starts cancelling it; the run ends `failed` with error code `cancelled`. `404` for an unknown id, `409` when the run has finished or another process runs it |
| `POST /v1/halt` | `200` with the number of runs it cancelled; from then on `POST /v1/runs` and the MCP tool are refused with `503` until `POST /v1/resume` |
| `POST /v1/resume` | `200`; accepts runs again |
| `GET /v1/runs/{id}` | `202` with `run-status/v1` while queued or running; `200` with `result/v1` once finished; `410` when the process that ran it ended first; `404` for an unknown id |
| `GET /v1/runs/{id}/events` | server-sent events for the run; `404` for an unknown id |
| `GET /v1/overview`, `/v1/inbox`, `/v1/batches/{id}`, `/v1/batches/{id}/tasks/{task}`, `/v1/pulls`, `/v1/analytics`, `/v1/repositories` | the [console's read routes](#console-routes): for the owner and for an account, each limited to the account's own runs and spend |
| `GET /v1/intake`, `/v1/accounts`, `GET /v1/system` | the console's read routes about the server itself: the owner's alone (`403` for an account) |
| `POST /v1/inbox/dismiss`, `POST /v1/inbox/restore` | [dismiss inbox items](#dismissing-inbox-items) or undo it, for the owner and for an account on its own items |
| `/v1/mcp` | MCP over Streamable HTTP, see [the MCP page](mcp.md) |
| `/ui/`, `/console/` | the [web UI](web-ui.md) and the [operator console](web-ui.md#the-operator-console-console): static files, served only when `http.ui` is `true`, without a key (the page calls the routes above with one) |

Every route answers `401` without a valid key (the static files of the [web UI](web-ui.md) under `/ui/` and the console under `/console/`, when `http.ui` is on, are the one exception) and `400` for a host it does not accept. With accounts, the run routes answer for the caller's own runs (see [accounts](#accounts-and-named-keys)). [ServerTests](../../tests/Chargehand.Tests/ServerTests.cs) covers `200`, `202`, `400`, `401`, `404`, `410` and `429` on these routes.

### POST /v1/runs

The body is `request/v1`. Before a run starts, the server checks that the body is JSON, that it matches the schema, that the preset exists, and that each caller block's sha256 matches its text. A failure is `400` with `{"errors": [...]}`, and no run starts. Otherwise the run starts and the response waits up to `Prefer: wait=N` seconds (default 10, at most 60). Both `200` and `202` carry `Location: /v1/runs/{id}`.

```bash
curl -s -X POST http://127.0.0.1:4300/v1/runs \
  -H "Authorization: Bearer $CHARGEHAND_API_KEY" \
  -H "Prefer: wait=30" \
  --data-binary @request.json
```

`samples/ContentEngineCall` is a complete caller in C#, built on `Chargehand.Contracts` alone.

### GET /v1/runs/{id}

A `202` body is `run-status/v1` with status `queued` or `running`; a run that another process is running also reads `running`. A `410` body has status `lost`: the process that ran it ended before it finished. A finished run comes back as `result/v1`.

### GET /v1/runs/{id}/events

A `text/event-stream` with the events `accepted`, `started`, `intake`, `node_started`, `node_finished` and `run_finished`. Each event's data is `run-status/v1`. The `intake` event carries the action intake chose, the action that runs and the number of nodes; `node_finished` carries the node's id, status and cost; `run_finished` carries the result. For a run this process does not hold (finished, lost, or running in another process), the stream has one event, read from the run log.

## Console routes

An operator console needs to read what the server and the driven runner already know. These routes show it and change nothing: they are all `GET` (any other verb is `405`), they read the run log, `learning.jsonl`, the saved state of unfinished batches under `batches/`, `intake/github.json`, `inbox/dismissed.json` and the [batch records](#batch-records) the server keeps for them. The owner's key sees everything. An account's key sees its own runs, batches, tasks, pull requests and spend and nothing system-wide that would show another account's work; `intake`, `accounts` and `system` describe the server and are the owner's alone. A run token reaches none of them (`403`). A body never holds a key, a token or a secret value: the profile is shown as settings, with secret items as set or not set, and a URL as its scheme, host and port. Each body carries a `contract_version` (`<name>/v1`) and takes additive changes only; there is no JSON Schema file for these yet. Each also carries `not_recorded`, the things the console might expect and the server does not keep, as plain sentences.

| route | contract | who | query | answers |
|---|---|---|---|---|
| `GET /v1/overview` | `overview/v1` | owner, account | | `server` (version, `halted`, runs held and queued or running, `max_unfinished`); `driven` (`enabled`; for the owner `runner.reachable`, `host` with `available_mb`, `total_mb`, `load1`, `cpus`, `free_after_reserve_mb`, `reserve_mb`; `sessions.running` against `max_parallel_total`; `rate_limit.paused` and `resets_at`); `standing` (the caller's caps and spend, as `GET /v1/accounts/me`); the owner's `accounts`; `inbox` counts. An account's body holds its own runs, sessions, spend and inbox: no host, runner or other account |
| `GET /v1/inbox` | `inbox/v1` | owner, account | `since` (`14d` or ISO 8601, default 14 days), `limit` (1 to 500, default 100) | `counts` and `items`, newest first, of three kinds. `question`: a task that ended `needs_input` and no batch has continued; it carries the `questions` and `answer`, the request to send (`POST /v1/runs`, a driven task with `continue_from` and `answers`, [ADR 0045](../adr/0045-governed-sessions.md)) with the batch's `repository`. `pull_request`: open and waiting for a person, with `reason` `policy_ask` (the repository's integrator policy is `ask`), `test_integrity` or `partial_draft`. `failure`: a failed task, a run that failed or was denied, a run whose process ended (`lost`); a cancelled run and a batch that failed only because tasks did not finish are left out. Identical failures (subject, code, repository, text) are one item, the newest, with `count`. An item a rule resolved or a person dismissed is not listed ([ADR 0064](../adr/0064-inbox-items-resolve-on-their-own-or-are-dismissed.md)): `counts` is what is still open, and `resolved` counts the rest in the window: `superseded` (a later task of the same account asked the same, or a later run of its own with the same text completed), `infrastructure` (a failure of chargehand's own, such as `checkout_invalid`, `internal` or `lost`, and a later run on the repository finished ok), `continued` (a question a batch answered or retried), `dismissed` and `duplicates` |
| `GET /v1/batches/{id}` | `batch/v1` | owner, account (own batch; `404` otherwise) | `events` (0 to 2000, default 500) | `run` (`run-summary/v1`), `repository`, `limits`, `credential_delivery`, `result`, the `plan` and `retro` artifacts, `tasks`, `timeline`, `pull_requests`, `artifacts` and `rate_limits`. A task has `state` (`pending`, `running`, `completed`, `failed`, `needs_input`, `not_started`, `cancelled`), `depends_on`, `size` and `size_from`, `budget` with its `source` (`task`, `flat`, `history` or `none`), `spend` (priced `usd`, `usd_estimate`, input, output, cache read and cache write tokens, turns, continuations), `governor` (mode, stop reason, decisions as counts per signal and rung, and `log`: every decision with its turn, rule, values, spend and budget, and `log_total`), `test_integrity_flagged`, `sessions` (every session, with `role` `first`, `continuation` or `ci_fix`, `runner`, `started`, `ended`, `status`, `reason`, turns, tokens, `usd_estimate`, `usd`, `exit_code`, `commits`), `live` (stage, turns, tokens of a running session), `questions` with `answered_by` and `answer`, `error`, its `pull_request`, and `ci` (the latest look at the pull request's checks, how many looks differed, and every CI fix attempt). `timeline.source` says where the events came from: `records` (kept on disk) or `memory` |
| `GET /v1/batches/{id}/tasks/{task}` | `batch-task/v1` | owner, account | `events` | the task as above with its whole `goal`, the result's `summary` and `result_error`, and the artifacts of its result as they are: `verification` (chargehand's own run and its `test_integrity`), `session_tests` (what the session said it ran), `changes` (files with line counts and the diff), `review` and `kept_work`; `timeline` holds this task's events, and `ci.history` the looks at the checks |
| `GET /v1/batches/{id}/tasks/{task}/sessions/{session}/transcript` | `transcript/v1` | owner, account (own batch; `404` otherwise) | `bytes` (1024 to 262144, default 65536) | the end of one session's transcript, for a session the task view lists: `lines` (whole lines, oldest first, each one `stream-json` event with secrets redacted, or a `chargehand_cut` stub for one over 32 KiB), `truncated` (earlier lines exist), `bytes`, `redacted` (the kinds of secret replaced, never a value), `cut`, and `available` with a `reason` when there is none (`session_gone`, `runner_unreachable`, `runner_too_old`, `runner_error`, `runner_unknown`, `driven_off`). Not stored, `Cache-Control: no-store`; see [the transcript](#the-transcript) |
| `GET /v1/pulls` | `pulls/v1` | owner, account | `repository` (`owner/name`), `state` (`open`, `merged_unchanged`, `merged_edited`, `merged`, `closed`), `awaiting=true`, `since`, `limit` | the pull requests chargehand opened, newest first: `state`, the integrator's `integrator_mode` (`off`, `ask`, `merge`), `awaiting_person` and its reason, `partial`, `test_integrity_flagged`, the batch and task |
| `GET /v1/analytics` | `analytics/v1` | owner, account | `since` (default 30 days), `bucket` (`day`, `week` from Monday, `month`; UTC), `group_by` (`none`, `repository`, `account`, `size`), `account` (owner only) | `tasks`: points per bucket and group with pass rate, spend, tokens with cache, turns, continuations and pull request outcomes; `total`; `governor` (decisions, and per mode the false-stop proxy of [ADR 0045](../adr/0045-governed-sessions.md): checkpointed tasks that still completed); `continuations`. `runs`: finished top-level runs per bucket with priced cost and tokens |
| `GET /v1/repositories` | `repositories/v1` | owner, account (the checkouts its `repositories` allow) | | the checkouts under `repository_roots`: `head`, `default_branch`, the `remote` without any credential, detected `ecosystems`, the proposed `verify` command, `integrator_mode`, whether intake watches it, and the open pull requests chargehand knows of (an account's own) |
| `GET /v1/intake` | `intake/v1` | owner | | the `automation.github` settings (`watched`, `allowed_authors`, `caps`), `starts` in the last 24 hours against the cap and when the next slot frees, running and asked `tasks` with their review `round`, `pending_reviews` (author, file, line; never the text), counts of `handled` triggers, `notices`: the reasons intake gave for not acting (`wait`, `cap`, `active`, `author`, `gone`, `notask`, `busy`, `rounds`), and `triggers`: the latest 100 of what it did with a trigger, newest first, with the time, the outcome (`started`, `continued`, `round_started`, `queued`, `ignored`), for `ignored` the reason (`author_not_allowed`, `active`, `author`, `cap`, `gone`, `not_chargehand_branch`, `review_rounds_off`) and the login that set the label or wrote the comment (never the comment's text) |
| `GET /v1/accounts` | `accounts/v1` | owner | | the owner and each declared account: caps, spend in the UTC day and month, unfinished runs, `credential` (`mode` `own_key`, `own_gateway`, `server` or `none`; `present` says the secret item resolves, never its value) and `keys` (id, label, created, revoked and `last_used`: the time of the key's last request, written at most once a minute; never the key, its salt or its hash) |
| `GET /v1/system` | `system/v1` | owner | | `version`, the redacted `profile` (its runners as name, scheme, host and port, platform and limits; never a key), `presets` (name, version, actions), the `runner` (`reachable`, the host snapshot, and the live `containers` with their kind and the batch and task the saved state ties them to, `networks`, `volumes`; the first runner only, as before), `runners` (one entry per listed runner: `name`, `host` as scheme, host and port, `reachable`, `error`, `elapsed_ms`, `draining` and `draining_reason`, `running` sessions against `max_sessions`, `container_count`; from the scheduler's last probe, reused when under 5 s old and not repeated for a runner in back-off; `running` is null with one runner) and the pinned `images` |

A task view is built from the batch's run in the run log, its task runs, the learning record of each task, the batch records and, while the batch is unfinished, the saved state under `batches/`. `timeline` comes from the batch's `events.jsonl` when it has one, so a finished batch's events outlive a restart; a batch without one falls back to the server's memory (all the events of a run this process holds, and the last 2000 of each of the last 25 finished runs after), and `timeline.available` is `false` for a batch with neither.

### The transcript

[ADR 0059](../adr/0059-session-transcript-for-the-console.md) decides it. The session's stream (`stream.jsonl`: Claude Code's `stream-json`, one event per line, its own credentials already replaced) stays in the session's output volume. The runner reads the end of it with `tail -c` and serves nothing else of it: `GET /out/<run>/stream.jsonl?tail=<bytes>`, 1 to 262144 bytes, refused without `tail`, and `tail` is refused for every other file. The server asks for 64 KiB (up to 256 KiB), drops the partial first line, stubs a line over 32 KiB and redacts what [`DiffScan`](driven.md)'s patterns find (AWS, GitHub, Anthropic and Slack tokens, private keys from header to footer) and the credentials it holds (the model credential of the batch's account or the server's, the push and forge tokens, the runners' keys, the owner's API key), also in their JSON-escaped form. Only the owner and the batch's account read it; another account gets `404`, a run token `403`. It is read on each request and kept nowhere: not in the records, the run log, an event, a log line or an artifact. It lasts as long as the output volume, which is the last session of a task, and an earlier session until a later one has its bundle. A secret that no pattern and no held credential covers (a password the model read from a repository file) is not redacted, so the console must show the lines as text and never as markup.

### Batch records

A driven batch leaves a directory `records/<run id>/` beside the run log (the server writes it; nothing else does). `events.jsonl` has the batch's events as `run-status/v1`, without the result of the last one, details cut at 1500 characters, a task's `session_progress` kept at most once in 30 seconds and the file held to 5000 events (a task's end and the batch's end are still written past that). `tasks.jsonl` has one tagged line per session (when it began and ended, its runner, role, why it ended, tokens and cost; a session cut short by a cancel or a start that failed is there too, as `cancelled` or `error`), per session's governor log (read from `governor.jsonl` in the session's output volume, the first 200 decisions, with their count; a runner too old to serve that file leaves the outcome file's first 50), per look at the pull request's required and optional checks while the integrator watches them (written when the look differs from the one before, 60 per task), and per CI fix attempt. Nothing in them is a secret or a prompt: events carry what the live stream carries, and the rest is names, counts, times, money and the governor's own log, whose values the session cuts at 200 characters and redacts. Directories are deleted oldest first past 500 batches or when idle for 180 days, never the directory of a batch that is running. A batch from before the records has none of this, and a route says so in `not_recorded`.

**What the server does not record**, so the console cannot show it today (each route also lists these in `not_recorded`):

- Anything about a batch that ran before the records existed: its events (unless this process still holds them), its earlier sessions, its governor decisions in full and its checks. The counts in `learning.jsonl` and the last session's run record remain.
- Events past the cap (5000 per batch) and the in-between `session_progress` of a task; a batch whose records were pruned.
- Who pressed an action beyond the account name in the `operator` event, and an action's result beyond the events the batch writes next (a cancelled task's `task_finished`, a retry's own batch).
- A transcript beyond its last 256 KiB, one whose output volume was removed, and any secret that is not recognisable as one.
- A pull request's checks outside the integrator's watch: it takes a look about every 30 seconds until the pull request merges, closes, is given up on or the wait ends, and keeps a look only when it differs from the one before. `pulls` shows the last outcome read back from the forge, about hourly for 14 days.
- Intake triggers before the log existed, review comments that were not requests, and the text of any comment.
- A key's use other than the time of its last request.

**Actions** the console takes are the existing ones, `POST /v1/runs` (an answer is a driven task with `continue_from`), `POST /v1/runs/{id}/cancel`, `POST /v1/halt` and `POST /v1/resume`, four on one task or pull request, and dismissing inbox items (below).

### Actions on pull requests and tasks

For the owner and the account that started the batch; another account gets `404`, a run token `403`, a `GET` `405`. Each writes an `operator` event to the batch (into the live stream and `events.jsonl`), is safe to send twice and answers `409` with a reason when the state forbids it.

| route | does | answers |
|---|---|---|
| `POST /v1/batches/{id}/tasks/{task}/cancel` | cancels one task of a batch this server runs, as a batch cancel does: a running session is interrupted, then killed, and nothing is pushed; a task that has not started never starts, and what depends on it is `not_started`. The outcome is final in the saved state, so a restart keeps it | `202` `{state: "cancelling", changed}`; `200` `changed: false` for a task already cancelled; `409` for a task that has ended, or a batch this server does not run |
| `POST /v1/batches/{id}/tasks/{task}/session/stop` | kills the task's running session container. The session leaves no outcome, so the task ends as after a session that failed; nothing is snapshotted | `202`; `409` when the task has no session running |
| `POST /v1/batches/{id}/tasks/{task}/retry` | starts a **new batch** of that one task, owned by the caller, with the batch's limits and the task as it was asked (its `depends_on` dropped, because what it depended on is in the old batch). If the failed task kept work (a `kept-work` artifact) the new task continues it with `continue_from` (ADR 0045). The batch it came from stays as it was: this is not a second attempt inside it | `202` `{run_id, continue_from}` with `Location`; a second retry of the same task returns the first with `200` and `changed: false`; `409` for a batch still running, or a task that is not failed, cancelled or unstarted (one waiting for an answer is answered instead); `503` when halted; the account's caps apply as for `POST /v1/runs` |
| `POST /v1/pulls/{owner}/{repo}/{number}/checks/rerun` | for a pull request chargehand opened: re-runs the failed jobs of its failed required checks through the GitHub App (`rerun-failed-jobs` for each workflow run, so the App needs the Actions permission to write) | `202` `{checks, rerun_runs}`; `409` when the pull request is merged or closed, no required check has failed (so a second call right after is a `409`), or the server has no credential for the forge; `502` with the forge's message when it refuses |

### Dismissing inbox items

`POST /v1/inbox/dismiss` takes items out of the inbox and `POST /v1/inbox/restore` puts them back; the body of both is `{"ids": [...]}`, 1 to 500 ids as `GET /v1/inbox` gives them (a pull request's id holds its URL, so ids are not in the path). The owner may dismiss any item; an account the items its own inbox shows, and an id it cannot see is `unknown`. Dismissing an item that stands for several identical failures dismisses them all, and restoring it restores them all. The answer is `200` `{action, changed, ids, unchanged, unknown}`: `ids` changed now, `unchanged` were so already (safe to send twice); `404` when every id is unknown, `400` for another body, `409` when the server keeps no dismissals, `403` for a run token. Dismissals are kept by item id in `inbox/dismissed.json` beside the run log, with the time and the account; a dismissal hides the item from everyone who sees it ([ADR 0064](../adr/0064-inbox-items-resolve-on-their-own-or-are-dismissed.md)).

## Run tokens (driven sessions)

A driven session ([ADR 0039](../adr/0039-driven-writing-sessions.md)) calls chargehand back for research and review. It does not get the server's key: the server issues it a **run token**, `chr1.<payload>.<signature>`, an HMAC over its claims with a key only that process holds. The token opens `POST /v1/runs`, `GET /v1/runs/{id}` and the MCP tool, and nothing else (every other route is `403`). Through it a session can start only the `default` and `review` presets, only on the repository and commit of its task, never a batch, and only while the task's token and dollar caps last; each call's budget is cut to what is left. The runs it starts record the task's run as their parent and count against its caps. They run on a separate gate of two, not the one-at-a-time gate that the batch holds while it waits for them. A token expires; the server keeps its signing key beside the run log, so a token survives a restart.

## Runs and the run log

Runs outlive the request that started them and execute one at a time unless `http.max_concurrent_runs` raises it (a driven batch counts as one run; its sessions' research and review calls have their own limit, `driven.max_session_calls`); the server holds at most 10 unfinished runs. The server and the CLI share the run log when they use the same file: `chargehand show` reads runs started over HTTP and MCP, and `GET /v1/runs/{id}` reads runs started from the CLI. A server restart leaves unfinished runs `lost`, and their callers resend ([ADR 0018](../adr/0018-callable-interface-http-mcp-run-store.md)); a driven batch carries on by itself: the server keeps its state in `batches/` beside the run log, takes the batch up at start, and removes containers, networks and volumes no batch owns ([ADR 0043](../adr/0043-driven-batches-survive-a-server-restart.md)).

## Container image

[Install with Docker Compose](install.md) runs these images from a ready compose file (`deploy/compose/`). A tag `v<Version>` publishes `ghcr.io/<owner>/chargehand:<Version>` through [release.yml](../../.github/workflows/release.yml), which first checks the tag against `Directory.Build.props` and the changelog. There is no `latest` tag and no image per commit: a deployment pins an exact version ([ADR 0024](../adr/0024-server-on-a-private-network-and-release-images.md)).

```bash
docker run -p <private-ip>:4300:4300 -v <dir-with-profile.json>:/config:ro -e CHARGEHAND_API_KEY=... \
  ghcr.io/<owner>/chargehand:<Version>
```

The [Dockerfile](../../Dockerfile) builds:

- `chargehand serve` with the Claude Code CLI at a pinned version: build argument `CLAUDE_CODE_VERSION`, default 2.1.283. A profile's `claude_code.version` must match it.
- git for worker clones, with `prompts/` and `presets/` under `/app`.
- A non-root user, working directory `/app`, `CHARGEHAND_PROFILE=/config/profile.json`, port 4300.
- `/app/runs` for the run log, and `/srv/chargehand/work` for worker clones (the `worker_root` that `profiles/example.json` uses).

OpenCode is not in the image; an OpenCode profile points `opencode.url` at a server that runs next to the container. Pass the worker's credential in the container's environment: with no `claude_code` block, chargehand looks for `ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN`. The server's repositories live under its `repository_roots`, and keeping them fetched is the deployment's job.

No test in the repository runs the image ([capabilities](capabilities.md#server-container-image)).

## Runner (driven sessions)

Driven sessions ([ADR 0039](../adr/0039-driven-writing-sessions.md)) start one container per task. The server never holds the container engine's socket: a separate **runner** does, and exposes a narrow API in front of it.

```bash
export CHARGEHAND_RUNNER_KEY=<key>
chargehand runner --listen <private-ip>:4310 --allowed-hosts <runner-name> \
  --images <name>@sha256:<digest> --egress-image <name>@sha256:<digest>
```

The runner refuses to listen off loopback without `--allowed-hosts`, needs the key in the environment (never an argument), and starts only images on `--images`, by digest. Every container it starts comes from one fixed template: non-root, read-only root filesystem, no capabilities, no Docker socket, no host mount, one internal network. A request names a run, a listed image and numbers under the runner's ceilings, nothing else, and a body with any other field is refused. It reads a fixed list of files from a session's output volume, and the end (at most 256 KiB) of the session's `stream.jsonl`, never the whole of it. It signals, reads, removes and joins only containers that carry chargehand's `chargehand.run` label, so it is safe on an engine other stacks share. `GET /count` and `POST /kill-all` work without the server's records. `GET /host` returns the host's `available_mb`, `total_mb`, `swap_total_mb`, `swap_free_mb`, `load1`, `cpus` and `memory_pressure_avg10` (Linux PSI, `some avg10` of `/proc/pressure/memory`, in percent) for the server's admission (a field the host does not report is left out; macOS has no PSI); it needs the same key. `GET /inspect/<id>` adds `memory_peak_mb`, the most memory the container has used, read from its cgroup's `memory.peak` when the runner can see the host's cgroups (the systemd and cgroupfs layouts are tried; a read-only bind mount of `/sys/fs/cgroup` is one way), else the largest `docker stats` reading (at most one per 5 seconds, which leaves out inactive page cache and can miss a short spike); it is left out when neither works. `--max-containers` counts every labelled container, running or not (sessions, each batch's egress proxy, test services), so set it above `driven.max_parallel_total` by the egress proxies and services of the batches you run at once. The helpers that move `task.json`, `seed.bundle` and the results through an output volume are named `chargehand-xfer-<run>`, and the ones that make a session's workspace `chargehand-prep-<run>`: a call that is cancelled mid-start removes its helper, one that finds a stopped helper of its name removes it and runs again, a call the engine itself fails (`docker run` exit 125) is run again up to 4 times, and at start the runner removes the helpers that are not running.

The runner runs in a container, where `/proc/meminfo` and `/proc/loadavg` normally show the host's values, unless something like lxcfs is mounted over them; `cpus` is what the runner process sees, so a CPU limit on the runner container lowers it. Whether that holds on a given server is not assumed: after deploying, compare `docker exec <runner-container> grep -E 'MemAvailable|MemTotal' /proc/meminfo` with `grep -E 'MemAvailable|MemTotal' /proc/meminfo` on the host. They should agree to within a few percent; if the container's `MemTotal` is its own limit, admission is judging the container, not the host.

The runner's own compromise is the security cost of this design: whoever holds its key can start listed images and remove labelled containers, and the process holds the engine's socket, which is root-equivalent on a rootful engine. Run it on a private network, behind its key, and prefer a rootless engine where you can.

### Deploying it with containers

Two services from the release image (`chargehand`), and the session image (`chargehand-session`, the same version) that the runner starts:

- **The server** (`chargehand serve`) has no engine socket. `driven.runner` names the runner's URL and the secret item for its key; `driven.images` lists the session image by digest; `driven.push_secret` names the credential that pushes `chargehand/<run>` and opens draft pull requests.
- **The runner** (`chargehand runner`, the same image: it carries the docker CLI) mounts the engine's socket and joins the server's network. Run it as the image's user with the socket's group added (`group_add`), listen on its container name and give `--allowed-hosts` that name. `--images` and `--egress-image` take the session image as `name@sha256:<digest>`, **with no tag** (a tagged reference is refused and the runner exits with its usage line). Two policy flags are required for a batch to run: `--source-roots` names the directory the server makes its checkouts in (`worker_root`; with none, no workspace is prepared), and `--outside-networks` names the network the server sits on (with none, the egress container cannot join it). A third, `--forwards`, lists the forwards an egress container may carry, as `listen-port=host:port` (`4300=chargehand:4300`: the port a session calls and the server's name and port on that network, the same name and port as `driven.network.mcp_forward`). The server names its forward in each request and the runner starts only a listed one; with none listed the egress container has no forward and a session's call back to the server is refused. `--keep-awake on|off`, `--drain-on battery,lid`, `--drain-file <path>` and `--host-source auto|proc|engine` are for a runner on a laptop ([Several runners](runners.md#a-mac-as-a-runner)). `--max-bundle-mb` (default 512, at most 4096) is the most a repository bundle may be when the server sends it the repository instead of sharing a disk ([Several runners](runners.md#a-runner-on-another-machine); such a runner needs no `--source-roots`). `--service-images` lists the images the runner may start as test services ([driven guide](driven.md#shared-services)), by digest like `--images`; with none it starts no service, and services count against `--max-containers`. A session may also mount package cache volumes named `chargehand-cache-<ecosystem>-<12 hex>`, which the runner accepts in that shape only. A gateway (`driven.network.model_url`) or a collector (`driven.network.otlp_url`) reached over `http` is a forward too, and must be listed the same way (`4318=collector:4318`).
- **Paths must mean the same to the engine as to the server.** The server makes each task's checkout under `worker_root` and the runner hands that path to the engine to mount, so `worker_root` has to be a bind mount whose host path equals its path in the container (a named volume would not resolve, and the engine would mount an empty directory).
- **The callback.** A session calls the server for research and review through its batch's egress container, as `http://chargehand-driven:<port>`: set `driven.network.outside` to the network the server sits on and `driven.network.mcp_forward` to the server's name and port on it (`name:port`), and add `chargehand-driven` to `http.allowed_hosts`. A repository the sessions work on must be a checkout under `repository_roots` with an https `origin`; a private one needs the fetch credential on the host. `repository_roots` must also cover the directory the server makes its checkouts in, `<worker_root>/.checkouts`: a session's research and review calls name its own checkout, and without that root the server refuses them with `repository_not_allowed` (the session then stops and asks).

A session's operational limits (wall clock, turns, tokens, memory, CPUs, processes) are tuned in the profile's `driven.limits`, not in `presets/driven.yaml`: a change to the preset file waits on Prompt CI and the owner's approval, a change to the profile does not ([driven guide](driven.md#limits-and-the-kill-switch)).

Keep `driven.enabled` false until a smoke batch of one task has produced a draft pull request.
