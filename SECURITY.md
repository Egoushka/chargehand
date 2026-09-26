# Security policy

## Reporting a vulnerability

Use GitHub private vulnerability reporting on this repository (Security tab → "Report a
vulnerability"). Don't open a public issue. Expect an acknowledgement within 7 days.

## Scope notes

chargehand drives an OpenCode server over HTTP. Some OpenCode responses (`/api/model*`,
`/api/provider*`, `/api/config*`) can include provider API keys in plaintext; the project never
logs or records these bodies, and fixtures are scrubbed before they are committed. Report any
path where a key, session content or a private identifier can leak into logs, traces or files.

## Supported versions

Pre-alpha: only the latest commit on `main`.
