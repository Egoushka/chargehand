# 0086. One core, doors in and doors out

- Status: accepted
- Date: 2026-10-28
- Spec: [Cycle A](../specs/2026-10-28-cycle-a-conversations-and-acp-design.md)

## Context

The maintainer wants chargehand to be the default tool for coding work. In practice it is used less than it could be.

On the maintainer's server from 2026-10-14 to 2026-10-28 there were 500 runs:
- 244 pull request reviews, which are automated;
- 119 code questions, bursty: 25 on one day, 1 on others;
- 64 driven batches, of which 19 completed, with none on 6 of the 14 days.

Work a person starts is rare because every door is a one-shot run. A question needs an MCP call from inside another
agent; a task needs an issue label or a JSON batch. Nothing can be messaged or approved while it runs.

On the other side, chargehand drives two agents through two full adapters of about 800 to 960 lines each. Meanwhile
the agent client protocol (ACP) reaches 43 agents in its registry, and editors (Zed, JetBrains, Neovim, Emacs, VS Code
extensions) host ACP agents. The ACP spike of 2026-10-27 showed approvals, diffs, resume, cancel and mid-turn messages
working for Claude Code through ACP.

## Options

1. **Add doors and agents one at a time** on the run model: a plugin per editor, an adapter per agent. Cheap for the
   first, and the cost grows with each one. None of them can steer a running task.
2. **One core with two seams.** A steerable conversation is the seam for doors in. ACP is the seam for doors out, and
   later for chargehand as an agent. The verifier, governor, caps, memory, signing and inbox stay as the shared core.
3. **Replace the agent** with an own agent loop. This is ruled out by the roadmap ("Not planned: an own agent loop")
   and by the Stage 6 spec, decision 1, which the spike upheld.

## Decision

Option 2.

- **Doors in** reach one resource: the conversation (`/v1/conversations`). A one-shot run stays as it is this cycle.
  Turning it into a conversation with no person in it is decided when that work starts.
- **Doors out** go through ACP by default. Claude Code keeps its native path for driven containers (`claude -p`), and
  OpenCode keeps HTTP until its ACP routes a model. Any other agent comes through the ACP runtime, with its launch
  command pinned in a reviewed list.
- **Naming.** The steerable resource is a conversation, because "session" already means a driven session (ADR 0039).
  ACP's `session` is internal to the ACP runtime.
- **The .NET ACP library** is a reusable project with no chargehand reference, so that it can be extracted and offered
  as a .NET SDK.
- **Credentials** through ACP are API keys or the model gateway only. Subscription logins stay with the unmodified
  `claude` binary.
- **Ordering.** The Stage 6 Tasks 1 to 3 (the spike, the steerable resource and the ACP runtime) move ahead of the 1.0
  stability work in the roadmap.

## Consequences

- A new door costs a thin adapter onto conversations, not a new run path. Cycle B's chargehand-as-an-ACP-agent and
  Cycle C's A2A server are such adapters.
- A new agent costs one entry in `acp-agents.json` and a live check, not a new runtime project. The other side of
  that: chargehand depends on each agent's ACP being right, and on its usage reporting for caps. An agent that reports
  no usage cannot run under a dollar cap unless the profile allows it explicitly.
- The 1.0 route-table snapshot (stability plan, Tasks 2 to 4) includes the conversation routes if they land first, as
  the Stage 6 spec already allowed.
- Two names, conversation and session, must stay distinct in code, docs and error codes.

## Reopen if

- After Cycle B, the share of the maintainer's coding work that starts through chargehand does not rise.
- ACP proves unable to carry approvals or usage for the agents in use.
- An official .NET ACP SDK appears that the library should defer to.

## Amendment, 2026-10-28: where the ACP runtime lives until `IWorkerRuntime` moves

Decision 8 puts the ACP worker runtime in `Chargehand.Runtimes.Acp`. `IWorkerRuntime` still lives in `src/Chargehand`,
which the skeleton rules forbid new projects to reference (ADR 0060), so the runtime is split: `Chargehand.Runtimes.Acp`
holds what needs no old type (the pinned list, the profile block, the read-only rules, the mode choice, usage
accounting, the agent's environment), and `src/Chargehand.Acp`, an old-style project beside `Chargehand.ClaudeCode`,
holds the `IWorkerRuntime` adapter. The adapter folds into the new project when the interface is ported, and
`IConversationRuntime` (Task 5) is implemented in the new project from the start. The sandbox is also an old type, so the
runtime starts an agent through `IAcpLauncher`, a port in the new project that `Chargehand.Acp` implements with the sandbox
(`AcpSandboxLauncher`). The checkout and the key are likewise passed in as delegates, and the port `IConversationRuntime`
is on the upward-port allowlist (a higher project implements a lower module's port).

Three small changes in the old core let a runtime like this one exist: `ISandbox.Wrap` returns the confined command line
for a child the caller keeps open, `IWorkerRuntime.ReportsUsage` and `.ResearchOnly` let the orchestrator refuse
`runtime_uncapped` and a writing preset before anything is spent, and `WorkerNode` falls back to a fresh session when a
runtime throws `NotSupportedException` from `ForkAsync` or `CompactAsync`, which the decision already assumed.

The first probe of the pinned agents corrected the list: Codex offers `api-key` and `chat-gpt` (a login), not the
method name first written down, and Gemini CLI offers `oauth-personal` (a login) next to `gemini-api-key`. The pinned
`auth_method` is the only one the runtime ever calls.
