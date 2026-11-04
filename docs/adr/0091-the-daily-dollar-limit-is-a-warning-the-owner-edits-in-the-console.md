# 0091. The daily dollar limit is a warning the owner edits in the console

- Status: accepted
- Date: 2026-11-03

## Context

Daily spend was only ever bounded by `max_usd_per_day` on an account (ADR 0050): a hard refusal (`budget_exceeded`, HTTP 429)
set in the profile, changed by editing the profile and restarting. The owner wants a daily figure to tell them a day is
running expensive, not to stop work, and wants to change it where they watch the server, the console. The monthly cap is
the real bound and is not part of this decision (assumption: the owner's wish, 2026-11-03, to keep it as it is).
This covers the warning slice of the intake governor's spend limits (ADR 0084).

## Prior art

Not searched: a one-field soft limit follows the shape of the existing editable settings (`PUT /v1/intake/sources`, ADR 0085).

## Options

1. Make `max_usd_per_day` soft. Breaks accounts that rely on it as a cap.
2. Add a server-wide soft limit beside the hard caps, stored outside the profile. Nothing existing changes.
3. Keep the profile as the only place and document it. The owner still edits a file and restarts.

## Decision

Option 2. A server-wide **soft daily dollar limit**, default 15 USD per UTC day, summed over the owner and every account from
the same priced spend `AccountPolicy` reads. When the day's spend reaches it the server writes one log line for that day and
the console shows a banner on every screen; no run is ever refused or clamped. The owner changes it with
`PUT /v1/limits/daily-warning` (`{"usd": 20}`, or `{"usd": null}` to switch the warning off; above 0 or null), read with `GET`,
both owner-only, kept in `limits/daily-warning.json` beside the run log and effective at once. The Accounts screen has the field
and shows today's spend against it.

The hard caps are unchanged: `max_usd_per_day` on an account stays a refusal for accounts that set it, and the monthly caps
stay hard.

## Consequences

- A day can pass the warning with no refusal; the owner reads the banner and decides.
- No inbox card: the inbox lists runs and pull requests, and has no plain-notice item. A card can follow if the inbox grows one.
- A restart forgets whether today's log line was written, so a restart after crossing writes it once more.

## Reopen if

The warning is ignored in practice and a day's overspend repeats: then a soft limit that pauses automated starts, as the
forecast hold does, is the next step.
