# 0089. Elastic runners: hourly cloud servers start when tasks wait and are deleted when idle

- Status: proposed
- Date: 2026-10-29

## Context

Driven sessions are bound by memory, not by the profile's counts. Admission ([ADR 0045](0045-governed-sessions.md)) starts a
session only when `MemAvailable` less `reserve_mb` covers its request: the 90th percentile of the repository's recent peaks.
Measured on the maintainer's server on 2026-10-29 (`learning.jsonl`, 122 tasks since 2026-10-13): peaks of 976 MiB median,
1455 MiB at p90 and 2335 MiB at most. With `reserve_mb` 1024 a session needs about 2.5 GiB free. That server is a 32 GB
cloud machine shared with about 140 other containers; it had 2.3 GiB available, so no new session was admitted at all,
whatever `max_parallel_total` said. Freeing memory there found about 4 GiB, which is two sessions.

The work is bursty: intake starts a batch when an issue is labelled, and a batch wants several sessions for an hour or two,
then nothing. A second permanent machine would sit idle most of the day; a laptop runner ([ADR 0058](0058-several-runners.md))
already exists but the maintainer's laptop has no memory to spare.

[ADR 0058](0058-several-runners.md) already does most of what a remote machine needs: `driven.runners` with several entries,
`workspace: bundle` (the server sends a git bundle, the runner needs no shared disk), per-runner `forwards`, draining, a
runner that is away for up to 10 minutes, and placement by free memory. What it lacks, from the code:

- **The pool is fixed at start.** `RunnerPool` builds its `_states` once from the profile's list
  (`src/Modules/Chargehand.Modules.Runners/Api/RunnerPool.cs`, constructor); no runner can join or leave while the server
  runs.
