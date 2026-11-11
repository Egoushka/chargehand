# The cache warmer: restore steps run ahead of the agent, driven by the event log

- Status: accepted 2026-11-11 (owner approved the idea from the efficiency report, area A). Builds on ADR 0090 (the event log).
- Date: 2026-11-11

## The job

A new checkout costs the agent its first minutes: `dotnet restore`, an install of the web dependencies, a check that the
Playwright browsers are there. The warmer does those steps as soon as the log says a checkout exists, so the agent finds
them done. It is a log consumer, like any other: it reads events, does work, writes what it did back as events.

## Decisions

1. **A module, `Chargehand.Modules.Warmer`, layer 3** (it reads the Events module at layer 2). It adds no table and no migration.
2. **Input events.** `chargehand.worktree.created` (the orchestrator, after it clones a checkout) and `chargehand.branch.queued`
   (any producer). Data: `{"checkout": "<name>/<rel>"}`, a path relative to the checkouts root. The envelope check refuses
   absolute paths under `work/`, so no absolute path travels in the log; `..` and rooted values are refused by the warmer too.
3. **Plan by files in the checkout.** `*.slnx`/`*.sln`/`*.csproj` at the top gives `dotnet restore`; `pnpm-lock.yaml` gives
   `pnpm install --prefer-offline --frozen-lockfile --ignore-scripts`; `package-lock.json` gives `npm ci --prefer-offline --ignore-scripts`; a
   dependency on `@playwright/test` gives a browsers check (never a download). The top level and each direct subdirectory are
   looked at, `node_modules` and `.git` excluded.
4. **Hit or miss.** A step is a *hit* when its result is already there (`project.assets.json`, `node_modules/.modules.yaml`,
   `node_modules/.package-lock.json`, a `chromium-*` folder) and is then not run; a *miss* when it ran and exited 0;
   *failed* when it did not. The warmer never fails the run: a failed warm leaves the agent exactly where it would have been.
5. **One at a time.** Jobs run in order, never in parallel; the box is memory-bound.
6. **Output event.** `chargehand.cache.warmed`, source `chargehand/warmer`, id `warm-<seq of the trigger>` (so a replay writes
   nothing new), scope of the trigger, data `{checkout, outcome, ms, steps:[{name, result, ms}]}`.
7. **Shared stores.** pnpm gets `store-dir` and `package-import-method=auto` through the environment the warmer gives its
   child processes (`pnpm_config_store_dir`, `pnpm_config_package_import_method`); the box volume is ext4, which has no
   reflinks, so `auto` hardlinks when the store shares a filesystem with the checkout and copies otherwise (the sandbox binds them as separate mounts). NuGet gets one folder, `NUGET_PACKAGES=<worker_root>/.nuget-packages`, because the sandbox's HOME is a throwaway directory.
8. **Wiring.** The orchestrator (optional `events` log, set when `http.events` is on) announces a checkout it has just cloned through `CheckoutAnnouncer`, best effort. `chargehand serve` runs `CacheWarmer` as a hosted service with the stores at `<worker_root>/.pnpm-store` and `<worker_root>/.nuget-packages`, beside the checkouts. With `http.events` off nothing is announced or warmed.
9. **Trust.** The warmer runs package-manager commands in checkouts of repositories under `repository_roots`. pnpm and npm run with `--ignore-scripts` (no lifecycle script) and `--prefer-offline` (a cold store is filled from the registry, a warm one is not touched on the network; plain `--offline` fails on a cold store). A folder with a `.pnpmfile.cjs` is not warmed, because pnpm runs that file as code even with `--ignore-scripts`. Symlinked folders are not followed, and a checkout name must be at least `<repo>/<commit>` deep and resolve, symlinks included, inside the checkouts root. **Sandbox.** Every step runs through `ISandbox` from `SandboxSelector.Select(profile.Sandbox)`, the same launcher sessions and verification use (ADR 0035; `SandboxedStepRunner`, no second profile). Inside it the root is read-only, credential locations are hidden, and the only writable places are the checkout, a private temp directory and two shared caches (`.pnpm-store`, `.nuget-packages` under `worker_root`, passed as `SandboxSpec.WritablePaths`, which sessions leave empty). The environment is the sandbox allowlist, the profile's `sandbox.env`, proxy and CA variables, and the cache settings; no token of the daemon reaches it. The network is the profile's one boolean `sandbox.network` (off by default): a cold fill needs it on. `package-import-method` is `auto`, since a bind mount can make a hardlink across mounts fail.
   Mitigations in code: pnpm also runs with `--ignore-pnpmfile` (a pnpmfile can be redirected from `.npmrc` or `pnpm-workspace.yaml`, so the name check alone is not enough) and `pnpm_config_manage_package_manager_versions=false` (the `packageManager` field would otherwise make pnpm download and run another pnpm); `dotnet restore` runs with `--disable-build-servers`; every child gets an allowlisted environment, not the daemon's (no tokens, no cloud keys). `Resolve` checks symlinks and the steps run later, a small time-of-check gap: only the orchestrator creates those folders.
   **Remaining risks.** Sessions have no write access to the shared stores, so S10 (a poisoned store) cannot start from a session. A step still runs repository code (`dotnet restore` evaluates MSBuild files) but confined as a session is: it can write only the checkout and the two caches, so a hostile repo can poison those caches or the checkout it was going to be run from. With `sandbox.network` on, the sandbox has no host allowlist, so a step can reach any host, exactly as a session can; a repo's `.npmrc` or `NuGet.config` can steer where packages come from. `sandbox.kind: none` removes the confinement for warming as it does for sessions. A warm is also skipped for a folder with `.pnpmfile.cjs`.

