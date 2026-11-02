# Events and watches: every action recorded, any action triggerable

- Status: proposed. The maintainer chose the four main options on 2026-11-02 (store, scope, first consumers, hosting
  model; each the recommended one), then scopes that gate reads and hook delivery for agent messages (decision 8). No task starts before he approves this spec; Task 0 needs no code and runs first.
- Date: 2026-11-02
- ADR: 0090 (Task 1 writes it from the Decisions below); switchknife ADR 0007 for the Journal change
- Builds on: [ADR 0061](../adr/0061-driven-step-journal.md) (step journal), [0068](../adr/0068-scheduled-work-within-a-monthly-cap.md)
  (schedules and their monthly cap), [0086](../adr/0086-one-core-doors-in-and-doors-out.md) (one core, doors in and
  out), the [Needs you spec](2026-10-28-needs-you-decisions-design.md) and `ApprovalsSink` (phone answers)
- Workspace rules it obeys: D-39 (nothing new adds a store), D-48 (nothing from `work/`), no messenger bots, a shared
  library comes from two working consumers and is validated on a third

## Conclusion

Do not build a new event bus. Every tool already writes its own events; none of them can be subscribed to. Put one
append-only log inside chargehand on cobalt, stored with `Switchknife.Journal`'s existing `events` table. Producers POST
CloudEvents-shaped envelopes. Subscribers read a resumable SSE stream (the pattern `BatchEventStream` already uses). A
**watch** is chargehand's always-on agent: a filter in code that wakes a capped one-shot run when matching events
arrive, so no model runs while nothing happens. Every action a watch (or a person, or an agent) may take is an entry in
an **action registry** with a reversibility class; anything external or irreversible becomes an approvals-app decision
item and runs only on the signed answer. Every event carries a **scope** (a project, a run, a channel or one agent's
inbox) that tokens are granted, and agents message each other by appending `agent.message.sent` to a scope: the log is
also the mailbox, across the Mac, cobalt's containers and watches.

The first two consumers are the video-studio money watch and Hindsight's session retain. The money watch goes live only
if the cheapest test (Task 0, already being built in another session) shows the maintainer acts on money signals when
they reach him. Being critical about it: video-studio's stall is a demand problem (0 clicks, 0 sales in every funnel row
since 2026-10-19), not a visibility problem. Events make the stall impossible to miss. They cannot make a Short worth
watching.

## Terms

- **Event:** one fact that something happened, with an id, a source, a type, a time and its cause. Immutable.
- **Producer / subscriber:** what appends events / what reads them in order and resumes from the last one it saw.
- **Action:** a named operation with an executor and a reversibility class, listed in the registry. Running one emits
  events.
- **Watch:** a standing entry in chargehand's config: which events wake it, how long to gather them, what run to start,
  its monthly cap and which actions it may take. Named "watch" because "session" (ADR 0039) and "conversation" (ADR 0086)
  are taken; `Chargehand.Modules.Watch` exists as an empty project.
- **Scope:** which project, run, channel or agent inbox an event belongs to; a token may read and write only the scopes
  it is granted (decision 8).
- **Message:** an `agent.message.sent` event from one agent to a scope. It is data for the recipient, never an
  instruction.
- **Causation / correlation:** `causationid` is the id of the event that directly caused this one; `correlationid`
  groups one chain (one watch firing, one run, one session).

## Goal

Any agent, watch or script can ask "what happened, who did it, and why" across the maintainer's tools, and react to it
within seconds, without polling five stores. Any registered action can be started by an event, with a person's approval
where it cannot be undone.

Done when:

1. Chargehand on cobalt serves `POST /v1/events`, `GET /v1/events` (query) and the SSE stream, and holds the last 24 hours
   of the maintainer's effectful agent actions (Claude Code sessions outside `work/`), chargehand runs and video-studio
   rows.
2. Session retain reaches Hindsight within 2 minutes of a session's end (p95), instead of the hourly job.
3. The money watch has run for 21 days and the bet below has been read against its check.
4. No event from `work/` and no secret is in the log (acceptance checks below).
5. A driven run on cobalt and a Claude Code session on the Mac complete a question-and-answer thread through the log
   without the maintainer relaying it, and neither can read a scope it was not granted.

## Where it stands

Everything below already records or reacts to events. Read 2026-11-02 from code (volumes for Postgres-backed stores
were not read: UNKNOWN).

