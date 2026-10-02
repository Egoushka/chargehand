# A client of your own: chargehand as the engine under a GUI

- Status: draft for the maintainer's review. The direction (a GUI that replaces Claude Code as the daily tool, with room for the maintainer's own features) was stated by the maintainer on 2026-10-02 and is recorded, not reopened. Every decision below is the proposer's.
- Date: 2026-10-02

## Goal

After 1.0, chargehand is the engine behind a client the maintainer uses every day for AI-assisted work, in place of Claude Code's own terminal and desktop app, and into which the maintainer can add features no vendor client has. The client is thin: it shows and steers sessions. Everything a feature needs (runs, evidence, memory, spend, isolation) is a read or a call on the chargehand server.

Done when: (1) a person starts, watches, steers, approves and cancels a session from the client, on a laptop and on a phone; (2) the same client drives sessions on at least two agent runtimes without a code path per runtime in the client; (3) a feature the maintainer wanted and no vendor client has (decision 6) is in daily use; (4) the maintainer has not opened Claude Code's own UI for a month.

## Where it stands

Evidence: commands and files at `a92cb67` (main), checked 2026-10-02 unless a link is given.

1. **The server already has the read side a client needs.** `GET /v1/runs` (state, cost, branch, pull request), `GET /v1/runs/{id}`, `GET /v1/runs/{id}/events`, cancel, halt and resume (`docs/guide/server.md`, "Runs and the run log"; ADR 0039). It has no way to send a message into a running session or to answer a tool-approval prompt: a run is a one-shot request.
2. **Sessions are headless and one-shot.** A driven session is `claude -p` in a container with a goal and a stop condition (ADR 0039). A person cannot join it. Interactive use needs a second session mode, not a change to the driven one.
3. **The runtime interface exists for read-only research and for driven sessions**, with Claude Code and OpenCode adapters (ADR 0020). It does not yet speak a standard session protocol.
4. **The agent client protocol (ACP) is the standard for this seam.** It is JSON-RPC 2.0 between a client and a coding agent; its registry lists about 50 agents and Claude Code joins through an adapter ([overview](https://www.philschmid.de/acp-overview), [VS Code client](https://github.com/formulahendry/vscode-acp)). Not verified here: the protocol's coverage of tool-approval prompts, session resume and image input for each agent. UNKNOWN until Task 1.
5. **The space is crowded.** Open-source desktop clients over CLI agents already exist: Emdash (parallel worktrees, 34 providers), 1Code, AgentGUI (browser client over ACP), OpenCode's own desktop app ([survey](https://github.com/arach/awesome-agent-clients)). A generic "chat over agents" client is not a reason for chargehand to build one.
6. **What chargehand has that those do not** (each is shipped and tested): citations resolved against the pinned commit and a support check on every claim; signed results; a sandboxed, containerised writing path whose branch chargehand verifies itself; MCP memory with retention only of resolved claims; per-run token and dollar caps; a kill switch.
7. **Subscription terms for use through a third-party client are unread.** `docs/guide/driven.md` already lists this for unattended parallel use. An interactive client on the maintainer's own login has the same open question. UNKNOWN; it decides whether the client runs on an API key.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | What "replace Claude Code" means | Replace the **client**, not the agent. The client talks to Claude Code, OpenCode and others through ACP. chargehand still does not write an own agent loop or model gateway (ROADMAP "Not planned" stands). Reopen only if Task 1 shows ACP cannot carry what the client needs (approvals, resume, edits shown as diffs). |
| 2 | Where the client lives | A web client served by `chargehand serve` first, reachable over the maintainer's private network and from a phone. A native shell (Tauri) wraps the same web client later, only if a browser tab proves not enough. One client codebase, not two. |
| 3 | The seam | A new **session** resource beside runs: `POST /v1/sessions`, `GET /v1/sessions/{id}/events` (server-sent events), `POST /v1/sessions/{id}/messages`, `POST /v1/sessions/{id}/approvals/{call}`, `DELETE /v1/sessions/{id}`. Additive: no change to `request/v1` or `result/v1`. The server speaks ACP to the runtime and the client speaks only this API. |
| 4 | Isolation | An interactive session runs in the driven container (ADR 0039) when it writes, in the read-only research path when it does not. The client shows which. No new isolation mechanism. |
| 5 | Result of a session | Ending a session produces a `result/v1` with checked claims, like any run, so a session's findings are as checkable as a research answer. This is the part no vendor client offers and is the reason to build this. |
| 6 | The first own feature | Inline evidence: every claim in the transcript shows its resolved citation and support verdict, and an unsupported claim is marked. It needs only data chargehand already produces. Memory recall in the client (MCP memory is shipped) is the second. |
| 7 | What is not built | A chat model picker of its own, a prompt library, a plugin store, an editor, a terminal emulator. If the maintainer needs a file view or a terminal, link out. |
| 8 | Order against unattended tracker-to-pull-request | The client first. Unattended pickup needs the driven wiring (0.9 era) and a trusted credential path; the client needs neither, and it is what the maintainer will use daily. Pickup is a mode of the same session resource (a session with no person). |

## Tasks (each one pull request)

1. **Spike: ACP coverage.** Drive Claude Code and OpenCode through ACP from a script: start, stream, send a message, approve and deny a tool call, resume, cancel. Record per agent what works. Gate for decision 1.
2. **Session resource and event stream** (decision 3) with a fake runtime, schema and route guards, additive only.
3. **ACP runtime adapter** behind the existing runtime interface; one agent first.
4. **Web client, read side:** the run and session list, a live transcript, cost and caps. No input yet.
5. **Web client, steering:** send, approve, deny, cancel; works at phone width.
6. **Second runtime** through the same adapter, with no client change (done-when 2).
7. **Inline evidence** (decision 6).
8. **Session result** on end (decision 5).
9. **Daily-use trial:** a month of use, a written list of what was missing.

Tasks 1 to 3 can start before 1.0; the session routes join the 1.0 route-table snapshot (`docs/plans/2026-09-30-stability-1.0.md`, Tasks 2 to 4) if they land first.

## Risks

- **Scope.** A client is open-ended. Mitigation: decision 7, and every task ends in something the maintainer can use that week.
- **A crowded space.** Mitigation: decision 5 and 6 are the differentiator; if inline evidence is not noticeably useful in the trial, stop and use an existing client.
- **ACP gaps.** Mitigation: Task 1 first; the fallback is the runtime's own streaming interface per agent, which costs a code path per runtime.
- **Terms of use for a subscription through a third-party client.** UNKNOWN. Mitigation: run on an API key with the existing caps until read.

## Out of scope

An own agent loop or model gateway, a native desktop app before the web client has proved itself, multi-user accounts, a hosted service.
