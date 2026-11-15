---
title: "Stability and compatibility"
description: "What the 1.0 declaration will freeze, what stays free to change, how a change to a frozen surface is made, the deprecation window, and how to report a break."
order: 10
section: "Project"
---

**Nothing is frozen yet.** This page is Task 6 of [the stability plan](../plans/2026-09-30-stability-1.0.md): a description of what a 1.0
declaration would freeze and how it would be kept, written ahead of the declaration itself so the guard tests exist before anything is
promised (`tests/Chargehand.Tests/SchemaCompatTests.cs` is one). The declaration is a separate, later step (the plan's Task 7): the maintainer writes it after 0.9's feedback, in its own pull
request, with its own ADR. Until then every surface below can still change in any release, including a breaking one. [ADR 0038](../adr/0038-stability-and-compatibility-policy.md), which would record the maintainer's decision on scope, does not exist yet — the ADR
sequence currently runs 0037, then 0039 (see [Architecture decisions](decisions.md)) — so the scope here is the plan's proposal, not a
decision.

## What would be frozen

The plan proposes that chargehand's extension points are configuration and contracts, not a C# library: `Chargehand.Contracts` (`src/Chargehand.Contracts/`) is the
only published library, and the interfaces inside the `Chargehand` tool package (`IWorkerRuntime`, `IMemoryProvider`,
`IEvidenceResolver`, `IPriceTable`, `ISandbox`, and others) stay internal. On that reading:

| Surface | Where it lives | Frozen would mean | Guarded today by |
|---|---|---|---|
| `result/v1`, `request/v1`, `run-status/v1`, `task-spec/v1`, `preset/v1` | `schemas/*/v1/*.json`, `src/Chargehand.Contracts/*.cs` | fields, types, enums and required-ness only loosen; new fields optional | `SchemaCompatTests` against the latest `v*` tag; `SchemaFixtureTests`; `ContractParityTests` (every enum in the C# contracts has the same values as its schema, in both directions) |
| Result signatures | `src/Chargehand.Contracts/ResultSignature.cs`, [ADR 0036](../adr/0036-support-checking-and-signed-results.md) | ES256, the RFC 8785 canonical form, `key_id` derivation, "minus `signature`" | `ResultSignatureTests`; `ResultSignatureVectorTests` (checked-in vectors generated in-test against a key embedded as text, never a file) |
| `Chargehand.Contracts`'s public API | `src/Chargehand.Contracts/` | public types and members stay; new ones may be added | `PublicApiSnapshotTests` against `tests/Chargehand.Tests/public-api/Chargehand.Contracts.txt`; additions pass, removals fail |
| Error codes and `retryable` | [ADR 0022](../adr/0022-error-codes-in-result-v1.md), the [error code table](reference.md#error-codes) | codes keep their meaning; new codes are additive | per-code tests; the table in the reference is the documented list a code must appear in |
| The profile format and its defaults | [the profile fields table](reference.md#profile-fields), `src/Chargehand/Config/`, [ADR 0026](../adr/0026-extension-model.md) | keys keep their meaning and default; removing a key needs a migration message for one minor before 1.0, none after | `GoldenProfileTests` against `tests/Chargehand.Tests/golden/profile.full.json`, a profile that sets every key |
| Memory and services mappings, secret templates | [ADR 0034](../adr/0034-memory-and-services-over-mcp.md), [Memory and services](memory-and-services.md) | placeholders, mapping fields and `{secret:item}` keep their shape | `ExtensionsCheckTests` and the mapping tests |
| CLI commands, flags and exit codes | [the reference](reference.md), `src/Chargehand.Cli/` | commands and exit codes in the reference keep working | `CliSurfaceTests` (the reference's command table against each command's `--help`) |
| The `orchestrate` MCP tool and the HTTP `/v1/*` routes | `docs/mcp-server-2025-12-11.schema.json`, [the MCP guide](mcp.md), [the server guide](server.md) | tool name, input schema, HTTP paths and status codes keep working | `McpSurfaceTests` and the HTTP route tests; additive only |
| Output text of `verify` and `extensions check` | — | **not proposed as frozen**: only their exit codes would be | — |

A frozen surface changes only additively after 1.0: a removal or a change of meaning waits for a 2.0, with its own schema major
(`schemas/<name>/v2`, a new `$id`; `tests/Chargehand.Tests/SchemaCompatTests.cs` fails an in-place breaking edit). Every row above already has a test that fails on a breaking change, ahead of any declaration — that
is deliberate: the plan's rule is that the guard exists before the promise is made, not after.

## What stays free to change

Proposed as not frozen, in any release: the C# types inside the `Chargehand` tool package (as opposed to `Chargehand.Contracts`), the
text of messages and command output, the run log's format, prompts, and anything the guide marks experimental. These can change, including
breaking, in a minor or even a patch release. The published library is `src/Chargehand.Contracts/`; the tool package's types live in `src/Chargehand/`.

## How a change to a frozen surface is made

- **Additive first.** A new field, enum value, flag or route is optional or chosen by its absence; nothing that already works stops
  working; `tests/Chargehand.Tests/PublicApiSnapshotTests.cs` fails a removal from the contracts' public API.
- **A breaking change is a major.** It lands behind a new schema major (`schemas/<name>/v2`), never by editing a published `v1` file in
  place; `SchemaCompatTests` is what catches an in-place breaking edit before it merges.
- **A deprecation runs for at least two minor versions.** The changelog entry that deprecates a key, flag, code or field says
  "deprecated" and names its replacement; removal waits until at least two more minors have shipped after that entry.

## How to report a break

Open an issue labelled `compat` naming the surface, the version where it broke, and the version where it last worked. Before 1.0, that
issue is useful feedback for the declaration (see the plan's feedback conditions); after 1.0 it is a bug against the frozen surface
itself.

## Where this stands

[The stability plan](../plans/2026-09-30-stability-1.0.md) tracks the remaining tasks. Task 1 (the maintainer's scope decision, recorded
in ADR 0038) is not done. The guard tests for Tasks 2 through 5 already exist in `tests/Chargehand.Tests/`, ahead of the plan's own
checklist. Task 7, the declaration itself — ADR 0038 accepted, this page linked from the README and the guide index, `ROADMAP.md`'s 1.0
line ticked, the version set, a changelog entry, the release — is the maintainer's, after the plan's feedback conditions hold, and is not
done by an agent.