| What | Records | Store, transport | Reacts? | Evidence |
|---|---|---|---|---|
| History ledger | 7,487 dated facts: pr, session, commit, issue, decision, release, stack, repo, audit | `context/history/events.jsonl` (workspace), DuckDB via `ws history query`, built by `ws history build` | no; chronicle reads it as a release asset | `context/history/spec.md`, row 1 of `events.jsonl` |
| chronicle | one `event` shape for 19 source adapters | Postgres `event` (`source, source_id, ts, kind, actor, text, payload…`), HTTP + MCP, nightly pull | `/freshness` flags silent sources | `chronicle-public/migrations/001_core.sql:71`, `api.py:128`; status Parked |
| Switchknife.Journal | step states and `events(seq, flow, kind, at, payload)` | SQLite or Postgres (`step_events`), in-process | no | `switchknife/src/Switchknife.Journal/Schema.cs:22`, `SqliteStepStore.cs:121-150`, ADR 0002 |
| chargehand runs | `RunEventKind`: accepted … run_finished, pr_opened, question, governor | run log JSONL, `journal.db`; SSE per run/batch with `Last-Event-ID` resume | intake polls GitHub; scheduler fires every minute | `Contracts/RunStatus.cs:46`, `BatchEventStream.cs:12-18`, `JsonlRunLog.cs`, `Scheduler.cs` |
| senses | `state_opened/closed/confirmed`, `device_new`, `sense_down/up` | Postgres `events` + outbox drained to ntfy `homelab-senses` every 10 s | `senses_wait_for` polls every 2 s; no ntfy subscriber found | `senses/db/migrations/0001_init.sql`, `EventKinds.cs`, `EventPublisher` |
| router-actions | approvals and runs of signed MikroTik scripts | SQLite `approvals`, `runs` (latest state only, no transition log); MCP `list_actions`, `request_approval`, `run_action`, `get_run`; Touch-ID signature | runs on approval | `RouterActionsTools.cs:17-64`, `Store.cs:51-58`, `bin/ra-approve.py` |
| approvals app | decision and info items, signed decisions | SQLite `items`; `PUT /items/{id}`, HMAC callback, UnifiedPush; subscribes to 5 ntfy topics | callback to the source on a tap | `approvals/docs/specs/2026-10-19-approvals-design.md:59-118`, `server/src/db.ts:7-20`, `feed.ts` |
| chargehand → approvals | questions and answers as decision items | `ApprovalsSink` PUT/DELETE, signed decision back | yes | `src/Chargehand.Automation/Notify/ApprovalsSink.cs` |
| Plainsight | signals (merged PRs, releases, notes) and a review queue | SQLite `signals`, `proposals`; MCP | no event out | `content-engine/.../Migrations.cs:44`, `Signal.cs` |
| video-studio | `publish`, `funnel`, `metrics` rows | `projects/*/experiments/log.jsonl` on the Mac, last committed 2026-10-19; launchd `mira-post` (Mon–Fri), `mira-sync` (daily) | no | `studio/experiments.py:17`; last rows 2026-11-01: clicks 0, sales 0 |
| nytka | day events (calls, media, silence) | Postgres `events`, rewritten per day; `list_events` | outbound webhook | `nytka/server/db/migrations/0059_events.sql:9` |
| Hindsight retain | one document per finished session | hourly launchd `com.yehor.session-retain`, state `~/.local/state/session-retain/sent.json` (604 entries) | hourly poll | `~/Projects/bin/session-retain.py` |
| Hook logs | prompt-gate 11,336, skill-hint 8,453, ai-watch 496 rows… | JSONL under `~/.local/state/*`; `llm-guard.py:1455` audit log | no | inventory 2026-11-02 |
| ntfy | the de facto bus: 9 topics (`homelab-alerts`, `-senses`, `-decisions`, `-inbox`…) | ntfy cache only | phone push; approvals ingests 5 topics | `homelab-gitops/ntfy/compose.yaml:4` |

No NATS, Kafka, Redpanda, RabbitMQ or `pg_notify` is deployed. feed-eater has a spec (not confirmed built) for an
`events` outbox with `GET /api/v1/events?since=`.

Gaps this spec closes: (1) nothing joins these by time or cause; (2) nothing outside a store can subscribe and resume;
(3) the maintainer's agents' own actions are logged only per hook, on the Mac; (4) router-actions and the approvals app
are the only gated action paths, each with its own registry.

**Load to design for.** Claude Code transcripts under `~/.claude/projects` show 24,900 tool calls in the last 24 hours
and 74,200 in 7 days (all projects, subagents included): about 0.3 per second on average. Effectful calls are a subset
(share UNKNOWN until Task 4 counts them). The history ledger's busiest day had 1,281 rows.

## Decisions

### 1. Store: `Switchknife.Journal` in chargehand, its own `events.db` (chosen 2026-11-02)

Measured on the maintainer's Mac (Apple M1, 8 cores, under memory pressure), Go benchmark of 600-byte envelopes, one
producer, one durable commit per event; delivery measured from append to a running subscriber at 200 events/s, 2,000
events; two repetitions (ranges). Then the same on the maintainer's server (the target host: 18 vCPU virtual machine, ext4
on a virtual disk, load average 50–90 from its other services during the runs). Method and raw lines: appendix.

**On the Mac** (Apple M1, 8 cores):

| Option | Appends/s, OS fsync | Appends/s, full flush | Delivery p95 (full flush) | RSS over baseline | New store? |
|---|---|---|---|---|---|
| JSONL file + tail | 29,061–31,089 | 258–287 | 9.7–10.0 ms (10 ms poll) | ~0 | no, but no index, no query |
| SQLite WAL (Journal) | 15,999–18,213 | 263–281 | 14.3–16.8 ms (10 ms poll, cross-process) | +5–8 MB | **no**: library and table exist |
| Postgres + LISTEN/NOTIFY | 11,406–12,502 | 255–267 | 5.5–5.7 ms | +11 MB server | yes: a `chargehand-postgres` container |
| NATS JetStream (embedded) | 37,634–46,951 (fsync every 2 min) | 254–288 (`sync_always`) | 4.8–8.6 ms | +16–19 MB | yes: a new service |

**On the server** (Linux; one commit with `fsync` per event, two repetitions; `fsync` is the full flush there, so one column):

| Option | Appends/s, one commit per event | Delivery p95 at 200/s (the log saturates, so this is queueing) | Peak RSS of the bench process |
|---|---|---|---|
| JSONL file + tail | 16–25 | 71–80 ms | 17 MB |
| SQLite WAL (Journal) | 15–16 | 360–509 ms | 25–26 MB |
| Postgres + LISTEN/NOTIFY | 25–33 | 187–294 ms | 20–76 MB, plus 101 MB server |
| NATS JetStream (embedded), `sync_always` | 19–24 | 344–505 ms | 34 MB |
| NATS JetStream, no sync (flush every 2 min) | 1,006–2,241 | 27 ms | 34 MB |

Batching, measured on the same server with a plain append-and-`fsync` of 600-byte lines (the cost any file-backed store
pays per commit), run twice, one `fsync` per batch:

| Events per commit | Events/s | Commit p50 | Commit p95 |
|---|---|---|---|
| 1 | 19–28 | 7.6–16 ms | 189–261 ms |
| 50 | 763–1,975 | 13–16 ms | 83–286 ms |
| 500 | 3,580–5,823 | 27–50 ms | 367–689 ms |

