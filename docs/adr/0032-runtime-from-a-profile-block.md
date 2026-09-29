# 0032. A profile's only runtime block names its runtime

- Status: accepted
- Date: 2026-09-29

## Context

Before ADR 0026 the runtime came from the profile's blocks: a `claude_code` block meant Claude Code, otherwise the
`opencode` block was required (`Program.cs` before #41). ADR 0026 replaced that with `RuntimeSelector`: the profile's
`runtime` field or `CHARGEHAND_RUNTIME` names one, otherwise `PATH` is probed for a known agent CLI.

A profile written before 0.4 has a block and no `runtime` field, so it now falls through to the probe. Seen
2026-09-29: a `serve` login agent on macOS, whose launchd `PATH` holds neither CLI, crash-looped with
`runtime_unavailable` after an update, although its profile has a complete `opencode` block. On a machine with both
CLIs on `PATH`, the same profile fails `runtime_ambiguous`. The 0.4 changelog's migration note covers `secret_store`
only.

## Options

1. Keep ADR 0026 as written and add a migration line: every existing profile needs a `runtime` field. Explicit, but
   breaks each deployment once, for a choice the profile already states.
2. The profile's only runtime block names the runtime, after the `runtime` field and `CHARGEHAND_RUNTIME` and before
   the probe. With both blocks or none, the probe decides as before.
3. Restore the old order (a `claude_code` block wins over `opencode`). Brings back the silent priority ADR 0026
   removed.

## Decision

Option 2. `RuntimeSelector.Select` takes the runtimes the profile has a block for (`Profile.RuntimeBlocks`) and
returns the one when there is exactly one. Order: `runtime` field, `CHARGEHAND_RUNTIME`, the only block, `PATH`.

## Consequences

- Profiles written before 0.4 keep their runtime with no migration, under launchd or any other minimal `PATH`.
- A block is configuration for a runtime, so writing one is taken as choosing it; `CHARGEHAND_RUNTIME` still
  overrides it for a one-off run on the other runtime.
- A profile with both blocks names neither: the probe decides, and two CLIs on `PATH` is `runtime_ambiguous`.
  Before 0.4 such a profile ran Claude Code silently.

## Reopen if

A runtime block gains settings that make sense without selecting that runtime (shared defaults, for example), so
that having a block no longer means choosing it.
