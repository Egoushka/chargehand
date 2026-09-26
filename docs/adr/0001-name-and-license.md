# 0001. Name and license

- Status: accepted
- Date: 2026-09-26

## Context

The working name `foreman` collides with established projects (a Procfile runner, a lifecycle-management
platform). The project is public from day one and ships a contract package that other services consume.

## Options

1. `chargehand` — a foreman synonym; on 2026-09-26 free on NuGet, PyPI and npm, no GitHub repository of that name.
2. `affidavit` — evokes evidence-backed claims; free on all three registries; ~190 small GitHub repositories.
3. `crewboss` — free on all three registries; 6 small GitHub repositories.
4. License MIT — shortest; no patent grant.
5. License Apache-2.0 — explicit patent grant and patent-retaliation clause.

## Decision

Name **chargehand**. License **Apache-2.0**: the patent grant matters for tooling others embed, and the cost
(license text, no NOTICE requirement unless we add one) is negligible.

## Consequences

Packages are `Chargehand.*`. Contributions are accepted under Apache-2.0 (inbound = outbound).

## Reopen if

A trademark conflict appears for the name.
