# Events and watches: every action recorded, any action triggerable

- Status: proposed. The maintainer chose the four main options on 2026-11-02 (store, scope, first consumers, hosting
  model; each the recommended one). No task starts before he approves this spec; Task 0 needs no code and runs first.
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
item and runs only on the signed answer.

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
events; two repetitions (ranges). Method and raw lines: appendix. Cobalt was not measured (the maintainer asked for the
Mac); Task 2's live check measures it there.

| Option | Appends/s, OS fsync | Appends/s, full flush | Delivery p95 (full flush) | RSS over baseline | New store? |
|---|---|---|---|---|---|
| JSONL file + tail | 29,061–31,089 | 258–287 | 9.7–10.0 ms (10 ms poll) | ~0 | no, but no index, no query |
| SQLite WAL (Journal) | 15,999–18,213 | 263–281 | 14.3–16.8 ms (10 ms poll, cross-process) | +5–8 MB | **no**: library and table exist |
| Postgres + LISTEN/NOTIFY | 11,406–12,502 | 255–267 | 5.5–5.7 ms | +11 MB server | yes: a `chargehand-postgres` container |
| NATS JetStream (embedded) | 37,634–46,951 (fsync every 2 min) | 254–288 (`sync_always`) | 4.8–8.6 ms | +16–19 MB | yes: a new service |

Reading: with a real flush every option is disk-bound at ~255–290 appends/s on this disk, which is still about 850× the
average load. Cross-process delivery for SQLite is bounded by the poll interval; inside chargehand there is no poll, since
the appender signals the SSE streams after commit (Task 2 measures the in-process p95). Speed does not separate the
options; D-39, the existing library and its existing consumer (`DrivenJournal`) do.

- File: `<data>/events.db`, separate from `journal.db`, so retention and backup are separate.
- One writer: the chargehand server. Nothing else opens the file.
- Switchknife change (ADR 0007, minor version): `ReadAllEventsAsync(after, limit, kindPrefix?)` across flows, an
  idempotent append keyed by `(flow, key)` that returns the existing `seq` on a repeat, and an in-process
  `IEventSignal` that wakes readers after commit. Same three on `PgStepStore`, so a Postgres move later is a config change.
- Mapping: `flow` = envelope `source`, `kind` = `type`, `key` = `id`, `payload` = the whole envelope as JSON, `seq` = the
  global order.
- **Reopen if** sustained appends pass 50/s (a sixth of measured capacity) or a subscriber outside the tailnet needs it.
  Then the order is Postgres (`Switchknife.Journal.Postgres` exists) before NATS.

### 2. Envelope: CloudEvents 1.0 attributes, plus five extensions

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
  "effect": "write"
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
| `effect` | `read`, `write`, `external` (leaves the box: a message, a publish, a payment) or `irreversible`. Scope is decision 4. |

Server refusals: missing `id`/`source`/`type`/`time` (400); a token writing another source (403); `data` over 16 KB
without `dataref` (413); a value matching the secret patterns chargehand's checkout scan already uses (422, the event is
not stored, the refusal is counted); anything marked or located under `work/` (422, D-48).

### 3. Transport

- **In:** `POST /v1/events` takes one envelope or an array of up to 500, returns the `seq` of each. Bearer token per
  source from the secret store. Tailnet only, like the rest of chargehand on cobalt.
- **Out:** `GET /v1/events?after=<seq>&type=<glob>&source=<glob>&subject=<s>&limit=` for queries;
  `GET /v1/events/stream?type=…` as SSE, each message's SSE id = `seq`, resumed with `Last-Event-ID`, as
  `BatchEventStream` does today.
- **MCP:** `events` (query, the same filters, newest first, capped at 200) on chargehand's MCP endpoint, so any agent can
  ask "what happened in the last hour in video-studio" without a new server.
- **ntfy stays the push-to-phone outlet,** not the log. Task 4's bridge copies the existing ntfy topics into the log
  so senses, alerts and approvals decisions are in it on day one without changing those producers.
