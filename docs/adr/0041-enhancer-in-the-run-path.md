# 0041. The prompt enhancer in the run path

- Status: accepted
- Date: 2026-10-05

## Context

ADR 0040 added the seam (`IPromptEnhancer`, the profile's `prompt_enhancer`) and nothing called it. `chargehand run`, `serve`
(POST /v1/runs and the MCP `orchestrate` tool) and `change` all reach `Orchestrator.RunAsync`, so that is the one place to call it.
The client spec (docs/specs/2026-10-02-a-client-of-your-own-design.md, decision 7) says a rewrite is shown to the user and the
original is sent unless the user accepts. A run started over HTTP, MCP or a script has no user at that moment.

## Options

1. Send the rewrite when the enhancer says `changed`. Silent change of a prompt the caller wrote; the caller cannot tell.
2. **Chosen.** Send the original always; report a rewrite that was offered as not accepted.
3. Return the rewrite in the result for the caller to resend. Needs a field in `result/v1`, a contract change for a feature with
   no client yet.

## Decision

- **The text sent is the request's text, unchanged, in every run** (interactive or not). A rewrite changes nothing in intake,
  worker prompts, the prompt chain or the run log. It is tagged on the run (`chargehand.enhancer.rewrite_offered`), never logged as text.
- **When:** after the request, preset and signing key are valid and before intake, so a request that is refused before anything
  is spent sends nothing. Skipped when the profile has no `prompt_enhancer` or the text is blank.
- **Context sent:** the four fields of ADR 0040. `repository` is the checkout's folder name, never its path (a path holds the
  home directory); `commit` only when it is a hex id; `task_kind` is the preset name; `client` is `chargehand`.
- **Feedback, once per run, after the result,** when the enhancer gave a `request_id`: `rewrite_accepted` is `false` when a
  rewrite was offered and null when there was none; `cost_usd` only when a worker ran (a run that stopped at intake prices
  nothing, and `0` would read as measured); `model` from the result's prompt chain, unless unknown; `score` null, because a run is
  not scored (scores belong to evals, which do not use the enhancer); `model_overridden` null.
- **Fail open.** The guard's rule stands: down, slow or wrong means the run goes on as before. Feedback waits at most the guard's
  deadline, so a stalled enhancer delays a finished run by at most `deadline_ms`.
- Not here: driven batches (their tasks do not go through `RunAsync`), and a client that shows the rewrite and lets a user accept.

## Consequences

- whetstone sees real prompts from chargehand runs and gets cost and model back, with `rewrite_accepted` false until a client
  can accept one. Its acceptance statistics from chargehand runs are therefore zero by construction.
- Accepting a rewrite later is a client feature: it sends the rewrite as the request text, and the next run reports it as a new
  prompt. No contract change is needed for that.

## Reopen if

A client can show a rewrite to a user, or a caller wants rewrites applied to its own non-interactive runs by an explicit opt-in.
