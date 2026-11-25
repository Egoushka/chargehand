# Land, don't push: a landing service, intent records and an operation log over plain git

- Status: proposed 2026-11-23. The maintainer approved the direction on 2026-11-23; agent races, the fourth stage
  first proposed, were declined the same day and are out of scope.
- Date: 2026-11-23
- Decision: [ADR 0102](../adr/0102-agents-land-changes-through-a-service-not-git-push.md)
- Builds on: [ADR 0046](../adr/0046-integrator-and-ci-fixer.md) (integrator, App tokens per owner),
  [ADR 0051](../adr/0051-intake-from-github.md) (intake, claim refs), [ADR 0074](../adr/0074-provenance-trailers-on-commits.md)
  (provenance trailers), [ADR 0084](../adr/0084-the-intake-governor-admits-queued-work-by-checks.md) (intake governor),
  [ADR 0090](../adr/0090-an-event-log-inside-chargehand.md) (event log), issue #737 (merge check before push).
- Schema rule: published majors take additive changes only. This spec adds three new schemas (`landing/v1`, `lease/v1`,
  `oplog/v1`) and changes none.

## Conclusion

Agents stop running `git commit` and `git push`. They edit files in a worktree or sandbox and call `land(spec, checks)`,
as an MCP tool or `ws land`. Chargehand's landing service snapshots the tree, commits it as the run, predicts conflicts
with `git merge-tree --write-tree`, merges one landing at a time per repository, checks `main` after the merge and
reverts on red. The credential is a one-hour GitHub App installation token scoped to one repository. Agents never see
it, and no personal access token is left. Every landed commit carries its intent: spec, checks, scope, risk, agent and
a pointer to the transcript. The maintainer approves the intent, and the diff is the evidence. Every snapshot and land
is an entry in an operation log, so any agent action can be listed and undone. Git and GitHub stay as they are: this
is a service in front of them, not a new version control system.

The work comes in four stages, each gated by the one before: 0 preconditions, 1 landing, 2 intent, 3 operation log.
Each stage has a machine-run check and a stop condition. Stage 1 must beat the 30-day baseline below, or the later
stages do not start.

## Problem

The owner's 30-day transcript scan of 2026-11-07 (workspace decision D-107; the scan is the `transcripts.py` step-0
script named there) counted, across personal repositories:

| Pain | 30 days |
|---|---|
| Conflicts on `main` | 181 |
| Push rejections | 40 |
| Sandbox bypass (share of Bash calls) | 33.0% |
| Foreground waits | about 110 h |

