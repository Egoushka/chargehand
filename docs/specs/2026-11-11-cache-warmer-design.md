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
7. **Stores.** pnpm gets `store-dir` and `package-import-method=auto` through the environment the warmer gives its
   child processes (`pnpm_config_store_dir`, `pnpm_config_package_import_method`); the box volume is ext4, which has no
   reflinks, so `auto` hardlinks when the store shares a filesystem with the checkout and copies otherwise (the sandbox binds them as separate mounts). Each repository gets its own store, `<worker_root>/.pnpm-stores/<repo>` (the first segment of the checkout name, one plain name or the step is refused), so a hostile repository can only touch the store its own later checkouts read. NuGet is different (decision 10): the shared folder `<worker_root>/.nuget-packages` is never writable from the sandbox.
8. **Wiring.** The orchestrator (optional `events` log, set when `http.events` is on) announces a checkout it has just cloned through `CheckoutAnnouncer`, best effort. `chargehand serve` runs `CacheWarmer` as a hosted service with the stores under `<worker_root>/.pnpm-stores/` and `<worker_root>/.nuget-packages` (filled only by the promoter), beside the checkouts. With `http.events` off nothing is announced or warmed.
9. **Trust.** The warmer runs package-manager commands in checkouts of repositories under `repository_roots`. pnpm and npm run with `--ignore-scripts` (no lifecycle script) and `--prefer-offline` (a cold store is filled from the registry, a warm one is not touched on the network; plain `--offline` fails on a cold store). A folder with a `.pnpmfile.cjs` or `.pnpmfile.mjs`, or whose `.npmrc` or `pnpm-workspace.yaml` sets `pnpmfile`, `global-pnpmfile`/`globalPnpmfile` or `configDependencies`, is not warmed, because pnpm runs that code even with `--ignore-scripts`. Symlinked folders are not followed, and a checkout name must be at least `<repo>/<commit>` deep and resolve, symlinks included, inside the checkouts root. **Sandbox.** Every step runs through `ISandbox` from `SandboxSelector.Select(profile.Sandbox)`, the same launcher sessions and verification use (ADR 0035; `SandboxedStepRunner`, no second profile). Inside it the root is read-only, credential locations are hidden, and the only writable places are the checkout, a private temp directory and the repository's own pnpm store (`.pnpm-stores/<repo>` under `worker_root`, passed as `SandboxSpec.WritablePaths`, which sessions leave empty) and, for the restore step only, its own per-run staging folder; the shared NuGet folder is not writable. The environment is the sandbox allowlist, the profile's `sandbox.env`, proxy and CA variables, and the cache settings; no token of the daemon reaches it. The network is the profile's one boolean `sandbox.network` (off by default): a cold fill needs it on. `package-import-method` is `auto`, since a bind mount can make a hardlink across mounts fail.
   Mitigations in code: pnpm also runs with `--ignore-pnpmfile` (a pnpmfile can be redirected from `.npmrc` or `pnpm-workspace.yaml`, so the name check alone is not enough) and `pnpm_config_manage_package_manager_versions=false` in the environment the runner always sets (the command-line flag does not work: pnpm reads `packageManager` before it; the variable also outranks the repository's `.npmrc`, `pnpm-workspace.yaml` and `devEngines`, checked by hand with pnpm 12.18; the `packageManager` field would otherwise make pnpm download and run another pnpm); `dotnet restore` runs with `--disable-build-servers`; every child gets an allowlisted environment, not the daemon's (no tokens, no cloud keys). The promoter's own `dotnet` calls (verify, the extraction restore) run with a 10-minute timeout that kills the process tree, an allowlisted environment and a private temporary `HOME`. An invalid `nuget_trusted_signers` entry stops startup with its position named, instead of being dropped. `Resolve` checks symlinks and the steps run later, a small time-of-check gap: only the orchestrator creates those folders.
   **Remaining risks.** Sessions have no write access to the shared stores, so S10 (a poisoned store) cannot start from a session. A step still runs repository code: `dotnet restore` evaluates the repository's MSBuild files and runs any target hooked into restore, and no flag turns that off (proven with a decoy `Directory.Build.props` hooked into `Restore`, A20: it writes outside the checkout when nothing confines it, and cannot under bubblewrap). It is confined as a session is: it can write only the checkout, its repository's pnpm store (content-addressed, hash-checked) and a throwaway staging folder, so a hostile repo cannot write the shared NuGet cache (decision 10). Packages from private or unsigned feeds are not warmed: the agent restores them itself. The checkout's `obj/project.assets.json` points at the deleted staging folder; the agent's own restore re-resolves from the shared cache. With `sandbox.network` on, the sandbox has no host allowlist, so a step can reach any host, exactly as a session can (proposal, not built: a per-profile host allowlist, for example the registry and nuget.org, enforced by the sandbox's network namespace and a proxy); a repo's `.npmrc` or `NuGet.config` can steer where packages come from. With `sandbox.kind: none` the warmer is switched off (it logs why) rather than run a repository's restore unconfined. The restore target is passed as `./name`, so a file named like an option cannot become one.
10. **NuGet promotion.** NuGet checks signatures only when it extracts a package and trusts a global-packages folder as it finds it, so a writable shared folder is a way to poison every later build. The restore step therefore runs with `NUGET_PACKAGES=<worker_root>/.nuget-staging/<id>` (the only writable NuGet path) and the warmer then, outside the sandbox, fills the shared folder itself (`NuGetPromoter`). The extraction restore is outside the sandbox but never evaluates repository content: its work folder holds only files the promoter wrote itself, and its own empty `Directory.Build.props`/`.targets`/`Directory.Packages.props` are the nearest MSBuild finds, so a decoy one planted above that folder is never imported (A20, proven against real `dotnet`). Only regular `staging/<id>/<ver>/<id>.<ver>.nupkg` files with a plain id and version count; every other file, link or folder in staging is ignored and never copied. The candidates are copied into a private feed, checked with `dotnet nuget verify --certificate-fingerprint` (batch, then one by one on failure) against the trusted signers, and extracted by a `PackageDownload` restore with only that feed as a source and `signatureValidationMode=require`. Unsigned or failing packages are skipped, named in the `chargehand.cache.warmed` step (`skipped`) and never promoted; if the extraction fails nothing is promoted. Staging and the work folder are deleted after every run. Trusted signers are the nuget.org repository certificate (two SHA-256 fingerprints) plus the profile's optional `nuget_trusted_signers` (64-hex SHA-256 fingerprints, added as author signers).

## Acceptance checks (machine-run)

| # | Check | How it runs |
|---|---|---|
| A1 | A dotnet checkout plans one `dotnet restore`; a pnpm one plans the cached install; an npm one `npm ci --prefer-offline`; Playwright adds the browsers check; an empty one plans nothing | `WarmPlanTests` |
| A2 | Both input events turn into a job; other types do not; a `checkout` with `..`, a rooted path or (on Unix) a backslash is refused | `CacheWarmerTests` |
| A3 | A step whose result exists is a hit and is not run; otherwise it runs and is a miss; a non-zero exit is failed and the next step still runs | `CacheWarmerTests` |
| A4 | Each job appends one `chargehand.cache.warmed` event with the checkout, outcome, steps and milliseconds; reading the same trigger twice appends once | `CacheWarmerTests` |
| A5 | The warmer ignores events from before it started and wakes on the log's signal | `CacheWarmerTests` |
| A6 | The module sits in `Skeleton.Layers`, references only lower layers, and exposes public types only in `.Api` | `Chargehand.Architecture.Tests` |
| A9 | A step goes through the sandbox launcher with the checkout, the caches, the profile network and the allowlisted environment; a timeout is a failure | `SandboxedStepRunnerTests` |
| A10 | The sandbox binds extra writable paths and a session without them is unchanged; a timed-out run returns within a few seconds even when an escaped grandchild holds its output open | `SandboxTests` |
| A11 | A new checkout is announced once, a reused one not | `CheckoutAnnounceTests` |
| A12 | The restore step runs with `NUGET_PACKAGES` set to its own staging folder and no write access to the shared NuGet folder | `SandboxedStepRunnerTests` |
| A13 | A staging step gets a fresh folder under the staging root, is promoted only on exit 0, and the folder is deleted on success, failure and a throwing promoter | `CacheWarmerTests` |
| A14 | An unsigned package is skipped and not promoted; a file planted in staging beside a valid package, a symlink or a hostile name never reaches the feed or the shared cache; a failed extraction promotes nothing | `NuGetPromoterTests` |
| A15 | The trusted signers (default repository fingerprints plus the configured ones) reach verify and a require-mode config; real `dotnet nuget verify` rejects an unsigned package | `NuGetPromoterTests` |
| A16 | Each step carries its checkout's repository as its scope; a scoped step gets `pnpm_config_store_dir` and write access for its own repository's store only; a scope that is not one plain name is refused before the sandbox runs | `CacheWarmerTests`, `SandboxedStepRunnerTests` |
| A17 | A pnpmfile named in `.npmrc` or `pnpm-workspace.yaml` (any mention of the key, in any case or form), a `configDependencies` entry or `.pnpmfile.mjs` skips the folder; a hooked workspace root skips every pnpm folder below it; pnpm runs with `--ignore-pnpmfile` | `WarmPlanTests` |
| A18 | A promoter `dotnet` call that outlives its timeout is killed and reported; its environment carries no daemon variable; an invalid signer fails with its position; proxy and CA variables (`HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`, `SSL_CERT_FILE`, `SSL_CERT_DIR`) do reach it | `DotnetProcessTests`, `NuGetPromoterTests` |
| A19 | Every step's environment carries `pnpm_config_manage_package_manager_versions=false`, even when the caller passes `true` | `SandboxedStepRunnerTests` |
| A20 | A decoy `Directory.Build.props` hooked into `Restore` writes outside the checkout when nothing confines the step, and cannot under real bubblewrap; a decoy planted above the promoter's extraction work folder is never imported, under real `dotnet` | `SandboxedStepRunnerTests`, `NuGetPromoterTests` |
| A8 | A new checkout is announced once as `chargehand.worktree.created` with its relative name; a log that fails never stops the run | `CheckoutAnnouncerTests` |
| A7 | The second checkout's install takes at least 20% less than a cold one (below that, the idea is dropped) | measured by hand, numbers below |

## Measured (2026-11-11, the box: ext4 on `/dev/md3`, 4 runs of each)

| Step | Cold (empty store) | Second checkout (store shared) | Change |
|---|---|---|---|
| `pnpm install --frozen-lockfile` (5 dev tools, 54 packages, pnpm 12.18) | 322 to 346 ms | 70 to 72 ms with `hardlink`; 92 to 104 ms with `copy` | -78% (hardlink), -71% (copy) |
| `dotnet restore Chargehand.slnx` | 4.6 to 6.6 s (empty `NUGET_PACKAGES`) | 3.9 to 5.4 s (packages present, `obj` clean) | about -15%, inside the noise |
| Same command as the warmer runs it, `--prefer-offline --frozen-lockfile --ignore-scripts` (2 packages, run through `npx pnpm@12.18.0`, so each time includes about 300 ms of launcher) | 1428 ms (empty store, fetched from the registry) | 479 ms | -66% |

Re-measured 2026-11-12 after the per-repository stores (a dev container, overlay filesystem, 4 runs of each): the warmer's
exact command and environment (`--prefer-offline --frozen-lockfile --ignore-scripts --ignore-pnpmfile`, store
`.pnpm-stores/<repo>`, `package-import-method=auto`, the switch variable), pnpm 12.18 run directly, 131 packages, a project
whose `packageManager` asks for pnpm 9: cold (empty store) 559 to 635 ms, second checkout 143 to 174 ms, about -73%. The
HTTP metadata cache was warm, so the cold figure is a floor. The same loop with only the command-line switch flag showed
no saving at all (about 1.0 to 1.2 s both ways): pnpm had switched to the 9.0.0 the project asked for on every run, which
is how the flag was found not to work.

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
