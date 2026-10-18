---
title: "Install with Docker Compose"
description: "One compose file runs the chargehand server, and with a profile switch the runner behind a docker socket proxy: prerequisites, init, .env, up, doctor, a first run and a first driven batch."
order: 3
section: "Get started"
---

`deploy/compose/` runs chargehand from its release images on one Docker host. It needs no .NET SDK and nothing from this repository but the files in that folder. It has two modes, picked by the profile you copy and by one compose switch:

| mode | starts | profile template | reaches the engine |
|---|---|---|---|
| **Core** | the server | `profile.core.json` | never: no runner, no socket, no mount of it |
| **Core + driven** | the server, the runner, a docker socket proxy | `profile.driven.json` | the runner, through the proxy ([ADR 0039](../adr/0039-driven-writing-sessions.md)) |

Core answers read-only questions about a commit over HTTP and [MCP](mcp.md). Driven adds [writing sessions](driven.md): one container per task, each ending in a draft pull request. Start with Core; the driven files stay unused until you pass `--profile driven`.

Files in `deploy/compose/`: `compose.yaml`, `.env.example`, `profile.core.json`, `profile.driven.json`, `pin-digests.sh`.

## Prerequisites

- Docker Engine with Compose v2.20 or later (`docker compose version`). `docker compose config -q` is the quick validity check.
- One model credential: an Anthropic API key, or a subscription token from `claude setup-token`. A container has no interactive login.
- A git repository with an `https` origin to ask about. The server clones from a checkout under `repository_roots`, never from your working tree.
- For driven mode, also: a Linux host (the work directory is a bind mount whose host path the engine must resolve; Docker Desktop and OrbStack work only with a shared path), and a push credential, such as a fine-grained GitHub token for that repository with contents and pull requests write, that cannot push the default branch.

## 1. Get the bundle and pick a mode

```bash
git clone https://github.com/Egoushka/chargehand && cd chargehand/deploy/compose
cp .env.example .env
cp profile.core.json profile.json        # Core; for driven: profile.driven.json
```