D-107 and D-108 fixed part of this with glue: per-run App tokens, serial merges in the integrator, auto-merge, the
`merge-tree` check before a push (#737), and the intake overlap hold. What is left:

- **Agents still hold git's write path.** An agent can run `git commit`, `git push` and `git push --force` with whatever
  credential its shell has. Guards are string matches on shell commands, which is why the sandbox is bypassed in a third
  of Bash calls.
- **Work is left uncommitted.** Worktrees end dirty or orphaned. Nothing snapshots them before a sweep removes them.
- **Conflicts are found late.** The merge check runs against `main` only, not against the other landings in flight.
  MergeGym (arXiv 2610.04779, 2026-10-03) found that 79 of 167 textual conflicts (47.3%) were between pull requests whose
  authored files did not overlap at all. A file lock or a declared-path hold misses those; a three-way merge finds them.
  AgenticFlict (arXiv 2604.03551, AIware 2026) found that 27.67% of 107K+ agent pull requests conflicted.
- **Review is of diffs.** What the owner wants to approve is the spec and its checks. The squash commit carries `Spec`,
  `Claim` and `Checks` trailers (ADR 0074, issue 735), but not the transcript, scope or risk, and nothing finds them
  from a line of code.
- **No undo.** An agent's bad rebase, reset or deleted worktree is gone unless git's reflog happens to have it.

## Prior art

- **Anthropic Scion** (https://www.anthropic.com/research/redesigning-source-control, 2026-10-13). "Every commit,
  rebase, or merge happens on the server, so there's nothing to push"; "Clients are always online"; "the development
  lifecycle in Scion discourages refs" except `main` and release branches; source control queries are "exposed via
  API". It also says "Scion is by no means a suitable replacement for git for general use", and that its authors hope
  "other developers can apply some of the most successful parts of Scion to git". This spec does that: commits and
  merges happen in one service, agents are always online, branches are transport only, and every operation is an API
  call.
- **jj** (jj-vcs/jj). The operation log and `jj undo` are the model for stage 3. The owner's pilot of 2026-10-30
  (workspace decision D-63) found that jj broke `ws ship` (`git push -u origin HEAD` on a detached HEAD), `git worktree
  remove` refused a jj workspace, and one agent's rebase left another's workspace stale. Stage 3 keeps the idea and
  stays plain git.
- **Worktree managers** (claude-squad, emdash, vibe-kanban, from the prior-art base). They isolate agents in worktrees
  and leave committing, merging and cleanup to the agent or the human. None of them lands changes.
- **Counter-view.** Simon Willison's guide treats git as "a key tool for working with coding agents"
  (https://simonwillison.net/guides/agentic-engineering-patterns/using-git-with-coding-agents/), and his note on the
  Scion post calls it "a reinvention of centralized source control along the lines of Subversion"
  (https://simonwillison.net/2026/oct/13/redesigning-source-control-at-anthropic/). This spec answers it by keeping git and GitHub, and by measuring every stage against the baseline:
  a stage that does not beat plain git stops.

## Design

### Where it lives

All of it lives in chargehand, as `src/Modules/Chargehand.Modules.Landing`. Chargehand already holds the pieces:
`GitHubInstallationTokens` (per-owner installations, tokens per repository), the integrator, the handover's merge
check, provenance trailers, the event log and the server's MCP tools. No new repository.

Considered and rejected: ghgate as the token broker. ghgate is a forward proxy. It would still let the agent's own
`git push` run, with a better token behind it. The point of stage 1 is that the agent has no push to make. ghgate
keeps its read path (cached, App-bucket reads). Its write path, which holds a personal access token today, ends once
`land` carries every write.

wheelhouse, the owner's session manager, reads leases and the operation log from chargehand's HTTP API. It shows who
holds which worktree and branch, and which landings are queued. The hub's `ws` gets the verbs `land`, `leases`, `oplog`
and `undo`. They are thin clients of the same API.

### Stage 0: preconditions, as tests against GitHub

Each item below is a test run against a throwaway repository on the owner's personal account. Its HTTP status and
response are stored as a fixture (scrubbed). Nothing in stage 1 is built on an assumption these tests could
contradict.

1. **Can an App installed on a personal account open a pull request?** One report says no: on personal-account
   installations "PR creation additionally requires a genuine collaborator role", and Apps cannot be collaborators
   (https://savas.me/2026/04/27/my-coding-agent-needed-its-own-github-identity/, update of 2026-07-02). ADR 0046 marks
   pull requests ready and enables auto-merge through the App, but chargehand opens them with the push token, and
   the ADR's per-owner update was checked against a fake host only. The test, with a token scoped to one repository:
   push a branch, `POST /repos/{o}/{r}/pulls`, `markPullRequestReadyForReview`, `enablePullRequestAutoMerge`,
   `PUT /pulls/{n}/merge`, push `refs/notes/intent`, push a revert.
2. **Stateless tokens.** Since 2026-10-02, installation tokens are `ghs_APPID_JWT`, about 520 characters instead of 40
   (https://github.blog/changelog/2026-10-02-stateless-github-app-installation-tokens-rolled-out/). Chargehand's
   `DiffScan` pattern `gh[pousr]_[A-Za-z0-9]{36,}` (`src/Shared/Chargehand.Kernel/DiffScan.cs`) stops at the
   underscore after the App id, so it does not match a stateless token. The test mints a real token and runs it
   through every redactor on the path: `DiffScan`, the event log's redaction, betterleaks, the agent-checks denylist,
   ghgate's logs. It also passes the token through every store and header on the path (secret store item,
   environment variable, `Authorization` header through ghgate) and checks that none truncates it.
3. **Serial merges on a user-owned repository.** D-107's open item: GitHub's merge queue on a user-owned repository is
   UNKNOWN. The test tries to enable it. Stage 1 does not depend on the answer, because the landing queue serialises
   merges itself, but a working merge queue would replace that part of it.

**Check.** `dotnet test --filter "FullyQualifiedName~LandingPreconditionTests"` passes against the recorded fixtures,
and `chargehand landing preconditions --repo <owner>/<throwaway>` exits 0 live. **Stop.** If item 1 fails (an App
cannot open or merge a pull request on a personal account), stage 1 does not start as written. The ADR gets an update
choosing between: moving the repositories to an organisation the owner controls; a machine user with fine-grained
tokens scoped to one repository (which keeps a PAT and fails stage 1's PAT check); or landing straight to `main` with
the App under a ruleset that requires the checks (no pull request). The choice is the maintainer's.

**Results (run live 2026-11-25, fixture `tests/Chargehand.Kernel.Tests/Fixtures/landing-stage0.json`, probe `scripts/landing-preconditions.mjs`).**
The chargehand integrator App was used against a private throwaway repository on the personal account (created and deleted by the run),
with a one-repository installation token.

1. **Pass.** The App opened a draft PR (201), marked it ready, merged it (200), pushed `refs/notes/intent` (201), pushed a revert branch
   and opened and merged the revert PR (201, 200). The report that an App cannot open PRs on a personal account does not hold for an App
   installed on all of the account's repositories with `pull_requests: write` and `contents: write`. `enablePullRequestAutoMerge` was
   refused with "Auto merge is not allowed for this repository": that is the repository's `allow_auto_merge` setting, so the landing
   service enables it per repository or merges directly. Open question 4 is answered: `refs/notes/intent` is accepted with an App token.
2. **Fail, then fixed.** The real token is `ghs_<7-digit app id>_<JWT-shaped body>`: 390 characters for a one-repository token with this
   App's permissions (up to about 520 in other shapes), with `_`, `-` and two `.` inside. Patterns of the form `gh[pousr]_[A-Za-z0-9]{N,}`
   stop at the underscore after the app id and match nothing. Checked with synthetic tokens of the same shape:

   | Place | Before | After |
   |---|---|---|
   | chargehand `DiffScan` (push scan, transcript redaction) | no match | PR: this one |
   | chargehand `Redaction.Scrub` after `Bearer` | redacted only `ghs`, left the tail | PR: this one |
   | betterleaks default rules | no match | rule `github-app-token-stateless` in `.githooks/betterleaks.toml`, this PR |
   | `bin/llm-guard.py`, `src/hookd/Guard.cs` | no match | workspace PR (see #1071) |
   | Bifrost guard `masker.go`, qmd `mirror.mjs` (the path to Langfuse) | no match | homelab PR (see #1071) |
   | `bin/leak-guard.py` | not a secret scanner (employer terms only); a token neither trips nor is altered | none needed |
   | HTTP `Authorization` header, GitHub API | accepted, not truncated (every call above used it) | none needed |
   | Langfuse | no redactor of its own; LiteLLM keeps message content off (`turn_off_message_logging`), and Bifrost's masker runs before it | UNKNOWN whether Langfuse truncates a 520-character string in a stored field; not tested to avoid writing to production traces |

   Other repositories carry the same pattern (claude-mods, wheelhouse daemon, chronicle-public, JARVIS, tin, feed-eater, agent-skills):
   listed on #1071, not part of the landing path.
3. **Fail, with the named fallback.** A `merge_queue` ruleset is rejected on the user-owned repository (422 "Invalid rule
   'merge_queue'"; a control ruleset with a `deletion` rule was accepted on the same repository, and the App gets 403 for rulesets). A
   merge queue is not available here, so the landing service's own one-at-a-time merge stays, as the spec says it does.

**Verdict.** Items 1 and 2 pass (2 after the pattern fixes above); item 3 fails with the fallback already in the design. Stage 1 may
start. `chargehand landing preconditions` as a CLI command is stage 1 work; until then the probe script is the live check.

### Stage 1: the landing service

**The call.** `land` takes a `landing/v1` request:

- `repo`: `owner/name`;
- `worktree`: a path the caller holds a lease on;
- `base`: the commit the work started from;
- `spec`: a path and commit, or an issue number;
- `checks`: the acceptance commands, each with an id;
- `scope`: path globs;
- `risk`: `none`, `low` or `high`;
- `run`: the run id, which carries the agent's identity.

The MCP tool and `ws land` take typed fields. Neither has a shell string anywhere in it.

**Snapshot.** The service builds a temporary index (`GIT_INDEX_FILE`), runs `git add -A` (which honours `.gitignore`),
then `write-tree` and `commit-tree` on `base`. The result is stored as `refs/snapshots/<run>/<seq>` in the service's
mirror of the repository. The worktree is never left with work that exists nowhere else. The agent's own index and
`HEAD` are not touched.

**Agents cannot write to git.** The raw write path is unreachable, not just guarded:

- an agent's sandbox carries no GitHub credential (no token in its environment, no credential helper);
- ghgate refuses `git-receive-pack` and every write method for agent client tokens;
- the only writer is the service, which reads the token from `IInstallationTokens` and never returns it.

A local `git commit` inside a sandbox stays possible and harmless: the snapshot reads the tree, not the commits.

**Identity and token.** The service gets a token from `GitHubInstallationTokens.GetAsync(owner, repo)`, which already
asks for one repository. The commit's author is the run (`<run name> <run id>`, at the App's noreply address), and its
committer is the App. The ADR 0074 trailers are added. The token is never written to disk and never logged.

**Conflict prediction.** On `land`, the service merges the snapshot with:

- the current `origin/main`, by `git merge-tree --write-tree --name-only`;
- each landing that is queued or merging in the same repository, the same way.

Results:

- a conflict with `main` returns `conflict` with the file list, and the agent rebases and lands again;
- a conflict with a queued landing queues the new one behind it, and the result says so;
- no conflict joins the queue.

There are no file locks. A three-way merge finds the conflicts that disjoint file sets hide (MergeGym's 47.3%).
MergeGym measured a median replay of 0.02 s, so the service replays every pair instead of guessing which ones matter.

**One merge at a time per repository.** The queue is per repository, first in first out. A landing can jump the queue
only when it is a revert. The head of the queue is merged onto the current `main` with `merge-tree`, pushed as branch
`land/<id>` and opened as a pull request. The integrator then merges it with ADR 0046's rules: auto-merge with
`expectedHeadOid`, or a pinned squash merge. Branches are transport only. A `land/<id>` branch is deleted when its pull
request merges or closes, and no agent creates a branch of its own.

**Check after merge, revert on red.** After the merge, the service waits (event-driven, not polling) for the required
checks on the merge commit on `main`. If any of them fails, it lands a `git revert` of that commit at the head of the
queue, comments on the pull request, and opens the run's continuation with the failing job's log (ADR 0046's CI fixer
path). It never force-pushes and never rewrites `main`.

**Leases.** A `lease/v1` record holds:

- `resource`: a worktree path on a host, or a branch;
- `holder`: session id, host and run;
- `acquired_at`, `heartbeat_at` and `expires_at`. The TTL is 15 minutes and the heartbeat comes every 60 s, both
  starting values.

Leases live in chargehand's SQLite store next to the event log. The VPS and the laptop both hold leases through the
same HTTP API, and `ws` sends the laptop's heartbeats. `refs/claims/<n>` stays the claim on an issue (ADR 0051), and a
lease is the claim on a working copy. `GET /v1/leases` feeds wheelhouse.

**Reaper.** Every 10 minutes (a starting value), for each worktree whose lease has expired or that has no lease and has
not been touched for 24 h:

1. snapshot it;
2. check that `git diff --quiet <snapshot> -- .` and an untracked-file list both come back empty against the snapshot;
3. then run `git worktree remove`.

If step 2 fails, the worktree stays and an event is raised. The reaper never runs `--force` on a worktree it has not
snapshotted and verified, and never deletes a branch that is ahead of `main` without a snapshot ref. This takes over
from `scripts/sweep-worktrees.sh`.

**Check.**

- `dotnet test --filter "FullyQualifiedName~Landing"` passes, including:
  - `Land_snapshots_an_uncommitted_tree`;
  - `Sandbox_push_has_no_credential`;
  - `Conflict_with_a_queued_landing_queues_behind_it`;
  - `Disjoint_file_conflict_is_found_by_merge_tree`;
  - `One_merge_at_a_time_per_repository`;
  - `Red_main_after_merge_lands_a_revert_first`;
  - `Reaper_never_removes_an_unsnapshotted_worktree`.
- 30 days after the owner's repositories switch to `land`, the D-107 transcript scan, rerun over those 30 days, reports
  these figures. All targets are starting values.

  | Measure | Baseline (D-107) | Target |
  |---|---|---|
  | Conflicts on `main` | 181 | ≤ 60 |
  | Push rejections | 40 | ≤ 5 |
  | Sandbox bypass | 33.0% | ≤ 15% |
  | Foreground waits | about 110 h | ≤ 40 h |

- Zero personal access tokens in the owner's deployment secrets. Every value is decrypted where the key lives, and
  only the count of values starting `ghp_` or `github_pat_` is printed.
- `ws doctor` reports no dirty main checkout and no orphan or unregistered worktree.

**Stop.** If conflicts plus push rejections are not at least halved against the baseline after 30 days, or a reaper
run ever loses work, landing goes back behind a profile switch. Agents return to `ws ship`, and stages 2 and 3 do not
start.

### Stage 2: intent-first review

**The record.** At merge, the integrator writes two things.

The squash commit's trailers, extended from D-108's change record: `Spec`, `Claim`, `Checks`, `Scope`, `Risk` and
`Agent`, plus `Landing: <id>`. `Provenance.ChangeRecordKeys` gains `Scope`, `Risk` and `Agent`.

A git note on `refs/notes/intent` for the same commit, as JSON:

- the spec's path and commit, or the issue;
- each check's id, command, exit code, and the sha256 of its log;
- the scope and the files actually changed;
- the risk;
- the run id and the agent's identity;
- the transcript pointer, which is an id in the owner's trace store plus the transcript's sha256, never the transcript
  text (repositories are public);
- the landing id and the snapshot ref.

Notes are written by the landing queue, which is serial per repository, so two writers never race on the notes ref.

**Review.** The pull request body starts with the spec, the checks and their results, and the transcript link, and the
diff comes after. The maintainer approves the spec before the work starts (D-81). At land, D-108's auto-merge rules
apply unchanged: every check the spec names passed, no file is outside `Scope`, and `Risk` is `none`. Anything else
waits for the maintainer.

**Migrations always wait for the maintainer.** A landing that adds or changes a database migration never auto-merges.
It needs a passing `efcore-migration-safety` review and the `migration-reviewed` label, and then the maintainer merges.

**Prompt blame.** `chargehand blame <path>:<line>` runs `git blame` and reads the commit's note. It prints the spec,
the checks and the transcript pointer that produced the line. The same works across the owner's roughly 60 repositories
through the API (`GET /v1/blame`).

**Check.**

- `dotnet test --filter "FullyQualifiedName~IntentRecord"` passes, including `Every_landed_commit_has_an_intent_note`,
  `Migration_never_auto_merges` and `Blame_returns_the_spec_for_a_line`.
- `chargehand intent audit --since <stage-2 start>` exits 0 across the owner's repositories. It finds no commit landed
  through `land` that lacks its trailers or its note.
- D-108's check carries over: of the next 10 landings with an intent record, at least 8 merge with no human review
  comment and no fix-up within 7 days (starting values).

**Stop.** If the 8-of-10 check fails, the record stays as documentation and the auto-merge rule is dropped, as in D-108.
If GitHub refuses pushes of `refs/notes/*` (stage 0, item 1), the note moves into the trailers alone, and the transcript
pointer becomes a trailer.

### Stage 3: operation log

**Entries.** Each `oplog/v1` line holds:

- `seq`, `at`, `host`, `repo` and `worktree`;
- `session` and `run`;
- `kind`: `snapshot`, `land`, `revert`, `reap` or `undo`;
- `before` and `after` (commit ids);
- `ref`.

An entry is written on every land, every reap, and on agent hook events (after an edit tool, after a Bash call that
changed the tree, at session end). Each `before` and `after` is a shadow ref, `refs/oplog/<host>/<seq>`, in the
service's mirror. The laptop's shadow refs are pushed to the VPS mirror. Shadow refs are never pushed to GitHub.

**List and undo.** `ws oplog [--repo] [--session] [--since]` lists entries across all repositories.
`ws undo <seq>` restores the entry's `before` tree into the worktree. The undo is itself a snapshot and an `undo`
entry, so it can be undone too. For a `land` entry, undo lands a revert through stage 1. Undo never rewrites `main`.

**Retention.** Shadow refs are kept for 30 days (a starting value), then pruned. A ref that a landing or an open lease
still points at is never pruned.

**Plain git.** There is no jj and no colocated repository. `ws ship`, the sweep and `git worktree` keep working
unchanged (D-63).

**Check.**

- `dotnet test --filter "FullyQualifiedName~OperationLog"` passes. Every kind of undo has a test that builds the state,
  applies the operation, undoes it, and asserts the tree and refs match the `before` state byte for byte:
  - `Undo_snapshot_restores_tree`;
  - `Undo_reap_restores_worktree`;
  - `Undo_land_lands_a_revert`;
  - `Undo_of_undo_restores_after`.
- The p95 snapshot time on a hook event is under 500 ms on the owner's largest repository (a starting value).

**Stop.** If snapshots on hook events push the p95 over 500 ms, hook snapshots go to session end only. If no one runs
`ws undo` in 60 days, the hook snapshots are turned off, and land and reap entries stay.

## Out of scope

- A new version control system, a self-hosted forge, or jj (D-63, D-107).
- Agent races: N agents per task with a selector that picks a winner. The maintainer declined them on 2026-11-23.
- Employer repositories. Landing applies to the owner's own repositories only.
- Changing `result/v1` or any other published schema.

## Tasks

1. Stage 0 precondition tests and the `DiffScan` fix for stateless tokens.
2. `landing/v1` and `lease/v1` schemas; the snapshot, the queue with `merge-tree` prediction, the `land` MCP tool and
   HTTP route; `ws land`.
3. Leases, heartbeats and the reaper. `GET /v1/leases` and the wheelhouse view.
4. Check after merge and revert on red; ghgate stops writing; personal access tokens retired from the deployment.
5. Stage 2: trailers, the intent note, migration hold, `chargehand blame`, `intent audit`.
6. Stage 3: `oplog/v1`, hook snapshots, shadow refs, `ws oplog` and `ws undo`.

## Acceptance checks

Each stage lands only when its own check passes. The stage sections above give each check in full, and its stop
condition. Every target below is a starting value.

1. Stage 0: `dotnet test --filter "FullyQualifiedName~LandingPreconditionTests"` passes, and `chargehand landing
   preconditions` exits 0 live.
2. Stage 1 tests: `dotnet test --filter "FullyQualifiedName~Landing"` passes.
3. Stage 1 against the D-107 baseline, from the 30-day transcript rerun:
   - conflicts on `main` ≤ 60 (baseline 181);
   - push rejections ≤ 5 (baseline 40);
   - sandbox bypass ≤ 15% (baseline 33.0%);
   - foreground waits ≤ 40 h (baseline about 110 h).
4. Zero personal access tokens in the deployment's encrypted env files, counted by prefix, with only the count
   printed.
5. `ws doctor` shows no dirty main checkout and no orphan worktree.
6. Stage 2: `chargehand intent audit` exits 0, so no landed commit is missing its intent note. `dotnet test --filter
   "FullyQualifiedName~IntentRecord"` passes.
7. Stage 3: `dotnet test --filter "FullyQualifiedName~OperationLog"` passes, and every undo kind has a reproducing test.
8. Every task: `scripts/check.sh` exits 0, with its last test line reading `Passed!  - Failed:     0`.
   `betterleaks git --redact -v` finds nothing, and the files touched contain no hostname, IP or home path.

## Open questions

1. Answered 2026-11-25 (stage 0 results): yes, an App installed on the personal account opens and merges pull requests.
2. Answered 2026-11-25: every pattern checked missed them; fixed (table in stage 0). Open: whether Langfuse truncates a long stored field (UNKNOWN).
3. Answered 2026-11-25: no. The service's own serial merge stays.
4. Answered 2026-11-25: GitHub accepts a `refs/notes/intent` ref pushed with an App token (201). Whether it keeps it across a squash merge is a stage 2 check.
5. How does the laptop reach the lease API when it is offline? Proposed answer: leases from an offline host expire,
   and the reaper only snapshots on that host when it is back, never removes from afar. To be confirmed in task 3.
6. Scope: the owner also asked for a "better git experience" for himself (session 01M858J7G1Y5AAMBQS6NPEPMZ3, wheelhouse). This spec covers agent actions only (humans appear as the reviewer of intent and in the lease view). Whether a human-facing git tool (his own commits, status, undo) belongs in this work is open.
