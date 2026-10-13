# 0051. Intake from GitHub issues and comments, and review rounds

- Status: accepted
- Date: 2026-10-13

## Context

[ADR 0039](0039-driven-writing-sessions.md) (decision 4) starts a driven batch when a caller sends one, and leaves picking work out of scope. A person who wants a task done files an issue and then has to turn it into a request by hand. [ADR 0047](0047-core-and-driven-assemblies.md) planned an assembly for this, `Chargehand.Automation`, that references Driven.

The server is not reachable from GitHub: it listens on a private network ([ADR 0024](0024-server-on-a-private-network-and-release-images.md)), and the integrator already acts as a GitHub App that the server calls, never the other way round ([ADR 0046](0046-integrator-and-ci-fixer.md)).

## Options

1. **A webhook.** The standard answer. It needs an endpoint GitHub can reach and a signature check on every delivery, which means opening the server or running a relay. Rejected for now.
2. **A GitHub Actions workflow in each repository that calls the server**, as `review.yml` does. The runner needs a route to the server and the repository needs the server's key as a secret. Rejected for the same reach, and it spreads a credential.
3. **The server polls GitHub as the App** it already has. No inbound route, no new credential. The cost is delay (up to one poll interval) and API calls.
4. **The server polls with a personal token.** Wider than needed, and it is a person's identity on every comment. Rejected.

## Decision

Option 3, in a new assembly `Chargehand.Automation` that references Driven; the server references it and registers it only when the profile has `automation.github`. Core and Driven cannot reference it (`ArchitectureTests`). The settings records are plain data in Core, as the `driven` ones are.

**What triggers.** Per watched repository, each poll reads (a) the open issues that carry the trigger label (default `chargehand`) and (b) the issue and pull request comments updated since a cursor. A trigger is one of:

