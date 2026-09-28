# 0031. Managed Agents as a worker runtime

- Status: proposed
- Date: 2026-09-28

## Context

Claude Managed Agents (beta header `managed-agents-2026-04-01`) is Anthropic's hosted agent harness: agents,
environments (a cloud sandbox or a self-hosted one), sessions, server-sent events, multiagent threads, outcomes,
memory stores, session budgets and GitHub repository mounts [S1]. The question before 0.7: can one session run a
chargehand node behind `IWorkerRuntime` (ADR 0004), first a read-only answer node and later a writing node, return
messages, usage and cost the run log can use, and leave chargehand's evidence contract (`result/v1`, citations
resolved against a pinned commit) enforced by chargehand?

This is a desk spike: primary documentation only, no live call, because every Managed Agents call bills an API key,
not the owner's Claude Code subscription [S1][S16]. Sources are listed at the end with their retrieval date; a claim
with no source is marked UNKNOWN.

What chargehand needs, from its own code:

- `IWorkerRuntime` (`src/Chargehand/Runtime/IWorkerRuntime.cs`): create, set instruction entries before the first
  prompt, submit, await idle under a client deadline, interrupt, read messages with per-call tokens and tool output,
  pending permissions answered once or rejected, fork before the first message, compact, diff, one-shot generate.
- `WorkerNode` (`src/Chargehand/Nodes/WorkerNode.cs`) polls messages every 5 s, rejects every pending permission
  request, interrupts at the USD cap or token budget, and builds the evidence scope from message ids, tool output
  and the runtime's diff.
- `GitEvidenceResolver` (`src/Chargehand/Verification/GitEvidenceResolver.cs`) resolves a `file` citation with
  local `git show <commit>:<path>` in the request's repository. No network; it does not depend on the runtime.
- ADR 0023: the worker reads a clone of the requested repository at the pinned commit under `worker_root`, built
  from a local source, so the commit need not exist on any remote.

## Findings

### Operation mapping

