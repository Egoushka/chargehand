# 0033. CI policy: a required gate per pull request, heavy scans off the pull request path

- Status: accepted
- Date: 2026-09-29

## Context

The maintainer asked for less CI load, floated as "merge to main only on release, stack changes, run few checks". Measured
on this repository (public, so hosted minutes are free), 2026-09-29:

- Checks a pull request waited on: `ci` about 60 s and CodeQL about 111 s, queue time about zero; `prompt-ci` about 8 s
  unless `prompts/` or `presets/` change.
- Pull request open to merge: median 4 min, p90 45 min. The wait is a human or a session, not a runner.
- Only `prompt-ci` was required (ruleset); `ci` was not. Auto-merge was off.
- CodeQL ran on every pull request (GitHub default setup) and Scorecard on every push to main, although neither gates a
  merge.

## Options

1. Batch merges behind a release branch and run the checks on the release. Cuts nothing that costs anything here, adds
   integration risk and a second long-lived branch, and cannot apply to a repository whose main is deployed on merge.
2. Keep every check on every pull request. The status quo: correct, and it puts advisory scans on the path of each merge.
3. One required check per pull request (format, build, test, secret scan, plus the cheap ones), prose-only pull requests
   skip the build jobs, advisory scans run weekly, on a release tag and on demand, and merge is automatic once the
   required checks pass.

## Decision

Option 3.

- `ci.yml` ends in a `gate` job that passes when every other job passed or was skipped. `gate` and `prompt-ci` are the
  required checks. A skipped job never blocks; a workflow skipped by a `paths` filter would, so the docs-only skip is
  inside the workflow (`changes`), not in its trigger.
- Docs-only means every changed file is `README.md`, `ROADMAP.md`, `CONTRIBUTING.md` or `docs/**/*.md`. Anything else,
  including `CHANGELOG.md` (the build job checks the version's section), `prompts/`, `presets/` and `plugins/`, runs the
  build.
- CodeQL moves from GitHub's default setup to `codeql.yml`: weekly, on `v*` tags, on demand. Scorecard drops its push
  trigger. Sonar already ran on main only. Dependency review and the commit check stay per pull request: seconds each.
- Auto-merge is allowed on the repository, so a pull request merges when `gate` and `prompt-ci` are green.

## Consequences

- A pull request that introduces a CodeQL finding is caught at the next weekly run or release tag, not before merge.
  Accepted: the scan never blocked a merge, and this repository takes no outside code without review.
- A docs-only pull request finishes in seconds.
- Merging on green needs no session waiting on CI. Rules that say "merge manually" no longer apply.
- Adding or renaming a job in `ci.yml` needs the `needs` list of `gate` updated; the ruleset does not change.

## Reopen if

- A finding shipped in a release that a pull request scan would have caught.
- The repository starts taking pull requests from people the maintainer does not review.
- Hosted runner minutes stop being free for this repository.
