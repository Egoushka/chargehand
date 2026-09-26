# 0016. Secrets and server access

- Status: accepted
- Date: 2026-09-26

## Evidence (phase 2 spike)

- The OpenCode V2 server authenticates with HTTP Basic, user `opencode`, password from
  `OPENCODE_SERVER_PASSWORD` (or `OPENCODE_PASSWORD`); without one it generates a random password. The spec
  declares no security scheme.
- Some OpenCode responses (`/api/model*`, `/api/provider*`, `/api/config*`) can return provider API keys in
  plaintext. The desktop app's background server stores its password in a plaintext file.
- Provider config can reference a key file (`{file:…}`), so the gateway key never enters the orchestrator.

## Decision

- Secrets live in the OS secret store (macOS Keychain on the laptop); the gitignored profile names the items
  (`profiles/profile.schema.json`: `password_secret`, `public_key_secret`, `secret_key_secret`).
- A launcher reads them at start: the OpenCode password into both the OpenCode server's and the orchestrator's
  environment; the tracing key pair into the orchestrator only; the gateway key into a 0600 file referenced by
  the OpenCode server's provider config only.
- The OpenCode server binds 127.0.0.1 with a generated password per install; the orchestrator never calls the
  key-bearing routes, and never logs request or response bodies of OpenCode or the gateway.

## Reopen if

The orchestrator moves to a server; then that server's secret store replaces the Keychain.
