# 0053. Accounts bring their own model key

- Status: accepted
- Date: 2026-10-13

## Context

[ADR 0050](0050-accounts-and-named-api-keys.md) gave a self-hosted server named callers with caps, and listed what it did not isolate: every account's runs and batches spent the server's one model credential, so one account used the model's rate limit and the operator's bill for all. Dollar caps on an account were measured against that shared bill, and an account of someone else's could not be asked to pay for its own use.

Facts used. A read-only run goes through one `IWorkerRuntime` the CLI builds at startup. The Claude Code runtime holds its credential and endpoint in the instance and sets them, and removes every inherited credential variable, on each CLI call; OpenCode keeps its provider credentials in its own server. A batch takes its credential from `claude_code` in `DrivenRun`: an API key goes into the session's environment, a gateway key with `driven.network.model_url` goes in with a base URL, and a subscription token never reaches a session (the batch's egress container exchanges a per-task token for it, [ADR 0039](0039-driven-writing-sessions.md) decision 9; measured on the subscription only). A session's own research and review calls ride a run token that already carries the account of its batch.

## Options

### Where an account's credential comes from

1. **A secret item named in the profile** (`http.accounts[].model`), resolved through the same secret sources as every other credential. Nothing new to store; revoking is a profile edit and a restart.
2. **A key the caller sends with each request.** The server would hold and forward a stranger's key; a key in a request body lands in logs and the run log.
3. **A table of keys beside `accounts.json`.** One more secret store to protect, for no gain over the existing sources.

### A subscription token for an account

The exchange keeps the token out of the session, but the token is one person's plan: it has no dollar price, so an account's dollar cap would not bind, and lending it out spends the owner's limits. Not offered.

## Decision

- **Option 1.** `http.accounts[].model` holds `api_key_secret` (required) and `base_url` (optional). An API key alone goes direct to Anthropic. A key with `base_url` is a key an Anthropic-compatible gateway issued, and goes to that gateway. `oauth_token_secret` is rejected by name, with the reason: a subscription token is the owner's.
- **Which credential a run uses.** The owner, a CLI run and stdio use the server's, unchanged. An account with `model` uses its own. An account without it uses the server's only if `allow_server_credential` is true (default false); otherwise its request is refused at admission with `403`, `error.code` `model_credential_missing`, the same body as the other refusals and, over MCP, a tool error naming the code. A run that gets past admission for an account that has since left the profile (a batch a restart took up) fails with `credential_unavailable` before anything is spent. `AccountCredentials` makes this decision in one place for the server, the runtimes and the driven runner.
- **Read-only runs.** `Orchestrator` takes a function from the run's actor to a runtime. For an account with `model`, `AccountRuntimes` makes a Claude Code runtime of its own on first use, over the same CLI and pin but the account's key and base URL, and keeps it. Its sessions and its environment are its own, so a run never sees another account's key, and none inherits the owner's subscription token. Intake, the workers and the support check all use it. The server refuses to start when an account has `model` and the runtime is OpenCode.
- **Batches.** An account's key is priced: the batch's dollar cap is required and binds, and the result carries dollars. The key reaches the session as `ANTHROPIC_API_KEY` (`credential_delivery: environment`), or with `base_url` as a gateway key (`gateway_key`), reached through a forward on the batch's egress container or, for `https`, allowed on that batch's egress only. The server's `driven.network.model_url` belongs to the server's credential and is not used for an account's key. The support check of a batch's claims runs on the account's runtime too. A session's research and review calls already carry the batch's account and so use its key.
- **Not exchanged.** An API key is not swapped for a per-task token: the gateway sets `Authorization: Bearer` and an API key travels in `x-api-key`, and the swap was never measured with a key. The account's key is in its own session containers' environment, like the server's API key today, and is among the values the diff scan looks for.
- **Doctor** checks that each account's key item resolves.

## Consequences

- **A behaviour change for existing accounts.** An account declared before this change spent the server's credential; with no `model` and no `allow_server_credential` it is now refused. Set `allow_server_credential: true` on it to keep the old behaviour. Unreleased, so no deployed profile breaks.
- The account's key is in an environment variable in its sessions. Use a gateway key (budget, model list, revocation) for an account whose repositories you do not trust.
- A key is resolved when the account's first run needs it and kept for the process: rotating it needs a restart.
- Memory providers and the prompt enhancer are not model credentials of the account and stay shared ([ADR 0050](0050-accounts-and-named-api-keys.md) lists them). The model's rate limit is no longer shared with accounts that bring their own key.
- OpenCode servers keep their provider credentials in their own config: they cannot take a per-account key.

## Reopen if

An account's key must stay out of its sessions (extend the exchange to `x-api-key` and measure it); a subscription must serve several people; or keys must rotate without a restart.
