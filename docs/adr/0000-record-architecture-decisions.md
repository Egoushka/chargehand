# 0000. Record architecture decisions

- Status: accepted
- Date: 2026-09-26

## Context

Design choices here rest on measurements that go stale (API versions, provider caching rules,
prices). Later contributors need the reason and the evidence, not just the outcome.

## Decision

Record each architectural decision as a numbered Markdown file in `docs/adr/`, from
`template.md`. Every ADR states the decision, the options considered, and the evidence behind
it — a spike result, a measurement, or an explicit "assumption". Each ADR names the condition
under which it should be reopened. Superseded ADRs stay, with a pointer to their successor.

## Consequences

Decisions are reviewable in pull requests like code. Evidence that refers to private
environments is summarised in generic terms; raw data stays outside the repository.

## Reopen if

—
