# 0078. The repository declares its environment, and its own CI verifies the change

- Status: proposed
- Date: 2026-10-23

## Context

chargehand decides a driven session's environment from a table of languages, and the table is copied into many places
that already disagree. A read of `main` at `f32be60c` found ecosystem knowledge in about 30 files, roughly 600 lines:

- Two detectors. `RepositoryDetector` (Gateway, 204 lines: 7 ecosystems, package hosts, test commands) feeds `init`,
  the console and `adopt`. `VerifyCommand.Detect` (Sessions, about 54 lines) feeds the handover, `SharedServicesPlan` and
  the `code` preset. They disagree on Node (`npm ci && npm test` against `npm test`), Python (pip install plus
  `pytest -q` against `pytest` or `unittest`), the order of Go and Rust, `dotnet test --nologo`, and whether
  `scripts/test*` and a Makefile `test` target count.
- Three build-file and test-file lists: `VerifyCommand.IsVerificationPath`, `TestIntegrity.BuildFile` and
  `Adherence.TestFile`. The Android lint change (#462) added Gradle files to the first only.
- One cache vocabulary (`nuget`, `npm`, `pip`, `gradle`, `maven`) copied six times: `CacheVolumes.Ecosystems`, its
  volume regex, `DrivenSharedServices`, the profile schema's enum, `ContainerTemplate`'s environment allowlist and the
  session Dockerfile's directories. It has no Go or Cargo, while the detector's vocabulary does.
- Two images, `session` (.NET 10 SDK, Node 26, Python; 2.1 GB on disk) and `session-jdk`, chosen by
  `driven.services.images`. Android, Go, Rust, PHP, Ruby and Swift repositories have no working path
  ([ADR 0075](0075-screenshots-of-ui-changes-and-verified-android-builds.md) left Android to CI).
- Twelve tool probes in `SessionEnvironment`, per-ecosystem probes in `AdoptProbes` (49 lines), a hard-coded list in
  `InitCli`, and host lists that `presets/driven.yaml` does not match, so `init` and the doctor always warn.

What it costs, from the [October baseline](../baseline-2026-10.md): of the last 30 driven tasks, 11 (37%) were lost to
the environment. Six verifiers exited 127 because the session image has no `make`; four pull requests were
"verified" by a command that ran no tests and then failed the repository's tests in CI; one repository had no test
command chargehand could find. Each new ecosystem means edits in up to ten places, and every missed place is a bug.

What the repositories already declare. The 10 repositories driven work ran on and the 4 public benchmark
repositories, read on 2026-10-23:

| declaration | repositories | share |
|---|---:|---:|
| `devcontainer.json` | 0 of 14 | 0% |
| a CI workflow | 14 of 14 | 100% |
| toolchain named in CI through `actions/setup-*` (or `gradle/actions/setup-gradle`, `android-actions/setup-android`) | 11 of 14 | 79% |
| a toolchain pin file mise reads (`global.json`, `.nvmrc`, `.python-version`, `.java-version`, `rust-toolchain.toml`, `mise.toml`, `.tool-versions`) | 2 of 14 | 14% |
| `scripts/check.sh` that CI runs | 3 of 14 | 21% |
| a Makefile | 2 of 14 | 14% |

What CI costs as the verifier. chargehand's own CI took 4.5 to 6.6 minutes from first to last check on three pull
requests of 2026-10-22, against 4.2 to 5.0 minutes for the fresh-container verifier, which ran format and build only.
A .NET server repository's CI took 12 to 17 minutes against a 1.5-minute verifier. One small repository's CI runs only
a secret scan (11 s), which verifies nothing.

What a toolchain manager costs. [mise](https://mise.jdx.dev/dev-tools/versions.html) installs a pinned version of most
toolchains and reads the idiomatic pin files above (opt-in per tool, `idiomatic_version_file_enable_tools`). Measured
on 2026-10-23 with mise 2026.10.17 on an arm64 Mac and a home connection, in a Debian base image with git, make, a C
toolchain and the mise binary (965 MB unpacked, not slimmed; today's session image is 2.1 GB), one cache volume per
repository, the toolchain version taken from the repository's pin file or its CI setup step, and the repository's own
test command. "Warm" is a second, fresh container on the same volume.

| repository (toolchain) | where the version came from | toolchain install, cold | test, cold / warm | result | volume after |
|---|---|---:|---:|---|---:|
| chargehand (.NET 10, Node 22) | `global.json`, CI `setup-node` | 38 s | restore 8 s + `npm ci` 5 s / 3 s + 3 s | ok (restore only) | 1.2 GB |
| ardalis/GuardClauses (.NET 8) | CI `setup-dotnet: 8.x` | 35 s | 12 s / 5 s | 959 passed. With .NET 10 only, as in today's session image, the test run aborted. | 0.9 GB |
| tj/commander.js (Node 22) | CI `setup-node` | 3 s | 8 s / 7 s | passed | 0.2 GB |
| more-itertools (Python 3.12) | CI `setup-python` | 4 s | 13 s / 14 s | passed | 0.1 GB |
| JodaOrg/joda-money (Java 21, Maven) | CI `setup-java` | 10 s | 21 s / 19 s | passed | 0.4 GB |
| spf13/cobra (Go 1.24; no path in chargehand today) | CI `setup-go` | 5 s | 7 s / 0 s | 1 test failed, only because the spike ran as root (it expects a write to fail) | 0.4 GB |
| BurntSushi/walkdir (Rust 1.90; no path today) | chosen | 14 s | 1 s / 1 s | 20 passed, once the image had a C linker | 1.3 GB |

The cold installs contacted `mise-versions.jdx.dev`, `mise-java.jdx.dev`, `github.com` and `api.github.com` (release
downloads), `nodejs.org`, `dl.google.com`, `archive.apache.org`, `sh.rustup.rs`, `dot.net`,
`builds.dotnet.microsoft.com`, `api.nuget.org` and `pypi.org`. On the production server (amd64, other bandwidth) the
times are UNKNOWN until the spike of the migration's step 3.

## Options

1. **The repository declares its environment (devcontainer first, then CI setup steps).** chargehand builds or pulls
   the image the repository names. Honours an open standard, but 0 of 14 repositories have a devcontainer today, and
   building a repository's own Dockerfile on the runner runs the repository's code with Docker build rights and a
   network, which the runner exists to prevent ([ADR 0039](0039-driven-writing-sessions.md)). The CI setup steps are
   present in 11 of 14; reading them needs a small map from well-known setup actions to tool names.
2. **One base image with a toolchain manager.** One image with git, make, a C toolchain and mise; the session's
   toolchain is whatever the repository pins, installed into a per-repository cache volume on first use. Removes the
   image variants and every per-ecosystem image decision. Costs a cold install on the first task of each repository
   and version (measured above), new download hosts, and cache volumes that grow.
3. **The repository's own CI is the verifier.** chargehand pushes the branch and waits for the checks that gate the
   merge, which the integrator already reads ([ADR 0046](0046-integrator-and-ci-fixer.md), update of 2026-10-21);
   a failed check goes to the CI fixer. Uses exactly the checks that will decide the merge, for every ecosystem the
   repository supports. Costs a CI round trip per attempt (5 to 17 minutes measured), CI minutes on private
   repositories, and depends on the CI watch, which ended on its first network error in 10 of 11 tasks of the
   baseline. A CI that tests nothing verifies nothing.
4. **Keep the table and add image variants per ecosystem** (ADR 0075's option 1, generalised). Every ecosystem is
   another image of about 1 GB to build, release, pin and audit, and another entry in each list above.

## Decision

Option 3 for verification and option 2 for the toolchain, with option 1 reduced to one input. No code outside one onboarding
helper names a language.

**1. Verification is the repository's CI.** After the session, chargehand pushes the branch and the pull request
waits for the checks that gate the merge. The fresh-container verifier (`ContainerVerifier` and the handover's
`VerifyCommand` path) is removed. Before the push, the session driver, not the model, runs the repository's check in
the session's own container, as a filter that saves a CI round trip; it is not proof and the pull request does not
call it verification. The check is the first of: the request's `context.verify`, the profile's
`driven.services.verify["owner/repo"]`, `scripts/check.sh`. With none, the driver runs nothing and CI is the only
check. There is no detected command any more.

The pull request says what verified it: the names of the required checks that passed, or "no required check; a person
merges" (the integrator already treats such a repository as `ask`). A command that checks nothing (`true`, `:`,
`exit 0`, `echo`, alone or under `sh -c`) is refused when the request arrives (CHARGEHAND-258). The CI watch retries a
failed poll with backoff and ends only after several consecutive failures or its time limit, so the fixer gets its
chance. A repository with no CI and no check is refused as today unless the change is documentation only.

**2. The toolchain is what the repository pins, installed by mise.** One session image: Debian, git, make, a C
toolchain, curl, unzip, Python 3 for scripts, and the mise binary. No SDK. Before the session starts, a helper
container with no model in it runs `mise install` in the checkout, with idiomatic pin files enabled for every tool mise
supports, writing into the repository's cache volume (decision 3). Only that helper may reach the toolchain hosts the
spike listed; the session's allowlist does not grow, because opening `github.com` release downloads to a session that
a model drives would let it fetch any binary on GitHub. The session puts the installed tools on `PATH`, and the
environment note lists `mise ls --current` instead of twelve probes. A repository that pins nothing mise
reads gets its tools from, in order: a `mise.toml` or `.tool-versions` it commits, the profile's
`driven.services.tools["owner/repo"]` (for repositories the owner does not want to change; the same shape as
`driven.services.verify`), or nothing (the base image's tools only). `chargehand adopt` proposes the `mise.toml` from the CI workflow's setup steps; that map of about
ten well-known setup actions to tool names is the one language table left, and its output is a file a person reviews
in a pull request. The model never chooses a toolchain.

**3. One cache volume per repository, and the home directory is on it.** A volume named
`chargehand-cache-<12 hex of owner/repo>` is mounted at the session user's home directory (`/home/session`, the
directory its account entry names, today a tmpfs), and `MISE_DATA_DIR` is under it. Every package manager's default
cache (`~/.nuget`, `~/.npm`, `~/.cache/pip`, `~/.gradle`, `~/.m2`, `~/go`, `~/.cargo`) then persists per repository
with no per-tool code. The mount point matters: Java reads the home directory from the account, not from `$HOME`, so a
volume mounted elsewhere with `HOME` pointed at it left Maven's `~/.m2` outside the volume in the spike. `CLAUDE_CONFIG_DIR` stays on a tmpfs, so transcripts
and session state do not persist; the driver deletes shell start-up files (`.bashrc`, `.profile`, `.bash_profile`,
`.zshrc`) from `HOME` before each session. The volume is not shared across repositories, for ADR 0048's reason. What
remains: a session can change what a later session of the same repository finds in `HOME`, which ADR 0048 accepted
for package caches and which matters less now, because the verdict comes from CI, which shares nothing with the
volume.

**4. Hosts are learned per repository, not listed per language.** The session's default allowlist is the preset's;
the toolchain hosts belong to the install helper only. The egress proxy already
writes one JSON line per connection decision to its log (`EgressCli`), which is lost with the batch's egress
container. The server keeps the refused ones per repository, and the owner approves a host for that repository.
`RepositoryDetector`'s host lists go. *Amended 2026-10-29:* an approval is kept by the server in its state directory
(`learned-hosts.json`), not written into the profile: where the profile comes from a GitOps repository the server cannot
write it, and the console approves in one step. The approved hosts join the batch's allowlist next to the preset's and
`driven.network.allow`.

**5. A devcontainer is an input later, not now.** When a repository has a `devcontainer.json` that names features
with versions, `adopt` may read them like CI setup steps. chargehand does not build devcontainer images.

## Consequences

Delete list, measured on `f32be60c`. "Breaks" is what a user of today's behaviour loses.

| what goes | file, lines | replaced by | breaks |
|---|---|---|---|
| the second detector | `VerifyCommand.Detect`, `DetectJvm`, `HasNpmTest`, `PyprojectUsesPytest`, `HasPythonTests` (`VerifyCommand.cs` 19-72, about 54 lines) | request, profile, `scripts/check.sh`, then CI | a request with no `context.verify` on a repository without `scripts/check.sh` gets no pre-push check; the `code` preset (ADR 0035) refuses such a repository instead of guessing `dotnet test` |
| the fresh-container verifier | `ContainerVerifier.cs` (88 lines, 6 call sites), the handover's verify step and its run-args | the pull request's checks | a repository with no CI and no check cannot ship code changes; the pull request waits for CI instead of arriving pre-tested |
| per-ecosystem test commands and hosts | `RepositoryDetector.cs` (204 lines; about 125 are ecosystem tables) | a reader of what the repository declares (CI present, pin files, `scripts/check.sh`) | `init` and the doctor stop printing per-language host lists; the console's ecosystem column becomes a "declares" column |
| the cache table | `CacheVolumes.Ecosystems` and regex (11 of 57 lines), `DrivenSharedServices` cache validation (about 28), five `ContainerTemplate` environment entries, the Dockerfile's `/cache/<ecosystem>` directories, the schema enum (11) | one volume per repository mounted at the home directory | a profile's `driven.services.caches` list is accepted and ignored, with a warning, for two minor versions, then refused |
| the JDK image and image selection | `images/session-jdk/Dockerfile` (24), the release job for it (about 50 lines of `release.yml`), `driven.services.images`, `SharedServicesPlan.PickImage`, `BenchRunner.NeedsOwnImage` | mise installs `java`, `gradle`, `maven` per repository | profiles that pin the JDK digest; runners whose `--images` lists it |
| SDKs in the session image | `images/session/Dockerfile` SDK layers (about 30 of 44 lines) | mise, through the install helper | the helper needs the toolchain hosts; the first task of each repository and version pays the cold install |
| twelve tool probes | `SessionEnvironment.cs` 34-39 | `mise ls --current` | none |
| per-ecosystem adopt probes | `AdoptProbes.cs` 33-81 (49 lines), `InitCli.cs:49` | `mise install` and the declared check, run once | `adopt` no longer guesses a lint command per language |
| duplicate build-file lists | `TestIntegrity.BuildFile` (8) and `IsVerificationPath` merged into one list in the Kernel | | none; the lists stop disagreeing |
| per-ecosystem test guidance | `plugins/chargehand/skills/change/SKILL.md` 79-82 and about 25 lines of guides | "run the check the environment note names" | none |

Kept on purpose, because they read output rather than choose an environment: the governor's probe regex, `Adherence`'s
test-step regex, `CiHistory`'s flaky-test regex and `RepositoryReadiness`'s CI file list.

In all, about 350 lines of C# and one image with its release job go; about 150 come in (the mise step in the driver,
the per-repository volume and `HOME`, the learned-host view, the CI watch's retry). The `code` preset loses detection
too; that is wanted, since a guess is how four pull requests were called verified.

Migration, one pull request per step:

1. **No code (the owner's profile).** Drop the production profile's `driven.services.verify` entry for chargehand
   (format and build only), so 0.18.0's `scripts/check.sh` default applies; turn on `driven.services.caches` for the
   five existing ecosystems. Measure the verifier and the test stage before and after.
2. **The CI watch survives a failed poll** (CHARGEHAND-251), and the no-op refusal (258). Prerequisites for option 3.
3. **Spike: the mise base image behind a switch** (`driven.environment: "mise"`, default off): the image, the driver's
   `mise install`, the per-repository home volume. Run one task per ecosystem the benchmark covers (.NET, Node,
   Python, Maven) plus one Go and one Rust repository, and record cold and warm start, task outcome and refused hosts.
4. **CI as the verifier behind the same switch:** the driver's pre-push check, the pull request's verification text,
   the fresh verifier skipped. Run the next 20 driven tasks of the owner's repositories with it.
5. **Make it the default** when, over those 20 tasks, the share lost to the environment falls from 37% to under 10%
   and the median time from batch start to a green pull request does not rise by more than the CI round trip.
6. **Delete** the rows of the table above, with the schema's deprecation for `caches` and `images`.
7. **Learned hosts** in the console.

## Reopen if

A repository's CI is too slow or too costly to be the gate (then a local verifier in the session image, still with
the repository's own check, not a detected one); the cold install of a toolchain dominates task time on the server (then
pre-warm the volume from `adopt`, or bake the most used toolchains into the image); a session poisons its repository's
`HOME` in a way that changes CI's verdict (then a read-only `HOME` layer); or devcontainers appear in the repositories
chargehand is used on (then honour their features).