- **Mac producers** append to a local spool (`~/.local/state/events/spool.jsonl`, under 1 ms, never blocks a hook); a
  launchd agent `com.yehor.events-flush` (KeepAlive) posts batches and keeps the spool when cobalt is unreachable. Ids
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

### 7. Retention

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

## The bet: check and stop condition

**Check,** read 21 days after the money watch goes live: at least 9 Shorts published (3 a week, 60% of the Mon–Fri
cadence `mira-post` is scheduled for), at least 70% of the watch's decision items decided within 24 hours, and watch
spend within its monthly cap (31 runs on the `cheap` preset, each capped by the preset).

**Stop:** fewer than 3 Shorts in those 21 days or fewer than 50% of items decided: switch the money watch off and
record why in ADR 0090. Spend reaching the cap in any month: the cap stops it on its own and the watch is reviewed. The log and
session retain stay either way, since they pay for themselves on the retain lag.

## Tasks (each one pull request)

0. **Cheapest test** (video-studio, the other session's work): no new code here; record the 7-day result in this spec.
1. **Decision records:** ADR 0090 from the Decisions; switchknife ADR 0007.
2. **Switchknife.Journal event stream** (switchknife): `ReadAllEventsAsync`, keyed idempotent append, `IEventSignal`,
   on SQLite and Postgres; minor release tag.
3. **The log in chargehand:** `events/v1` contract, `events.db`, `POST /v1/events`, query, SSE stream, the `events` MCP
   tool, source tokens, refusals, retention. Chargehand emits its own `RunEventKind` events and `schedule.due` into it.
4. **Producers:** the Claude Code hook with spool and `com.yehor.events-flush` (workspace), and the ntfy bridge
   (chargehand). The hook PR reports the effectful share of a day's tool calls.
5. **Consumer 2:** `session-retain.py --follow` (workspace).
6. **Action registry and gate:** `automation.actions`, `POST /v1/actions/{id}`, `act`, gate through `ApprovalsSink`.
7. **Watches and consumer 1:** `Chargehand.Modules.Watch` engine, `money-watch` template, the `studio` producer
   (video-studio). Starts only if Task 0 passed.
8. **Extraction review** after both consumers have run 14 days and a third is named: extract the subscriber client to
   `Switchknife.Events`, or record why not.

## Acceptance checks

These run in `scripts/check.sh` (chargehand) or the switchknife test run unless marked live. A live check runs on the
maintainer's server, and its output goes in the pull request with ids scrubbed.

**Task 2 (switchknife)**
- `EventStreamTests`: `ReadAllEventsAsync` returns events of every flow in `seq` order; a repeat append with the same
  `(flow, key)` returns the first `seq` and stores nothing; a reader awaiting `IEventSignal` wakes after the commit, never
  before it. Same tests against `PgStepStore` with `Switchknife.Testing.Postgres`.

**Task 3 (log)**
- `EventEnvelopeTests`: an envelope missing `id`, `source`, `type` or `time` gets 400; `data` over 16 KB without
  `dataref` gets 413; a `data` value matching a secret pattern gets 422 and nothing is stored; an event with a `work/`
  path anywhere in `subject` or `data` gets 422.
- `EventIngestTests`: a batch of 500 returns 500 `seq` values; the same batch again returns the same values and adds no
  rows; a token for `video-studio` writing `source: claude` gets 403.
- `EventStreamRouteTests`: a subscriber that disconnects after event 10 and reconnects with `Last-Event-ID: 10` receives
  11 onward, none lost and none repeated; a `type` glob filters both the query and the stream.
- `SchemaCompatTests` stays green (`events/v1` is additive to the contracts).
- Live `events-bench`: `scripts/events-bench.sh` on cobalt appends 5,000 envelopes with a full flush and streams 2,000 at
  200/s: at least 250 appends/s and delivery p95 at most 50 ms (the Mac measured 263–281 and 14–17 ms cross-process).

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
- **Building the bus instead of the business.** The money watch exists to restart publishing; if Task 0 fails, it is
  not built.

## Out of scope

- A messenger bot of any kind. The approvals app is the only interface a watch reaches the maintainer through.
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
