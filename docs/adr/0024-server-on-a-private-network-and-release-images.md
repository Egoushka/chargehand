# 0024. chargehand serve on a private network, and release images

- Status: proposed
- Date: 2026-09-28

## Context

`chargehand serve` binds 127.0.0.1 and accepts only a loopback `Host` (ADR 0018): a page in a local browser could
otherwise reach it through DNS rebinding. An MCP client on another machine cannot reach it, and the server is up only
while the machine that started it is. Prompt CI already runs on a server (ADR 0022).

## Options

1. Keep loopback; tunnel each client to the machine that runs the server.
2. Bind a private-network address, with the `Host` check naming the server, next to the bearer key. Always on.
3. Put the server on the public internet behind the key. One leaked key and anyone runs workers on the deployment's bill.

## Decision

- **Bind:** option 2. `http.listen` sets the address (default `127.0.0.1`); `http.allowed_hosts` adds host names to
  `localhost` and `127.0.0.1`. The server refuses to start when `listen` is not loopback and `allowed_hosts` is
  empty: beyond loopback the `Host` check is the only guard against DNS rebinding, so it must name the server. The
  bearer key stays on every route. The port is published on a private network only.
- **Image:** a `Dockerfile` builds `chargehand serve` with the Claude Code CLI at a pinned version (build argument
  `CLAUDE_CODE_VERSION`, which must match `claude_code.version`), git for worker clones (ADR 0023), `prompts/` and
  `presets/`. It runs as a non-root user from `/app`; the profile mounts at `/config/profile.json` and uses
  `secret_store: env`, since the Keychain is macOS only. OpenCode is not in the image: a Linux build of the pinned
  2.0.16 is unverified (ADR 0022), and an OpenCode profile points `opencode.url` at a server run next to the
  container.
- **Versions:** a tag `v<Version>` builds and pushes `ghcr.io/<owner>/chargehand:<Version>`, and fails when the tag
  and `Directory.Build.props` disagree. No `latest` and no image per commit: a deployment pins an exact version, so
  an upgrade is a reviewed change there.

## Consequences

- MCP clients on the private network reach one always-on server.
- A request's `context.repository.path` is a path on the server. Code that exists only on a client machine
  (uncommitted, unpushed) is out of reach; a local `chargehand serve` stays for that.
- The server's repositories live under its `repository_roots`; keeping them fetched is the deployment's job.
- The subscription or API credential for workers lives in the server's environment, as the runner's does.

## Reopen if

A client outside the private network needs the server; or OpenCode 2.0.16 (or its successor) ships a verified Linux
build worth carrying in the image.
