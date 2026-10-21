# 0070. Commits say which run wrote them

- Status: accepted. Extends 0039 (handover), 0046 (integrator) and 0035 (the `code` preset).
- Date: 2026-10-18

## Context

A line chargehand wrote shows `git blame` a commit and nothing about why: which goal, what it cost, which checks it passed. The run log has the answers, but nothing links a commit to a run, and a squash merge on GitHub drops every commit message of the branch. The link has to travel with the commit, in a form plain git reads: `git log --format='%(trailers:key=Chargehand-Run)'`. Once pushed, a commit's SHA is public history, so the format is hard to change later: it is an ADR.

## Options

1. **Git notes** (`refs/notes/chargehand`). Does not change the SHA. But notes are not pushed or fetched by default, and GitHub's squash merge never carries them. Rejected.
2. **A sidecar file in the repository** (`.chargehand/runs.json`). Touches every branch, conflicts between branches, and is public content in the repository. Rejected.
3. **Trailers in the commit message.** Plain git, survives clone and push. Costs one rewrite of the commit messages before the push, and needs care to survive a squash merge.

## Decision

Option 3. The trailers, after the body and before any `Co-Authored-By`:

```
Chargehand-Run: <run id>
Chargehand-Task: <task id> (<tracker ref>)      when there is one
Chargehand-Runtime: <runtime and version>, <model>
Chargehand-Spend: $<usd>, <tokens> tokens, <n>% cache
Chargehand-Checks: <short result of the verification or acceptance checks>
Chargehand-Result: sha256:<hash of the signed result/v1>      only when it exists before the commit
```

A line whose value is unknown is left out. A value is one line, at most 200 characters. `Provenance` (Kernel) writes, reads and places them; a message that already has `Chargehand-Run` is not stamped again.

**Where they are written.**

- *`code` preset:* `RunWorkspace.CommitAsync` is given the message with the trailers. The result is not named: it records this commit, so its hash cannot be inside it.
- *Driven sessions:* the session image is not touched. The handover, in its scratch clone, after chargehand's own verification and before the push, rewrites the commits past the base that have no `Chargehand-Run` yet with `git commit-tree`: same tree, same author and committer (name, address, date), parents mapped. The rewrite is deterministic, so a repeated handover makes the same SHAs and the push finds them on the remote; a commit from an earlier session, fetched back from the remote with its trailers, is kept. The spend and checks are those of the handed-over session (the task's total for a partial handover, where the branch holds several sessions' work). CI fix sessions stamp their own commits the same way. The driver's `WIP:` stop snapshot needs no stamp: it is a commit of the session branch and is stamped at the handover like the rest. `Chargehand-Result` is not written for driven sessions either: the task's result is built after the handover and cites the pushed head.

**Surviving a squash merge.** GitHub keeps a trailer only if it is at the end of the message it is given. Two measures, both in the server:

- The pull request body ends with the run's trailer block, so the repository's "pull request title and description" default keeps it, and a person squash-merging by hand sees it.
- When the integrator merges (`MergeNowAsync`) or enables auto-merge, it reads the pull request's commits and passes the `Chargehand-*` lines of the first commit of each run as the squash commit's message body (`commit_message` / `commitBody`), so a task and the CI fixes on its branch all appear. If the commits cannot be read, the merge goes ahead with GitHub's own message. GitLab's merge request squash is not handled; its description carries the block as on GitHub.

**Reading them back.** `chargehand why <path>:<line>` runs `git blame` for the line, reads the trailers of that commit, then adds what this machine's run log and batch records know: the goal (a `code` run's request, or a driven task's goal through the batch's records), the session's role, status, turns and spend. With `--stream <stream.jsonl>` it lists the session messages that edited the file; the stream is deleted with the session's output volume (0059), so it cannot be found by itself. A commit without `Chargehand-Run` is reported as a person's. It works without a profile, from the trailers alone.

## Consequences

- Every pushed commit of a driven branch has a new SHA relative to the bundle. Nothing keys on the bundle's SHAs after the handover: the outcome's commit, the CI watcher's head and the continuation seed all use the pushed commit.
- The merge now makes one more GitHub call (the commits), and the squash commit body is the trailers alone, not GitHub's default. The pull request body still has the goal and the report.
- Spend in a trailer is a dollar equivalent where the credential has no price. It is a record, not a bill.
- Commits made before this change, and by people, have no trailers; `why` says so. A merge on GitHub's web UI with a default that ignores the pull request body loses them; the branch's own commits are still stamped.
- The server needs this release for any of it. Session images do not change.

## Reopen if

A squash default that drops both the body and the trailers is common among the repositories chargehand merges into; a second runtime needs fields beyond these; or reading the run log by run id proves too slow with a large log (`why` reads the whole log, like `show`).