`profile.json` and `.env` are gitignored; the profile holds secret item names, never values ([server guide](server.md#the-bearer-key)). Both templates set `runtime` to `claude_code`, the one runtime in the image, and map the preset model placeholders to `anthropic/sonnet` and `anthropic/haiku`.

## 2. Fill `.env`

```bash
openssl rand -hex 32     # one per key below
```

| variable | value |
|---|---|
| `CHARGEHAND_API_KEY` | bearer key for every route. Required; compose refuses to start without it |
| `ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN` | the worker's credential. Exactly one. An API key bills per token; the token uses your subscription |
| `CHARGEHAND_IMAGE` | server and runner image, a release tag. Defaults to 0.10.0 |

Driven mode adds `CHARGEHAND_RUNNER_KEY`, `CHARGEHAND_PUSH_TOKEN`, `CHARGEHAND_SESSION_IMAGE` and `CHARGEHAND_WORK_DIR` (see step 3). Each variable is documented in `.env.example`. The compose file hands each service only what it reads: the runner never gets the model credential.

**Pin by digest.** A tag can move; a digest cannot. After choosing a [release](https://github.com/Egoushka/chargehand/releases):

```bash
./pin-digests.sh 0.10.0
```

It rewrites `CHARGEHAND_IMAGE` to `tag@sha256:...`, sets `CHARGEHAND_SESSION_IMAGE`, and replaces the session image in `profile.json`. The runner starts only images given by digest, so in driven mode the session image must be pinned and the value in `.env` and in `driven.images` must be the same; the script keeps them so. Run it again for each upgrade.

## 3. Repositories, and the driven directory

The server reads repositories under `/srv/chargehand/repos`, a named volume by default. A one-shot `prepare` service gives the volume to the image's user (uid 1654) before the server starts, and git refuses a checkout owned by anyone else, so clone as that user:

```bash
docker compose run --rm --no-deps --entrypoint git chargehand \
  clone https://github.com/<owner>/<repo>.git /srv/chargehand/repos/<repo>
```

A private repository needs a fetch credential the clone can use; none is built in. To use a directory you manage instead, set `CHARGEHAND_REPOS_DIR` to its absolute path; `prepare` takes the directory itself, not what is in it, so `chown -R 1654:1654` an existing checkout. A request pins a commit of that checkout, and keeping it fetched is yours to do (`git fetch` the same way).

**Driven mode only.** The server makes each task's checkout under `worker_root`, and the runner hands that path to the engine to mount, so the directory must exist at the same path on the host. Set it in `.env`; compose mounts it at that path and `prepare` hands it to uid 1654:

```bash
echo 'CHARGEHAND_WORK_DIR=/srv/chargehand/work' >> .env
```

With a different path, set `CHARGEHAND_WORK_DIR` to it and make `worker_root` in `profile.json` the same. `repository_roots` must also cover `<worker_root>/.checkouts`; the driven template lists `worker_root` for that. Then set `CHARGEHAND_RUNNER_KEY` and `CHARGEHAND_PUSH_TOKEN` in `.env`, and add your ecosystem's package hosts in step 4.

## 4. Run `chargehand init` against your repository

`init` reads the repository's marker files and prints the ecosystems, the proposed test command (`context.verify`) and the package hosts a driven session needs. It runs in the image, against the checkout from step 3; it writes the starter profile to a throwaway path and needs no network:

```bash
docker compose run --rm --no-deps chargehand init /srv/chargehand/repos/<repo> --profile /tmp/init.json
```

Copy from its output: the test command, which you send as `context.verify` in each driven request, and the hosts it lists as "added to `driven.network.allow`", which go into `profile.json` under `driven.network.allow` (`doctor` fails the setup until they are there). Leave the rest of your `profile.json` as it is: the starter profile's paths are the container's, which the template already sets. `init` and `doctor` ship from 0.9.0 on.

## 5. Bring it up

```bash
docker compose up -d                       # Core
docker compose --profile driven up -d      # Core + driven
docker compose ps                          # every service healthy
```

The server needs a model credential to start: without one it exits with `claude is not signed in and no Claude Code credential is set`. The server publishes port 4300 on `127.0.0.1` only. To reach it from other machines, put a VPN or a reverse proxy with TLS in front, add its host name to `http.allowed_hosts`, and keep the bearer key ([ADR 0024](../adr/0024-server-on-a-private-network-and-release-images.md)). Compose keeps three networks: `chargehand-outside` (a route out, for the model API and git), and two `internal` ones (server to runner, runner to proxy) with none. The server and the runner drop all capabilities, and every service but the proxy cannot gain privileges; the run log and batch state live in the `chargehand-runs` volume.

## 6. Check it with `doctor`

```bash
docker compose exec chargehand dotnet /app/bin/Chargehand.Cli.dll doctor
```

One `ok`, `warn` or `fail` line per check, with the fix. A credential is checked for presence, not for validity. In driven mode it also checks that the runner answers, that every session image is pinned, that the secrets exist, and that `driven.network.allow` covers your repository's package hosts. It exits 1 on a `fail`. Fix, edit `profile.json`, `docker compose restart chargehand`, and run it again.

## 7. First run

Ask a read-only question. `path` is the container's path, and the commit must exist in that checkout:

```bash
commit=$(docker compose run --rm --no-deps -T --entrypoint git chargehand -C /srv/chargehand/repos/<repo> rev-parse HEAD)
set -a; . ./.env; set +a                 # CHARGEHAND_API_KEY into this shell
curl -s -X POST http://127.0.0.1:4300/v1/runs \
  -H "Authorization: Bearer $CHARGEHAND_API_KEY" \
  -H "Prefer: wait=60" \
  -d '{"contract_version":"request/v1","text":"Which module starts the application, and what does it set up first? Cite the code.",
       "context":{"interactive":false,"preset":"default","repository":{"path":"/srv/chargehand/repos/<repo>","commit":"'"$commit"'"}}}'
```

A `200` carries `result/v1` ([how to read it](quickstart.md#read-the-result)); a `202` means the run goes on, and `GET /v1/runs/<id>` returns it when it ends. For an MCP client, point it at `http://127.0.0.1:4300/v1/mcp` with the same bearer header ([MCP guide](mcp.md)).

## 8. First driven batch

Driven mode only. Start with one task and a token cap; the batch is the smoke test of the whole path (server, runner, proxy, session image, push credential). The request is [`request/v1`](reference.md) with a `driven` block:

```bash
curl -s -X POST http://127.0.0.1:4300/v1/runs \
  -H "Authorization: Bearer $CHARGEHAND_API_KEY" \
  -H "Prefer: wait=5" -D - \
  -d '{"contract_version":"request/v1","text":"first batch",
       "context":{"interactive":false,"preset":"driven","repository":{"path":"/srv/chargehand/repos/<repo>","commit":"'"$commit"'"},
                  "verify":["<the test command from init>"]},
       "driven":{"tasks":[{"id":"t1","goal":"Add one small test for <something that exists>"}],"max_parallel":1,"max_tokens_total":1000000}}'
```

The `Location` header names the batch. Follow it with `docker compose exec -e CHARGEHAND_URL=http://127.0.0.1:4300 chargehand dotnet /app/bin/Chargehand.Cli.dll watch <run-id>`; it prints one line per event and ends with the draft pull request link. chargehand pushes `chargehand/<run>` and opens a draft pull request; it merges nothing unless the profile opts in ([merging by policy](driven.md#merging-by-policy)). `verify` is a JSON argument vector, run in a clean container; a test that writes scripts to `/tmp` needs the `TMPDIR` form from [the driven guide](driven.md#how-a-task-is-isolated). A batch that ends `failed` names the stage in its result; `docker compose logs runner` and `docker compose logs docker-proxy` show the engine side.

## Upgrade, stop, remove

```bash
./pin-digests.sh <new version>     # then read the changelog for that version
docker compose pull && docker compose up -d          # add --profile driven in driven mode
docker compose down                # stops; volumes (run log, repositories) stay
docker compose down -v             # also deletes them
```

## What the socket proxy does and does not do

The runner reaches the engine through `tecnativa/docker-socket-proxy`, pinned by digest and allowed five API sections: containers, images, volumes, networks and POST. It filters by endpoint, not by request body, so it cannot stop a privileged or bind-mounting container create. The runner's fixed container template is the gate for that, and the proxy removes only the rest of the engine's API (for example swarm, secrets, exec and the system endpoints). The socket stays root-equivalent on a rootful engine: anyone who holds the runner key or the proxy can start listed images on the host. Both sit on internal networks with no published port. On a rootless engine set `DOCKER_SOCKET` to its socket. The full cost is in the [server guide](server.md#runner-driven-sessions).

## If something fails

| symptom | cause and fix |
|---|---|
| compose says `CHARGEHAND_API_KEY` is missing | fill it in `.env` |
| the server exits at start and complains about the profile | `profile.json` is missing next to `compose.yaml` (Docker then creates a directory of that name): remove it and copy a template |
| `repository_not_allowed` | the request's `path` is not under `repository_roots`; use the container path `/srv/chargehand/repos/<repo>` |
| `dubious ownership` or `permission denied` on a checkout | the checkout is not owned by uid 1654: clone it as in step 3, or `chown -R 1654:1654` it |
| the server restarts in a loop | read `docker compose logs chargehand`: no model credential in `.env`, or an invalid profile |
| the runner exits at start with a usage line | a missing `CHARGEHAND_RUNNER_KEY`, or a session image given as a tag |
| `runtime_version_mismatch` | the profile's `claude_code.version` differs from the image's CLI: leave the block out (then 2.1.283 or newer of major 2 is taken), or write the version as `>=2.1.283` |
| a batch ends `container_unavailable` | the engine is unreachable from the runner: `docker compose logs docker-proxy`; check `DOCKER_SOCKET` |

## What has been checked

On a local engine with an image built from the commit that added this bundle: `docker compose config -q` in both modes (CI repeats it), Core and Core + driven healthy, `init` and `doctor` run in the container (`doctor` failed on a missing package host until it was added, then passed), a clone into the repository volume as uid 1654, and, through the runner and the proxy, the creation and removal of a network, a volume and an egress container joined to `chargehand-outside`. The proxy answered 403 for the swarm, secrets, exec and info endpoints. Not checked: a request that reaches a model, and a driven batch with a session container (the maintainers' [measured deployments](driven.md#not-done-yet) used the same proxy sections). The release images of 0.8.8 predate `init` and `doctor`.
