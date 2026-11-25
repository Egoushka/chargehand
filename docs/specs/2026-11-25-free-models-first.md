# Free models first: a run tries the free models of its task class, then its paid model

- Status: proposed 2026-11-25.
- Decision: [ADR 0103](../adr/0103-free-models-first-then-the-paid-slot.md)
- Builds on: [ADR 0004](../adr/0004-opencode-major-and-runtime-adapter.md) (OpenCode adapter), [ADR 0006](../adr/0006-presets.md)
  (presets), [ADR 0019](../adr/0019-prompt-ci-and-routing-report.md) and [0025](../adr/0025-prompt-ci-on-a-self-hosted-runner.md)
  (Prompt CI, the routing report), [ADR 0093](../adr/0093-runs-on-public-repositories-use-a-free-model-pool.md)
  (repository visibility from GitHub), [ADR 0095](../adr/0095-a-budget-aware-model-router-drains-pools-in-order-and-paces-each-day.md)
  (budget router).
- Contract: the maintainer's workspace decision D-238 (free models graded per task class) and the model list it reads,
  `free-models.toml`. This repository holds a synthetic copy for tests, never the real list.

## Problem

Chargehand's slots (`provider/worker-model`, `provider/critic-model`, `provider/small-model`) all map to paid models, so
every run spends the subscription or API credit even where a free model would do. The maintainer keeps a list of free
models with a data tier and a grade per task class, and wants those tried first.

## What the list says

Each model has a `tier` (`private`: zero retention and no training; `public`: may train), `tasks` (grade `good`, `ok`
or `no` per class `ask`, `review`, `edit`, `plan`, `mechanical`, `critic`) and an order. Rules of the list's router:

1. A repository GitHub reports as public gets the `private` and `public` models; every other repository, including one
   whose visibility cannot be read, gets `private` models only.
2. A model routes for a class only if graded `good` or `ok` for it: `good` first, file order inside a grade. Ungraded and
   `no` never route.
3. Employer work is refused, never sent to a free model.
4. A `free/…` model needs the gateway pool's key, so it drops out where that key is not present.

## Decisions

**D1. The router is ported, the list is mounted read-only.** The router is `ws free route` (Python, in the maintainer's
workspace). The server runs in a container with neither. Calling it would need a new network path from the container
to a developer machine that is often off. So chargehand reads the list from a mounted file (`free_first.models_file`)
and ports the tier, fence and route logic to C#, and tests pin the same refusals. The file is read on every run, so a
changed copy takes effect without a restart. A file that is missing, unreadable or unparsable means no free model: the
paid slot runs.

**D2. The node kind picks the task class.** One run uses one preset, and every node of a run uses one slot, so the
decision is made per run.

| Preset node, by its model placeholder | Class |
|---|---|
| `provider/small-model` (`cheap`, `prior-art`, `draft`) | `mechanical` |
| `provider/critic-model` (a preset's `critic` block) | `critic` |
| `provider/worker-model` in preset `review` | `review` |
| `provider/worker-model` in presets `design`, `split` | `plan` |
| `provider/worker-model`, any other preset that does not write | `ask` |
| any node with `writes: true` (`code`, `driven`) | `edit` |

`edit` runs are not routed in this release: a failed free attempt leaves a half-written working tree, and falling back
needs a clean tree. `edit` stays in the map so the class exists when that is designed. A critic block is read by no code
today (ADR 0006: its critic has no writing node to review yet), so the `critic` class is mapped and tested but no run
reaches it until a critic node exists.

**D3. A second runtime per run.** The profile keeps one server runtime (`claude_code` on the maintainer's deployment).
Free models need OpenCode, so the profile's `opencode` block, which names an OpenCode server by URL and a secret item for
its password, is also read when `runtime` is `claude_code` and `free_first` is set: it is the free engine. A run that
routes to a free model takes its worker runtime from it; every other run is unchanged. The server is not the desktop
app's and holds the providers of the list; chargehand never calls its `/api/provider*`, `/api/config*` or `/api/model*`
(ADR 0004).

**D4. The data rules hold in the server too.** The OpenCode server runs with its own XDG home and
`OPENCODE_DISABLE_CLAUDE_CODE=1`, `OPENCODE_DISABLE_PROJECT_CONFIG=1`, `OPENCODE_CONFIG_PROJECT_DISABLE=1` and no other
`OPENCODE_*`, so it does not send an instruction file from the host to the model. A repository is refused (paid slot) when
its real path has a `work` or `.devbox` segment, or when a symlink inside it leads to one (the walk is bounded: past
its limit the repository is refused). Free-model output is untrusted: it enters the run as a worker's answer and passes
the same citation and schema checks as any other, and nothing in it is executed.

**D5. Fallback.** The attempts of a run are the routed free models in order, then the paid slot. A free attempt falls
through to the next when it ends in `rate_limited`, `provider_unavailable`, a model the server does not know, a worker
that ended failed, or a result that fails the run's own checks (schema or citation check). The paid attempt is last
and its outcome is the run's. A run on a repository that does not qualify, or with no routed model, runs the paid slot
only, as today.

**D6. The run log says which model ran.** `CallRecord.Model` already carries the model of each call. The run record gets
`attempts`: the model, its runtime (`opencode` or `claude_code`), whether it is free and how the attempt ended, in order,
so a fallback is visible. The `free_first` decision (class, tier, models offered, or the reason for none) is recorded
too. Additive: no schema major changes.

## Profile

```json
"free_first": { "models_file": "/config/free-models.toml", "deny_path_segments": ["work", ".devbox"] },
"opencode":   { "url": "http://opencode:4096", "password_secret": "chargehand-opencode-password", "version": "2.4.1" }
```

Absent `free_first`: nothing changes. `free_first` without an `opencode` block is a profile error. Values here are
placeholders; the personal values live in the deployment's profile.

## Acceptance checks (machine-run)

| # | Check | Runs as |
|---|---|---|
| F1 | The synthetic list parses; a malformed list, an unknown tier or an unknown grade fails closed (no model) | `FreeCatalogTests` |
| F2 | Route: `good` before `ok`, file order inside a grade, ungraded and `no` never route, a `free/` model drops out without the pool key | `FreeRouteTests` |
| F3 | Tier: public repository gets both tiers; private, internal, unknown and a failed lookup get `private` only | `FreeRouteTests` |
| F4 | Fence: a path with a `work` segment (also through `..` and a symlinked parent), a `.devbox` segment, a symlink in the repo that leads to one, and a walk past its bound are all refused | `FreeFenceTests` |
| F5 | Class map of D2, every preset in `presets/` | `FreeTaskClassTests` |
| F6 | Attempts: rate limit on the first free model moves to the second, then the paid slot; a failed check moves on; `edit` never routes; no model means paid only | `FreeFirstRunTests` |
| F7 | The run record lists the attempts, and the model of each call is the model that ran | `FreeFirstRunTests` |
| F8 | `scripts/check.sh` (the devbox form in CLAUDE.md) passes | CI |
| L1 | Live: a `cheap` run (small slot) on a personal repository, through the deployed server, has its model call on a free model; the run log shows it | live check |
| L2 | Live: with the first free model made to answer 429, the same run falls back and the log shows both attempts | live check |

## Value hypothesis and cheapest test

Hypothesis: a share of `ask`, `review` and `mechanical` runs can move off the paid slots with no loss in accepted results.
Cheapest test: L1 on one run. Measuring the share needs the grading run of D-238; this spec does not claim it.

## Reopen if

A graded `ok` model's attempts fall back more often than they finish (D-238 stop condition); the list's format changes
major; or `edit` grades appear and a clean-tree retry exists.
