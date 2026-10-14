---
title: "Review pull requests with a workflow"
description: "A reusable GitHub Actions workflow that has a chargehand server review any repository's pull requests and keeps one comment up to date: what it needs, how to call it, what the comment shows, and why forks are skipped."
order: 13
section: "Guides"
---

`.github/workflows/review.yml` is a reusable workflow. A repository calls it from a `pull_request` workflow; it sends the pull request's diff to a chargehand server, waits for the `review` preset's result, and posts one comment on the pull request. The next push edits that comment instead of adding another. On `main`, not yet released.

## What it needs

- **A chargehand server the runner can reach** ([Run the HTTP server](server.md)), with its bearer key. The server binds a private network by design ([ADR 0024](../adr/0024-server-on-a-private-network-and-release-images.md)), so a GitHub-hosted runner reaches it only through a tunnel or a public address behind the key. Otherwise run the job on a self-hosted runner on that network (the `runs-on` input). The host in `server-url` must be in the server's `http.allowed_hosts`, or the server answers `400`.
- **A checkout of the repository on the server's machine, under its `repository_roots`, that holds the pull request's head commit.** The workflow sends the diff and the goal as inputs, but the reviewer reads the files at that commit, and a citation can only resolve against it. Keeping the checkout fetched is the deployment's job (a timer that runs `git fetch`, for example); with a commit missing the run fails with `checkout_invalid` and the comment says so. `repository-path` names that checkout.
- **On the runner:** `bash`, `curl`, `jq` and `gh`, all on GitHub-hosted Ubuntu runners. The workflow uses the `job.workflow_*` context, so it runs on github.com, not on GitHub Enterprise Server.

## Add it to a repository