## Acceptance checks (machine-run)

| # | Check | How it runs |
|---|---|---|
| A1 | A dotnet checkout plans one `dotnet restore`; a pnpm one plans the cached install; an npm one `npm ci --prefer-offline`; Playwright adds the browsers check; an empty one plans nothing | `WarmPlanTests` |
| A2 | Both input events turn into a job; other types do not; a `checkout` with `..` or a rooted path is refused | `CacheWarmerTests` |
| A3 | A step whose result exists is a hit and is not run; otherwise it runs and is a miss; a non-zero exit is failed and the next step still runs | `CacheWarmerTests` |
| A4 | Each job appends one `chargehand.cache.warmed` event with the checkout, outcome, steps and milliseconds; reading the same trigger twice appends once | `CacheWarmerTests` |
| A5 | The warmer ignores events from before it started and wakes on the log's signal | `CacheWarmerTests` |
| A6 | The module sits in `Skeleton.Layers`, references only lower layers, and exposes public types only in `.Api` | `Chargehand.Architecture.Tests` |
| A9 | A step goes through the sandbox launcher with the checkout, the caches, the profile network and the allowlisted environment; a timeout is a failure | `SandboxedStepRunnerTests` |
| A10 | The sandbox binds extra writable paths and a session without them is unchanged | `SandboxTests` |
| A11 | A new checkout is announced once, a reused one not | `CheckoutAnnounceTests` |
| A8 | A new checkout is announced once as `chargehand.worktree.created` with its relative name; a log that fails never stops the run | `CheckoutAnnouncerTests` |
| A7 | The second checkout's install takes at least 20% less than a cold one (below that, the idea is dropped) | measured by hand, numbers below |

## Measured (2026-11-11, the box: ext4 on `/dev/md3`, 4 runs of each)

| Step | Cold (empty store) | Second checkout (store shared) | Change |
|---|---|---|---|
| `pnpm install --frozen-lockfile` (5 dev tools, 54 packages, pnpm 12.18) | 322 to 346 ms | 70 to 72 ms with `hardlink`; 92 to 104 ms with `copy` | -78% (hardlink), -71% (copy) |
| `dotnet restore Chargehand.slnx` | 4.6 to 6.6 s (empty `NUGET_PACKAGES`) | 3.9 to 5.4 s (packages present, `obj` clean) | about -15%, inside the noise |
| Same command as the warmer runs it, `--prefer-offline --frozen-lockfile --ignore-scripts` (2 packages, run through `npx pnpm@12.18.0`, so each time includes about 300 ms of launcher) | 1428 ms (empty store, fetched from the registry) | 479 ms | -66% |

Plain `--offline` on an empty store fails (`pnpm` asks for an online install first), which is why the warmer uses `--prefer-offline`.

Reading: pnpm passes the 20% bar by a wide margin, and hardlink beats copy. The network here is fast, so the cold pnpm number
is a floor; a slower link widens the gap. `dotnet restore` does not pass on its own: most of its time is evaluating 40-odd
projects, not downloading, and NuGet was already shared. The warmer's saving for dotnet is that the restore happens before
the agent asks, not that it is faster (the first restore the agent runs is a no-op). The sandboxed run imports with `auto` (hardlink or copy); it was not re-measured because the sandbox tool is not installed on this box, so the pnpm numbers above are the unsandboxed hardlink figures. Stop condition for the whole idea: if
the `chargehand.cache.warmed` events show a pnpm saving under 20% in use, drop the pnpm part.

## Left for later

- Emit `chargehand.branch.queued` from the batch runner once a producer wants it; today only the orchestrator emits
  `chargehand.worktree.created`.
- A tree-hash key and `ms_saved` per lookup (report area A) once there is a second cache beyond restore.
