---
title: "Quickstart"
description: "Ask one read-only question about a commit from a chargehand checkout, then read the result/v1 that comes back and the run it left in the log."
order: 2
---

You clone chargehand, write a small profile, and ask one question about a commit of a repository. The run prints `result/v1` on stdout and leaves a record in the run log.

## Prerequisites

- The .NET 10 SDK. `global.json` pins SDK 10.0.100 and rolls forward to later feature bands.
- git. chargehand clones the repository you ask about at the commit you pin.
- A clone of chargehand, written `<checkout>` below. Run the commands on this page from it.
- One worker runtime on `PATH`, at its pinned version:

| runtime | binary | pinned version | credentials |
|---|---|---|---|
| OpenCode | `opencode` | 2.0.18 | for a server chargehand starts: the provider variables OpenCode reads, such as `ANTHROPIC_API_KEY` |
| Claude Code | `claude` | 2.1.283 | the CLI's own login (run `claude` once and sign in), or one of `ANTHROPIC_API_KEY` and `CLAUDE_CODE_OAUTH_TOKEN` |

A runtime at another version fails with `runtime_version_mismatch`. With both CLIs on `PATH`, name one with `CHARGEHAND_RUNTIME` (`opencode` or `claude_code`) or the profile's `runtime` field, or the run fails with `runtime_ambiguous` (on main, not yet released).

## Write a profile

Every command reads `profiles/local.json` (gitignored) unless `--profile <path>` or `CHARGEHAND_PROFILE` names another file. The flag goes before the command: `dotnet run --project src/Chargehand.Cli -- --profile <path> run`.

On main the file itself is optional: with no file, chargehand uses defaults (on main, not yet released). One part stays required. The shipped presets name placeholder models (`provider/worker-model`, `provider/small-model`, `provider/critic-model`), and the profile's `models` map turns them into real model ids. With no map they reach the runtime unresolved. The [package README](../../src/Chargehand.Cli/README.package.md) says a profile is optional except while the presets name placeholder models, and the [README](../../README.md#quick-start) says a profile with a `models` map stays required for Claude Code "until 0.4 lands the fix".

`scripts/change-e2e.sh` writes this minimal profile for Claude Code. Save it as `profiles/local.json`:

```json
{
  "schema": "profile/v1",
  "models": {
    "provider/worker-model": "anthropic/sonnet",
    "provider/critic-model": "anthropic/sonnet",
    "provider/small-model": "anthropic/haiku"
  }
}
```

For OpenCode, map the same three ids to `provider/model` ids your OpenCode server serves.

The README's quick start copies `profiles/example.json` instead, which fills in secrets, an `opencode` block, prices, telemetry, the HTTP server and memory. If you start from it, delete the blocks you do not use. An `opencode` block makes chargehand connect to its `url` instead of starting its own server. The [reference](reference.md#profile-fields) lists the fields. A profile names secrets by item name and never holds them.

Without a `prices` entry for a model, chargehand cannot price its calls: `usage.usd` may be `null`, and the USD caps cannot fire, while the per-node token budgets still apply (on main, not yet released).

### OpenCode

With no `opencode` block, `run`, `serve` and `mcp` start their own `opencode serve` on `127.0.0.1` and a free port, with a random password and their own state under `chargehand/opencode` in the per-user data directory, and stop it on exit. Providers come from the environment variables OpenCode reads. To configure more, edit `xdg/config/opencode/opencode.json` in that directory; chargehand never overwrites it.

To use a server you run yourself, start it with `scripts/opencode-serve.sh <opencode-binary> profiles/local.opencode.json 4296` (copy the config from `profiles/opencode.example.json`) and add an `opencode` block with `url`, `password_secret` and `version`. chargehand then starts nothing ([ADR 0030](../adr/0030-default-opencode-server.md)).

### Claude Code

