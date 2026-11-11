# Moving `Profile` into the new layout

ADR 0060: "The `Profile` record goes away: each module binds and validates its own options." Checklist section 1 of
`2026-11-08-rewrite-port-checklist.md` tracks it. This plan covers what is left in `src/Chargehand/Config/Profile.cs`.

## State

- Already moved (PR 822 and its siblings): `SecretSource`/`SecretSources`, `TelemetrySettings`, `DeliverySettings` (Kernel,
  `KernelSettingsJsonContext`), `DrivenNetwork` (Gateway), `PromptEnhancerSettings` (Research), `InboxSettings` (Console),
  `InstallPaths` (Kernel), `DrivenRunnerSet` (Runners). Every module section binds through its own source-generated context,
  combined in `Profile.LoadJson`.
- Left in `Profile.cs`: the root `Profile`, `McpServerSettings`, `MemoryListConverter`, `HttpSettings`, `DrivenSettings`.
- Users: 46 files name `Profile` (Chargehand 17, Server 13, Mcp 6, Driven 2, Automation 2, Cli 1, ClaudeCode 1, Modules 3,
  Kernel 1). The Modules and Kernel hits are comments or the remaining settings types, not real dependencies.

## Who owns what (Skeleton.Layers: references point down)

- `McpServerSettings` to Memory (layer 2). Memory already owns `MemoryProviderSettings` and `MemoryMapping.Validate(memory,
  serverNames)`, which names servers; Research (9) and the MCP client read it from above. Kernel is rejected: it stays small.
- `MemoryListConverter` goes with `MemoryProviderSettings` to Memory (`[JsonConverter]` on the property stays in the root).
- `DrivenSettings` stays a composition record: it holds sections of Runners, Gateway, Forge, Handover and Intake, so it can
  only live where all are visible (Hosts). Its `Validate*` calls become one `DrivenOptions.Validate` in the host.
- `HttpSettings` composes Accounts, Events and Conversations sections: Hosts.Server, with the root.
- The root `Profile` (+ `ProfileJsonContext`, `Load`, `LoadJson`, `Secret`, `Roots`, `ServicesFor`) goes to Hosts last. It
  is composition only; the module that reads a section receives that section, never the root.

## Order of slices

1. Done here: `McpServerSettings` to Memory.Api (+ its context entry and tests).
2. `MemoryListConverter` to Memory, with the memory-list tests.
3. Make consumers take the part they need instead of `Profile`: `McpConnectionPool` already takes the server map and a
   `Func<string,string>` secret resolver; do the same for `Doctor`, `ExtensionsCheck`, `ConsoleSystem`. No type moves.
4. `DrivenSettings` validation into one host-side validator; `HttpSettings` to Hosts.Server.
5. Old `Chargehand`, `Driven`, `Automation`, `Mcp` consumers are ported by their own checklist items; each now needs only a
   section type. The last one deletes `Profile.cs`, and the hosts compose the root.

## Keeping the old code compiling

No type forwarding and no using-alias: earlier ports moved the type, added the `using ...Api;` to each user and deleted the
old declaration in the same PR (the PRs are small, 20 or so files of one-line edits). Property names, JSON keys and defaults
do not change, so `ProfileCompatTests` (snapshots of `profiles/example.json` and the inputs under `ProfileCompat/`, read
through reflection and through the source-generated contexts) is the guard at every step.

## Risks

- A section loses its source-generated metadata: the type is then read by the root's context or fails at run time under AOT.
  `A_modules_section_is_bound_by_the_modules_source_generated_context` names the context each type must come from.
- Validation order: `Profile.Load` reports problems in a fixed order, and `ProfileMessageTests` pins messages and actions.
  Moving a `Validate` changes neither text nor order.
- `internal` validators become `public` on the moved types: keep them in the `.Api` namespace and out of docs of other
  modules.
- Merge conflicts: 46 files touch `Profile`; land slices one at a time, small.
- Upward references: the hosts compose the root; a module that needs another module's section is a layering error, not a
  reason to keep `Profile`.
