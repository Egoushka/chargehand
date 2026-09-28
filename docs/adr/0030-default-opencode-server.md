# 0030. A default OpenCode server when the profile has no opencode block

- Status: accepted
- Date: 2026-09-28

## Context

ADR 0026 made the profile optional and let a detected Claude Code run with no `claude_code` block. OpenCode did not
follow: `Connect()` in `src/Chargehand.Cli/Program.cs` threw `runtime_unavailable` when the runtime was OpenCode and the
profile had no `opencode` block, because chargehand connected to a server and never started one. The user had to run
`scripts/opencode-serve.sh` and write the block (url, password secret, version) by hand. Owner's decision, 2026-09-28:
start OpenCode with no profile block as a default; the user can configure it later.

What a server needs is already settled: its own `HOME` and XDG directories, loopback only, a password in
`OPENCODE_SERVER_PASSWORD`, autoupdate off (ADR 0004, `scripts/opencode-serve.sh`); worker checkouts outside the home
(ADR 0003, unchanged). Checked by hand against OpenCode 2.0.18 on `PATH`: the server starts and answers `/api/info` in
about 200 ms and stops on dispose.

## Options

**Model and provider configuration of the started server**
1. Copy `profiles/opencode.example.json` or a profile-named config into the server's state. That file names a gateway
   and placeholder models; with nothing filled in, the server would fail on a provider it cannot reach.
2. Copy the user's own OpenCode config and `auth.json`. Brings their agents, skills and plugins into every worker,
   which ADR 0003 and ADR 0004 isolate against.
3. **Chosen.** Write a minimal config only when the state has none: `autoupdate` off, `share` disabled, the `title`
   agent disabled (ADR 0004). Providers come from OpenCode's own detection, i.e. the provider environment variables
   the server inherits. The user edits that file, or signs in with the state's XDG directories, to go further; it is
   never overwritten.

**Port**
1. A fixed port (the script's 4296). Collides with a server the user already runs.
2. **Chosen.** A free loopback port found just before start. Another process can take it in between; that shows as
   the server exiting before it answers.

## Decision

When the runtime is OpenCode (profile, `CHARGEHAND_RUNTIME`, or `opencode` alone on `PATH`) and the profile has no
`opencode` block, `run`, `serve` and `mcp` start `OpenCodeServerProcess` (`src/Chargehand.OpenCode`):

- `opencode serve --hostname 127.0.0.1 --port <free>` from `PATH`, working directory and `HOME` in
  `chargehand/opencode/home` under the per-user data directory (next to the default run log, ADR 0027), XDG
  directories in `chargehand/opencode/xdg`, `OPENCODE_DISABLE_AUTOUPDATE=1`.
- A random 32-byte password, hex, in `OPENCODE_SERVER_PASSWORD`; it lives only in the two processes.
- Stdin closed at once and stdout and stderr read by chargehand, never passed through, so `chargehand mcp` keeps its
  protocol streams (as for every child since #51). The last output line goes into the error when the server exits
  before answering.
- Ready when `/api/info` answers, polled every 100 ms for up to 30 s. Then the usual version check against
  `OpenCodeWorkerRuntime.PinnedVersion` (2.0.16, ADR 0004).
- Killed with its process tree when chargehand exits (`ProcessExit`, which also runs on SIGTERM and after Ctrl-C).

An explicit `opencode` block always wins: chargehand connects to that URL and starts nothing.

## Consequences

OpenCode now runs with no profile file, like Claude Code. A PATH `opencode` of another version fails the pin with
`runtime_version_mismatch`; the fix is the pinned version or an `opencode` block. Parallel chargehand processes each
start a server on the same state directory. A SIGKILLed chargehand leaves its server running. With no provider
variables set, the server starts but the first model call fails with OpenCode's own error.

## Reopen if

Parallel servers on one state directory corrupt or lock its database, orphaned servers pile up after killed runs, or
the free-port race shows up in practice.