Save the secret `CHARGEHAND_API_KEY` (the server's key) and, as a repository variable, the server's URL. Then add `.github/workflows/review.yml` to the repository:

```yaml
name: review
on:
  pull_request:
    types: [opened, synchronize, reopened]

permissions:
  contents: read

jobs:
  review:
    if: ${{ !github.event.pull_request.draft }}
    permissions:
      contents: read
      pull-requests: write
    uses: Egoushka/chargehand/.github/workflows/review.yml@<tag>
    with:
      server-url: ${{ vars.CHARGEHAND_URL }}
      repository-path: /srv/repos/my-repo
    secrets:
      chargehand-api-key: ${{ secrets.CHARGEHAND_API_KEY }}
```

Replace `<tag>` with a release tag, or with a full commit sha. The workflow checks out its own scripts at the exact commit it runs from, so the tag decides the workflow and the scripts together. Until a release carries the workflow, use the sha of a commit on `main`.

| input | default | meaning |
|---|---|---|
| `server-url` | required | the server's base URL. It appears in the job log, so pass it from a variable or a secret, not literally, if the address is private |
| `repository-path` | required | the checkout's path on the server |
| `preset` | `review` | the preset that reviews the change |
| `max-findings` | `10` | findings listed, highest confidence first |
| `fail-on` | `none` | `none` never fails the check; `high` fails it on a completed review with a finding of confidence 0.8 or more |
| `timeout-minutes` | `20` | the job's limit; the wait for the result ends 3 minutes before it |
| `runs-on` | `["ubuntu-latest"]` | runner labels, as a JSON array |

| secret | meaning |
|---|---|
| `chargehand-api-key` | required: the server's key |
| `comment-token` | optional: a token that writes the comment, for example a bot's. Default `GITHUB_TOKEN` |

The caller's job must grant `pull-requests: write` and `contents: read`: a reusable workflow cannot hold more permission than its caller gives it. The workflow itself asks for nothing else. The pull request is not checked out.

`fail-on: high` is a rough gate: `result/v1` has no severity field. A finding of confidence 0.8 or more has survived the support check, which halves the confidence of a claim its citation only partly backs ([support checking](support-and-signing.md#claims-checked-for-support)). It is still a model's opinion; start with `none`.

## What the comment shows

```markdown
### chargehand review

Two findings: a null dereference and a missing test.

- Parse() dereferences the result of Find() without a null check.
  Support: `supported`, confidence 0.9.
  Evidence: [`src/Parser.cs:10-12`](https://github.com/acme/app/blob/<head sha>/src/Parser.cs#L10-L12), `input diff`
- No test covers an empty file.
  Support: `partial`, confidence 0.4.
  Evidence: [`tests/ParserTests.cs:3`](https://github.com/acme/app/blob/<head sha>/tests/ParserTests.cs#L3)

Open questions:
- Partly supported by its citation: ...

Run `<run id>`, $0.1832; citations checked against `<head sha>`.
```

A file or diff citation links to the cited lines at the head commit. Another kind (an input, a URL) shows as plain text. A path with `..`, a leading `/` or characters outside a conservative set is shown but not linked. The support verdict is `supported`, `partial` or `not checked`; a claim judged unsupported has already moved to the open questions. The first line of the comment is a hidden marker, which the next run finds to edit the comment; if the token cannot edit a comment that carries the marker (someone else wrote it), the run tries the next one and posts a new comment only when none can be edited.

A run that fails, stops for input or cannot reach the server leaves a comment that says so and why. The error message is the server's, so it can name a path on the server (`checkout_invalid` does): think about that before you use the review on a public repository. Whether the check fails in those cases does not depend on `fail-on`: a review that did not run does not block the pull request, and the job log and the step summary hold the same text.

## Untrusted pull requests

The review job holds the server key and sends the pull request's text to a model. Two rules follow.

- **Never call it from `pull_request_target`.** That event runs with the base repository's secrets and write token on a pull request anyone can open. The title, body, diff and file contents are written by the author, and the model reads them; a prompt injection in them would run with the key in reach. The workflow refuses the event with an error. Use `pull_request`.
- **A pull request from a fork is skipped with a notice.** GitHub gives such a run no secrets and a read-only token, so there is no key to call the server with and no permission to comment. The same holds for Dependabot's pull requests. If you want fork reviews, run the workflow yourself after you have read the change, from a trusted event such as a label you apply.

The workflow reads the pull request's title and body from the event file and its diff from the API into files, and builds the request with `jq --rawfile`. Nothing the author writes is placed in a shell command; the workflow's own test fails on an expression inside a `run:` block ([ReviewScriptsTests](../../tests/Chargehand.Tests/ReviewScriptsTests.cs)). Every action is pinned by commit sha.

What leaves GitHub: the diff cut at 60000 characters, the title and body cut at 8000, the repository path and the head commit go to your server, and from there to the model provider your profile names.

## How it is built and checked

Five scripts under `scripts/`, each testable on its own, are shared by the reusable workflow and by [self-review.yml](../../.github/workflows/self-review.yml), which reviews this repository's own pull requests on the maintainer's runner:

| script | does |
|---|---|
| `review-request.sh` | builds the `request/v1` from a goal file and a diff file (`self-review.sh` uses it too) |
| `review-remote.sh` | posts the request to `/v1/runs`, polls `/v1/runs/{id}`, prints the `result/v1`. When the server gives none (unreachable, wrong key, lost run, deadline) it prints a failed `result/v1` of its own |
| `review-comment.sh` | renders the comment from a result (`self-review.yml` uses it too) |
| `review-post.sh` | posts the comment or edits the earlier one |
| `review-gate.sh` | applies `fail-on` |

[ReviewScriptsTests](../../tests/Chargehand.Tests/ReviewScriptsTests.cs) runs them: the request against the schema, the comment on a sample result, the poster against a stand-in `gh`, and `review-remote.sh` against a real in-process server (a finished run, a run polled to its end, a wrong key, an unreachable address, a deadline, a refused request). No test runs the workflow on GitHub; the first run on a real pull request is the check of the workflow file itself.
