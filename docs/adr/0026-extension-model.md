# 0026. The extension model

- Status: accepted; runtime selection amended by 0032; the memory and services rows detailed by 0034
- Date: 2026-09-28

## Context

D2 (naked core) and D4 (stacking) require every optional capability to sit behind a category with a declared
default, an extension point and a combine rule, so a user connects only what they choose. Three parts of the
codebase are not shaped that way yet, and block goal 0.4's exit ("a clean machine with only one agent CLI installed
and signed in gets an answer with resolved evidence, with no profile file"):

- `Profile.Load` (`src/Chargehand/Config/Profile.cs`) requires a file and requires `WorkerRoot`, `DefaultPreset`,
  `IntakeModel` and `Prices` on it, none defaulted.
- `Profile.Secret` is a hard-coded switch on `SecretStore` (`"env"` or Keychain via `security`), not an extension
  point; a user who wants a different store cannot add one without a code change.
- The runtime the orchestrator drives is chosen by a hard-coded `if` in `src/Chargehand.Cli/Program.cs`, and
  `IPriceTable.PriceUsd` (`src/Chargehand/Budget/IPriceTable.cs:20`) throws `KeyNotFoundException` for a model with
  no price entry, so an unpriced profile cannot run at all.

Owner's brainstorm, 2026-09-28, resolved the mechanism for each. `WorkerMessage`
(`src/Chargehand/Runtime/IWorkerRuntime.cs:64`) carries token counts only, no cost figure from either runtime
adapter — "the runtime's own cost report" (spec.md goal 0.4) needs an interface change neither adapter has today.

## Options

**Runtime selection when more than one agent CLI is found and none is named**
1. Fixed priority order (e.g. claude before opencode), silently pick the first found. Simplest; picks silently on a
   dev machine with both installed, which the worker-runtime category's own combine rule ("no silent fallback",
   spec.md §6) already rules out for routing.
2. **Chosen.** Fail with every CLI found and how to name one explicitly (profile field or environment variable).
   Consistent with the category's own no-silent-fallback rule; costs one more error path.

**`worker_root` default with no profile file**
1. System temp directory. No cleanup story, matches "nothing configured" most literally, but a long-running clone
   can be swept mid-run by the OS or a temp-cleaner.
2. **Chosen.** A fixed directory outside `$HOME` (ADR 0003 already forbids checkouts under it — see
   `Orchestrator.OutsideHome`, `src/Chargehand/Orchestrator.cs:372`), created on first use. Persists across reboots;
   documented and, later, overridable.

**Secret command template execution**
1. Shell string run through `sh -c`, `{item}` substituted. Lets an adapter use a pipe or shell feature (e.g.
   `pass show {item} | head -1`); opens a shell-injection surface to review on a public repository.
2. **Chosen.** Argv array, `{item}` substituted per element, run directly via `Process` — the shape
   `Profile.Keychain` already uses. No shell, no injection surface; an adapter needing shell features is out of
   scope until one asks for it.

**Missing price entry**
1. Add a cost figure to `IWorkerRuntime`/`WorkerMessage`, populate it from OpenCode's and Claude Code's own reported
   cost where present, fall back to token budgets only when the runtime reports none. Most faithful to spec.md's
   wording; a real interface change to both runtime adapters for a goal about running with nothing configured.
2. **Chosen.** `PriceUsd` returns `decimal?`; no entry means unknown, not zero-and-hidden. `RunCapUsd` stays
   inert for that model; the token budgets already enforced per node kind (`kind.Budget.MaxInputTokens`,
   `MaxUsd`) remain the real guardrail. The runtime's own cost report becomes a later goal, named below as
   deferred rather than silently dropped.

## Decision

**Profile is fully optional.** `Profile.Load` returns owner-visible defaults when its file is absent instead of
throwing:

| field | default |
|---|---|
| `WorkerRoot` | a fixed directory outside `$HOME`, created on first use |
| `DefaultPreset` | `cheap` |
| `IntakeModel` | unset — `CreateAsync`/`NodeSpec.Model` accept `null` and the runtime uses its own default model |
| `Prices` | empty — every `PriceUsd` lookup is unknown until a price file is added |
| `RunCapUsd` | `1.00m` (already defaulted, unchanged) |
| `SecretStore` | replaced — see below |