| `IWorkerRuntime` | Managed Agents | Fit |
|---|---|---|
| `CreateAsync(NodeSpec)` | Hold the spec locally; create the session lazily at the first submit, `POST /v1/sessions` with `agent` as `agent_with_overrides` (`model`, `system`, `tools` replace the base agent's per session, no new agent version) [S2]. One base agent resource serves every preset. | yes |
| model (`NodeSpec.Model`) | `model` override; never clearable; `effort` inside it [S2]. | yes |
| `SetInstructionAsync` | Joined into the `system` override at creation; `system` is fixed for the session's lifetime [S3]. A later `system.message` event exists on listed models [S11]; the port allows entries before the first prompt only, so it is not needed. | yes |
| `SubmitAsync` | First prompt: `initial_events` with one `user.message` (session starts `running`) [S2]; later: `POST /v1/sessions/{id}/events` [S2]. | yes |
| `AwaitIdleAsync` | `GET /v1/sessions/{id}/events/stream`, opened before sending; reconnect = new stream, list history, skip seen ids [S4]. Idle is `session.status_idle` with `stop_reason`: `end_turn`, `requires_action`, `retries_exhausted`, `budget_reached` [S4][S10]; end is `session.status_terminated` or `session.error` [S11]. Map `end_turn` to Succeeded, `budget_reached` to Interrupted, `retries_exhausted`, terminated and error to Failed. | yes |
| `InterruptAsync` | `user.interrupt`; returns when queued, the turn ends with `session.status_idle` whose `stop_reason` is `end_turn`: "there is no stop reason specific to interruption" [S4]. The adapter must remember it interrupted to report Interrupted. | yes, with local state |
| `ReadMessagesAsync` | `GET /v1/sessions/{id}/events`, paginated, `types[]` filter [S4]. `user.message` to User, `agent.message` to Assistant text, `agent.tool_use`/`agent.tool_result` to `ToolOutput`, `agent.thread_context_compacted` to Compaction, `session.status_idle` to Idle [S11]. Per-call tokens from `span.model_request_end.model_usage` [S11]. Event ids (`sevt_…`) become `session_message` locators. | yes; call attribution UNKNOWN (U3) |
| `PendingPermissionsAsync` / `AnswerPermissionAsync` | `requires_action` idle carries `stop_reason.event_ids`; answer each with `user.tool_confirmation`, `result` `allow` or `deny`, optional `deny_message`; the session waits indefinitely [S5]. Matches "once or reject, never saved" (ADR 0006). | yes |
| preset permission rules | Per tool only: `enabled`, and a policy `always_allow`, `always_ask` or `auto` [S5][S6]. No path patterns. A call to a tool not enabled is denied without evaluation [S5]. | partial, see read-only |
| `ForkAsync` | No fork, branch or copy operation: session operations are retrieve, list, update, archive, delete [S3]. Fallback as ADR 0020: a fresh session with the same spec before the first user message. Whether its prefix hits the first session's cache is UNKNOWN (U1, U2). | fallback |
| `CompactAsync` | No client-requested compaction; the harness compacts on its own and reports `agent.thread_context_compacted` [S1][S11]. Steered compaction (ADR 0010) is lost; `CompactAsync` is a no-op. | no |
| `DiffAsync` | Cloud: no exec or diff endpoint; files written to `/mnt/session/outputs/` come back through the Files API [S15]. Self-hosted: the clone is local, so `git diff` as in ADR 0020. Answer nodes need no diff. | cloud: gap; self-hosted: yes |
| `GenerateAsync` | Not a session feature; the Messages API with the same key. | outside Managed Agents |
| usage and cost | `session.usage` event and the session's `usage`: token totals, `list_cost` (whole cents, public list rates, not the contracted price), `active_seconds`, web search counts [S10]. | yes, needs an interface change (below) |
| USD cap | `budget.max_list_cost` at creation, enforced by the platform between model requests, overshoot bounded by one request per thread; the session goes idle with `budget_reached`, not terminated [S10]. Stronger than `WorkerNode`'s 5 s polling watcher. | yes |
| cleanup | `DELETE /v1/sessions/{id}` removes record, events and sandbox; a running session must be interrupted first [S3]. | yes |

C# surface: the docs' C# samples use `client.Beta.Agents.Create`, `client.Beta.Environments.Create`,
`client.Beta.Sessions.Create`, `client.Beta.Sessions.Events.Send` and `client.Beta.Sessions.Events.List`
[S2][S5][S8]; the NuGet package is `Anthropic`, 12.51.0 the latest listed [S17]. A C# method for the event stream is
UNKNOWN (U4); a raw SSE read with `HttpClient` works either way. Rate limits: 300 create and 1,200 read requests per
minute per organization [S11].

### Where the checkout comes from

- **Cloud sandbox.** A `github_repository` resource with `checkout: {"type": "commit", "sha": …}`; the URL must be
  `https://github.com/<owner>/<repo>`, `authorization_token` is required and is not echoed back, the clone lands under
  `/workspace` [S7]. The commit must therefore be on GitHub, and the owner supplies a token. ADR 0023's local-only
  commits (a chat's worktree) cannot be served; the adapter must check the commit is on the remote and refuse
  otherwise. The alternative, uploading files, is capped at 500 per session as read-only copies under
  `/mnt/session/uploads/` [S15]; this repository tracks 214 files, but larger ones do not fit, and archives need
  `bash` to extract [S15], which a read-only node does not get.
- **Self-hosted sandbox.** Only `memory_store` resources are accepted; a `file` or `github_repository` resource is
  rejected with 400 [S9]. The worker runs tools in its `--workdir`, and the file tools are confined to it (plus
  `allowed_roots`), with `write` and `edit` refusing `read_only_roots` [S9][S11]. This fits ADR 0023 exactly: the
  workdir is the clone at the pinned commit under `worker_root`. The worker helper ships for Python, TypeScript and Go;
  "EnvironmentWorker is not currently available in the C# SDK" [S9]. Chargehand would run the `ant beta:worker` CLI
  as a child process or implement the Environments Work endpoints itself (protocol UNKNOWN, U5). The worker downloads
  the agent's skills into `<workdir>/skills/` [S9], which would write into a reused clone if the agent had skills; the
  base agent has none.

### Read-only enforcement for answer nodes

The toolset starts from `default_config.enabled: false` and enables `read`, `glob` and `grep` only; `bash`, `write`,
`edit`, `web_fetch` and `web_search` stay off [S6]. A call to a disabled tool is denied without evaluation [S5]. No
tool left can write or run a process, in either sandbox. Two differences from today's adapters:

1. **Path rules** (`read *.env deny`, `presets/default.yaml`) have no server-side counterpart [S5][S6]. The only
   per-call control is `always_ask` on `read`, `glob` and `grep`, with the adapter evaluating the preset's rules on
   each `agent.tool_use` input and answering at once, inside `AwaitIdleAsync`, rather than surfacing requests to the
   watcher (which rejects everything). Idle time waiting for a confirmation is not billed [S16], but every call costs a
   round trip. On a self-hosted sandbox the worker's own file tools can enforce roots instead [S9].
2. **Trust.** Enforcement moves from a local process chargehand starts to Anthropic's harness. Every `agent.tool_use`
   carries `evaluated_permission` and usually `evaluation` [S5]; the adapter should fail the node on any tool name
   outside the enabled set, as a check on the harness rather than a substitute for it.

`auto` is not usable here: it lets the server run a call it judges safe with no one seeing it first [S5].

### Evidence resolution

Unchanged, and still chargehand's. `file` and `commit` citations resolve against the local repository at the node's
commit; the worker read the same commit, from the GitHub mount (cloud) or the clone (self-hosted). `session_message`
citations resolve against the `sevt_…` ids the adapter returns, `url` against tool output, `diff` against
`DiffAsync`. Outcomes are not a substitute: the grader scores an artifact against a rubric with a model and returns
`satisfied`, `needs_revision`, `max_iterations_reached`, `failed` or `interrupted` [S13]. That is a judgment, not a
resolved reference; at most a later critic. Public text keeps saying "citations checked".

Multiagent threads share one sandbox, filesystem and budget [S12][S10]; chargehand's independent siblings (ADR 0017)
stay separate sessions. Memory stores [S14] duplicate the 0.6 memory category (ADR 0026) and are not used.

### Cost against the Claude Code subscription

Managed Agents bills tokens at model list rates, caching multipliers included, plus $0.08 per session-hour while the
session is `running`; idle, rescheduling and terminated time is free [S16]. It needs an API key [S1]; there is no
subscription path. ADR 0020's subscription mode costs nothing per token within the owner's plan limits. Estimate (not
measured): one default-preset answer node, 300k context tokens of which 90% cache reads, 5k output tokens, on a
Sonnet 5 class model at $2 input, $0.20 cache read, $2.50 cache write and $10 output per MTok [S16], costs roughly
$0.15–0.40 in tokens and under $0.01 in runtime. The preset caps a node at $1.00 anyway.

`list_cost` gives chargehand the runtime's own cost report that ADR 0026 deferred, but `WorkerMessage` has no cost
field; using it needs that deferred interface change. Without it, token counts times the price table still work,
minus the runtime charge. `list_cost` is list price, so it overstates a discounted account [S10].

### Data and retention

Managed Agents "is not currently eligible for Zero Data Retention" or HIPAA BAA coverage; sessions store history,
sandbox state and outputs server-side until deleted [S1][S3]. A self-hosted sandbox keeps execution local, but "tool
inputs and outputs still flow to Anthropic's control plane" [S9], so file contents leave the machine in both modes.
GitHub repositories are cached for faster later sessions [S7]; how long is UNKNOWN (U7). Under D12 (the private-code
track) Managed Agents is not a runtime for private code by default; a profile would opt in per repository, and the
adapter deletes each session after the run.

## Options

1. **Build a cloud adapter now for read-only answer nodes.** Fits the port with three gaps (fork, steered
   compaction, per-path rules), strong budget enforcement, no local agent CLI. Every run costs API money, commits must
   be on GitHub, and it adds nothing to what the subscription-backed Claude Code adapter answers today.
2. **Build a self-hosted adapter.** Keeps ADR 0023's clone and local diff, so it also fits writing nodes; needs a
   worker chargehand cannot import in C# (child `ant` process or an own Work-endpoint client), and still bills the API
   and sends file contents to Anthropic.
3. **Defer; decide at 0.7 planning after a capped paid live check.** 0.7's "sandbox" is where a hosted sandbox
   could pay off; answer nodes already have two runtimes.

## Decision

Proposed: option 3. No adapter code before 0.7. The port fits well enough that it is not the blocker; the blockers
are cost (API-only, the owner runs on a subscription), retention (not ZDR-eligible) and GitHub-only checkouts in
the cloud sandbox. At 0.7 planning, run the live check below; build option 2 only if a writing node needs a sandbox
chargehand does not already have, and option 1 only if a user without an agent CLI asks for it.

Tasks if the decision flips to build (S ≤ 0.5 d, M 1–2 d, L 3–5 d):

| # | task | size |
|---|---|---|
| 1 | `IWorkerRuntime` reports runtime cost (ADR 0026's deferred goal); `WorkerNode.Usage` prefers it over the price table | M |
| 2 | `Chargehand.ManagedAgents`: cloud adapter, lazy create with overrides, event list mapping, SSE idle with reconnect, interrupt state, budget from the node cap, delete after the run | L |
| 3 | Preset to toolset translation (enabled set, `always_ask` for path-ruled tools, in-adapter answers, fail-closed) with unit tests | S |
| 4 | Checkout: map the request's repository to its GitHub URL, refuse a commit the remote lacks, token from the secrets category | S |
| 5 | `RuntimeSelector`: `managed_agents` selectable by name only, never auto-detected (no CLI to probe, spends money) | S |
| 6 | Recorded, scrubbed event fixtures and a contract test for the adapter's event subset | S |
| 7 | Self-hosted variant: `ant beta:worker` per session with `--workdir` = the clone, `read_only_roots` for answer nodes, local diff | M |
| 8 | Writing node on 7: diff from the clone, branch back to the source (ADR 0023's open phase-6 choice) | L |

Paid live check (owner approval required; API key, not the subscription): a script against a public repository,
each session with `budget.max_list_cost` "100" ($1.00), an organization spend limit of $10, at most five sessions,
expected spend $1–3. It must settle U1–U4 and confirm: `github_repository` checkout by commit sha; a disabled tool is
denied and an `always_ask` read round-trips; `user.interrupt` ends in `end_turn`; `budget_reached` at a 50-cent cap;
`list_cost` against token counts times the price table; `DELETE` removes the session.

## Consequences

- No code change; `IWorkerRuntime` keeps two implementations.
- ADR 0026's worker-runtime row gains a candidate beside ACP; its "no silent fallback" rule means Managed Agents is
  never picked without being named.
- If built, a runtime chargehand does not start enforces the read-only rule; the adapter checks tool names on every
  event.

### UNKNOWN, with the source that would settle each

| id | question | settled by |
|---|---|---|
| U1 | A fork or branch operation for a session | the sessions API reference, `https://platform.claude.com/docs/en/api/beta/sessions`; else the live check |
| U2 | Whether a second session with the same agent and system prompt reads the first one's cache | live check: `cache_read_input_tokens` on the second session's first request |
| U3 | Which `span.model_request_end` produced which `agent.message` (per-call attribution) | the events API reference; else the live check |
| U4 | A C# SDK method for the session event stream | the SDK source, `https://github.com/anthropics/anthropic-sdk-csharp` |
| U5 | The Environments Work endpoint protocol for a worker written in C# | `https://platform.claude.com/docs/en/api/beta/environments/work` |
| U6 | Whether self-hosted sessions pay the $0.08 per hour runtime; the pricing page meters `running` time without naming an environment type [S16] | the billing console after a self-hosted run |
| U7 | Retention of the GitHub repository cache and of a deleted session's data | `https://platform.claude.com/docs/en/manage-claude/api-and-data-retention` |
| U8 | Whether the cloud sandbox has `git` and a writable mount for writing nodes | `https://platform.claude.com/docs/en/managed-agents/cloud-sandboxes-reference` |

## Reopen if

0.7 planning starts; the owner approves API spend; Managed Agents leaves beta or becomes ZDR-eligible; or the C#
SDK gains `EnvironmentWorker`.

## Sources

All retrieved 2026-09-28.

| id | source |
|---|---|
| S1 | https://platform.claude.com/docs/en/managed-agents/overview |
| S2 | https://platform.claude.com/docs/en/managed-agents/sessions |
| S3 | https://platform.claude.com/docs/en/managed-agents/session-operations |
| S4 | https://platform.claude.com/docs/en/managed-agents/events-and-streaming |
| S5 | https://platform.claude.com/docs/en/managed-agents/permission-policies |
| S6 | https://platform.claude.com/docs/en/managed-agents/tools |
| S7 | https://platform.claude.com/docs/en/managed-agents/github |
| S8 | https://platform.claude.com/docs/en/managed-agents/environments |
| S9 | https://platform.claude.com/docs/en/managed-agents/self-hosted-sandboxes |
| S10 | https://platform.claude.com/docs/en/managed-agents/budgets |
| S11 | https://platform.claude.com/docs/en/managed-agents/reference |
| S12 | https://platform.claude.com/docs/en/managed-agents/multiagent-orchestration |
| S13 | https://platform.claude.com/docs/en/managed-agents/define-outcomes |
| S14 | https://platform.claude.com/docs/en/managed-agents/memory |
| S15 | https://platform.claude.com/docs/en/managed-agents/files |
| S16 | https://platform.claude.com/docs/en/about-claude/pricing (Managed Agents pricing, model pricing) |
| S17 | https://github.com/anthropics/anthropic-sdk-csharp and https://api.nuget.org/v3-flatcontainer/anthropic/index.json |
