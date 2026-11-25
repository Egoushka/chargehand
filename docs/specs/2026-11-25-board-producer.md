# Board producer: wheelhouse board items become pool items (A7 of the workspace spec 2026-11-25-work-kinds.md)

- Status: proposed 2026-11-25. Issue: #1080.

## What

`BoardProducer` (Chargehand.Automation) reads the wheelhouse board and enqueues one pool item per board item whose
work kind has `runner = "chargehand"`. Today intake reads GitHub only (`GitHubIntake.ReviewGate.cs`).

- Kinds come from the `[kind.NAME]` tables of the workspace `work-kinds.toml` (`WorkKinds.Parse`): `runner`, `model_class`,
  `gates`, `budget_usd`, `max_minutes`, `night`.
- Board items come from an `IBoardSource` (`WhBoardSource` runs `wh board ls --work KIND --state planned --json`).
- An item is produced when its state is `planned`, its kind's runner is `chargehand`, and `source.repo` names a repository.
- The pool item has source `board`, area `board-<id>`, and a body that opens with the kind, model class, gates, budget and
  minutes lines, then the item's ask.
- Inside the night window an item whose kind has `night = false` is skipped; it is produced after the window closes.
- An item already in the pool (same repository and area) is not enqueued again.
- The producer only enqueues. It never claims or starts an item, and never writes to the board.

## Acceptance checks (machine-run)

Command: `dotnet test tests/Chargehand.Tests --filter FullyQualifiedName~BoardProducerTests`

| # | Check |
|---|---|
| 1 | a planned `runner = chargehand` item becomes one pool item with the kind's model class, gates and budget in its body |
| 2 | items of other runners, other states, or without `source.repo` are not enqueued |
| 3 | in the night window a `night = false` item is skipped and a `night = true` item is produced; outside it both are produced |
| 4 | producing twice enqueues once |
| 5 | `WorkKinds.Parse` reads the real file's shape (inline tables, arrays, comments) |