**Runtime selection.** A `RuntimeSelector` replaces the `if` in `Program.cs`. A profile or environment variable
naming a runtime wins. Otherwise it probes `PATH` for known agent CLIs: none found raises `RuntimeUnavailable`
naming what to install; more than one found raises a new `RuntimeAmbiguous` error naming every CLI found and the
field or variable that picks one. No priority-order fallback.

**Secrets by command template.** `SecretStore` (a single string) is replaced by an ordered list, each entry an
extension in this category; the category's combine rule is first success wins:

```json
"secrets": [
  { "env": true },
  { "command": ["security", "find-generic-password", "-s", "{item}", "-w"] }
]
```

`command` runs as an argv array via `Process`, no shell; `{item}` is substituted per array element, matching the
existing `Keychain()` call. Default with no profile: `env` only. The old Keychain switch is expressible as an
explicit `command` entry — profiles carrying `"secret_store": "keychain"` need that one-line migration, noted in
the changelog.

**Prices are optional.** `IPriceTable.PriceUsd` returns `decimal?`; `null` propagates as unknown cost through
`WorkerNode.Usage` and `Orchestrator.Execute` rather than throwing or silently costing `$0` that reads as measured.
`RunCapUsd` cannot bind an unpriced model; per-node-kind token budgets remain enforced regardless of price data.

**Category table, documented now, coded only where a second adapter or this goal needs it (D4):**

| category | default (0.4) | extends through | combine | ships code in 0.4 |
|---|---|---|---|---|
| worker runtime | detected agent CLI | native adapter, ACP (0.7) | routed by node kind, no silent fallback | yes |
| secrets | environment variables | command template | first success wins | yes |
| prices | none (unknown cost) | price file or URL | overlay, later wins | yes (`null` plumbing only; no file-loading adapter yet) |
| memory | none | MCP memory server | fan-out, labelled by source | no — 0.6 |
| services in runs | reading the repository | MCP server per preset | union of tools | no — 0.6 |
| tracing | local run log | OTLP exporter | fan-out | no — already partly separate (Langfuse) |
| task sources | CLI, HTTP, MCP requests | MCP (issue trackers) | independent, per-source pause | no — after 1.0 |
| evidence checks | git, session, inputs, diff | new kind (tests, support check) | all must pass | no — 0.7–0.8 |

## Consequences

- `Profile.cs`: `Load` builds defaults when its file is missing; `WorkerRoot`, `DefaultPreset`, `IntakeModel`
  become optional with the defaults above; `SecretStore` (string) is replaced by `Secrets` (ordered list).
- `Program.cs`: the hard-coded runtime `if` is replaced by `RuntimeSelector`; `ErrorCode` gains
  `RuntimeAmbiguous`.
- `IPriceTable.PriceUsd` returns `decimal?`; every caller (`WorkerNode.Usage`, `Orchestrator.Execute`,
  `RoutingReport`, evals scoring) is updated for the nullable cost.
- `ClaudeCodeWorkerRuntime.CreateAsync` and `OpenCodeWorkerRuntime` accept an unset model and fall back to the
  runtime's own default.
- `profiles/profile.schema.json` and `profiles/example.*` are updated for the new `secrets` shape; a profile still
  carrying `"secret_store"` needs the one-line migration above.
- Deferred, not in scope for 0.4: the runtime cost report (needs an `IWorkerRuntime` interface change), memory and
  services fan-out (0.6), ACP runtimes (0.7), evidence-check extension (0.7–0.8), task sources (after 1.0).
- Dogfood findings 1–3 (error cause and action, MCP run id before timeout, `repository_not_allowed` message) and
  packaging for the MCP Registry are out of this ADR's scope — bounded tasks and a separate spike respectively, per
  the 2026-09-28 brainstorm's scope split.

## Reopen if

- A second secret-store adapter needs a shell feature (a pipe, environment expansion) the argv-array template
  cannot express.
- The runtime cost report goal starts: this ADR's "prices optional" section is revisited once `IWorkerRuntime`
  carries a cost figure.
- A category in the table above gets a second adapter before its listed goal: composite/combine code for it moves
  up from "documented" to "coded" ahead of schedule.
