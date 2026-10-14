# 0048. Shared services for driven sessions: an environment note, package caches, test services, a JDK image

- Status: accepted
- Date: 2026-10-13

## Context

A driven session starts in a container that knows nothing about itself. Four costs follow, each seen in the audit of the batches run so far:

- **The session probes the container.** It runs `which`, `--version` and `ls /usr/bin` to learn what exists; the governor's `environment_probing` signal ([ADR 0045](0045-governed-sessions.md)) counts those turns. Every turn re-reads the whole context, so a probe is not free.
- **Android and Gradle repositories cannot be verified.** The session image has the .NET SDK, Node and Python ([ADR 0039](0039-driven-writing-sessions.md)); it has no JDK and no Gradle, so a task on such a repository can be written but its tests cannot run, and chargehand's own verification in a fresh container fails the same way.
- **Every session downloads its dependencies cold.** The image points NuGet at `/work/.nuget/packages` on the task's own workspace volume (`images/session/Dockerfile`), npm and pip write under the `/home/session` tmpfs, and the volumes are removed with the task. The verifier restores again in a second container. A repository with a large restore pays it twice per task.
- **Repositories with test dependencies cannot run their tests.** A suite that needs a database fails in a container with no route out and no Docker.

The network is closed on purpose: the batch network is `--internal`, with one egress container that enforces the allowlist ([ADR 0039](0039-driven-writing-sessions.md), decision on egress). Anything shared between containers is a path by which one task could affect another, so each piece below states what it can carry.

Facts used. `ContainerTemplate.RunArgs` is the only place a session's `docker run` line is built, and a caller cannot add a mount. The runner builds the same line from a body that names nothing but a run, a listed image and numbers ([ADR 0039](0039-driven-writing-sessions.md)). `RepositoryDetector` (`chargehand init`) already names the ecosystems of a repository from its top-level files and proposes its test command; `VerifyCommand.Resolve` is what the handover runs. The restart sweep ([ADR 0043](0043-driven-batches-survive-a-server-restart.md)) removes every labelled container, network and volume that no resumed batch owns.

## Options

### The environment note

1. Edit the static system prompt (`SessionCli.DefaultPrompt`) to describe the image. It is the same for every repository and cannot say what this repository's test command is; and the prompt is the text Prompt CI and the adherence measurements were taken against, so a per-repository edit would move it.
2. Put the facts in the goal. The research and review calls the skill makes quote the goal; an environment block in it would travel into every worker request and cost tokens there.
3. A note the in-container driver appends to the prompt file next to the static text, built from the image's own manifest and the server's facts about the repository.

### Package caches

1. One cache volume per ecosystem, shared by every repository. Warmest. A session runs the repository's own code (build scripts, `postinstall` hooks) with write access to the cache, and the package managers do not re-check a package already in their cache against the registry (NuGet's global packages folder, Gradle's modules cache, Maven's local repository, pip's cache). A hostile repository, or a session steered by a hostile page it read, could write a changed `foo 1.2.3`; the next repository to restore `foo 1.2.3` runs it. The allowlist limits where bytes may come from, not what is already on disk.
2. One cache volume per ecosystem and repository.
3. One per task. Today's behavior, in effect: nothing is shared.
4. A read-only shared cache filled by a trusted warm-up job. Safe, but it needs a job, a refresh policy and a list of what to warm; the restore of a repository that is not on the list still goes cold.

### Test dependencies

1. A service per task. Isolated; costs a container (and a startup) per task and counts against `--max-containers`.
2. A service per batch and repository, declared in the profile, started on the batch network and removed with it.
3. Let the repository's own tests start containers. Needs a Docker client and socket in the session; refused by [ADR 0039](0039-driven-writing-sessions.md).

### A Gradle and Maven image

