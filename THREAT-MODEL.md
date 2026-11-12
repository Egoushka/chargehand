# Threat model: chargehand

Last reviewed: 2026-11-12 (security gate on the task pool change, ADR 0099).

This file starts with the task pool. The rest of the server (runs, accounts, intake, the console) is not modelled here yet;
see section 4.

## 1. What are we working on

The task pool (ADR 0099, `src/Modules/Chargehand.Modules.Pool`): the owner's chats queue tasks with the MCP tool
`enqueue`; a governor in the server starts one worker per repository and area, charged to the automation account, when its
checks allow.

```
owner's chat ──MCP enqueue──▶ server (owner key) ──▶ pool.db ──governor tick──▶ worker = driven batch ──▶ repository checkout
                                                                          ▲
                                       run session (run token) ───────────┘ refused: a run never enqueues
```

**Trust boundaries:** MCP caller → server; queued item → worker prompt; worker → repository.

**Assets:** the automation account's token budget; the repositories the server may change; `pool.db` (task text the owner
wrote); the server's availability.

**Actors:** the owner; an account key holder; an agent session holding a run token; text inside a queued item.

**Dangerous functions:** starting a worker (spends tokens, writes to a repository); writing `pool.db`.

## 2. What can go wrong

| Threat | STRIDE | Entry point |
|---|---|---|
| An account key or a run's session queues work charged to the automation account, outside its own grant | E | `enqueue` |
| A queued item names a repository the server should not touch | T | `enqueue` → governor |
| A credential pasted into a task reaches the worker's prompt, the run log or `pool.db` | I | `enqueue` |
| A runaway producer fills `pool.db` and the disk | D | `enqueue` |
| One area that cannot start stalls every other area | D | governor tick |
| A worker that hits its token ceiling drops its items, or one with no progress loops forever | D | worker end |
| After a restart a resumed batch runs again beside a new worker | T | server start |

## 3. What are we going to do about it

| Threat | Control | Where | Test that proves it |
|---|---|---|---|
| Spend outside a grant | Owner key only; a run token or account key gets `forbidden`; no caller identified, refused (that branch is not reachable over HTTP and has no test) | `src/Chargehand.Server/EnqueueTool.cs` | `PoolServerTests`: account key, run token |
| Unknown repository | Refused unless in `automation.github.repositories`, without echoing it | `EnqueueTool.cs`, `PoolRunStarter.Knows` | `PoolServerTests`: unknown repository |
| Credential in a task | Refused with `secret` before it is written; the value is not echoed | `EnqueueRequest.Validate` | `PoolServerTests`, `PoolTests.Enqueue_refuses_bad_input_with_stable_codes` |
| Disk fill | `http.pool.max_queued` (default 1000 queued items, a starting value); body at most 16 KB. Done and failed items stay in `pool.db`: no pruning yet | `TaskPool.EnqueueAsync` | `PoolTests.Enqueue_refuses_with_full_once_max_queued_items_wait` |
| One area stalls the rest | A held scope carries its reason and the tick tries the next one; a check or starter that throws holds its scope too | `TaskPool.TickAsync` | `PoolTests.A_scope_the_starter_refuses_does_not_stall_the_scopes_behind_it` |
| Ceiling loss or loop | A ceiling stop, read from the worker's row of the batch, hands its items back with a resume card; a stop with no tokens used, or the third ceiling stop in a row in one area, ends them as failed | `PoolRunStarter.Card` | `PoolServerTests` card tests |
| Double run after restart | Workers whose batch resumed are adopted; only the rest are released | `PoolRunStarter.AdoptAsync`, `PoolService` | `PoolServerTests`: restart adoption |

Text inside a queued item is the owner's instruction to the worker, so prompt injection through it is out of scope: only the
owner can enqueue.

## 4. Did we do a good enough job

- 2026-11-12: one independent reviewer read the pool change for security, in two rounds. The built-in
  `/security-review` was not run on it.
- Not modelled yet: the run API, accounts and keys, intake, the console, the event sink. The next entry point, asset or
  boundary added to any of them adds its rows here.

## Accepted risks

None recorded.

## Reporting

See [SECURITY.md](SECURITY.md).