- **A runner that leaves the profile fails its tasks** ("A task whose runner is no longer in the profile fails with that
  reason", docs/guide/runners.md, Restarts). A machine that is deleted on purpose must never hold a task.
- **Nothing creates or pays for a machine.** No code talks to a cloud API, and nothing bounds what machines cost.

Hetzner Cloud, the provider the maintainer's server runs on, bills cloud servers by the hour, has a REST API with
`user_data` (cloud-init), labels, firewalls and private networks, and refuses a create with an error when a type has no
capacity. Prices read from Hetzner's public price API on 2026-10-29 (EUR, with IPv4, Nuremberg/Falkenstein):

| Type | vCPU (dedicated) | RAM | Per hour | Per month |
|---|---:|---:|---:|---:|
| CCX13 | 2 | 8 GB | 0.0697 | 43.49 |
| CCX23 | 4 | 16 GB | 0.1386 | 86.49 |
| CCX33 | 8 | 32 GB | 0.2227 | 138.99 |
| CCX43 | 16 | 64 GB | 0.4431 | 276.49 |
| CCX53 | 32 | 128 GB | 0.8558 | 533.99 |

The same day every shared-vCPU type (CPX, CX, CAX) showed "currently unavailable" on Hetzner's site. So a create can fail
for capacity, and the design must handle it rather than assume a type exists.

At p90 a CCX33 holds about 11 sessions by memory (32 GB less 2 GB for the OS and the runner, less `reserve_mb`, over
2.5 GiB) and about 12 by load at `max_load_per_cpu` 1.5. Run 3 hours a day, it costs about 0.67 EUR a day, against
139 EUR a month kept on.

## Prior art

- Query: "How do CI or agent-runner systems start ephemeral cloud machines when jobs are queued and delete them when idle,
  with a cost cap?"
- Coverage: miss (the base held nothing that matched).
- Facts: none yet. Two chargehand research runs were sent to fill it, on `philips-labs/terraform-aws-github-runner`
  (3355ae0, scale-up on queued jobs, scale-down of idle runners on AWS) and `Cyclenerd/hcloud-github-runner` (a7f862a, one
  Hetzner Cloud server per job); their results go to `prior_art_ingest` and their fact ids are added here before this ADR is
  accepted.

## Options

1. **A bigger permanent server.** Simplest, no code. A 256 GB refurbished dedicated server costs about 124 EUR a month
   (Hetzner auction, 2026-10-29) with a 2014 CPU; a 64 GB cloud CCX43 costs 276 EUR a month. Paid every hour whether a
   task runs or not, and the services and the sessions still share one machine.
2. **Kubernetes with a cluster autoscaler** (the Hetzner provider exists). Scales nodes from pending pods. It needs the
   whole runner model (containers per session, per-batch networks, egress gateway, test services) rewritten as pods and
   network policies, and a control plane that itself costs memory. Far larger than the gap.
3. **Provision per task**: one server per session, deleted when the session ends. No sharing, so no admission, but each
   task pays a boot (1–3 minutes plus image pulls) and a whole billed hour, and the batch network and its egress container
   are per batch, not per task.
4. **An elastic pool of runners on hourly cloud servers.** The server creates a runner server when tasks wait for a runner,
   adds it to `RunnerPool` as a `bundle` runner, and drains and deletes it when it has been idle; a ledger bounds count and
   money, and a sweep deletes any labelled server the ledger does not know. Reuses everything of ADR 0058.

## Decision

Option 4. The provider sits behind an interface; Hetzner Cloud is the first implementation.

1. **Profile.** `driven.elastic` (absent: nothing changes):

   ```json
   "elastic": {
     "provider": "hetzner",
     "api_token_secret": "hcloud-token",
     "pool": "main",
     "types": ["ccx33", "ccx23"],
     "locations": ["nbg1", "fsn1"],
     "network": "runners",
     "max_servers": 2,
     "max_eur_per_day": 5,
     "max_eur_per_month": 60,
     "wait_seconds": 60,
     "idle_minutes": 10,
     "max_hours": 8,
     "runner": { "max_sessions": 12, "reserve_mb": 1024, "weight": 1 }
   }
   ```

   `types` and `locations` are tried in order. `pool` names this server's servers among others in the same cloud
   project. The money caps are required, like `api_fallback_daily_usd`: there is no unbounded mode. The values above are an
   example; the profile sets them.
2. **When a server is created.** Every placement round `RunnerPool` already computes why each runner passed a task over.
   The elastic pool reads it: when at least one task has waited `wait_seconds` and every runner passed it over for memory,
   load or `max_sessions` (not for draining or being away), and no elastic server is being created, it creates one. The
   type is the first in `types` whose sessions by memory cover the waiting tasks, else the largest. A first task that
   chargehand would have started on an idle host anyway (ADR 0045) does not count as waiting.
3. **Caps before a create.** A create needs: fewer than `max_servers` servers in the ledger; today's spend plus the
   server's price for `max_hours` within `max_eur_per_day`; the month's spend plus the same within `max_eur_per_month`.
   Spend is hours billed (each started hour, from the provider's created time) times the price the provider returned for
   that type and location at create. The ledger is a JSON file next to `api-fallback.json`, written before the API call
   (state `creating`) and after it, so a crash between the two leaves a record the sweep resolves. A refused create says
   which cap.
4. **Capacity.** A create refused for capacity or an unavailable type or location tries the next location, then the next
   type. When all fail, the pool waits 10 minutes before it tries again and says so once in a `session_progress` event; the
   tasks keep waiting as they do today.
5. **The machine.** A stock image with a container engine, `user_data` that installs nothing from the network but the
   pinned runner image digest and the session image digests of `driven.images` (pulled at boot), and starts
   `chargehand runner` with `--listen <private address>:4310`, `--allowed-hosts` the server's private address, the profile's
   `--images`, `--egress-image`, `--forwards`, `--fallback-host` and `--max-containers`, and `--max-bundle-mb`. The server
   generates a fresh runner key for each server and passes it in `user_data`; it is never logged, and it dies with the
   server. The server is attached to `network` (a private network that the chargehand server's host is also on) and to a
   firewall that allows inbound 4310 only from the server's private address and nothing from the internet. The runner is
   reached at its private address; `forwards` point at the server's private address.
6. **Joining the pool.** `RunnerPool` gains `Add(PoolSite)` and `Retire(name)`. A created server is added as a runner
   named `<pool>-<n>` with `workspace: bundle`, the platform of its type, and `elastic.runner`'s limits, once its
   `GET /host` answers. Until then it is `starting` and holds no task. A server that does not answer within 10 minutes of
   create is deleted and counted as spent.
7. **Leaving.** An elastic runner with no session of this server for `idle_minutes`, and within 10 minutes of the end of
   its current billed hour, is set draining, then deleted once it is empty; a server older than `max_hours` is drained the
   same way regardless. `Retire` removes it from placement and keeps it known for tasks that continue on it ("A task stays
   on its runner"): it is not deleted while any task placed on it has not finished. A task of a deleted server can only be
   one whose runner died, and it fails `container_unavailable` as a vanished runner does today.
8. **Sweep.** At server start and every 5 minutes the pool lists the provider's servers with label
   `chargehand-pool=<pool>`. One the ledger does not know, or one the ledger marked deleted, is deleted. One the ledger
   knows but the provider does not is closed in the ledger. Restart: elastic runners in the ledger that still exist are added
   back to `RunnerPool` before batches resume, so a resumed task finds its runner.
9. **Doctor and console.** `chargehand doctor` checks the token item, that the network and firewall exist, and the month's
   spend against the cap. The console's runners view shows elastic runners with their state (`starting`, `ready`,
   `draining`), age, billed hours and spend.
10. **Module.** `src/Modules/Chargehand.Modules.Elastic`, public types only in its `.Api` namespace: `ICloudServers`
    (create, delete, list by label, price) with `HetznerCloudServers` over `HttpClient` and source-generated JSON (Native
    AOT), `ElasticLedger`, and `ElasticPool`, which drives `RunnerPool` through `Add` and `Retire`. A second provider is a
    second `ICloudServers`.

## Acceptance checks

Each is a test that fails before the change and passes after it, against a fake provider API built from Hetzner's
documented responses (no network in tests), except the last.

- `ElasticPoolTests.A_task_that_waits_past_wait_seconds_for_memory_creates_one_server_of_the_first_type`
- `ElasticPoolTests.A_task_passed_over_only_for_a_draining_runner_creates_nothing`
- `ElasticPoolTests.Two_waiting_rounds_while_a_server_is_starting_create_one_server`
- `ElasticPoolTests.A_create_past_the_daily_or_monthly_cap_is_refused_and_says_which`
- `ElasticPoolTests.A_capacity_refusal_tries_the_next_location_then_the_next_type`
- `ElasticPoolTests.A_server_that_never_answers_is_deleted_after_ten_minutes_and_counted_as_spent`
- `ElasticPoolTests.An_idle_server_is_drained_and_deleted_before_its_billed_hour_ends`
- `ElasticPoolTests.A_server_with_an_unfinished_task_is_not_deleted`
- `ElasticPoolTests.A_server_past_max_hours_is_drained_and_deleted`
- `ElasticSweepTests.A_labelled_server_the_ledger_does_not_know_is_deleted`
- `ElasticSweepTests.A_creating_record_with_no_server_is_closed`
- `ElasticSweepTests.After_a_restart_the_ledgers_live_servers_rejoin_the_pool_before_batches_resume`
- `RunnerPoolTests.A_runner_added_at_run_time_takes_a_waiting_task`
- `RunnerPoolTests.A_retired_runner_takes_no_new_task_but_keeps_its_running_one`
- `HetznerCloudServersTests.Create_sends_labels_network_firewall_and_user_data_and_never_logs_the_runner_key`
- `ProfileTests.Elastic_without_money_caps_is_refused`
- Live, `scripts/elastic-live-check.sh` against a real project: one synthetic driven task on a profile whose only runner
  is elastic; it passes when the task's pull request is recorded, the server is deleted within `idle_minutes` plus one
  billed hour, the label lists no server afterwards, and the ledger's spend is at most 0.50 EUR.

## Consequences

- Sessions no longer compete with the services on the server's own host; that host stays a runner and is preferred while
  it has room (placement by free memory and `weight`, as today).
- More sessions at once use a subscription's rate window faster. A batch on a subscription still stops or falls back on
  its limit ([ADR 0088](0088-driven-batches-fall-back-to-the-api-key-inside-the-egress-gateway.md)); elastic runners make
  that limit, not memory, the usual bound.
- Every session on an elastic runner pays a bundle upload and the first one on a server pays the image pulls (about
  1.6 GB for the mise image). The live check records boot-to-ready time.
- The cloud token can create and delete servers in its project, and `user_data` (with the runner key) is readable through
  the same token. Use a project that holds only the runner servers.
- The OAuth token or API key that a batch's egress container holds now sits on a machine that lives a few hours. It never
  touches disk there, as on any runner.

## Reopen if

- Boot-to-ready exceeds 5 minutes in the live check: build a snapshot with the images preloaded.
- A capacity refusal leaves tasks waiting for more than an hour on 3 days of a month: add a second provider.
- An elastic server is found billing with no ledger record (the sweep missed it).
- Sessions per server by memory differ from the estimate by more than 30%.
