# Draft of the 1.0 stability declaration

> Draft only, not published anywhere. This is [the stability plan](2026-09-30-stability-1.0.md)'s Task 6: carrying its embedded draft
> declaration text into its own file so it cannot be mistaken for the declaration itself. Nothing here is decided: the maintainer edits
> this after 0.9's feedback and the plan's six feedback conditions, writes ADR 0038 as `accepted`, and publishes the result as the plan's
> Task 7 — not before, and not by an agent. Until then every surface it names can still change in any release, including a breaking one.
> See [docs/guide/stability.md](../guide/stability.md) for the surface table and the guards that already exist for it.

## The text, as proposed in the plan

> chargehand 1.0 declares these stable: the JSON schemas `result/v1`, `request/v1`, `run-status/v1`, `task-spec/v1` and `preset/v1` and
> the `Chargehand.Contracts` package's public API; result signatures (ES256 over the RFC 8785 form); the profile and preset formats,
> the memory and services mappings and secret templates; the CLI's commands, flags and exit codes; the `orchestrate` MCP tool's input
> and the HTTP `/v1` routes; the error codes. Stable means: after 1.0 they change only by adding optional things. A removal or a
> change of meaning waits for 2.0 and its own schema major. Not stable: the C# types inside the `Chargehand` tool package, the text
> of messages and command output, the run log's format, prompts, and anything marked experimental in the guide. A surface is
> deprecated for at least two minor versions before it is removed in a major. Report a break as an issue labelled `compat`.

## Before this can be published

From the plan's own conditions — none of these are checked yet:

1. **Outside use.** At least five outside people have used chargehand, and at least one program other than chargehand's own code
   consumes `result/v1`.
2. **What they asked to change.** Every issue or comment about a contract, profile key, flag or error code, triaged as *needs a break*,
   *additive*, or *no change*; any *needs a break* done before 1.0.
3. **The `support` field in practice**, from the benchmark plan's audit task.
4. **The signature in practice.** At least one third party verified a signed result without help, or feedback names what blocked them.
5. **The extension surface.** At least one profile or preset written by someone other than the maintainer.
6. **No open `broken` item touching a frozen surface.**

And the plan's open questions for the maintainer (scope, the deprecation window's length, whether `profile/v1` gets a JSON Schema, and
what counts as enough outside consumers) are answered in ADR 0038 first.
