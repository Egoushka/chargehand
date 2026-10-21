---
title: "Start from a GitHub issue"
description: "The shortest way to have chargehand work on your repositories: create and install a GitHub App, add four keys to the profile, label an issue, and get a pull request that closes it."
order: 14
section: "Guides"
---

You label an issue `chargehand`; chargehand works on it in a container and opens a draft pull request whose body closes the issue when it merges. This page is the short version of [Start work from GitHub](intake.md). It assumes a server that already runs [driven sessions](driven.md) (`driven.enabled`, the session images and a runner). No push credential is needed: the App you create below also pushes the branch and opens the pull request. Not yet run against GitHub end to end: the calls are tested against a fake host ([ADR 0051](../adr/0051-intake-from-github.md) lists what is UNKNOWN).

## 1. Create the GitHub App

On GitHub: Settings, Developer settings, GitHub Apps, New GitHub App.

- **Webhook:** off. chargehand polls; the server does not need to be reachable from GitHub.
- **Repository permissions:** Contents read and write, Pull requests read and write, Issues read and write, Checks read, Actions read, Commit statuses read. Nothing else: no administration, no workflows. Only for the [`security-alerts` schedule](schedules.md#security-alerts): Code scanning alerts read and Dependabot alerts read.
- **Where can it be installed:** only on this account, unless others will install it.

Create it, note the **App ID**, and generate a **private key**. Put the key's PEM in your secret store under a name such as `chargehand-app-key` (the profile names the item, never the key).

## 2. Install it

From the App's page, Install App, on your account or organisation, and choose the repositories chargehand may work in. With discovery on (below), every repository the installation can see is watched, so choose "Only select repositories" unless you mean all of them.

## 3. The profile

Add to the profile the server runs with:

```json
{
  "driven": {
    "enabled": true,
    "integrator": { "app_id": 123456, "private_key_secret": "chargehand-app-key" }
  },
  "automation": {
    "github": { "discover": true, "allowed_authors": ["your-login"] }
  }
}
```

`discover: true` watches every repository the App is installed on, so there is no list of checkouts to keep: chargehand clones each one into `<worker_root>/repos/<owner>/<repo>` the first time it sees it and fetches it before each task, with the App's token, on the server's host. `allowed_authors` is who may start work; nobody else's label counts. The App pushes `chargehand/<run>` and opens the draft pull request with an installation token minted at each push, so `driven.push_secret` is optional; when it is set it wins and the App only reads, comments and integrates. Restart the server. Within one poll (2 minutes) chargehand creates the `chargehand` label in each repository that lacks it.

## 4. Label an issue

Write an issue that says what you want done, the way you would brief a colleague, and apply the `chargehand` label. You must be on `allowed_authors`: the person who applies the label is the one who counts, not the issue's author.

## What happens next

1. Within one poll chargehand comments on the issue with the run id, and starts one task on a branch `chargehand/<run>`.
2. When the task ends it comments again: the draft pull request, or what stopped it, or its questions. **Reply on the issue** to answer them; your reply continues the task on the same branch.
3. The pull request's body ends its goal with `Closes #<n>`. Merge it (or let the [integrator](driven.md#merging-by-policy) merge it, if the repository is `merge` there) and GitHub closes the issue.
4. Review comments you leave on the pull request start a review round on the same branch, up to three per pull request together with CI fix attempts.

At most 2 tasks run at once and 10 start in any 24 hours; [the limits](intake.md#limits) are profile keys.

## Turning a repository off

`GET /v1/intake/sources` lists the watched repositories; `PUT /v1/intake/sources` with `{"sources": [{"repository": "owner/repo", "enabled": false}]}` switches one off, with the owner's key. Off, its labels and comments wait until it is on again. Uninstalling the App from a repository stops it being discovered at the next poll.
