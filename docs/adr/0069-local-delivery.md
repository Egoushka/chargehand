# 0069. Local delivery

- Status: proposed; implemented with this ADR, not yet reviewed by the maintainer
- Date: 2026-10-18

## Context

Some repositories must not leave the machine they sit on. The `code` preset already never pushes (ADR 0035), but its
branch stays in a run clone under `worker_root` and the caller fetches it by hand; driven sessions push and open pull
requests by design (ADR 0039), and the integrator, CI fixer and intake all act through a forge (ADR 0046, 0051, 0067).
The profile can say where a request may point (`repository_roots`, ADR 0023) but not what may leave. Memory sources
and the prompt enhancer receive the request text whatever the repository is (ADR 0034, 0040). Design spec:
`docs/specs/2026-10-18-local-delivery-design.md`.

## Options

1. A flag on the request only. A caller that forgets it pushes code that must stay.
2. A server-wide switch. Simple, but one profile then cannot hold both a shared checkout that may push and a private one.
3. A request field plus a profile list of directory roots that force the mode; the profile wins. A guard at each path that pushes.

## Decision

Option 3.

- `request/v1` `context.delivery`: `local` or `push`, optional. Profile `delivery.local_only`: directories whose repositories
  are always local. A `push` request for such a repository fails with the new error code `local_only`.
- A local `code` run lands its branch in the caller's repository with a plain, unforced `git fetch` from the run clone into
  a new `refs/heads/chargehand/<run>/<node>`. It changes no working-tree file, the index, `HEAD` or any existing ref. This
  amends ADR 0023's "the source repository is only read" for local delivery, and for nothing else.
- Driven sessions are refused for a local run. The hand-over, integrator, CI fixer and the intake profile keys each carry
  their own guard as well, so a fault in one check does not push.
- A local run recalls from and retains into only memory sources marked `local_ok`, and skips the prompt enhancer.

## Consequences

- A repository under a `local_only` root can use read-only and `code` runs on a server, and cannot be pushed from it,
  by request or by a driven batch, even by mistake.
- A person who wants driven sessions on a private repository has no way yet. That is a follow-up (fetch the bundle into the
  caller's repository instead of pushing).
- The guard keeps work code off a forge from a server that sees it. It does not keep a server from seeing it: that rests
  on the server's `repository_roots`.
- `local_ok` defaults to false, so an existing profile's memory behaves as before for `push` runs and a local run uses no
  memory until a source is cleared.
- Landing writes objects and a ref into the caller's repository's `.git`; a read-only mount of it makes the run fail
  `delivery_failed` with the branch kept in the run clone.

## Reopen if

Driven sessions are wanted on repositories that must stay local; a second delivery mode is wanted (patch file, bundle);
telemetry needs to honour the mode.
