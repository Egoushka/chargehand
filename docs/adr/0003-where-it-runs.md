# 0003. Where it runs

- Status: accepted
- Date: 2026-09-26

## Context

Candidates: the owner's laptop, or headless on the owner's server. The orchestrator drives its own OpenCode
server (ADR 0004), which runs tools against local checkouts.

## Evidence (phase 2 spike)

- Per session: create 0.2–0.5 s; first model call time-to-first-byte median 2.9 s (p90 10.6 s) against 1.55 s
  for later calls, because the masking proxy scans a system prompt it has never seen (each contains the session
  ID). None of this depends on where the orchestrator runs.
- The laptop was heavily loaded (load average ≈ 13 on 8 cores) and has dropped remote bridges under load before;
  the server had ~10 GiB free memory. OpenCode on the server was not tested.
- **Skill leak:** OpenCode discovers skills by walking up from a session's directory, so a worker checkout under
  the user's home picked up 27 personal skills (8.6 KB) into every system prompt. Isolating `HOME` and the XDG
  directories did not stop it; a worker directory outside the home did.

## Decision

v0 and v1 run on the laptop, loopback only. Worker checkouts and worktrees live **outside the OpenCode user's
home** (profile `worker_root`). Headless deployment stays on the roadmap's "later" list.

## Consequences

The launcher must verify that `worker_root` is not under `$HOME`. Runs compete with interactive work for CPU.

## Reopen if

A phase 3 benchmark run fails or times out because of laptop load, or callers need the orchestrator while the
laptop is off (the phone/GUI path).
