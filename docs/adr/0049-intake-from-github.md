# 0049. Intake from GitHub issues and comments

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
- Review comments on a chargehand pull request are not handled here. They reuse the poller (CHARGEHAND-184).

## Reopen if

A reachable endpoint becomes available (then a webhook replaces the poll, and the state file, the goal and the limits stay); polling costs more than the delay is worth; or the label's actor cannot be read reliably (then the issue's author and a mention become the only triggers).
