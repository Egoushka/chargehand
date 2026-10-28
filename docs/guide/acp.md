---
title: "ACP agents as research workers"
description: "Run Codex, Gemini CLI and other agent client protocol agents as read-only research workers: the pinned list, the key, the three read-only guards and the usage rule."
order: 17
section: "Guides"
---

`runtime: "acp:codex"` (or `acp:gemini`) runs a research preset on an agent that speaks the [agent client protocol](https://agentclientprotocol.com) (ACP), through [`AgentClientProtocol.Net`](../../src/Shared/AgentClientProtocol.Net/README.md) and the adapter `Chargehand.Acp`. The worker is read-only, runs on an API key or a gateway's key, and its usage is what the agent reports ([ADR 0086](../adr/0086-one-core-doors-in-and-doors-out.md), decisions 8 to 10). It runs research presets: every shipped preset that does not write (`code` is the one that does). A preset that writes is refused `invalid_request` before anything is spent. Claude Code and OpenCode keep their own runtimes.

## Set up

1. Create a secret item that holds the key: the provider's API key, or the virtual key of a gateway. chargehand reads it by name through the profile's `secrets` sources and never prints it.
2. Name it in the profile, and name the runtime:

```json
{
  "schema": "profile/v1",
  "runtime": "acp:codex",
  "acp": {
    "agents": {
      "codex": { "api_key_secret": "openai-api-key" },
      "gemini": { "api_key_secret": "gemini-api-key", "base_url": "https://gateway.example/gemini" }
    }
  },
  "prices": { "openai/gpt-5.1-codex": { "input": 1.25, "output": 10, "cache_read": 0.125, "cache_write": 0 } }
}
```

`CHARGEHAND_RUNTIME=acp:gemini` or a request's runtime works the same way; the profile field wins. `chargehand doctor` checks that the key item resolves. A model reaches the price table as `provider/model` (the provider is the agent's `provider` in the pinned list; the model is the one the agent reports, or the preset's); with no price entry the cost is unknown, never zero, and the token budget still ends a run that reads too much.

There is no setting for a subscription login. An agent starts with a private home directory and only its key (and gateway) in its environment, and chargehand calls `authenticate` with the one method the pinned list names. Codex's `chat-gpt` and Gemini CLI's `oauth-personal` are never chosen.

## The pinned list

`acp-agents.json` names every agent a profile may use: `id`, the launch `command` and `args` (the package with its version, so a run never starts "latest"), `version`, `sha256` where the registry gives one (it does not for npm packages), the variable the key goes in (`api_key_env`), the gateway variable (`base_url_env`), the `auth_method`, the agent's own read-only mode (`read_only_mode`), whether it reports usage (`reports_usage`) and fixed variables (`env`). A run never fetches this list. `scripts/refresh-acp-agents.py` compares the entries that name a `registry_id` with the ACP registry and, with `--write`, updates their `version` and `args`; review the diff and repeat the live check before committing. Adding an agent is a reviewed entry plus a live check.

The first launch of an agent started by `npx` downloads its package, which can take longer than the 2 minutes a run allows for startup: run the command once by hand to warm the cache, or install the package and point `command` at it.

## Read-only, three ways

| Guard | What it does |
|---|---|
| Permission requests | Every `session/request_permission` whose tool kind is not `read`, `search` or `think` is rejected with the agent's own reject-once option (`cancelled` when it offers none): edits, deletes, moves, execution, fetches, mode switches and kinds nobody has seen. A read is allowed once, never for good. Each rejection is a message in the run's transcript (`chargehand show <run>`). The client offers the agent no file-system or terminal methods. |
| The agent's mode | `read_only_mode` from the pinned list (Codex `read-only`, Gemini CLI `plan`), else the first of `read-only`, `readonly`, `read_only`, `plan` that the agent lists, is set with `session/set_mode` before the first prompt. An agent with none relies on the other two. |
| The sandbox | The agent starts under the profile's `sandbox` ([ADR 0035](../adr/0035-sandboxed-writing-workers.md)): `sandbox-exec` on macOS, `bwrap` on Linux. It may read the machine except credential directories and write only in a private directory (its home and temp), so the checkout is read-only at the operating system. The network stays on, because the agent calls its model. `sandbox.kind: none` is an explicit opt-in that removes this guard; with no sandbox available and `auto`, the run is refused `sandbox_unavailable`. |

## Usage, and the dollar cap

Usage is what the agent reports: the token counts of a turn's result, and between updates the context gauge of `usage_update` as input tokens, so a budget can still end a turn that is running. Every run has a dollar cap (`run_cap_usd`, the preset's budget, `context.budget_usd`), and a cap holds only on counted tokens. An agent whose pinned `reports_usage` is `false` is therefore refused `runtime_uncapped` before intake, unless the profile sets `acp.allow_uncapped: true`; the result then carries the open question "Usage was not recorded" and `usd` stays unknown. Both pinned agents report usage in their source (Codex 2.3.0 and Gemini CLI 0.66.0 emit `usage_update` and per-turn token fields); whether the figures match the provider's bill is UNKNOWN until the live check.

## What is not supported

- **Services.** A preset's granted MCP servers are not handed to an ACP agent: the protocol cannot limit an agent to a server's granted tools. The run records them as unavailable.
- **Fork and compaction.** A split's later nodes start a fresh session and pay for their own prefix. Codex advertises `session/fork`; the client library does not call it yet.
- **Accounts with their own model credential** (`http.accounts[].model`) need `claude_code`.
- **Writing presets and driven sessions.** Research only.
- **A gateway for Codex.** `OPENAI_BASE_URL` is set from `base_url`; whether the adapter's Codex honours it is UNKNOWN. Codex's own gateway auth method needs a client capability this client does not offer.

## What was measured

On 2026-10-28, with an empty home and no key, each agent was started by `npx` and sent `initialize`: Codex (`@agentclientprotocol/codex-acp` 2.3.0) answered protocol version 1 and the auth methods `api-key` and `chat-gpt`; Gemini CLI 0.66.0 answered version 1 and `oauth-personal`, `gemini-api-key`, `vertex-ai` and `gateway` (it will not start without `GEMINI_API_KEY` set to some value). The runtime's behavior is tested against the scripted fake agent of the library's tests (`AcpRuntimeReadOnlyTests`, `AcpUsageTests`, `AcpRuntimeLifecycleTests`), under the real `sandbox-exec` on macOS (`SandboxTests`).

The live check of the issue (one synthetic question on `claude_code`, `acp:codex` and `acp:gemini`, every citation resolved at the pinned commit, under 5 USD) needs the two key items. It has not been run.