1. A second image in `driven.images`, chosen by the repository's ecosystem.
2. Install the JDK and Gradle into the one image. Every session pays the pull and every audit covers it, for repositories that never use it (the session image's README already states that the design does not build images on demand for Java, Go or Rust).
3. Install at session start. The network is closed, and the install would repeat per task.

### The code index

1. Keep reading the server's checkout. The server's own copy of a repository can move under a running batch, and a task must read the commit it was started at.
2. An index built from the pinned clone and mounted read-only.

## Decision

**1. The environment note: option 3.** Before the first turn, the driver appends a short block to the prompt file it already writes (`--append-system-prompt-file`), after `SessionCli.DefaultPrompt`, which is unchanged. It names what the image has and lacks, the network limit, the repository's detected ecosystems, the command chargehand will run on the branch, where the caches are and which services exist. Its two sources:

- The image manifest `/opt/chargehand/environment.json`, written at build time by `chargehand session --probe <file>`, which runs each tool's own `--version` (`dotnet`, `node`, `npm`, `python3`, `pip3`, `git`, `java`, `gradle`, `mvn`, `go`, `cargo`, `docker`) and records the version of those that answer and the names of those that do not. A variant image built from the base runs it again. An image without the file is probed once by the driver at start (a process per tool, 30 s each at most), never by the model. A tool is "absent" only because the probe saw it fail.
- A new optional `environment` member of `task.json` that `TaskRunner` fills from `RepositoryDetector.Scan` of the pinned checkout, `VerifyCommand.Resolve(context.verify, checkout)` (the command the handover runs, and whether it came from the request or from detection), the cache ecosystems and the services. Older session images skip the member (the lenient parse of #218).

The note is not in the goal, so research and review calls do not carry it, and it is not in `SessionCli.DefaultPrompt`, so the adherence measurements of that text stand. It is on whenever driven sessions are: it needs no profile key and no server change.

**2. Package caches: option 2, one volume per ecosystem and repository.** `driven.services.caches` lists ecosystems (`nuget`, `npm`, `pip`, `gradle`, `maven`). For each, the session container and the verifier container of one repository get a named volume `chargehand-cache-<ecosystem>-<12 hex of the repository's hash>`, read-write, at `/cache/<ecosystem>`, and the package manager's variable points at it (`NUGET_PACKAGES`, `npm_config_cache`, `PIP_CACHE_DIR`, `GRADLE_USER_HOME`, `MAVEN_OPTS=-Dmaven.repo.local=...`). The repository is `owner/name` of the checkout's remote, so ssh and https spellings and letter case give one volume.

Why not shared across repositories: see option 1. A cache is trusted by whoever writes it, and a session writes it while running a repository's code; one repository's session must not be able to choose what another repository's build executes. Why not read-only: the verifier and the session both restore, and a restore writes. What remains with per-repository volumes: a task can poison its own repository's cache for later tasks of that repository and for the verifier. That is the code that repository's sessions already run, with the same access, and the result is a draft pull request that a person reviews and CI re-runs; lock files and the package managers' integrity checks (`npm ci` against `package-lock.json`) still apply to what the lock file names. It is a residual risk, not an absent one. Two tasks of one repository running in parallel share a volume; these tools lock their caches.

How the hardening is kept. The template accepts a cache only as a volume name of that exact shape, one per ecosystem, and mounts it at the fixed path; no caller-supplied path, bind mount or other volume gets in (`ContainerTemplate.RunArgs`, `RunnerPolicy.RefuseStart`). The five variables are allowed in the environment only with their one fixed value. The volumes are not created with the run label, so the restart sweep, which removes labelled resources that no resumed batch owns, leaves them. They are created by the engine on first use and take the image's ownership of `/cache/<ecosystem>` (the same mechanism as `/work`), so the image must have those directories: caches need a session image from this release. They grow without bound; pruning is the operator's (`docker volume ls -q -f name=chargehand-cache-`), and a size cap or an age rule is not built.

**3. Test services: option 2.** `driven.services.repositories` maps `owner/repo` to up to four services, each `{name, image, port, env?, tmpfs?, note?, memory_mb?}`. After the batch network exists and before the first session, `BatchServices` starts each from its pinned image (`IServiceEngine.StartServiceAsync`, `ContainerTemplate.ServiceArgs`): detached, on the batch's internal network only with the service name as network alias, `--cap-drop ALL` plus `CHOWN`, `DAC_OVERRIDE`, `FOWNER`, `SETGID`, `SETUID` (what a database image's entrypoint needs to fix its data directory and drop privilege), `no-new-privileges`, memory, CPU and pid limits, no mount, no published port, the environment in an env file. The data directory is a `--tmpfs`, so the service leaves no volume behind and runs at memory speed. Sessions and the verifier get `CHARGEHAND_SERVICES=name=name:port,...`, the names are added to `NO_PROXY`, and the note lists them with the profile's one-line `note`. Services are removed before the batch network (a network with an attached container cannot be removed); a restart finds them by name and keeps those still running; the sweep recognizes `chargehand-svc-<service>-<batch>` as the batch's. A service that cannot start ends the batch before any session starts.

The runner starts a service only from an image on its new `--service-images` list, on a `chargehand-net-*` network, under its memory ceiling and `--max-containers`. Environment values in a profile are test values for a container with no route out (`POSTGRES_PASSWORD=test`), never a real secret; names starting `LD_` are refused.

Not built: a readiness wait (the note says a service may need a few seconds and tells the session to retry; measured on Postgres 17, the first connection after start was refused once and the second succeeded about 1 s later); a service per task (all tasks of a repository in a batch share one server, so tests that need isolation use distinct database names).

**4. The image variant: option 1.** `images/session-jdk/Dockerfile` builds `FROM` the session image, named by digest, and adds a JDK (Debian's `openjdk-21-jdk-headless`), Gradle (a pinned version, downloaded from `services.gradle.org` and checked against its published SHA-256) and Maven; it regenerates the manifest. The release workflow builds and pushes it after the session image and prints both digests. `driven.services.images` maps an ecosystem name to a digest (`{"gradle": "...", "maven": "..."}`); `RepositoryDetector` finds the ecosystems of the pinned checkout and the first in its fixed order that has an entry selects the image for that repository's sessions, workspace helper and verifier. Each value must also be in `driven.images`, which is what the runner allows. The egress container and the volume helper keep `images[0]`. The variant is a superset, so a repository with both .NET and Gradle projects works in it.

Not in the variant: the Android SDK (platform tools, build tools and licenses, from `dl.google.com`). A pure JVM Gradle or Maven repository verifies; an Android one still does not, until a further variant (or a build argument) adds the SDK and the profile allows its hosts.

**5. The code index: decided, not built.** An index of the base commit is a read-only named volume keyed by repository and commit, `chargehand-index-<repo 12 hex>-<commit 12 hex>`, filled once by a helper container with no network from the pinned clone that `Orchestrator.CheckoutForDrivenAsync` already makes, and mounted read-only at `/index` in the batch's sessions, with `CHARGEHAND_INDEX_PATH` in the environment. Because it is built from the pinned clone and never from a session's output, and mounted read-only, a session cannot write to it, and a given volume name always holds one commit. It needs an indexer on the image, a choice of which one, and the server's research tools to read the mount; the first is the maintainer's to choose and the last is a server change. Deferred.

The audit's "a checkout fast-forwarded every two minutes" was not found in this repository: a session's research and review calls already name `CHARGEHAND_REPOSITORY_PATH`, the pinned clone under `<worker_root>/.checkouts/<name>-<id>/<commit>`, and the token is limited to that path and commit. The fast-forwarding checkout is presumably the maintainer's deployment's own (the source repository the server clones from, or a code index outside this repository). UNKNOWN until read on the server.

## Consequences

- The note removes the probing turns whose cost the governor measures; whether the `environment_probing` signal falls is to be measured on the next batches (nothing here has been run against a live model).
- A repository restores once per repository, not once per task and again in the verifier, after the first task. The first task pays the cold restore as before.
- Caches and services are off unless `driven.services` is set. With it unset, nothing is mounted, started or chosen; the note is the only change.
- Enabling `driven.services.caches` needs a session image from this release (it has `/cache/<ecosystem>` owned by the session user) and a runner from this release (the start body gains `caches`, which an older runner refuses as an unknown field, so the task would end `container_unavailable`). `task.json` gains `environment`, which older images skip.
- A cache volume is a new place state persists between tasks of a repository, outside the run's cleanup. Its contents are the repository's own and are not covered by the diff scan.
- Services count against `--max-containers`; raise it by the number of services of the largest batch.
- The Gradle and Maven package hosts are not in the driven preset's allowlist (`presets/driven.yaml` is gated by Prompt CI); `driven.network.allow` adds them, and `chargehand init` lists them for a detected Gradle or Maven repository.
- The JDK variant is about as large again as the base image; its pull time on the server is not measured.

**What the maintainer's server change is.** Deploy a runner and a server built from this release; rebuild or pull the session image and the new `chargehand-session-jdk` image; list the JDK image's digest in the runner's `--images` and in the profile's `driven.images` and `driven.services.images`; start the runner with `--service-images <digest>` for each service image and raise `--max-containers`; set `driven.services` in the profile; add the Gradle and Maven hosts to `driven.network.allow` for repositories that use them; decide a pruning rule for `chargehand-cache-*` volumes. Nothing in this repository does any of it.

## Reopen if

A cache poisoning is observed, or a task's tests pass on one run and fail on a cold one for a reason in the cache (then a verifier on a cold or read-only cache, at the cost the cache saves); caches grow past what the host can keep (then a size or age rule); the number of parallel tasks per repository makes one shared service server a bottleneck (then a service per task); the Android SDK is needed (then a variant with it); or the code index is built (then record its indexer and where the research tools read it).
