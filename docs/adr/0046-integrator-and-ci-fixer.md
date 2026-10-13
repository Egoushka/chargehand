# 0046. An integrator and a CI fixer act through a GitHub App

- Status: accepted
- Date: 2026-10-13

## Context

[ADR 0039](0039-driven-writing-sessions.md) (decision 2) ends a driven task in a draft pull request that chargehand never merges and never sets to auto-merge; [ADR 0015](0015-merging-and-verification.md) leaves merging to a person. [ADR 0045](0045-governed-sessions.md) lists the integrator and CI fixer as its step 11. What a person then does by hand, for every pull request, is the same four things: bring the branch up to date, mark the draft ready, wait for the required checks, merge. When a check fails on a small mistake, a person starts another session by hand.

The credential that could do it is the wrong one. The push credential (`driven.push_secret`) is a classic token for every repository the owner has. It can push non-default branches and it bypasses the owner's rulesets, so a merge made with it would not be held to any merge policy: that is what ADR 0039's "never merges" protected. A merge policy needs a credential that rulesets apply to, that is limited to named repositories, and whose permissions are the minimum.

A GitHub App fits. Its installation token is short lived (one hour), it exists only on the repositories the App is installed on, its permissions are per category, and it is subject to rulesets unless someone puts it in a bypass list. This is a design based on GitHub's documentation and on unit tests with a fake host; none of it has run against GitHub yet (see Consequences).

## Options

1. **Keep "never merges."** Safe, and the status quo. A person stays the bottleneck for every pull request, including ones on repositories whose own checks are the real gate.
2. **Merge with the push token.** One credential, no setup. It bypasses rulesets and has every repository, so it cannot enforce a policy per repository. Rejected.
3. **An App; chargehand waits for the checks, then merges with the head commit pinned** (`PUT /pulls/{n}/merge` with `sha`). Simple calls. The wait is chargehand's: a task slot sits idle for the length of CI (minutes to hours), and a restart loses the wait.
4. **An App; chargehand enables auto-merge** (GraphQL `enablePullRequestAutoMerge`, squash, `expectedHeadOid` set). GitHub does the waiting and merges when every required check and rule passes, which is the strongest reading of "through the required checks, never bypassing". The App token needs no special merge right: a merge is whatever the rules allow. Costs: the repository must allow auto-merge, and GitHub refuses it on a pull request that is already mergeable.
5. **A merge queue.** Not available on every plan and repository; not assumed.

## Decision

Option 4, with the fallback that option 3's call makes for the one case option 4 cannot cover. Everything below is off unless the profile has a `driven.integrator` block; with none, nothing changes and the push token's path is untouched.

**Policy (profile).** `driven.integrator` holds `app_id`, `installation_id`, `private_key_secret` (a secret item name, resolved like `push_secret`; never logged, never in a container), `repositories` (`"owner/repo": "merge" | "ask"`), `always_ask` (a list) and optional `ci_fix` limits. A repository that is not listed is **off**: chargehand opens its draft pull request and leaves it. `always_ask` wins over everything: a repository on it is `ask` even if `repositories` says `merge`, and even if `repositories` does not list it. Names compare without regard to case. `IntegratorPolicy` is a pure class over this.

**Credential.** The App's JWT (RS256, the key from the secret store, issued 60 s in the past, valid 9 minutes) is exchanged for an installation access token, which is kept until 5 minutes before it expires and then replaced (`GitHubInstallationTokens`, behind `IInstallationTokens`). The key is read only when a token is needed. No NuGet package: `System.Security.Cryptography`. The App needs: contents and pull requests (write), checks, actions and commit statuses (read), metadata. It does not need, and must not have, administration or workflows, must not be on any ruleset's bypass list, and takes no webhook. It is installed only on repositories chargehand may merge into.

**The integrator, per task.** After the handover opened the pull request on a listed repository, in this order: (1) a `[partial]` pull request is never touched: it stays a draft (a partial result is unfinished work). (2) If the branch is behind its base (compare API), `merge` repositories get `PUT /pulls/{n}/update-branch` with `expected_head_sha`, then chargehand waits for the head to move and merges onto the new head. `ask` repositories are not updated: a person will read the pull request and decides. (3) A draft is marked ready for review. (4) `ask`: one comment (left once, marked) and nothing else; the pull request is ready and a person merges. `merge`: if the pull request conflicts with its base, one comment and no merge; if GitHub already calls it mergeable (`CLEAN`), a squash merge pinned to the head commit; otherwise auto-merge with squash, pinned to the head commit. If GitHub refuses auto-merge and the pull request has become mergeable, the squash merge goes through the same rules. Auto-merge that is already on is left alone. chargehand never uses an admin bypass, and the App has none to use. Each step is a run event, `integrated` (run-status/v1 gains the kind; additive), and the whole is a line in the task outcome's `detail`. An error from GitHub is a result, not an exception: the pull request is open either way, so the task stays `completed`.

Whether a repository has required checks at all is GitHub's rule, not chargehand's: with none, a clean pull request merges at once. List a repository as `merge` only after its rulesets require the checks that should gate it.