Reading:
- **The server's disk does about 10× fewer flushes than the Mac** (15–33 per second against 255–290) and is noisy: the
  tail is 10–50× the median, because the virtual disk is shared with the other services. Whether a quiet disk would be
  faster is UNKNOWN; the numbers above are what the log gets in practice.
- **One flush per event is not the design.** The average load is about 0.3 events/s, which one-commit-per-event covers
  50–100× over, but a burst does not: a 500-event `POST`, or a Mac spool flush, would take 15–30 s. So **a batch is one
  transaction and one flush**, on `POST /v1/events` and in the spool flush. That gives 3,580–5,823 events/s at 500 per
  commit, about 10,000× the average load.
- **The options are still not separated by speed.** With a real flush all four are disk-bound at the same 15–33
  commits/s; only the unflushed NATS differs, and it trades away the durability the log exists for. Postgres is
  ahead of SQLite by 1.6–2× under one producer, which does not pay for a container and 101 MB. D-39, the existing library and its
  existing consumer (`DrivenJournal`) decide.
- **Delivery.** Cross-process delivery for SQLite is bounded by the poll interval on the Mac; inside chargehand there is no
  poll, since the appender signals the SSE streams after commit. The 200/s column above is the paced test overrunning a
  15–33/s log, so it measures queueing, not delivery; Task 3's live check measures delivery below saturation.

- File: `<data>/events.db`, separate from `journal.db`, so retention and backup are separate.
- One writer: the chargehand server. Nothing else opens the file.
- Switchknife change (ADR 0007, minor version): `ReadAllEventsAsync(after, limit, kindPrefix?)` across flows, an
  idempotent append keyed by `(flow, key)` that returns the existing `seq` on a repeat, a batch form of it (up to 500
  events, one transaction, one flush, one signal), and an in-process `IEventSignal` that wakes readers after commit. Same
  four on `PgStepStore`, so a Postgres move later is a config change.
- Mapping: `flow` = envelope `source`, `kind` = `type`, `key` = `id`, `payload` = the whole envelope as JSON, `seq` = the
  global order.
- **Reopen if** sustained appends pass 350/s (a tenth of the lowest batched capacity measured on the server), the p95 of
  a 500-event commit passes 1 s (above the worst measured, 689 ms), or a subscriber outside the tailnet needs it.
  Then the order is Postgres (`Switchknife.Journal.Postgres` exists) before NATS.

### 2. Envelope: CloudEvents 1.0 attributes, plus six extensions

CloudEvents so that other people's tools can read it (open by default). Contract `events/v1` in `Chargehand.Contracts`.

```json
{
  "specversion": "1.0",
  "id": "01JC9Z6V5Q8K3M2T4R7W1XYZAB",
  "source": "video-studio/ai-persona",
  "type": "video.funnel.recorded",
  "subject": "ai-persona",
  "time": "2026-11-01T21:00:17Z",
  "datacontenttype": "application/json",
  "data": {"clicks": 0, "signups": 0, "sales": 0, "amount": 0.0, "slug": null},
  "seq": 48211,
  "correlationid": "01JC9Z6V5Q8K3M2T4R7W1XYZAA",
  "causationid": null,
  "actor": {"kind": "job", "id": "com.yehor.mira-sync", "name": null, "run": null},
  "effect": "write",
  "scope": "project:Egoushka/video-studio"
}
```

| Field | Rule |
|---|---|
| `id` | Producer-chosen, unique per `source`: a ULID, or a deterministic id when the fact has one (`pr:Egoushka/chargehand#703`, as the history ledger does). A repeat of `(source, id)` is a no-op that returns the first `seq`, so producers retry freely. |
| `source` | `<tool>[/<instance>]`; a producer's token may write only its own prefix (as approvals binds tokens to `<source>:`). |
| `type` | `<domain>.<noun>.<past verb>`: `claude.session.ended`, `chargehand.run.finished`, `action.requested`, `approval.decided`. |
| `time` | When it happened (UTC); `seq` is when chargehand stored it. Order by `seq`. |
| `data` | ≤ 16 KB inline; larger content goes behind `dataref` (CloudEvents extension, a URL or a path). |
| `seq` | Set by the server; absent on input. |
| `correlationid`, `causationid` | Optional on input; a watch's run and every action it starts carry them, so a chain reads back from any link. |
| `actor` | `kind` = `person`, `agent`, `job` or `service`; `run` = chargehand run id when there is one; `name` = the run's or watch's fun name (from the derivation on the open `ch-fun-names` branch). |
| `effect` | `read`, `write`, `external` (leaves the box: a message, a publish, a payment) or `irreversible`. What is recorded at all is decision 4. |
| `scope` | `project:`, `run:`, `channel:`, `agent:` or `global`; the server fills the default when it is absent and refuses a scope outside the token's write grants (decision 8). |

Server refusals: missing `id`/`source`/`type`/`time` (400); a token writing another source (403); `data` over 16 KB
without `dataref` (413); a value matching the secret patterns chargehand's checkout scan already uses (422, the event is
not stored, the refusal is counted); anything marked or located under `work/` (422, D-48).

### 3. Transport

- **In:** `POST /v1/events` takes one envelope or an array of up to 500, returns the `seq` of each; an array is one
  transaction and one flush (Decision 1). Bearer token per source from the secret store. Tailnet only, like the rest of chargehand on cobalt.
- **Out:** `GET /v1/events?after=<seq>&type=<glob>&source=<glob>&scope=<glob>&subject=<s>&limit=` for queries;
  `GET /v1/events/stream?type=…` as SSE, each message's SSE id = `seq`, resumed with `Last-Event-ID`, as
  `BatchEventStream` does today.
- **MCP:** `events` (query, the same filters, newest first, capped at 200) on chargehand's MCP endpoint, so any agent can
  ask "what happened in the last hour in video-studio" without a new server.
- **ntfy stays the push-to-phone outlet,** not the log. Task 4's bridge copies the existing ntfy topics into the log
  so senses, alerts and approvals decisions are in it on day one without changing those producers.
