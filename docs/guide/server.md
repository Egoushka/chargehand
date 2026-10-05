---
title: "Run the HTTP server"
description: "chargehand serve: the profile's http block, the bearer key, binding and allowed hosts, each route with its status codes, and the release image."
order: 4
section: "Guides"
---

`chargehand serve` hosts the HTTP interface and the MCP endpoint in one process. Runs started through either land in the same run log as CLI runs.

## Start it

`serve` needs an `http` block in the profile; without one it exits with `profile has no http settings (ADR 0018)`. The block from `profiles/example.json`:

```json
{
  "schema": "profile/v1",
  "http": { "port": 4300, "api_key_secret": "chargehand-api-key" }
}
```

Keep your `models` map in the same profile ([Quickstart](quickstart.md#write-a-profile)). Then:

```bash
export CHARGEHAND_API_KEY=<key>
dotnet run --project src/Chargehand.Cli -- serve
```

The server prints its address on stderr. It connects to the worker runtime once at startup, so a runtime that is down stops it; a runtime that goes down later fails each run with `runtime_unavailable` ([ADR 0022](../adr/0022-error-codes-in-result-v1.md)).

## The bearer key

Every route, `/v1/mcp` included, requires `Authorization: Bearer <key>`. That holds on loopback too, because any local process can reach the port. A missing or wrong key gets `401`.

The key is the secret item that `http.api_key_secret` names, resolved through the profile's `secrets` sources in order. With no `secrets` list, chargehand reads the item from the environment, upper-cased with `-` turned into `_`: `chargehand-api-key` reads `CHARGEHAND_API_KEY`. A `command` source runs an argv template instead, such as the macOS Keychain lookup in `profiles/example.json`. The `secrets` list replaces `secret_store` ([changelog 0.4.0](../../CHANGELOG.md#040---2026-09-29)).

## Binding and allowed hosts

| field | default | meaning |
|---|---|---|
| `http.port` | `4300` | TCP port |
| `http.listen` | `127.0.0.1` | IP address to bind |
| `http.allowed_hosts` | none | host names accepted besides `localhost` and `127.0.0.1` |
| `http.api_key_secret` | required | the secret item that holds the bearer key |

The server compares each request's `Host` header with `localhost`, `127.0.0.1` and `allowed_hosts`, and answers any other host with `400`. That check guards against DNS rebinding. When `listen` is not loopback, the server refuses to start unless `allowed_hosts` names at least one host, because beyond loopback the check must name the server. Bind a private network only ([ADR 0024](../adr/0024-server-on-a-private-network-and-release-images.md)). The server sends no CORS headers and caps request bodies at 1 MB.

With no `repository_roots`, `serve` allows repositories under `worker_root` only, and `context.repository.path` is a path on the server's machine ([ADR 0028](../adr/0028-default-repository-roots.md)).

## Routes

| route | answers |
|---|---|
| `POST /v1/runs` | `200` with `result/v1` when the run finishes within the wait; `202` with `run-status/v1` when it does not; `400` with `errors` for a bad request; `429` when 10 runs are unfinished |
| `GET /v1/runs?status=&since=&limit=` | `200` with `{"runs": [run-summary/v1, ...]}`, newest first: state, preset, cost, branch and pull request link when the result has them, and the error code of a failed run. `status` is a run state (`queued`, `running`, `lost`, `completed`, `needs_input`, `failed`, `denied`), `since` an ISO 8601 time, `limit` 1 to 500 (default 50); `400` for a bad value |
| `POST /v1/runs/{id}/cancel` | `202` when this server holds the run and starts cancelling it; the run ends `failed` with error code `cancelled`. `404` for an unknown id, `409` when the run has finished or another process runs it |
| `POST /v1/halt` | `200` with the number of runs it cancelled; from then on `POST /v1/runs` and the MCP tool are refused with `503` until `POST /v1/resume` |
| `POST /v1/resume` | `200`; accepts runs again |
| `GET /v1/runs/{id}` | `202` with `run-status/v1` while queued or running; `200` with `result/v1` once finished; `410` when the process that ran it ended first; `404` for an unknown id |
| `GET /v1/runs/{id}/events` | server-sent events for the run; `404` for an unknown id |
| `/v1/mcp` | MCP over Streamable HTTP, see [the MCP page](mcp.md) |

Every route answers `401` without the key and `400` for a host it does not accept. [ServerTests](../../tests/Chargehand.Tests/ServerTests.cs) covers `200`, `202`, `400`, `401`, `404`, `410` and `429` on these routes.

### POST /v1/runs

The body is `request/v1`. Before a run starts, the server checks that the body is JSON, that it matches the schema, that the preset exists, and that each caller block's sha256 matches its text. A failure is `400` with `{"errors": [...]}`, and no run starts. Otherwise the run starts and the response waits up to `Prefer: wait=N` seconds (default 10, at most 60). Both `200` and `202` carry `Location: /v1/runs/{id}`.

```bash
curl -s -X POST http://127.0.0.1:4300/v1/runs \
  -H "Authorization: Bearer $CHARGEHAND_API_KEY" \
  -H "Prefer: wait=30" \
  --data-binary @request.json
```

`samples/ContentEngineCall` is a complete caller in C#, built on `Chargehand.Contracts` alone.

### GET /v1/runs/{id}

A `202` body is `run-status/v1` with status `queued` or `running`; a run that another process is running also reads `running`. A `410` body has status `lost`: the process that ran it ended before it finished. A finished run comes back as `result/v1`.

### GET /v1/runs/{id}/events

A `text/event-stream` with the events `accepted`, `started`, `intake`, `node_started`, `node_finished` and `run_finished`. Each event's data is `run-status/v1`. The `intake` event carries the action intake chose, the action that runs and the number of nodes; `node_finished` carries the node's id, status and cost; `run_finished` carries the result. For a run this process does not hold (finished, lost, or running in another process), the stream has one event, read from the run log.

## Run tokens (driven sessions)

A driven session ([ADR 0039](../adr/0039-driven-writing-sessions.md)) calls chargehand back for research and review. It does not get the server's key: the server issues it a **run token**, `chr1.<payload>.<signature>`, an HMAC over its claims with a key only that process holds. The token opens `POST /v1/runs`, `GET /v1/runs/{id}` and the MCP tool, and nothing else (every other route is `403`). Through it a session can start only the `default` and `review` presets, only on the repository and commit of its task, never a batch, and only while the task's token and dollar caps last; each call's budget is cut to what is left. The runs it starts record the task's run as their parent and count against its caps. They run on a separate gate of two, not the one-at-a-time gate that the batch holds while it waits for them. A token expires, and it dies with the server.

## Runs and the run log

Runs outlive the request that started them and execute one at a time; the server holds at most 10 unfinished runs. The server and the CLI share the run log when they use the same file: `chargehand show` reads runs started over HTTP and MCP, and `GET /v1/runs/{id}` reads runs started from the CLI. A server restart leaves unfinished runs `lost`, and their callers resend ([ADR 0018](../adr/0018-callable-interface-http-mcp-run-store.md)).

## Container image

A tag `v<Version>` publishes `ghcr.io/<owner>/chargehand:<Version>` through [release.yml](../../.github/workflows/release.yml), which first checks the tag against `Directory.Build.props` and the changelog. There is no `latest` tag and no image per commit: a deployment pins an exact version ([ADR 0024](../adr/0024-server-on-a-private-network-and-release-images.md)).

```bash
docker run -p <private-ip>:4300:4300 -v <dir-with-profile.json>:/config:ro -e CHARGEHAND_API_KEY=... \
  ghcr.io/<owner>/chargehand:<Version>
```

The [Dockerfile](../../Dockerfile) builds:

- `chargehand serve` with the Claude Code CLI at a pinned version: build argument `CLAUDE_CODE_VERSION`, default 2.1.283. A profile's `claude_code.version` must match it.
- git for worker clones, with `prompts/` and `presets/` under `/app`.
- A non-root user, working directory `/app`, `CHARGEHAND_PROFILE=/config/profile.json`, port 4300.
- `/app/runs` for the run log, and `/srv/chargehand/work` for worker clones (the `worker_root` that `profiles/example.json` uses).

OpenCode is not in the image; an OpenCode profile points `opencode.url` at a server that runs next to the container. Pass the worker's credential in the container's environment: with no `claude_code` block, chargehand looks for `ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN`. The server's repositories live under its `repository_roots`, and keeping them fetched is the deployment's job.

No test in the repository runs the image ([capabilities](capabilities.md#server-container-image)).

## Runner (driven sessions)

Driven sessions ([ADR 0039](../adr/0039-driven-writing-sessions.md)) start one container per task. The server never holds the container engine's socket: a separate **runner** does, and exposes a narrow API in front of it.

```bash
export CHARGEHAND_RUNNER_KEY=<key>
chargehand runner --listen <private-ip>:4310 --allowed-hosts <runner-name> \
  --images <name>@sha256:<digest> --egress-image <name>@sha256:<digest>
```

The runner refuses to listen off loopback without `--allowed-hosts`, needs the key in the environment (never an argument), and starts only images on `--images`, by digest. Every container it starts comes from one fixed template: non-root, read-only root filesystem, no capabilities, no Docker socket, no host mount, one internal network. A request names a run, a listed image and numbers under the runner's ceilings, nothing else, and a body with any other field is refused. It signals, reads, removes and joins only containers that carry chargehand's `chargehand.run` label, so it is safe on an engine other stacks share. `GET /count` and `POST /kill-all` work without the server's records.

The runner's own compromise is the security cost of this design: whoever holds its key can start listed images and remove labelled containers, and the process holds the engine's socket, which is root-equivalent on a rootful engine. Run it on a private network, behind its key, and prefer a rootless engine where you can.

### Deploying it with containers

Two services from the release image (`chargehand`), and the session image (`chargehand-session`, the same version) that the runner starts:

- **The server** (`chargehand serve`) has no engine socket. `driven.runner` names the runner's URL and the secret item for its key; `driven.images` lists the session image by digest; `driven.push_secret` names the credential that pushes `chargehand/<run>` and opens draft pull requests.
- **The runner** (`chargehand runner`, the same image: it carries the docker CLI) mounts the engine's socket and joins the server's network. Run it as the image's user with the socket's group added (`group_add`), listen on its container name and give `--allowed-hosts` that name. `--images` and `--egress-image` take the session image as `name@sha256:<digest>`, **with no tag** (a tagged reference is refused and the runner exits with its usage line). Two policy flags are required for a batch to run: `--source-roots` names the directory the server makes its checkouts in (`worker_root`; with none, no workspace is prepared), and `--outside-networks` names the network the server sits on (with none, the egress container cannot join it). A third, `--forwards`, lists the forwards an egress container may carry, as `listen-port=host:port` (`4300=chargehand:4300`: the port a session calls and the server's name and port on that network, the same name and port as `driven.network.mcp_forward`). The server names its forward in each request and the runner starts only a listed one; with none listed the egress container has no forward and a session's call back to the server is refused.
- **Paths must mean the same to the engine as to the server.** The server makes each task's checkout under `worker_root` and the runner hands that path to the engine to mount, so `worker_root` has to be a bind mount whose host path equals its path in the container (a named volume would not resolve, and the engine would mount an empty directory).
- **The callback.** A session calls the server for research and review through its batch's egress container, as `http://chargehand-driven:<port>`: set `driven.network.outside` to the network the server sits on and `driven.network.mcp_forward` to the server's name and port on it (`name:port`), and add `chargehand-driven` to `http.allowed_hosts`. A repository the sessions work on must be a checkout under `repository_roots` with an https `origin`; a private one needs the fetch credential on the host.

Keep `driven.enabled` false until a smoke batch of one task has produced a draft pull request.
