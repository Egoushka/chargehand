# 0075. Screenshots of UI changes and verified Android builds

- Status: proposed
- Date: 2026-10-22

## Context

CHARGEHAND-193 asks for two things on a driven pull request: a change to a UI attaches screenshots of it, and a change to an Android app is verified by building it. Neither fits the images chargehand ships today.

- The session image (`images/session/Dockerfile`, ADR 0039) has the .NET SDK, Node, Python and git. It has no browser, so a session cannot render a page, and the verification container (`ContainerVerifier`, the same image) cannot either.
- The JDK variant (`images/session-jdk/Dockerfile`, ADR 0048) adds a JDK 21, Gradle and Maven and stops there: "it has no Android SDK" (`docs/guide/driven.md`), and ADR 0048 names the SDK as a reopen condition. An Android repository is written but its build and tests cannot run; verification fails the same way.
- Sessions run with a read-only root, a tmpfs home and egress only through the batch proxy to an allowlist (ADR 0039, ADR 0048). Neither the SDK's hosts (`dl.google.com`, `maven.google.com`) nor a browser's are on it.
- A pull request comment cannot carry an image through the REST API: a screenshot has to be stored somewhere the comment can link (a commit on a branch, a release asset, or an artifact store on chargehand's server). The console serves only its own build today.

Image sizes below are estimates from published images, not measurements of a chargehand build; the first step of either option is to build the variant and record `docker image ls` before and after.

- Android SDK (command-line tools, platform-tools, one build-tools and one platform, licences; no NDK, no emulator): published SDK images list 1.2 to 1.6 GB in all, on top of a JDK. Assumption: about 1 GB over the JDK variant. The Android Gradle plugin and its dependencies then fill the Gradle cache (`/cache/gradle`, ADR 0048) at the first build, outside the image. A Gradle daemon for an Android build wants 3 to 4 GB of memory, above the S budget class's limits.
- Chromium for screenshots: Playwright's headless shell with its system libraries and fonts; a packaged Playwright toolchain lists about 280 MB. Assumption: 250 to 400 MB over the session image. It needs a larger `/dev/shm` than Docker's default 64 MB, or `--disable-dev-shm-usage`.

## Options

1. **Two more image variants, chosen by ecosystem** as the JDK one is (ADR 0048): `session-android` (`FROM session-jdk`, plus the SDK by build argument for the platform and build-tools versions) and `session-browser` (`FROM session`, plus Playwright's headless Chromium). The base image stays as small as it is; only repositories that need a variant pull it. Costs: about 1 GB and 0.3 GB more on each runner that pulls them, two more digests to pin and release, the SDK and browser hosts on the allowlist, a larger memory limit for Android tasks, and a place to store screenshots.
2. **Put both into the base session image.** One image, every repository can do both. Every runner pulls roughly 1.3 GB more for every repository, including the many that need neither, and the base image's release slows by the same.
3. **Leave both to the repository's own CI.** No image change: the integrator already waits for required checks (CHARGEHAND-244), so an Android build in GitHub Actions gates the merge, and a screenshot job in CI can comment on the pull request. chargehand verifies nothing itself for these repositories, and a repository without such CI gets nothing.

## Decision

Not taken: the desk decides. Proposed: option 1 for Android (a variant, as ADR 0048 foresaw) and option 3 for screenshots until a store for them is chosen, since the browser is the cheaper half but the store is an open design question of its own.

## Consequences

To be written with the decision. With option 1: `driven.services.images` gains `android` (a repository with `app/build.gradle*` and an `android` block) and `browser`; the release workflow builds and pushes the variants; `chargehand init` lists the SDK hosts; the owner pins the digests and allows the hosts.

## Reopen if

A measured variant is far from the estimates above, the runner's disk cannot hold the variants, or GitHub's API gains a way to attach an image to a comment.