With no credential set, workers use the CLI's own login. To use another credential, set one of `ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN` (from `claude setup-token`); setting both is an error. Without a `claude_code` block, chargehand picks the mode from what its secret sources resolve, by default your environment: `ANTHROPIC_API_KEY` alone selects per-token API billing, `CLAUDE_CODE_OAUTH_TOKEN` alone your subscription, and neither the CLI's login. An API key left in your shell therefore switches the workers to API billing. A `claude_code` block in the profile (`version`, `binary`, and at most one of `api_key_secret` or `oauth_token_secret`) overrides that ([ADR 0020](../adr/0020-claude-code-runtime-adapter.md)).

## Allow the repository

A request names a repository and a commit. chargehand clones the repository at that commit under `worker_root` and runs the worker in the clone, so the worker reads the commit and never your working tree. Uncommitted and ignored files do not reach it.

- `repository_roots` lists the directories a repository may sit under; `"/"` allows any.
- Without `repository_roots`, `run` and `mcp` allow `worker_root` and the directory they were launched in. `serve` allows `worker_root` only.
- `worker_root` must sit outside your home directory. On main it defaults to `/var/tmp/chargehand/work`, created on first use.
- chargehand refuses a clone that tracks a file the preset denies reading (`*.env`, `*.env.*`) with `checkout_has_secrets`.

The shortest path is a question about chargehand itself. Run from `<checkout>` and the checkout is the launch directory, so you need no roots.

## Write a request

The smallest valid request is `schemas/request/v1/examples/valid-minimal.json`:

```json
{ "contract_version": "request/v1", "text": "Which modules call the payment client? Cite file:line.", "context": { "interactive": true, "preset": "default" } }
```

It names no repository, so intake stops it with `needs_input` before a worker starts. A question about code needs `context.repository`. Save this as `request.json`, with the output of `git rev-parse HEAD` as the commit and a question about this repository from `evals/example.jsonl`:

```json
{
  "contract_version": "request/v1",
  "text": "Where does a split node decide whether to fork the first node's session, and what must match for it to fork? Cite the code.",
  "context": {
    "interactive": false,
    "preset": "default",
    "repository": { "path": "<checkout>", "commit": "<commit>" }
  }
}
```

| field | meaning |
|---|---|
| `contract_version` | always `request/v1` |
| `text` | the request in natural language |
| `context.interactive` | `false`: never block on a question, return `needs_input` instead. Only the MCP tool reads it: with `true` it asks the client intake's questions. The CLI returns `needs_input` either way. |
| `context.preset` | a preset name from `presets/`: `default`, `cheap`, `thorough`, `strict`, `draft`, or `review` (on main, not yet released) |
| `context.repository` | `path` of a local git checkout and the `commit` to pin, 7 to 40 hex characters |
| `context.budget_usd` | optional USD bound for the run, divided evenly across a split's nodes |
| `context.approved` | optional; `true` runs past a preset's approval thresholds |
| `inputs`, `caller_blocks` | optional facts and prompt blocks from a program caller; claims cite inputs by id |

## Run it

```bash
dotnet run --project src/Chargehand.Cli -- run < request.json
```

`run` prints `result/v1` on stdout. It exits 0 when the run completed, 1 when it failed or was denied, 2 on a usage error such as an invalid request, and 3 when it needs input. If chargehand cannot reach the runtime (binary missing, server not running, another version), you still get a failed `result/v1`, and its `error` says what to do.

## Read the result

This is `schemas/result/v1/examples/valid-completed.json`, formatted:

