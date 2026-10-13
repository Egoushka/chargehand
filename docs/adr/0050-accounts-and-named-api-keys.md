# 0050. Accounts and named API keys for a self-hosted server

- Status: accepted
- Date: 2026-10-13

## Context

The server has one bearer key (`http.api_key_secret`). Whoever holds it can start any run, read any run, cancel any run and halt the server. Run tokens ([ADR 0039](0039-driven-writing-sessions.md)) exist only for a driven session's own calls. Caps are per run (`context.budget_usd`) and per batch (`driven.max_usd_total`, `max_tokens_total`); nothing bounds what one caller spends in a day, and the run log records no caller. A server shared by a few people or a few programs cannot tell them apart, bound them apart, or let one stop reading the other's results.

Until now multi-user accounts were not planned. The maintainer reopened that on 2026-10-13, for a **self-hosted server only**: one operator, a handful of named callers, one deployment. A hosted service with sign-up, billing or tenants is not the aim and nothing below is designed for it.

Facts used. `ChargehandServer` checks the bearer with a fixed-time comparison in one middleware and the run-token check follows it. `RunService` keeps unfinished runs in memory and the rest in the run log, which the CLI shares. `ResultContract` is signed over its canonical JSON ([ADR 0036](0036-support-checking-and-signed-results.md)), so a field added after signing breaks the signature. The MCP transport ties a session to the user of the request that opened it, when the request has one.

## Options

### Where an account's keys live

1. **Secret-store items named in the profile**, one per key. No new file. But each key is a secret the operator provisions by hand in the secret store, adding or revoking one is a profile edit and a restart, and the server holds every key in clear to compare it.
2. **A keys file the server manages**, holding a salted hash per key, written by `chargehand accounts add`. The key is shown once and exists nowhere else; revoking is one command and takes effect at once. The cost is a state file beside the run log that a deployment must keep (the container's `/app/runs` volume already is one), and a lost file means every account key must be made again.
3. **Signed tokens** like run tokens. Nothing stored, but revoking one needs a deny list and a leaked signing key mints any account.

### What an account may do

Rights in the profile (a reviewed file), keys in the file (never reviewed, never committed). The profile may sit in a repository, so it must not hold a key, even hashed.

## Decision

- **Keys: option 2.** A key is `chk1.<id>.<secret>`: a public 16-hex-character id and 32 random bytes in base64url. `accounts.json` beside the run log holds `{id, account, label, salt, hash, created, revoked}` per key; `hash` is SHA-256 over a per-key random salt and the secret. A salted fast hash is enough because the secret is 256 random bits, not a password. The file is written to a temporary file, flushed and renamed, readable by its owner only. The server finds the record by id, hashes the presented secret with that record's salt and compares in constant time; an unknown id still computes a hash against a dummy salt. It re-reads the file when its size or time changes, so a revoked key stops working without a restart. A key whose account the profile no longer declares opens nothing.
- **Rights: `http.accounts` in the profile.** `name` (lowercase letters, digits, hyphens, at most 32; `owner` is reserved), `max_usd_per_day`, `max_usd_per_month`, `max_tokens_per_day`, `max_tokens_per_month`, `max_concurrent_runs`, `driven` (default **false**: a batch pushes branches and opens pull requests with the server's credential, so it is opt-in) and `repositories` (directories the account's `context.repository.path` must be, or be below). Every cap left out is no cap.
- **The existing key is the `owner` account.** `api_key_secret` keeps working as before: every route, every run, no caps, no migration. Its runs now record `actor: "owner"`. Records written before this change have no actor and belong to the owner.
- **Actor.** `actor` is added, optional, to the start record, the run record, `result/v1`, `run-status/v1` and `run-summary/v1`. It is set inside the orchestrator before the result is signed, so a signed result stays valid. A run started from the CLI has none. A run token carries the actor of the batch that issued it, so a session's research and review calls belong to that account; a token issued before this change reads as the owner's.
- **Scope.** An account lists, reads, streams and cancels only runs whose actor is its name; the owner sees all. Another account's run answers `404`, not `403`, so ids are not probed. `POST /v1/halt` and `/v1/resume` are the owner's alone (`403`). A batch may `continue_from` only a run of the same account. The MCP session is tied to the account that opened it.
- **Caps are checked when a run is requested.** Windows are the UTC day and the UTC calendar month. Spend is what the account's runs cost in the window: a finished run by its result's `usage` (dollars where priced, input plus output tokens), a run still going by its calls so far, and a batch once, by its own result, not again through its tasks and their research calls (the cheap read-only run that plans a batch is recorded under the account but counted in neither). A run counts in the window it finished in. If a cap is reached the request is refused, `429` with a `Retry-After` for the window's end and a body `{"errors": [...], "error": {"code": "budget_exceeded", "window": "day|month", "unit": "usd|tokens", "limit", "spent", "remaining": 0, "resets_at"}}`; over MCP the tool error starts with `budget_exceeded:`. A request that is admitted has its `budget_usd` (and, for a batch, `max_usd_total` and `max_tokens_total`) cut to what the tightest window has left. `concurrency_exceeded` (`429`), `driven_not_allowed`, `repository_not_allowed` and `continue_not_allowed` (`403`) use the same body.
- **CLI.** `chargehand accounts add <account> [--label]` prints the key alone on stdout, once; `list` shows accounts, caps and key ids and states, never keys; `revoke <key-id>`.

## Consequences

- Backward compatible: a profile with no `accounts` behaves as before, and the one key is the owner.
- **A cap can be overshot** by the budget of a run already underway: spend is known when a call or a run finishes. Clamping the budgets bounds one run, not several started in the same instant (the count-then-start check is not atomic, as for `MaxUnfinished`). A subscription run has no dollar price and adds none to a dollar cap: pair it with a token cap.
- The keys file is a new piece of state. `accounts` commands must run where the server's run log is, and two commands at the same moment can lose one write.
- Reading the whole run log per admitted request is as costly as `GET /v1/runs` already is; it is read only for an account with a cap in that window.
- **What this does not isolate** (CHARGEHAND-202 is the work for separate runners and networks; [ADR 0052](0052-accounts-bring-their-own-model-key.md) gave an account its own model credential): accounts share one worker runtime and its credential, so one account can use up the model's rate limit for all; one worker root and one set of checkouts; the memory providers, whose namespace is the server's, so a fact one account's run retained can be recalled by another's; the prompt enhancer; and, for driven batches, the runner, the container engine, the session image, the egress allowlist, the push credential (every pull request is opened as the same identity) and the package caches of a repository. The operator reads the whole run log. MCP tasks are held in one in-memory store, and whether a task id is bound to the account that created it was not checked.

## Reopen if

A hosted service is wanted; accounts must be kept apart at the container or network level (CHARGEHAND-202); or keys must come from an identity provider instead of this file.
