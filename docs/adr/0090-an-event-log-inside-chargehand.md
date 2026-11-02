# 0090. An event log inside chargehand

- Status: accepted
- Date: 2026-11-02
- Spec: [Events and watches](../specs/2026-11-02-events-and-watches-design.md)

## Context

Every tool the maintainer runs writes its own events and none of them can be subscribed to. The spec's inventory of
stores found no way to join them by time or cause, no way for a process outside a store to subscribe and resume, and
no record of agents' own actions beyond per-hook logs on the Mac. Agents on different hosts also have no address to
message each other through; `SendMessage` reaches sessions on the same Mac only and keeps no record.

What is real today, counted over 14 days to 2026-11-02 outside `work/`: two polls could be pushes (`hz deployed --wait`,
about 50 calls a day, and the hourly session retain). The first consumer the design was drawn for, a money watch for
the video-studio funnel, is parked as an idea. The relay test (about 6.5 hand relays a week against a starting
threshold of 5) does not decide whether cross-host messages are needed. Load: 24,900 tool calls in 24 hours (about 0.3
a second); of 49,183 calls in 14 days only 11.2% are filtered out by tool name, so most would be recorded.

Measured on the maintainer's server (the target host): one durable commit per event gives 15 to 33 appends a second for
every option tried; 500 events in one commit give 3,580 to 5,823 a second. The options differ little in speed once the
flush is real.

## Prior art

- Query: "How do agent platforms record every agent action in an append-only event log that agents and automations can subscribe to and resume from?"
- Coverage: hit by count (8 facts from 7 tools), thin in substance: the facts are agent-run tracing (OpenAI Agents SDK
  tracing), checkpointed graph execution (LangGraph, Microsoft agent framework), failure analysis, and a polling SCM
  observer. None is a cross-tool, subscribable, resumable log. Nothing was ingested; the wire format follows open
  standards instead (CloudEvents 1.0 for the envelope, server-sent events with `Last-Event-ID` for resume), and the
  storage choice was settled by measurement (spec, decision 1).
- Facts: 01a187fe-435e-772b-9540-089c5aa96d20, 01a187fe-435e-7031-a8ef-08f23feb7bfc, 01a19806-883a-72f0-bf54-0615b70f529d

## Options

1. **A new bus or database** (NATS JetStream, Postgres with LISTEN/NOTIFY). Faster in a vacuum, but with a real flush
   all four options are disk-bound at the same rate; each adds a service (workspace rule D-39: nothing new adds a store).
2. **One append-only log inside chargehand,** on `Switchknife.Journal`'s existing `events` table, its own `events.db`.
   No new service; the library and a consumer (`DrivenJournal`) exist. Single writer.
3. **Do nothing:** keep per-tool stores and polls. Costs nothing; leaves the three gaps open.

## Decision

Option 2, with the shape in the spec's decisions 1 to 9:

- Store: `Switchknife.Journal` with its own `events.db`, one writer (chargehand), a batch is one transaction and one
  flush. Switchknife gains `ReadAllEventsAsync`, a keyed idempotent append, an `IEventSignal` and an indexed `tag`
  (its ADR 0007).
- Envelope: CloudEvents 1.0 plus six extensions (`seq`, `correlationid`, `causationid`, `actor`, `effect`, `scope`);
  contract `events/v1`.
- Transport: `POST /v1/events`, query, SSE stream resumed with `Last-Event-ID`, an `events` MCP tool; Mac producers
  spool and flush; ntfy stays the phone push.
- Scope of recording: actions with effect, no reads, prompts or content; nothing from `work/`; no secrets.
- Action registry with reversibility classes and the approvals gate; watches (filter in code, capped one-shot runs);
  scopes and agent messages. These have no named consumer yet and were kept by the maintainer's choice; each is built
  after the log and the two consumers, and has a stop condition in the spec.
- First consumers: `hz deployed --wait` (waits on a deploy event, box state as fallback) and session retain (follows
  the stream, the hourly job stays as sweeper).
- Retention: 90 days in `events.db`, then monthly JSONL in the backup set (starting value).

## Consequences

- The log is the most private file on the box: tailnet only, a token per source, summaries and hashes instead of
  content, refusals for secrets and `work/`.
- Chargehand down means Mac producers spool, box producers retry, and consumers fall back to their old poll.
- The first two consumers gain latency only; the spec says so. Value for the registry, watches and messages is untested
  until a consumer exists, and their stop conditions decide whether they stay.
- A shared `Switchknife.Events` client is extracted only after a third consumer (spec, Task 8).

## Reopen if

- Sustained appends pass 350 a second, or the p95 of a 500-event commit passes 1 second, or a subscriber outside the
  tailnet needs the log: move to Postgres (`Switchknife.Journal.Postgres` exists) before any broker.
- The log's two consumers need their fallback in more than 1 of 5 waits or sweeps (spec, the bet).
- The registry or messages find no consumer within 21 days of shipping (spec, the bet): remove them.
