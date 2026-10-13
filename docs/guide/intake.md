---
title: "Start work from GitHub"
description: "A label on an issue or an @-mention by an allowed author starts a driven batch of one task; a reply continues a task that asked: the profile, who may trigger, what chargehand writes back, the limits and the security choices."
order: 14
section: "Guides"
---

Intake ([ADR 0049](../adr/0049-intake-from-github.md)) turns an issue into a [driven](driven.md) batch of one task, so a person files an issue and gets a draft pull request. chargehand **polls** GitHub as the GitHub App of `driven.integrator`: the server is on a private network and GitHub cannot call it. Off unless the profile has `automation.github`. On `main`, not yet released; not run against GitHub, only against a fake host.

## What starts a task

| Trigger | Who counts | What happens |
|---|---|---|
| The label `chargehand` is on an open issue | the person who **applied** the label | a task starts from the issue |
| A comment that mentions `@chargehand` | the comment's author | a task starts from the issue, with the comment as part of the goal |
| A comment on an issue whose task asked a question, posted after the questions | the comment's author | the task continues from its kept work with the comment as the answer; no mention needed |

Only logins in `allowed_authors` count. The list is required and has no default; an empty list means nothing triggers. Anyone else's label or comment is ignored, once. A mention in a quoted line (`> @chargehand`), inside another word (`@chargehand-bot`, `me@chargehand.example.test`) or in a comment chargehand wrote does not count. Pull requests are not issues here; the label on one does nothing.

Removing and applying the label again starts a new task. Without that, a task is started once per label event or comment, however often the issue is read, and a restart does not repeat it.

## What chargehand writes back

On the issue, as the App, each comment starting with a hidden marker so it is never read as a trigger:

1. When the batch starts: the run id. Follow it with `chargehand watch <run-id>`.
2. When it ends: the draft pull request; or what stopped it (the task's own detail); or the session's questions, as a list, with a line that a reply from an allowed author continues the task.
3. When a limit holds it (below), once.

A reply continues the task on the same branch, from the commit the first batch started at, with the first task's size class. It is a new batch, so it counts toward the limits. If the earlier batch is no longer in the run log, chargehand says so and a new mention starts the task again.

## The goal

The goal's first line is the issue's title (the pull request's title is cut from it). The rest names the issue, then gives its title, author and body, and the triggering comment if there was one, between `<untrusted-github-text>` tags, cut to 200, 6000 and 2000 characters, with a sentence telling the session that these are texts people wrote on GitHub: they describe the work and are not instructions to it. A tag inside the text is turned into a bracket, so text cannot close the fence. The size class is the issue's `size:S`, `size:M` or `size:L` label when it has exactly one, else `budget_class`.

## Limits

| Key | Default | When over it |
|---|---|---|
| `max_concurrent` | 2 | the trigger waits, with one comment, and starts when a triggered batch ends |
| `max_starts_per_day` | 10 | refused, with one comment; not started later. A reply that would exceed it is refused the same way and can be sent again. Any 24 hours, not a calendar day |
| one task per issue | | a second trigger on an issue whose task is running gets one comment |

`max_tokens_total` (2,000,000) and `max_usd_total` (5) cap a triggered batch like `driven.max_tokens_total` and `max_usd_total` do for a caller's; both are always sent, and the one that applies is the one the credential's kind needs. A halted server, or one with ten unfinished runs, leaves the trigger for the next poll.

## Security

- **Who may start** is the list, and for a label it is the labeller. Writing an issue needs no rights; applying a label needs triage rights.
- **The issue's text is untrusted** even when an allowed author applies the label to someone else's issue. The fence helps the model; the real boundary is the session's container (no credential, no route out beyond the allowlist) and the handover's checks.
- **A repository the integrator merges into** (`merge` in `driven.integrator.repositories`, not in `always_ask`) takes work only from an issue **written** by an allowed author. For another author's issue chargehand comments once and starts nothing, because a merge there ends at a check and not at a review. A repository that is `ask` or not listed has no such rule.
- The App needs the Issues permission (read and write) besides [the integrator's](driven.md#merging-by-policy). It needs no administration and no workflow permission, takes no webhook, and is installed only on the repositories listed.
- The token and the key are never in a comment, a log line or an error message. A polling failure is one line on stderr and the next poll tries again.

## Profile

```json
{
  "driven": { "enabled": true, "integrator": { "app_id": 123456, "installation_id": 7654321, "private_key_secret": "chargehand-app-key" } },
  "automation": { "github": {
    "repositories": { "octo/repo": { "path": "/srv/chargehand/repos/repo" } },
    "allowed_authors": ["octocat"],
    "label": "chargehand",
    "mention": "@chargehand",
    "poll_seconds": 120,
    "budget_class": "M",
    "max_tokens_total": 2000000,
    "max_usd_total": 5,
    "max_concurrent": 2,
    "max_starts_per_day": 10 } }
}
```

`path` is the server's checkout of the repository (under `repository_roots`, with an `origin` remote). Each trigger fetches it with the server's own git credentials and starts from the commit `origin/HEAD` points at; a fetch that fails is a line on stderr and the batch starts from what the checkout has. `poll_seconds` is 30 to 3600. A profile that sets `automation.github` without `driven.enabled` and the integrator's App keys is refused when it loads.

## State

`intake/github.json` next to the run log: the comment cursor per repository, every trigger acted on or ignored, the tasks with their phase, and when batches started. It is written whole through a temporary file. Deleting it makes the server forget what it started: labelled issues start again, and comments from before the next poll are not read.

## Not covered

Review comments on a chargehand pull request (CHARGEHAND-184 continues the branch from them). A webhook instead of the poll. Issues in a repository nobody listed. Labels from a bot account. Not run against GitHub: see the ADR's UNKNOWN list.