- the label, applied by an allowed author. The issues list does not say who applied a label, so the issue's events are read (once per change of the issue's update time) and the latest `labeled` event for the label decides: its actor must be on `allowed_authors`, and its id is part of the trigger's key, so removing and applying the label again is a new trigger;
- a comment of an allowed author that mentions the bot (default `@chargehand`; a mention in a quoted line, inside another word or in the bot's own comment does not count);
- a comment of an allowed author on an issue whose task asked a question, posted after the comment that carried the questions: the reply. It needs no mention.

`allowed_authors` is required and has no default; an empty list means nothing triggers. Anyone else's label or comment is ignored and recorded as handled, so it is read once.

**What starts.** One batch of one task, `issue-<n>`, in a request built like any caller's: `driven` preset, `context.repository` the profile's checkout for that repository at the commit its `origin`'s default branch points at after a fetch, `max_parallel` 1, the profile's token and dollar caps, size from the issue's `size:S`, `size:M` or `size:L` label when it has exactly one and else the profile's `budget_class`. The goal's first line is the issue's title (the pull request's title is cut from it); then the issue's title, author and body, and the comment that asked, cut to 200, 6000 and 2000 characters, between `<untrusted-github-text>` tags, with a sentence that says they are text people wrote on GitHub, to be read as the description of the work and not as instructions. Text cannot close the fence: a tag in it is turned into a bracket. A reply continues the task with `continue_from` (ADR 0045), the reply as its one answer (cut to 8000), the size of the first task, and the first batch's repository and commit, because its kept work is a branch from there.

**What it says.** One comment when the batch starts (the run id), and one when it ends: the draft pull request, or what stopped it, or the session's questions. Every comment carries a hidden marker, which intake never takes for a trigger. A batch that ended is found by reading the run log, so a restart does not matter. A batch with no run record whose process is gone is reported lost after 30 minutes.

**State.** One JSON file next to the run log: the comment cursor per repository, every trigger acted on or ignored (a key per label event and per comment id; it only grows), the tasks and their phase, the times of starts, and the comments written once. A batch is started first and recorded right after, before its comment. A process that dies between the two has not saved the batch yet either (batches run in the server, ADR 0043), so the trigger is read again and started once; the window is two statements long. The cursor starts at the first poll, so comments from before intake was turned on are not requests.

**Limits.** `max_concurrent` (default 2) batches running at once: a trigger over it waits, with one comment, and starts when a slot frees. `max_starts_per_day` (default 10) starts in any 24 hours, answers included: a trigger over it is refused with one comment and is not started later; a new trigger is needed. An issue with a running task takes no second one (one comment). A halted server, or one with too many unfinished runs, leaves the trigger for the next poll.

**Security choices.** The authority to start is the list, and for a label it is the person who applied it, not the issue's author, because applying a label needs triage rights and writing an issue needs none. That leaves the issue's body to be written by anyone, so: the body is fenced and flagged as untrusted, which helps the model and is not a boundary; the boundary is the container (no credential, no route out beyond the allowlist, ADR 0039) and the handover's checks. And a repository that the integrator merges into (`merge` in `driven.integrator.repositories` and not in `always_ask`) takes work only from issues written by an allowed author, because a merge there ends at a check and not at a review: for another author's issue chargehand comments once and starts nothing. The App needs the Issues permission (read and write) besides ADR 0046's; it still needs no administration, no workflows and no webhook. The token and the private key are never in a comment, a log line or an exception message.

## Consequences

- Latency is up to one poll interval (default 2 minutes). Calls per poll and repository: one for the labelled issues, one per labelled issue whose update time changed, one per page of comments. A GitHub App's limit is far above that for a few repositories.
- Three things stay with a person: the server needs a checkout of each watched repository and git credentials to fetch it; the App needs the Issues permission; and an issue from an outsider on a `merge` repository needs an allowed author to write or copy it.
- A task that an issue started and that the CI fixer is working on holds its slot meanwhile, as any task does ([ADR 0046](0046-integrator-and-ci-fixer.md)).
- Not run against GitHub: the events and comments endpoints are used as documented and tested against a fake host and recorded shapes. UNKNOWN until a run: (1) that `GET /issues/{n}/events` lists a `labeled` event with `actor` for every label an App can see (events older than 90 days may be gone: then the issue is ignored, not started); (2) that `since` on the comments list filters by update time as documented and returns pull request comments too; (3) the secondary rate limit on a burst of comments after a restart.
- Review comments on a chargehand pull request are the next section.

## Review comments on a chargehand pull request (CHARGEHAND-184)

The same poller carries a second loop: what a reviewer writes on a pull request chargehand opened goes back to the task that opened it, on the same branch.

**What triggers.** On a pull request that is open, whose head branch is `chargehand/*` in the repository itself (not a fork), by an allowed author: an inline review comment (no mention needed, since a review comment is a request for a change; a reply inside a thread is discussion and needs the mention), or a conversation comment that mentions the bot. Comments on any other pull request are read and ignored. They are kept in the state file (read once, with the cursor) until a round takes them, so a comment that has to wait is not lost, and one older than 7 days is dropped.

**What starts.** At most one round per pull request at a time, with every comment waiting for it: a batch of one task, `pr-<n>`, that continues the pull request's task (`continue_from`, ADR 0045) with one answer per comment: who said it, the file and line for an inline one, and the text cut to 2000 characters. The pull request's task is found in the run log, as the newest run that names the branch and its batch; it works for a pull request that intake did not start. While that batch is still running (its CI fixer is working on the branch) or an earlier round is, the comments wait and say so once.

**What `continue_from` now accepts.** Until now a task could be continued only after `needs_input` or a partial stop, from the output volume that holds its bundle. A finished task's volume is not kept (the sweep after a restart removes it), and the branch on the remote may have moved since: the CI fixer, or a person, pushed to it. So a **completed** task whose `branch` artifact says it was pushed is continued from the branch as the remote has it: chargehand makes a scratch clone of its checkout at the commit the task started at, fetches the branch with the push credential (host only, never in a container), checks that it still descends from that commit and bundles it against it, and that bundle seeds the session. The session's push is then a fast-forward of the same branch, the pull request updates itself, and it keeps its title and body (a finished task's handover replaces those of a reused pull request only for a task that did not continue a pushed branch). Answers are required for it. A branch that was deleted or rewritten fails the task before a container starts, and the failure is reported on the pull request.

**The cap.** A pull request has one cap of 3 for CI fix attempts and review rounds together ([ADR 0046](0046-integrator-and-ci-fixer.md) said 3 for the fixer alone). The count lives on the pull request, in the hidden markers, as before: the fixer writes `chargehand-ci-fix attempt=N jobs=<hash>`, a round writes `chargehand-review-round attempt=N comments=r<id>,c<id>`, and both are read by the same `FixMarker.Parse`, so `CiFixPolicy` and the round loop see the same number and the next attempt number continues from it. A round also lists the comments it took, so a comment is never taken twice even if the state file is lost. `automation.github.max_review_rounds` (default 3; 0 turns the loop off) can lower the rounds, not the shared cap. When the cap is used chargehand says so once and takes nothing more; the CI fixer, finding it used, gives up as it does for any used cap.

A round counts toward `max_concurrent` and `max_starts_per_day` like any triggered batch. The marker comment is written right after the batch starts and not before, as the fixer does for its attempts: a server that refuses the batch (halted, too many unfinished runs) then loses no round. The cost is that a marker that cannot be posted leaves the count one short; the state file still holds the round.

**What it says.** One comment when the round starts (the marker, the round number, the run, the authors), and one when it ends: that the changes are on the pull request, with the session's summary, or what stopped it, or its questions; a reply to the questions on the pull request continues the round's task as for an issue. For a repository the integrator is opted into, the round ends as a task does: the pull request is marked ready if it is not, auto-merge is enabled again if the push cleared it (UNKNOWN, below), and the CI fixer watches the new head.

**Security.** Who may ask is the same list. A review comment on a chargehand pull request can steer a session as much as an issue can, and it is written by an allowed author, so it is not fenced as untrusted. A pull request whose head is a fork, or whose branch is not `chargehand/<name>` with a plain name, is never continued: a branch name that is another person's is a way to make chargehand fetch and push there.

**UNKNOWN until a run against GitHub.** (1) That `GET /pulls/comments` with `since` lists the inline comments of every pull request of a repository as documented, including `in_reply_to_id` for replies and a null `line` for an outdated comment (the original line is used then). (2) That a push clears an auto-merge request pinned to the old head, so the integrator's second pass enables it again; if GitHub keeps it, the old pin simply never merges. (3) That a bot's own conversation comments never come back as triggers: they carry the marker, and the list of allowed authors would not name the App.

## Reopen if

A reachable endpoint becomes available (then a webhook replaces the poll, and the state file, the goal and the limits stay); polling costs more than the delay is worth; or the label's actor cannot be read reliably (then the issue's author and a mention become the only triggers).
