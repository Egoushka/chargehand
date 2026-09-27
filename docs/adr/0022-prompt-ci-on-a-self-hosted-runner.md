# 0022. Prompt CI on a self-hosted runner, any runtime

- Status: proposed
- Date: 2026-09-27

## Context

ADR 0019 runs Prompt CI by hand on the owner's machine (`scripts/prompt-ci.sh <pr>`), so a gated pull request waits
until the owner is there. Its "Reopen if" names the way out: evals on a server, where a protected environment with a
required reviewer holds the review step. Since then the orchestrator has two runtimes (OpenCode, ADR 0004; Claude
Code, ADR 0020), and a runtime can bill per token or by subscription. The runtime and provider that suit a gate are
the deployment's choice, not the repository's.

## Options

1. Keep the manual run and add a poller on the owner's machine. Automatic only while that machine is on.
2. GitHub-hosted runner with the model, gateway and tracing keys in a protected environment, the runtime installed
   per run. Puts the keys in GitHub, which ADR 0019 ruled out.
3. A self-hosted runner on a server that reaches the gateway and the tracing backend.

## Decision

- **Runner:** option 3, ephemeral (one job, then a clean start), labelled `chargehand-eval`. It carries the .NET SDK,
  the GitHub CLI and whichever runtimes its profiles name, each at its pinned version; an OpenCode server stays on
  loopback (ADR 0016). Its deployment is not part of this repository.
- **Runtime:** the runner holds one eval profile per runtime, named by `PROMPT_CI_PROFILE_<RUNTIME>`, and a default in
  `PROMPT_CI_RUNTIME`. A pull request label `prompt-ci:<runtime>` picks another; a runtime the runner has no profile
  for fails the gate, and so does a label last added by someone without write access (triage can label, and the
  label picks what a gate spends). Adding a runtime or provider is a profile on the runner, not a change here.
- **Workflow:** `.github/workflows/prompt-ci.yml` on `pull_request_target`, so main's copy runs and its token can
  post the status on a fork's pull request. `classify` reads the changed file names through the API and settles
  `prompt-ci` when no prompt or preset changed. `review`, in the environment `prompt-ci-review` with the owner as
  required reviewer, runs for a fork's pull request or a preset change and stands in for `--reviewed`. `gate` runs
  `scripts/prompt-ci.sh` on the runner against main's build; the pull request still contributes only `prompts/` and
  `presets/`. A push, a body edit (a declared trade) or a `prompt-ci:` label reruns the gate.
- **Cost under a subscription:** an eval profile prices a subscription's models at their API list prices, not at zero
  as ADR 0020 suggests for runs. `Gate.Decide` compares dollars, and a zero base turns the cost check off without a
  sign (`sumBase == 0`, no pair with a positive cost). List prices keep it live as a token-weighted cost.
- **Calibration:** T, C and `reference_claims` were measured on one runtime and model (ADR 0019). A runtime or model
  gets an A/A per cell on the runner before its verdicts count; until then they are advisory.
- **By hand:** `scripts/prompt-ci.sh` still runs with the flags of ADR 0019.

## Consequences

- No gated pull request waits for the owner's machine. A fork's pull request or a preset change waits for one
  approval in GitHub instead.
- Every job on the runner can read its variables: model, gateway and tracing credentials. A job reaches the runner
  only through main's workflow, or through a fork's workflow after the owner approves the run (repository setting:
  require approval for all outside contributors). The token that registers the runner is dropped before it starts. A
  subscription credential has no spend cap, so a leak of it costs more than a capped eval key.
- A subscription's rate limits are shared with other use. An arm still limited after the retries of ADR 0019 stops
  the gate with `error`; rerun later, or pick another runtime by label.
- ADR 0019's "Where" and "Key" are replaced by this ADR; its gate, scores and cells are unchanged.
- Not verified until the first run on a runner: OpenCode's `{env:...}` substitution for a key in its config, and a
  Linux build of OpenCode 2.0.16 (not on npm or the GitHub releases, checked 2026-09-27).

## Reopen if

A job other than main's workflow runs on the runner without an approval; a runtime's A/A noise exceeds T/2; or a
provider's terms stop covering unattended runs.
