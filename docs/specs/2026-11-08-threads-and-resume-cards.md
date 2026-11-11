# Threads and resume cards: where a piece of work stands, read from the log

- Status: proposed 2026-11-08, an addendum to [Events and watches](2026-11-02-events-and-watches-design.md) (approved
  2026-11-02, ADR 0090). The maintainer chose this direction on 2026-11-08 (option A of four: the log is where an agent
  learns where things stand). Nothing in the approved spec changes; this adds three event types, two read routes and
  their checks.
- Date: 2026-11-08
- Builds on: the approved spec's decisions 1 to 3 and 8 (store, envelope, transport, scopes) and its Task 3

## Conclusion

An agent that starts cold re-derives where the work stands from handoff files, which grow without bound (the
maintainer's workspace handoff reached 1,137 lines). Instead, at each step an agent appends one `thread.step.recorded`
event to the log: the thread it belongs to, the goal, what this step did and what the next step is. The server derives
the open threads from those events and returns a **resume card** per thread: the goal, the last step, the next step,
who moved it last and when. A session that starts reads the card for its folder or thread and continues.

Two more types are reserved, not built: `signal.rule.fired` and `wake.job.started|finished`, for scheduled jobs on the
maintainer's server that run hand-written rules over facts. They are named now so their producers, a later slice, need
no schema change.

## Terms

- **Thread:** one piece of work an agent may hand to another: a task, an issue, a decision being carried out. It has a
  stable id the agents choose (`d-114`, `Egoushka/chargehand#812`). It exists once it has one step.
- **Step:** one `thread.step.recorded` event. Steps are facts: a later step does not edit an earlier one, it follows it.
- **Resume card:** the thread's state derived from its steps, in a fixed shape and as a short text block a session can
  read without any other file.
- **Signal job / wake:** a scheduled chargehand job that evaluates a hand-written rule over facts in the log
  (`signal.rule.fired` when it matches) and may wake a run (`wake.job.started`, `wake.job.finished`). Anything that
  leaves the box stays a proposal through the approvals gate (approved spec, decision 5).

## Event types

Types follow the envelope rule `<domain>.<noun>.<past verb>` (approved spec, decision 2), which `events/v1`'s schema
enforces with three or more dotted parts. So the names asked for as `thread.step`, `signal.fired` and `wake.*` are
written `thread.step.recorded`, `signal.rule.fired`, `wake.job.started` and `wake.job.finished`.

### `thread.step.recorded` (built in this slice)

```json
{
  "specversion": "1.0",
  "id": "01JCB2W8M0R5V3T7K9Q1XYZAB",
  "source": "claude",
  "type": "thread.step.recorded",
  "subject": "d-114",
  "time": "2026-11-08T21:14:03Z",
  "datacontenttype": "application/json",
  "data": {
    "goal": "One event log a fresh session can resume work from",
    "done": "Spec addendum merged; threads view designed",
    "next": "Build ingest, query and the card on a chargehand-local store",
    "state": "open",
    "refs": ["https://github.com/Egoushka/chargehand/pull/<n>"]
  },
  "actor": {"kind": "agent", "id": "cc/<session>", "name": "Brave Capybara", "run": null},
  "effect": "write",
  "scope": "project:Egoushka/chargehand"
}
```

| Field | Rule |
|---|---|
| `subject` | The thread id. Required for this type. 1 to 128 characters, `^[A-Za-z0-9][A-Za-z0-9._:/#-]*$`. |
| `data.done` | Required, 1 to 2,000 characters: what this step did, in a sentence or two. Not command output or file content. |
| `data.next` | Required while the thread is open, 1 to 2,000 characters: the one next step a fresh agent should take. |
| `data.goal` | Required on a thread's first step; optional after it. The card shows the latest one given. |
| `data.state` | `open` (default) or `closed`. A closed thread leaves the open list; a later step with `open` reopens it. |
| `data.refs` | Optional, at most 20 strings of up to 300 characters: pull requests, branches, issues, paths. |
| `scope` | As for any event (approved spec, decision 8). The thread's scope is its latest step's scope. |

Server refusals, besides the envelope's: a `thread.step.recorded` without `subject`, without `data.done`, or open
without `data.next` gets 400; a first step without `data.goal` gets 400. The secret and `work/` refusals apply to every
field, as for any event.

### Reserved, not built: `signal.rule.fired`, `wake.job.started`, `wake.job.finished`

| Type | `subject` | `data` (planned) |
|---|---|---|
| `signal.rule.fired` | the rule id | `rule`, `facts` (the `seq`s it matched), `proposal` (the action it proposes, or null) |
| `wake.job.started` | the job id | `job`, `trigger` (the `seq` of the `signal.rule.fired` or `schedule.due` that woke it) |
| `wake.job.finished` | the job id | `job`, `outcome` (`ok`, `failed`, `skipped`), `proposals` (count) |

The server stores these like any event in this slice: no type-specific validation, no producer. Their `causationid`
chains a wake to the signal that caused it. The jobs, their rules and their checks are a later slice.

## Read routes

Both are derived from events on every call or from a projection kept beside the store; nothing is stored that the
events do not hold. Both are filtered by the caller's read grants (approved spec, decision 8): a thread whose latest
step is outside the grants does not exist for that caller.

- **`GET /v1/threads?state=open&scope=<glob>&limit=<n>`** returns the threads in the given state (`open` default,
  `closed`, `all`), newest activity first, at most `limit` (default 50, at most 200). Each item is the card's fields
  below without `text`. `scope` narrows the list; a SessionStart hook passes the folder's `project:<owner>/<repo>`.
- **`GET /v1/threads/{id}/card`** returns one card. An unknown thread, or one outside the read grants, gets 404 (not
  403, so a caller cannot probe for threads it may not read). The id is URL-encoded in the path.

The card:

```json
{
  "thread": "d-114",
  "goal": "One event log a fresh session can resume work from",
  "state": "open",
  "scope": "project:Egoushka/chargehand",
  "last_step": {"seq": 48212, "time": "2026-11-08T21:14:03Z", "done": "Spec addendum merged; threads view designed",
                "actor": {"kind": "agent", "id": "cc/<session>", "name": "Brave Capybara", "run": null}},
  "next": "Build ingest, query and the card on a chargehand-local store",
  "owner": {"kind": "agent", "id": "cc/<session>", "name": "Brave Capybara", "run": null},
  "last_activity": "2026-11-08T21:20:41Z",
  "steps": 3,
  "refs": ["https://github.com/Egoushka/chargehand/pull/<n>"],
  "text": "Thread d-114 (open) ..."
}
```

- `owner` is the actor of the latest step: whoever moved the thread last holds it until someone else records a step.
- `last_activity` is the `time` of the newest event, of any type, whose `subject` or `correlationid` is the thread id
  and which the caller may read; at least the latest step's time. So tool calls a run tags with the thread show the
  thread is alive between steps.
- `refs` is the union of all steps' refs, newest first, at most 20.
- `text` is the card as plain Markdown of at most 2,000 characters: goal, state, next step, last step with who and
  when, refs. It is what a session reads; the JSON fields are for code.

## What the SessionStart hook needs (a later workspace slice, not built here)

One call at session start, `GET /v1/threads?state=open&scope=project:<owner>/<repo>` with the Mac's token, then
`GET /v1/threads/{id}/card` for the newest (or for a thread id the session was started with), and the card's `text`
injected as context wrapped as another agent's text (approved spec, decision 8: data, never instructions). Writing a
step is one `POST /v1/events`. Nothing in the hook needs another route.

## The check (from the maintainer's decision)

Read once the hook exists: 10 fresh sessions each resume an open thread from its card alone, without a handoff file;
at least 8 of 10 pick the right next step (the card's `next`, or what the maintainer judges equivalent). **Stop:**
fewer than 5 of 10 after two revisions of the card's shape; handoff files stay and the log stays an audit trail. 8, 5
and 10 are the maintainer's numbers. This needs the hook and real threads, so it is not in `scripts/check.sh`.

## Tasks

- **T1 (this addendum):** docs only.
- **T2 (chargehand):** the approved spec's Task 3 on the store its Task 2 describes (ingest, query, SSE stream with
  `Last-Event-ID`, source tokens with scope grants, the secret and `work/` refusals), plus `thread.step.recorded`
  validation, `GET /v1/threads` and `GET /v1/threads/{id}/card`, and schema examples for the four types.
- **T3 (workspace, later):** the SessionStart hook that injects the card, and a step writer agents call.
- **T4 (chargehand, later):** signal jobs and wakes that produce the reserved types.

## Acceptance checks

All run in `scripts/check.sh` (`dotnet test`) unless marked otherwise.

**Schema (T2)**
- `SchemaFixtureTests` validates new examples under `schemas/events/v1/examples/`: `valid-thread-step.json`,
  `valid-signal-rule-fired.json`, `valid-wake-job-started.json`, `valid-wake-job-finished.json`, and
  `invalid-type-two-parts.json` (a type like `thread.step`, which the schema refuses).
- `SchemaCompatTests` stays green: the change to `events/v1` is additive (examples, descriptions), no required field
  added, no pattern narrowed.

**Thread steps (T2)**
- `ThreadStepTests`: a `thread.step.recorded` without `subject`, without `data.done`, open without `data.next`, or
  first without `data.goal` gets 400 and stores nothing; a later step without `goal` is stored and the card keeps the
  earlier goal; a step with `state: closed` and no `next` is stored.

**Threads view (T2)**
- `ThreadCardTests`: after three steps on thread T by two actors, the card has `steps` 3, `owner` and `last_step` of
  the third, `next` of the third, `goal` of the latest that gave one, and `refs` the union newest first; a
  `chargehand.tool.called` event later with `correlationid` T moves `last_activity` to its time and nothing else; the
  `text` is at most 2,000 characters and contains the goal, the next step and the thread id.
- `ThreadListTests`: with threads A (open), B (closed) and C (open, newer activity), `GET /v1/threads` returns C then
  A; `state=closed` returns B; `state=all` returns three; a step on B with `state: open` puts it back in the open list;
  `scope=project:Egoushka/a` returns only threads whose latest step is in that project.
- `ThreadScopeTests`: a token granted only `project:Egoushka/a` lists only that project's threads; its
  `GET /v1/threads/{id}/card` for a thread in `project:Egoushka/b` gets 404, not 403; an event in a scope it may not
  read never moves `last_activity` on a card it may read.

**Reserved types (T2)**
- `ReservedTypeTests`: a `signal.rule.fired` and a `wake.job.finished` envelope are stored and read back by
  `GET /v1/events?type=signal.*` and `type=wake.*` with their `causationid` intact; no thread is derived from them.

**Live (T3, read by the maintainer)**
- The check above: 10 resumes, at least 8 right.
