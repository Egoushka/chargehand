# 0070. A request switch for the prompt enhancer

- Status: proposed; implemented with this ADR, not yet reviewed by the maintainer
- Date: 2026-10-19

## Context

whetstone's replay gym re-runs stored prompts through chargehand with challenger templates to get trial evidence. Every run
reaches `Orchestrator.RunAsync`, which asks the `prompt_enhancer` (ADR 0040, 0041) about the text and reports the outcome. A
replay would then be offered to the enhancer as a new prompt and reported on, so the enhancer would learn from its own
experiment. Only the caller knows a run is a replay. whetstone's `enhance/v1` also takes `context.parent_request_id` (a prompt
written by another prompt's run) and `feedback/v1` takes `outcome.review_findings`, neither of which ADR 0040's four-field
context could carry.

## Options

1. A profile key. Server-wide, so one server cannot serve both replays and real work.
2. **Chosen.** `request/v1` `context.enhance: false`, optional, additive.
3. Refuse the enhancer by preset or client name. Implicit, and a replay would need its own preset.

## Decision

- `context.enhance`: `false` skips the enhancer for the run, with no `enhance` call and no `feedback`; absent or `true`
  changes nothing. Local runs keep skipping it regardless (ADR 0069).
- `context.parent_request_id` (1 to 128 characters): the enhancer's request id of the prompt whose run wrote this request's
  text. It is sent as `context.parent_request_id` of `enhance`; an id outside the length is dropped, not an error. The
  context is now five fields: the four of ADR 0040 and this id, which the enhancer itself issued.
- `EnhanceOutcome` gains `review_findings`, sent in `feedback` when set. No run path sets it yet: chargehand's reviews of a
  change run inside driven sessions, which do not go through `RunAsync` (ADR 0041).

## Consequences

- A gym replay sends `enhance: false`, so nothing of it reaches whetstone from chargehand.
- `request/v1` stays v1: both fields are optional.

## Reopen if

Driven batches call the enhancer, or a review result can be tied to the enhanced run, so `review_findings` can be filled.