- **Mac producers** append to a local spool (`~/.local/state/events/spool.jsonl`, under 1 ms, never blocks a hook); a
  launchd agent `com.yehor.events-flush` (KeepAlive) posts the spool in batches of up to 500, one commit each, and keeps the spool when cobalt is unreachable. Ids
  make the replay safe.

### 4. Scope: actions with effect (chosen 2026-11-02)

Recorded: runs (chargehand, driven, scheduled), tool calls that edit, execute or call out (`Edit`, `Write`, `Bash`,
MCP tools not named `get_*`/`list_*`/`search_*`/`read*`), publishes, deploys, approvals and decisions, metric and funnel
rows, session start and end. Not recorded: reads, prompts, file contents, command output.

Each tool-call event carries the tool name, the repo, a one-line summary the hook builds (e.g. `Edit src/Foo.cs`), the
SHA-256 of the full input, and the exit status. Nothing from a `cwd` under `~/Projects/work/` (D-48): the hook drops it
before the spool.

### 5. Action registry and the approval gate

`automation.actions` in the profile, a reviewed list like `acp-agents.json`:

```json
{ "id": "video.publish-next", "title": "Publish the next queued Short",
  "class": "external", "executor": {"kind": "http", "url": "https://automation.example/webhook/studio-publish"},
  "input": {"$ref": "schemas/video-publish-next.json"}, "max_per_day": 2 }
```

- `class`: `read` and `reversible` run at once; `external` and `irreversible` become an approvals decision item through
  `ApprovalsSink` (approve, reject, edit), and run only on the signed decision (HMAC, 10-minute freshness, single use: the
  approvals contract).
