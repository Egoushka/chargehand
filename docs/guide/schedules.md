---
title: "Scheduled work"
description: "Recurring work on a clock within a monthly cap: dependency upgrades, doc drift, flaky-test suspects and a morning report, with results posted to a notification URL."
order: 15
section: "Guides"
---

Scheduled work ([ADR 0068](../adr/0068-scheduled-work-within-a-monthly-cap.md)) starts runs without a caller, like [intake](intake.md) does, but from a clock. Off unless the profile has `automation.schedules`. Not yet run against a real server: only against fakes.

```json
"automation": { "schedules": {
  "notify_url": "https://ntfy.example/my-topic",
  "entries": [
    { "id": "deps", "at": "weekly mon 05:00", "template": "dependency-upgrades", "repository": { "path": "/srv/repos/app" }, "max_per_month": 4, "budget_class": "S" },
    { "id": "docs", "at": "weekly fri 05:00", "template": "doc-drift", "repository": { "path": "/srv/repos/app" } },
    { "id": "morning", "at": "daily 06:00", "template": "morning-report" }
  ]
} }
```

## Keys

- `at`: `daily HH:MM`, `weekly <mon..sun> HH:MM` or `monthly <1-28> HH:MM`, always UTC. Not cron. Days stop at 28 so every month has one.
- `template` or `goal`, exactly one. `goal` is free text and runs read-only on the `default` preset; add `"driven": true` to run it as a driven batch of one task that may open a pull request.
- `repository.path`: the server's checkout, under `repository_roots`. Each run starts from the commit `origin/HEAD` points at after a fetch. Not used by `morning-report`.
- `max_per_month`: 1 to 100, counted per calendar month in UTC. Default 4; 31 for `morning-report`. An occurrence over the cap is skipped and logged.
- `budget_class`: S, M or L for a driven entry (default S).
- `notify_url`: an ntfy-style URL. The text goes in the body, the `Title` header carries the title. The URL can hold a secret topic, so it is never logged. Required by `morning-report`.

## Templates

| Template | Kind | What it does |
|---|---|---|
| `dependency-upgrades` | driven | Updates dependencies to the latest compatible minor and patch versions, runs the tests, opens one pull request listing what changed and what was skipped. Needs `driven.enabled`. |
| `doc-drift` | read-only | Finds statements in README and docs/ that the code contradicts; each finding cites the doc line and the code line. |
| `flaky-tests` | read-only | Reads the test code for flake patterns (timing, ordering, shared state). chargehand has no tool for CI history, so it cannot see runs that passed and failed on the same commit; the template tells the worker to say so. |
| `morning-report` | none | No model call. Last 24 hours of top-level runs and batches (finished, failed, need you, denied, still going), spend, and the open inbox items, POSTed to `notify_url`. |

## Behaviour

The scheduler wakes every minute. An entry is due when its next occurrence after the last one it dealt with has passed. The first wake of a new entry owes nothing. After downtime, the missed slots run once at the first wake, not once per slot. An occurrence is dealt with when it starts, when the cap is used, or when the entry's previous run is still going (both skips are logged). A start that fails (server halted, checkout will not fetch, notification service down) is retried after 10 minutes and is not counted.

When a read-only run ends, its summary and findings with their cited places are POSTed, cut to 3500 characters. A driven run posts nothing; its pull request and questions are in the inbox. Without `notify_url` a read-only result stays in the run log. State is in `schedules/state.json` beside the run log.

Validation errors read `automation.schedules: ...` and refuse the profile when it loads.
