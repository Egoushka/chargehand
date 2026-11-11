# Releasing

[release-please](https://github.com/googleapis/release-please) keeps one pull request open against `main`
([`release-please.yml`](../.github/workflows/release-please.yml)); merging it cuts a release
([`release.yml`](../.github/workflows/release.yml)). This page says which version number a change gets.

## Features and fixes bump the patch

Before 1.0, `release-please-config.json` sets both:

- `"bump-minor-pre-major": true` — a commit with a `!` (a breaking change) still bumps the minor version.
- `"bump-patch-for-minor-pre-major": true` — everything else, including a `feat:` commit, bumps the patch.

So `feat:` and `fix:` commits land in the open release-please pull request as today and ship as patch releases
(`0.N.x`) when it merges. A feature no longer moves the minor version on its own.

## A milestone cuts the minor by hand

A minor version marks a milestone closing, not a count of features shipped since the last one. To cut one, push an
empty commit to `main` whose message ends with the footer `Release-As: 0.<N+1>.0`:

```bash
git commit --allow-empty -m "chore: release 0.<N+1>.0

Release-As: 0.<N+1>.0"
```

release-please reads the footer on its next run and sets that version instead of the patch bump, in the same pull
request flow.

A milestone is a named goal — tracked as a milestone of the repository's GitHub issues, or
in [`ROADMAP.md`](../ROADMAP.md) — not a round number or a tally of merged pull requests.

## Publish the merge rate

Each release, replay 20 merged pull requests and publish the merge rate on the website's live status
([benchmark guide](guide/benchmark.md#the-merge-rate-published-each-release)): `chargehand bench replay import` and
`baseline`, `chargehand bench run` on the valid tasks, then `chargehand bench publish <results.json> --out merge-rate.json`.
It runs on the maintainer's machine (it calls a model), not in the release workflow.