```json
{
  "contract_version": "result/v1",
  "task_id": "t-1",
  "node_id": "n-1",
  "trace_id": "0af7651916cd43dd8448eb211c80319c",
  "prompt_chain": {
    "blocks": [
      { "name": "core/worker", "version": "0.1.0", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "source": "registry" }
    ],
    "as_sent": { "opencode_version": "2.0.16", "agent": "build", "model": "provider/model-name", "date": "2026-09-26" }
  },
  "status": "completed",
  "summary": "divide() raises ZeroDivisionError when b is 0.",
  "claims": [
    { "text": "divide() evaluates a / b without a guard.", "evidence": ["e1"], "confidence": 0.95 }
  ],
  "evidence": [
    { "id": "e1", "kind": "file", "locator": "src/calc.py:5-7", "commit": "193a371" }
  ],
  "artifacts": [],
  "open_questions": [],
  "confidence": 0.9,
  "usage": { "input": 3, "output": 120, "cache_read": 5600, "cache_write": 90, "usd": 0.0002 }
}
```

| field | meaning |
|---|---|
| `task_id` | the run id. A real one looks like `run-<yyyyMMdd>-<HHmmss>-<6 hex>`, and `show`, `cache` and `GET /v1/runs/{id}` take it. |
| `node_id` | the node that produced the result: the answering node kind (`worker`, or `draft` for the draft preset), `merge` for a split, `intake` when intake stopped the run, `run` when the run failed outside a node |
| `trace_id` | the W3C trace id of the run |
| `prompt_chain.blocks` | each prompt block in placement order, with its version, sha256 and source (`registry`, `caller` or `runtime`) |
| `prompt_chain.as_sent` | the runtime version, agent, model and date of the calls. A Claude Code run puts `claude-code/<version>` in `opencode_version`. |
| `status` | `completed`, `needs_input`, `failed` or `denied` |
| `summary` | the short answer |
| `claims` | each claim's text, the ids of its evidence (at least one), and a confidence from 0 to 1 |
| `evidence` | each reference: `id`, `kind` (`file`, `diff`, `url`, `session_message`, `commit`, `input`), `locator`, and for files the `commit`. A file locator is `path:line` or `path:start-end`. |
| `artifacts` | inline content (at most 64 KiB) or a URI, each with its sha256: a draft, or the diff of an improved request |
| `open_questions` | claims whose citations did not resolve, intake's questions, and what a node did not get to |
| `confidence` | the result's confidence, 0 to 1 |
| `usage` | input, output, cache-read and cache-write tokens, and `usd` from the profile's price table (`null` when a model has no price) |
| `error` | failed results only: `code`, `message`, `retryable`, and an `action` when there is something to do |

In this example the claim cites lines 5 to 7 of `src/calc.py` at commit `193a371`. In a real run the resolver checks that such lines exist at that commit before the result leaves its node.

## Look at the run

```bash
dotnet run --project src/Chargehand.Cli -- show <run-id>
```

`show` prints the run's preset, intake action, status, duration, cost, claim count and number of open questions, then one line per model call: node, kind, model, prompt tokens, cached tokens, cache rate, output tokens, latency and cost. A run with a start record and no result reads `running` while its process lives and `lost` after. `cache <run-id>` lists cache reads and writes per call and names the first block that broke a prefix the nodes should have shared.

From a checkout, the run log is `runs/run-log.jsonl`; [the reference](reference.md#run-log) says where it goes elsewhere.

## If the run fails

| `error.code` | what to do |
|---|---|
| `repository_not_allowed` | put the repository under one of `repository_roots`, or add a directory that contains it (`"/"` allows any); keep `worker_root` outside your home |
| `checkout_invalid` | point `context.repository.path` at a git working tree, and pin a commit it has |
| `checkout_has_secrets` | pin a commit that does not track the denied files, or remove them from the repository |
| `runtime_unavailable` | install the runtime, sign in, or start the OpenCode server the profile names |
| `runtime_version_mismatch` | install the pinned version, or set the version you run in the profile's `claude_code` or `opencode` block |
| `runtime_ambiguous` | name the runtime with `CHARGEHAND_RUNTIME` or the profile's `runtime` field |

The [reference](reference.md#error-codes) lists every code. Next: [use it from an MCP client](mcp.md), or [run the HTTP server](server.md).