- `executor.kind`: `run` (a chargehand template or goal with a preset), `http` (an allowlisted webhook, the approvals
  app's allowlist rule) or `mcp` (a tool on the homelab gateway). router-actions stays an `mcp` executor of class
  `irreversible`: its Touch-ID signature remains the gate for router scripts, and chargehand never signs for it.
- Doors: `POST /v1/actions/{id}`, the MCP tool `act`, and a watch's result. All three go through the same gate.
- Every invocation emits `action.requested` → `action.gated` → `approval.decided` → `action.started` →
  `action.finished` or `action.failed`, chained by `causationid`. "Every action triggerable" means exactly this: an
  action becomes triggerable by being registered, and from then on any event can start it through a watch.

### 6. Watches: always-on agents that cost nothing while idle (chosen 2026-11-02)

`automation.watches.entries` in the profile, beside `automation.schedules`:

```json
{ "id": "money", "on": ["video.*", "schedule.due:money-daily"], "gather": "30m",
  "template": "money-watch", "preset": "cheap", "max_per_month": 31,
  "actions": ["video.publish-next", "video.draft-script", "video.park-niche"] }
```

- The filter is code (type globs, `source`, `subject`, field equality on `data`). No model call decides whether to wake.
- `gather`: after the first match, wait this long and start one run with every matching event. 30 minutes is a starting
  value: `mira-sync` writes its rows within one minute (log timestamps), so 30 minutes coalesces one sync.
- The run gets the gathered events as `inputs[]`, the `events` tool for history, and only its listed actions. Its
  result proposes at most 3 actions; they go through the gate. A watch never sends anything except through the
  registry.
- Absence ("no publish in 3 days") needs a clock: a schedule entry (ADR 0068) now also emits `schedule.due:<id>`, and a
  watch listens to it.
- State: a cursor per watch (last handled `seq`) in the journal under flow `watch:<id>`. After a restart the watch
  resumes from it: at-least-once delivery; the run's key is `watch:<id>:<first seq>-<last seq>`, so a repeat does not
  start a second run.
- Caps: `max_per_month` as schedules count it; an occurrence over the cap is skipped and emits `watch.skipped`.
- Loop guard: an event whose causation chain contains this watch does not wake it again; chains deeper than 5 are
  dropped and logged (5 is a starting value; no chain today is longer than 3: event, run, action).
- Conversations as watch bodies (context kept between firings) wait until conversations survive a restart (ADR 0086).

### 8. Scopes and agent messages (chosen 2026-11-02)

The maintainer asked for scopes and for "some way for agents to basically communicate via this logger". He chose scopes
that gate reads, and delivery into a running Claude Code session by a hook. A message is an event like any other, so
it gets the log's ids, order, resume, retention and refusals for free, and a watch can wake on it.

What exists and stays: a conversation already takes messages (`POST /v1/conversations/{id}/messages`, Cycle A); the
desktop app's `SendMessage` reaches sessions on the same Mac only, with no record; a watch wakes on events. This decision
adds the missing pieces: an address for every kind of agent, delivery across hosts, and a record of who told whom what.

**Scope.** The sixth extension attribute, one string per event:

| Scope | Holds | Default for |
|---|---|---|
| `project:<owner>/<repo>` | what happened in one repository | tool-call and session events from a checkout of it |
| `run:<id>` | one chargehand run, batch or conversation, and every action it took | `RunEventKind` events |
| `channel:<name>` | a named room; `events.channels` in the profile lists its members | nothing; set by the sender |
| `agent:<kind>/<id>` | one agent's inbox. `kind` = `cc` (Claude Code session), `oc` (OpenCode session), `run`, `conv`, `watch` | messages addressed to one agent |
| `global` | box-wide facts (the ntfy bridge, deploys, schedules) | anything with no narrower scope |

- **Grants.** Every token in `events.tokens` lists the scope globs it may write and read, beside its `source` prefix.
  The maintainer's Mac token reads `project:*`, `channel:*`, `agent:cc/*`, `global`. A run's token is minted when the run
  starts, passed in its environment, and revoked when it ends: it reads and writes `run:<id>`, `agent:run/<id>`, its own
  `project:`, and the channels its preset lists, nothing else. So a driven container can talk to its peers on the same
  repository and cannot read another project's actions. Every token may also write `agent.message.sent` to any `agent:`
  inbox, as with a postbox: only the inbox's owner reads it, and the thread cap below bounds what a sender can push.
- **Filtering.** `scope=<glob>` joins the query and stream filters. The server intersects it with the token's grants: an
  event outside them is never sent, and an exact scope the token lacks gets 403. Writing outside the write grants: 403.
- **Storage.** Switchknife ADR 0007 adds an optional indexed `tag` column to `events` (generic; chargehand stores the
  scope there) and a `tag` filter to `ReadAllEventsAsync`, so a scoped query over 90 days uses the index.
- The Mac is one trust domain (the maintainer's sessions); its hook separates sessions by filtering, not by token.

**Messages.** Type `agent.message.sent`, `scope` = the recipient (an inbox, a channel or a project), `actor` = the sender:

```json
{ "type": "agent.message.sent", "source": "claude", "scope": "agent:run/<id>",
  "actor": {"kind": "agent", "id": "cc/<session>", "name": "Brave Capybara"},
  "correlationid": "<thread id: the first message's id>", "causationid": "<the message this answers, or null>",
  "data": {"kind": "question", "text": "Is the migration in #703 safe to rebase onto?", "expects_reply": true} }
```

- `data.kind`: `note`, `question`, `answer`, `handoff`. `text` follows the envelope rules (16 KB, secret refusal, no
  `work/` paths).
- A reply sets `causationid` to the message it answers and keeps its `correlationid`, so a thread reads back from any
  message.
- `agent.message.delivered` (emitted by the delivery path, `causationid` = the message) tells the sender it reached the
  recipient's context; `agent.message.undeliverable` says why it never will (ended conversation, revoked run).
- No address for a person. `scope: person:*` gets 422: an agent reaches the maintainer only through the approvals gate
  (decision 5), and no message is a messenger bot by another name.
- Thread cap: past 50 messages in one `correlationid` within 24 hours the server refuses with 429 and emits one
  `agent.thread.capped`. 50 is a starting value to calibrate; it exists so two agents cannot ping-pong without bound.
- **Messages are data, never instructions.** Every delivery path wraps a message in a block naming the sender, its fun
  name and scope, and stating that it is another agent's text. A message cannot approve, sign or widen anything: an
  effect still goes through the action registry, and the receiving session's permission mode does not change.

**Doors.** On chargehand's MCP endpoint, beside `events`:
- `events_publish`: append one event into the caller's write grants; `source` comes from the token.
- `events_wait`: block until an event matching a filter (`scope`, `type`, `after` seq, `correlationid`) arrives, or
  return `matched=false` after `timeout_seconds` (default 30, 1 to 120: the contract `senses_wait_for` already uses, so
  agents see one shape). Woken by `IEventSignal`, not by polling.

**Delivery, one path per kind of recipient:**

| Recipient | How it receives |
|---|---|
| `agent:cc/<session>` (Claude Code on the Mac) | `com.yehor.events-flush` also holds one SSE subscription for `agent:cc/*` and `project:*` messages and appends each to `~/.local/state/events/inbox/<session>.jsonl` (project messages go to every live session in that repo). A `UserPromptSubmit` and `PostToolUse` hook reads the lines past its cursor, a local read with no network call, returns them as `additionalContext` in the wrapper above, and emits `agent.message.delivered`. An idle session sees them on its next turn. |
| `agent:conv/<id>` | chargehand forwards to the conversation's messages door; queued while a turn runs, as ACP does. |
| `agent:watch/<id>` | the watch's filter matches its own inbox like any event (`"on": ["agent.message.sent"], "scope": "agent:watch/<id>"`). |
| `agent:run/<id>`, `agent:oc/<id>` | pull: the worker calls `events_wait`. A one-shot run stays not steerable (ADR 0086). |
| `channel:<name>` | pull (`events_wait`, `events`) and watches; the hook does not push channels, so a busy room cannot flood a session. |

Sessions on the same Mac keep `SendMessage` for live back-and-forth; the log adds what it lacks: another host, another
agent kind, a watch, and a record.

### 9. Retention

90 days in `events.db`, then monthly JSONL files in chargehand's backup set. 90 is a starting value: at the full 25k
calls a day and ~600 bytes each, that is ~1.4 GB, the upper bound; Task 4's count gives the real size.

## The first two consumers

1. **Money watch (video-studio).** Producers: `studio` emits `video.short.published`, `video.metrics.recorded` and
   `video.funnel.recorded` beside each `log.jsonl` row (the same data), plus `schedule.due:money-daily`. The
   `money-watch` template reads 14 days of those events and the queue, and posts at most one decision item a day: what
   changed in money terms, and up to 3 options (publish the next queued slug, draft a new hook, park the niche), each a
   registered action. Validates: the watch engine, the registry, the gate, absence via the clock.
2. **Session retain (Hindsight).** Producer: the Claude Code `SessionEnd` hook emits `claude.session.ended` (session id,
   project, bank). Consumer: `bin/session-retain.py --follow` subscribes to the SSE stream with its last `seq` in its
   state file and retains each session on arrival; the hourly job stays as the sweeper for misses and for `work/`
   sessions, which never enter the log (D-48). Validates: the SSE contract from a process outside chargehand, on the
   Mac, across disconnects.

They are deliberately different kinds (a watch inside chargehand, a plain subscriber outside it), so the contract is
shaped by both before anything is extracted. A third consumer (senses interrupt or JARVIS) decides whether the
subscriber client becomes a `Switchknife.Events` package (Task 8).

## Value hypothesis and its cheapest test

**Hypothesis (money):** when the funnel's state reaches the maintainer as one decision with options at the moment it
changes, he publishes again, and publishing is the precondition for any sale. Since 2026-10-19: 0 Shorts committed, 0
clicks, 0 sales. **Hypothesis (people):** agents that can read what other agents did stop re-asking the maintainer;
measured as control words per session, which D-39's audit put at a third of what he types.

**Cheapest test (Task 0, no event code):** the money-first morning summary another session is adding to video-studio
now is the polling version of the money watch. Read it after 7 days: on how many days did the maintainer act on it
(publish, or decide)? At 4 of 7 or more, build the watch (Tasks 6–7). Below 4, the problem is not that he cannot see
the state; stop the money watch, and keep only the log and session retain.

**Hypothesis (messages):** the maintainer relays between sessions by hand (pasting one session's finding into another,
handing over notes folders); a mailbox that crosses hosts removes those relays. **Cheapest test, before Task 9:** count
in the last 14 days of Mac transcripts the user turns that paste or quote another session's output, and the
`SendMessage` calls between sessions. Under 5 relays a week, build Tasks 9–10 only after the money watch's check is read;
at 5 or more, they follow Task 5. 5, and the 10 and 3 below, are starting values: no count of relays exists yet,
and this test produces the first one.

## The bet: check and stop condition

**Check,** read 21 days after the money watch goes live: at least 9 Shorts published (3 a week, 60% of the Mon–Fri
cadence `mira-post` is scheduled for), at least 70% of the watch's decision items decided within 24 hours, and watch
spend within its monthly cap (31 runs on the `cheap` preset, each capped by the preset).

**Check for messages,** read 21 days after Task 10 ships: at least 10 threads that crossed hosts or agent kinds closed
with an `answer` and no maintainer relay, and no event from a scope outside its token's grants in any reply (the grant
audit query in Task 3's tests, run live). **Stop:** under 3 such threads in 21 days: remove the hook delivery and keep
`events_wait` and the log.

**Stop:** fewer than 3 Shorts in those 21 days or fewer than 50% of items decided: switch the money watch off and
record why in ADR 0090. Spend reaching the cap in any month: the cap stops it on its own and the watch is reviewed. The log and
session retain stay either way, since they pay for themselves on the retain lag.

## Tasks (each one pull request)

0. **Cheapest test** (video-studio, the other session's work): no new code here; record the 7-day result in this spec.
1. **Decision records:** ADR 0090 from the Decisions; switchknife ADR 0007.
2. **Switchknife.Journal event stream** (switchknife): `ReadAllEventsAsync` with the `tag` filter, the indexed `tag`
   column, keyed idempotent append and its batch form, `IEventSignal`, on SQLite and Postgres; minor release tag.
3. **The log in chargehand:** `events/v1` contract, `events.db`, `POST /v1/events`, query, SSE stream, the `events` MCP
   tool, source tokens with scope grants, scope defaults and filtering, refusals, retention. Chargehand emits its own `RunEventKind` events and `schedule.due` into it.
4. **Producers:** the Claude Code hook with spool and `com.yehor.events-flush` (workspace), and the ntfy bridge
   (chargehand). The hook PR reports the effectful share of a day's tool calls.
5. **Consumer 2:** `session-retain.py --follow` (workspace).
6. **Action registry and gate:** `automation.actions`, `POST /v1/actions/{id}`, `act`, gate through `ApprovalsSink`.
7. **Watches and consumer 1:** `Chargehand.Modules.Watch` engine, `money-watch` template, the `studio` producer
   (video-studio). Starts only if Task 0 passed.
8. **Extraction review** after both consumers have run 14 days and a third is named: extract the subscriber client to
   `Switchknife.Events`, or record why not.
9. **Agent messages** (chargehand): `agent.message.*` types, `events_publish`, `events_wait`, run tokens minted and
   revoked with the run, the conversation bridge, the thread cap, `events.channels`.
10. **Hook delivery** (workspace): the inbox mirror in `com.yehor.events-flush` and the `UserPromptSubmit`/`PostToolUse`
    hook. Needs Tasks 4, 5 and 9.

## Acceptance checks

These run in `scripts/check.sh` (chargehand) or the switchknife test run unless marked live. A live check runs on the
maintainer's server, and its output goes in the pull request with ids scrubbed.

**Task 2 (switchknife)**
- `EventStreamTests`: `ReadAllEventsAsync` returns events of every flow in `seq` order; a repeat append with the same
  `(flow, key)` returns the first `seq` and stores nothing; a batch of 500 is one transaction (one signal, all or none,
  a repeat of the batch stores nothing); a reader awaiting `IEventSignal` wakes after the commit, never
  before it; a `tag` filter returns only that tag's events, and the query plan uses the `tag` index. Same tests against `PgStepStore` with `Switchknife.Testing.Postgres`.

**Task 3 (log)**
- `EventEnvelopeTests`: an envelope missing `id`, `source`, `type` or `time` gets 400; `data` over 16 KB without
  `dataref` gets 413; a `data` value matching a secret pattern gets 422 and nothing is stored; an event with a `work/`
  path anywhere in `subject` or `data` gets 422.
- `EventIngestTests`: a batch of 500 returns 500 `seq` values; the same batch again returns the same values and adds no
  rows; a token for `video-studio` writing `source: claude` gets 403.
- `EventStreamRouteTests`: a subscriber that disconnects after event 10 and reconnects with `Last-Event-ID: 10` receives
  11 onward, none lost and none repeated; a `type` glob filters both the query and the stream.
- `EventScopeTests`: a token granted `project:Egoushka/a` querying `scope=*` receives only that project's events, and the
  same on the stream; an exact `scope=project:Egoushka/b` gets 403; a write outside the write grants gets 403; an event
  sent without `scope` is stored with its default (`project:` from the checkout, else `global`).
- `SchemaCompatTests` stays green (`events/v1` is additive to the contracts).
- Live `events-bench`: `scripts/events-bench.sh` on cobalt, against the real server and `events.db`, with a full flush:
  (a) 5,000 envelopes in batches of 500 reach at least 1,500 events/s with a commit p95 of at most 1 s; (b) 500 single-event
  appends reach at least 10 appends/s; (c) 1,000 events paced at 10/s over the SSE stream arrive with a p95 of at most
  250 ms. Reasons: the server measured 3,580–5,823 events/s at 500 per commit, 15–33 appends/s single, and 71–509 ms p95
  when the log was saturated; each floor is below the worst measured value. The 250 ms is a starting value to calibrate
  on the first run. The Mac measured 263–281 single appends/s and 14–17 ms cross-process.

**Task 4 (producers)**
- Hook test (workspace `tests/`): a fake `PostToolUse` for `Read` writes nothing; for `Edit` writes one spool line with
  the summary and the input hash and without the input; for any `cwd` under `~/Projects/work/` writes nothing.
- Live: `curl` of `GET /v1/events?type=claude.session.ended` for the last 24 hours, compared with the top-level session
  transcripts outside `work/` modified in the same window: equal within 2.

**Task 5 (retain)**
- Live: for 20 consecutive sessions, the time from the `claude.session.ended` event to Hindsight's document `cc-<sid>`
  has p95 under 120 s; with chargehand stopped for 10 minutes, every session ended in that window is retained after it
  returns.

**Task 6 (actions)**
- `ActionRegistryTests`: an unknown executor kind or a class outside the four fails profile load; an `external` action
  creates one approvals item and does not execute; a correctly signed approve executes it once; a bad signature gets
  403, a decision older than 10 minutes 410, a reused decision id 409; each invocation emits the six-step chain with
  linked `causationid`s.

**Task 7 (watches)**
- `WatchEngineTests` (fake clock and runtime): 5 matching events within `gather` start 1 run whose inputs hold all 5; an
  occurrence over `max_per_month` starts nothing and emits `watch.skipped`; a restart between the events and the run
  starts exactly one run; an event caused by the watch's own action does not wake it; a chain of depth 6 is dropped and
  logged; a non-matching event starts nothing and makes no model call.
- Live: 7 days of the money watch produce at most one approvals item a day (`chargehand:watch.money:<date>`), and the
  run log shows its spend.

**Task 9 (messages)**
- `AgentMessageTests`: a message to `person:*` gets 422; a reply with `causationid` set keeps the thread's
  `correlationid`; the 51st message in one thread within 24 hours gets 429 and exactly one `agent.thread.capped` is
  stored; a message whose `text` matches a secret pattern gets 422.
- `RunTokenTests`: a token minted for run R reads `run:R`, `agent:run/R` and R's project and gets nothing else from
  `scope=*`; it may send a message to `agent:cc/<S>` but reading `scope=agent:cc/<S>` gets 403; after R ends the token
  gets 401.
- `EventsWaitTests` (fake clock): `events_wait` returns within 1 s of a matching append and without a poll loop (the
  signal is awaited); it returns `matched=false` at the timeout; `timeout_seconds` of 0 or 500 is clamped to 1 or 120.
- `ConversationBridgeTests`: a message to `agent:conv/<id>` produces one queued conversation message and one
  `agent.message.delivered`; to an ended conversation, one `agent.message.undeliverable` and no message.

**Task 10 (hook delivery)**
- Hook test (workspace `tests/`): an inbox file with 2 messages for session S and 1 for T makes the hook for S return
  the 2 in the untrusted-message wrapper and advance its cursor; the next call returns nothing; T's message never
  appears in S's context; a message for a repository under `work/` never reaches the inbox file (D-48). The hook makes
  no network call (it runs with networking off in the test).
- Live: a driven run on cobalt sends a `question` to `agent:cc/<S>`; S sees it on its next tool call and replies; the
  run's `events_wait` returns the reply. Over 20 such threads, append to inbox-file p95 under 2 s, and
  `agent.message.delivered` exists for each (2 s is a starting value: SSE across the tailnet plus one file append).

## Risks

- **The log is the most private file on the box.** It holds what every agent did. Mitigations: tailnet only, a token
  per source, summaries and hashes instead of content, the secret and `work/` refusals, and the same backup encryption
  as chargehand's other state.
- **Single writer.** If chargehand is down, Mac producers spool; box-side producers retry; the ntfy bridge catches up
  from ntfy's cache (12 hours by default) and loses anything older. Acceptable for the first consumers; a reason to
  revisit Postgres if a consumer needs more.
- **Watch spend and loops.** Monthly caps, `cheap` preset, the loop guard, and `watch.skipped` events visible in the
  morning report.
- **Volume surprise.** If effectful tool calls are most of the 25k a day, the log is mostly hook noise for a watch;
  type filters keep watches unaffected, retention bounds the disk.
- **One agent steering another.** A message is text another model reads, so it is a prompt-injection path between
  agents, and the sender may be a driven container that read hostile code. Mitigations: the untrusted wrapper, no
  message can approve or widen anything, effects still go through the registry, and grants keep a run inside its own
  project and channels.
- **Chatter.** Two agents can talk without bound at model cost. The thread cap stops one thread; each run's preset cap
  stops its spend; channels are pull-only so a busy room cannot flood a session's context.
- **Building the bus instead of the business.** The money watch exists to restart publishing; if Task 0 fails, it is
  not built.

## Out of scope

- A messenger bot of any kind. The approvals app is the only interface a watch reaches the maintainer through, and
  agent messages have no address for a person.
- Replacing `SendMessage` between sessions on the same Mac, or making a one-shot run steerable (ADR 0086).
- A model loop that runs while nothing happens.
- Events from `work/` (D-48).
- Replacing ntfy as the phone push, or migrating senses, chronicle, nytka or the approvals app off their own stores.
  They stay sources of truth; the log copies facts with their ids.
- A general rules engine (joins, windows over many types). Watches filter; the run reasons.
- Replication between hosts, or access from outside the tailnet.

## Appendix: benchmark

Program: Go 1.26.4, `modernc.org/sqlite` v1.34.4, `nats-server` v2.10.24 embedded with an in-process connection,
`embedded-postgres` v1.30.0, `pgx` v5.7.2 (kept at the session's scratchpad, not committed). Each option in its own
process: 5,000 appends from one producer, one commit per event; then a subscriber running while 2,000 appends are paced at
200/s, latency = receive time minus the timestamp written into the envelope. "OS fsync" uses `fsync(2)`; "full flush"
forces the drive cache (`F_FULLFSYNC`; SQLite `fullfsync`, Postgres `wal_sync_method=fsync_writethrough`, NATS
`sync_always`), which is what `fsync` gives on cobalt's Linux. RSS is the process's peak; for Postgres, plus its server
processes as `ps` reports them. JSONL and SQLite subscribers poll every 10 ms because they are separate readers; inside
chargehand the reader is signalled instead.

```
DUR=os    jsonl     append= 29061 ev/s  p50= 5.03ms p95= 9.93ms p99=10.43ms rss=23MB
DUR=os    sqlite    append= 15999 ev/s  p50= 5.59ms p95=10.63ms p99=11.07ms rss=30MB
DUR=os    pg        append= 11406 ev/s  p50= 0.53ms p95= 2.86ms p99=13.06ms rss=26MB (+server 11MB)
DUR=os    nats      append= 46951 ev/s  p50= 0.10ms p95= 0.27ms p99= 0.64ms rss=39MB
DUR=full  jsonl     append=   258 ev/s  p50= 5.01ms p95= 9.66ms p99=10.34ms rss=20MB
DUR=full  sqlite    append=   281 ev/s  p50= 9.37ms p95=14.34ms p99=43.82ms rss=25MB
DUR=full  pg        append=   255 ev/s  p50= 4.39ms p95= 5.46ms p99= 9.08ms rss=23MB (+server 11MB)
DUR=full  nats-sync append=   254 ev/s  p50= 3.62ms p95= 4.82ms p99= 8.67ms rss=39MB
-- repetition 2
DUR=os    jsonl     append= 31089 ev/s  p50= 5.07ms p95= 9.91ms p99=10.38ms rss=22MB
DUR=os    sqlite    append= 18213 ev/s  p50= 5.54ms p95=10.80ms p99=46.76ms rss=31MB
DUR=os    pg        append= 12502 ev/s  p50= 0.49ms p95= 1.35ms p99= 2.79ms rss=25MB (+server 11MB)
DUR=os    nats      append= 37634 ev/s  p50= 0.14ms p95= 0.45ms p99= 1.95ms rss=38MB
DUR=full  jsonl     append=   287 ev/s  p50= 5.11ms p95= 9.96ms p99=10.57ms rss=19MB
DUR=full  sqlite    append=   263 ev/s  p50=10.16ms p95=16.75ms p99=22.15ms rss=26MB
DUR=full  pg        append=   267 ev/s  p50= 3.90ms p95= 5.71ms p99=11.90ms rss=23MB (+server 12MB)
DUR=full  nats-sync append=   288 ev/s  p50= 3.83ms p95= 8.62ms p99=21.35ms rss=38MB
```

**On the server.** The same program built for Linux (`CGO_ENABLED=0 GOOS=linux`), run twice from a systemd slice that was
started only while the CPU pressure of the services slice was at most 50% (it hovered at 25–55%, so every run shared the
CPU and the disk with production load; load average 50–90). On Linux `DUR=os` and `DUR=full` are the same `fsync`
for JSONL, SQLite and Postgres (`fsync_writethrough` is macOS-only; Postgres used its default `fdatasync`), so only the full
run was made; NATS is measured both without sync and with `sync_always`. The program's RSS units were fixed for Linux; the
Postgres server figure sums that user's Postgres processes. The paced stream (200/s) overruns a 15–33/s log, so its latency
includes queueing. The batch table is a separate 40-line script: a 600-byte line repeated N times, one `write` and one
`fsync` per batch, 60–100 commits per size, run twice.

```
-- repetition 1
DUR=full  jsonl     append=   16 ev/s  e2e@200/s p50=  9.05ms p95= 79.65ms p99=194.09ms rss=17MB
DUR=full  sqlite    append=   15 ev/s  e2e@200/s p50= 36.95ms p95=359.72ms p99=721.61ms rss=25MB
DUR=full  pg        append=   33 ev/s  e2e@200/s p50= 18.07ms p95=187.17ms p99=355.22ms rss=76MB (+server 101MB)
DUR=full  nats      append= 2241 ev/s  e2e@200/s p50=  0.46ms p95= 27.53ms p99=123.34ms rss=34MB
DUR=full  nats-sync append=   24 ev/s  e2e@200/s p50= 37.93ms p95=505.28ms p99=990.80ms rss=34MB
-- repetition 2
DUR=full  jsonl     append=   25 ev/s  e2e@200/s p50=  9.15ms p95= 71.20ms p99=177.19ms rss=17MB
DUR=full  sqlite    append=   16 ev/s  e2e@200/s p50= 65.25ms p95=508.58ms p99=853.53ms rss=26MB
DUR=full  pg        append=   25 ev/s  e2e@200/s p50= 22.05ms p95=294.26ms p99=593.03ms rss=20MB (+server 101MB)
DUR=full  nats      append= 1006 ev/s  e2e@200/s p50=  0.42ms p95= 27.34ms p99=110.32ms rss=34MB
DUR=full  nats-sync append=   19 ev/s  e2e@200/s p50= 19.63ms p95=344.47ms p99=699.32ms rss=34MB
-- batch script, run 1                       -- batch script, run 2
batch=  1 events/s=    28 commit p50= 7.6ms p95=189.1ms   batch=  1 events/s=    19 commit p50=16.3ms p95=260.9ms
batch= 50 events/s=  1975 commit p50=15.8ms p95= 83.1ms   batch= 50 events/s=   763 commit p50=13.1ms p95=286.0ms
batch=500 events/s=  5823 commit p50=26.7ms p95=366.6ms   batch=500 events/s=  3580 commit p50=49.6ms p95=688.5ms
```
