# 0068. Scheduled work within a monthly cap

- Status: accepted
- Date: 2026-10-18

## Context

Intake (ADR 0051) starts work from GitHub. Some work has no trigger but the calendar: weekly dependency upgrades, a check for docs the code has outgrown, a morning summary. Run by hand it is forgotten; run by an unbounded timer it is a spend risk. Assumption: a small fixed syntax covers the owner's needs; cron is more than needed.

## Options

1. An external cron calling `POST /v1/runs`. No server change, but no cap, no catch-up, no state, and the result goes nowhere.
2. A scheduler hosted in the server, with a monthly start cap per entry and its own state file. More code, one place for the rules.
3. Full cron syntax. Expressive; hard to validate, and a typo can mean a run every minute.

## Decision

Option 2. `automation.schedules` lists entries with `daily HH:MM`, `weekly <day> HH:MM` or `monthly <1-28> HH:MM` (UTC). `Scheduler` wakes every minute and starts due entries through `IBatchStarter`, like intake. The occurrence handled is stored per entry, so a restart or downtime runs a missed slot once. A cap of `max_per_month` starts per UTC calendar month (default 4) skips and logs the rest. An entry whose last run is still going is skipped. Templates are code constants, not files: nothing extra to ship. Read-only results go to an ntfy-style `notify_url` as plain text; `morning-report` makes no model call and reads the server's own run log and inbox.

## Consequences

State lives in `schedules/state.json` beside the run log. The notification URL may carry a secret topic and is never logged. `flaky-tests` reads CI history through the forge (CHARGEHAND-197): the jobs that failed and then passed on the same commit in the last 14 days, as input to the goal; without history it reads test code only. `security-alerts` (CHARGEHAND-190) turns open code-scanning and Dependabot alerts, read as the App, into one batch per run with a task per alert not taken before, at most `max_tasks`; the taken alerts are in the state file, and a run with no new alert starts nothing and does not count against the cap. Scheduled starts bypass account admission and run as the owner, like intake. A driven run's result is not posted; its pull request is in the inbox.

## Reopen if

Flakes need test-level history the job logs do not give (a test-report artifact), an entry needs a time zone, or the cap needs to be spend-based rather than count-based.
