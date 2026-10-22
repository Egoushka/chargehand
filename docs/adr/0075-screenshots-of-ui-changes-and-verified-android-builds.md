# 0075. Screenshots of UI changes and verified Android builds

- Status: accepted
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

Option 3, decided by the maintainer on 2026-10-22: Android builds and screenshots are left to each repository's own CI. chargehand ships no new image variant. Reasons:

- The integrator merges only through required checks (CHARGEHAND-244): a pull request whose Android build fails in the repository's CI does not merge, so the build is verified before anything lands, by the same toolchain the repository already maintains.
- A repository with no test command chargehand can run still ships when its own CI is the gate (CHARGEHAND-245), so the session image need not build the app for the work to be delivered.
- When a required check fails, the CI fixer starts a session on the branch with the failing jobs' logs, up to 3 attempts per pull request (`docs/guide/driven.md`, CI fixer), which closes most of the loop a local build would have.
- The Android repository that prompted the question already has a required `build` check.

Against option 1 the cost was concrete (about 1 GB per runner, another digest to release and pin, SDK hosts on the allowlist, a larger memory limit, and Google's Linux build tools are x86_64 only) while the benefit is a shorter feedback loop for a repository that receives little driven work today. Screenshots would also need a store that does not exist yet.

## Consequences

- No image changes: `images/` keeps the session image and its JDK variant; no SDK hosts are added to `chargehand init`, the doctor or the allowlist; `driven.services.images` gains no `android` entry.
- An Android repository is written in the JDK variant; its build and tests run only in its own CI, so a session cannot see a build failure until the pull request's checks report it, and each round trip costs a CI run and a fixer attempt.
- A repository without required checks gets no verification of an Android build or a UI change from chargehand.
- `docs/guide/driven.md` keeps saying the JDK variant has no Android SDK.

## Reopen if

An Android repository gets regular driven work and CI round trips become the bottleneck (then option 1 for Android: measure the variant first); a store for screenshots is chosen; or GitHub's API gains a way to attach an image to a comment.