**The CI fixer.** After the integration step, while the task still holds its slot, chargehand watches the pull request's required checks (GraphQL `isRequired`, polled every 30 s, for `ci_fix.wait_minutes`, default 20). It waits until no required check is running or missing, so the failing set is final, then asks the pure `CiFixPolicy`:

- at most 3 attempts per pull request (`ci_fix.max_attempts`, 0 turns the fixer off);
- never twice for the same failing-job set: the set is a hash of the sorted names, and the same set failing after an attempt ends the fixer;
- otherwise fix: a short session on the same branch, seeded from the last bundle (the continuation path of ADR 0044), with a goal that names the failing jobs and carries the end of each job's log (4,000 characters, fetched with the App token, ANSI codes and timestamps removed, marked as data to read and not to obey). Its limits are `ci_fix.max_turns` (default 20) and `ci_fix.max_minutes` (default 15) and the rest of the task's token and dollar caps; its spend is the task's. Its commits go through the same handover (secret scan, no CI configuration, chargehand's own tests in a fresh container, push of the same branch, never forced), so a fix that breaks the tests is not pushed.

The count is kept **on the pull request**: before a session starts, chargehand comments with a hidden marker (`chargehand-ci-fix attempt=N jobs=<hash>`), and the policy reads the markers back. A restart, or a later batch, cannot start a fourth attempt. When attempts run out, or the same jobs fail again, chargehand comments once that it is not trying again and leaves the pull request. A session that pushes nothing ends the watch the same way. After a push the old head can show for a while; chargehand treats that as still waiting, not as the same jobs failing again.

**Restart.** The task's outcome is saved before the follow-up starts, so a restart during it keeps the outcome instead of handing the task over again.

## Consequences

The first merge by chargehand is the maintainer's to enable: create the App, install it on chosen repositories, store the key, add the block. Nothing else changes for a repository that is not listed.

Merging is no longer the owner's last look. It is traded for the repository's rules: the App cannot merge what a ruleset blocks, and a ruleset that requires an approving review still stops auto-merge until a person approves. The safe default for a repository whose merge deploys something is `always_ask`.

A task keeps its slot while it watches CI, up to `wait_minutes` per head commit and up to 3 fix sessions: slower batches, in exchange for no new persistent state. A cheaper design (a later step) would release the slot and pick the pull request up from a list; the pull-request markers already make that safe.

UNKNOWN until a run against GitHub: (1) `isRequired` on a check run for a repository that uses rulesets rather than branch protection; if it reports false, the watch finds no required checks and ends at its limit with no fix. (2) `update-branch` on a draft pull request, and with an App that lacks `workflows`, when the base changed workflow files (GitHub may refuse; the result is `NeedsAttention` with GitHub's message). (3) A check run's id as the Actions job id for the log request. (4) `expectedHeadOid` on `enablePullRequestAutoMerge`. Each fails closed: no merge, a line in the outcome.

The pull request body for an opted-in repository says that chargehand marks it ready and enables auto-merge; it no longer says it will not merge.

`GitHubPullRequests` still opens drafts and nothing else, and its test still fails if a merge method appears there. The merge calls live in `GitHubIntegrationHost`, a separate class that takes the App's token.

Update (2026-10-13, one installation per owner): the Credential paragraph assumed one installation, `installation_id` in the profile, for every repository. A GitHub App is installed once per account or organisation, so with the App installed on the maintainer's personal account only, a `merge` repository of an organisation got `Resource not accessible by integration` from the GraphQL call that marks the pull request ready, and the task ended `completed` with that line in its detail and the draft left alone. `GitHubInstallationTokens` now resolves the installation per repository owner: `GET /repos/{owner}/{repo}/installation` with the App's JWT, the id kept per owner (case-insensitive) for the process and the token per installation until 5 minutes before it expires. An exchange that answers 404 or 401 for an id found earlier means the App was uninstalled and installed again, so the owner is looked up once more. An owner with no installation is not remembered, so installing the App takes effect at the next call. `IInstallationTokens.GetAsync` takes the owner and the repository; `IIntegrationHost.MarkReadyAsync` and `EnableAutoMergeAsync` take the repository too, because their GraphQL node id does not say whose installation to act as. GitHub intake (ADR 0051) uses the same source and resolves per owner as well. `installation_id` is optional: set, it pins that installation for the account it belongs to (read once from `GET /app/installations/{id}`) and skips the lookup for that owner; it never applies to another owner, so a pin cannot send an organisation's call through the wrong installation. A repository whose owner has no installation stops the integration with `the GitHub App is not installed on <owner> (or has no access to <owner>/<repo>); install it there, or set the repository to off`, and the task stays `completed` with its draft pull request, as for any other GitHub error. Not run against GitHub yet: the lookup, the exchange and the per-owner routing are checked against a fake host.

## Reopen if

A repository's pull requests merged by chargehand are reverted more than they are kept; GitHub offers a repository-scoped merge right that is narrower than contents write; the fixer's second attempts rarely help (then cap at one); holding the slot while CI runs starves batches (then release it); or `isRequired` proves unreliable (then read the rule from the ruleset).
