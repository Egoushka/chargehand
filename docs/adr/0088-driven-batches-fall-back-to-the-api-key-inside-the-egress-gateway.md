# 0088. Driven batches fall back to the API key inside the egress gateway

- Status: proposed
- Date: 2026-10-29

## Context

Issue #632 made worker runs subscription-first: a turn that ends on the subscription's limit runs again on
`claude_code.api_fallback_key_secret`, under `api_fallback_daily_usd`, with the day's spend in `api-fallback.json`
(`ApiFallback`, #642). Driven batches were left out, and a subscription 429 still stops them
(`BatchMachineTests.An_unavailable_provider_stops_the_batch_without_fallback`; ADR 0039 as amended on 2026-10-29: "a batch
never switches to an API key on its own").

The worker-run design does not carry over, for three reasons found in the code:

- **One credential per batch.** On the subscription, the session holds only a per-task run token. The batch's egress
  container (`ModelGateway`) holds the real OAuth token, swaps it in, and forwards to one upstream host (ADR 0039,
  decision 9; `SiteRoute.Of` builds `ModelGatewaySpec(credential, tokenKey)`). No second credential can reach the
  session, and none should.
- **The session cannot be restarted on a new credential.** A worker turn reruns the same CLI session with `--resume`
  and `--bare`. A batch session is a long-lived container whose `ANTHROPIC_BASE_URL` points at the gateway, so the
  credential can change only behind that URL.
- **The cap lives on the server; the calls happen in the egress container.** `ApiFallback` checks today's spend before
  each call and adds `total_cost_usd` after. The egress container sees token counts per response (`GatewayEvent.Tokens`),
  not dollars, and has no channel back to the server during the batch.

Also: the runner service refuses any upstream but `api.anthropic.com` for the egress model endpoint (ADR 0039, status
note). The fallback goes to `api_fallback_base_url`, which is a gateway in the maintainer's setup.

## Options

1. **Keep stopping.** A batch on a subscription limit ends its tasks `rate_limited` and the scheduler's gate waits for
   the reset (#578). No new code, no spend. The cost is the night: a limit at 01:00 stops the queue until the reset,
   often 4 to 5 hours.
2. **Retry the task on an API-key session.** The task ends `rate_limited`; the batch starts a continuation
   (`continue_from`) in a new session whose credential is the fallback key, delivered as `gateway_key` today is.
   - For: each part exists already. The swap is per task, and the egress gateway is unchanged.
   - Against: the turn in flight is lost, and the new session starts cold, rereading the repository and its own
     transcript. The batch also needs a second egress route (a per-task credential mode), which ADR 0039 avoided on
     purpose. Every limit costs one wasted turn.
3. **Fall back inside the egress gateway.** The gateway holds both credentials. When the subscription answers 429 with
   its limit (not a budget refusal), it sends the same request again on the fallback key and returns that answer. The
   session never sees the 429, and its turn goes on.
   - For: no lost work, no second credential in any session, and one place to enforce the cap.
   - Against: the gateway buffers request bodies so it can replay them, it needs a dollar price for the fallback's
     tokens, and the cap needs a reservation, because calls are made where the ledger is not.

## Unknown before building

Nothing in this repository shows these; the first step of the build records them from a real subscription limit (one
request, its status, headers and body, with the credential and text scrubbed), and decisions 2 and 3 are fixed to what
it shows:

- Which headers carry the subscription's reset on a 429 (`anthropic-ratelimit-unified-*` is the assumption), and
  whether the body tells a usage limit from a per-minute one.
- Which `anthropic-beta` values the CLI sends only with an OAuth token, and whether the API refuses them with a key.
- Whether a streamed request is refused before its first byte, so it can be replayed whole.

## Decision

Option 3, behind the same switch as worker runs: a batch falls back only when the profile names
`api_fallback_key_secret` and `api_fallback_daily_usd`, on a subscription credential (`credential_delivery:
token_exchange`). An API-key batch and a `gateway_key` batch are unchanged.

1. **Two credentials in the egress container.** `ModelGatewaySpec` gains an optional fallback: the key, its upstream
   host (from `api_fallback_base_url`, else `api.anthropic.com`) and the batch's dollar allowance. The runner carries
   them through the same private env file as the credential. It accepts the fallback host when it passes
   `ModelGateway.RefuseHost` and equals the profile's `api_fallback_base_url` host: still one named host per
   credential, never one taken from a request.
2. **When to switch.** The gateway sees the raw HTTP answer, not the CLI's result that #639 classifies, so the rule is
   about time: a 429 whose reset is more than 5 minutes away switches. Seconds-long rate limits do not; the CLI retries
   those as it does today, and switching on them would spend money for nothing. The reset comes from the response's
   rate-limit headers, else `Retry-After`. A 429 with neither switches only when its body names the usage limit. A
   budget refusal (402, or a body `GatewayRefusal.IsBudget` matches) is passed through as it is today. Until the
   reset, new requests go straight to the fallback; the first request after it goes on the subscription again. That
   is the same rule as `The_first_turn_after_the_reset_goes_on_the_subscription_again`.
3. **Replay.** A request body is buffered up to the gateway's 64 MiB limit so it can be sent twice. On the fallback,
   the gateway sends `x-api-key` instead of `Authorization: Bearer`, and drops any `anthropic-beta` value only an OAuth
   token is accepted with and the `ANTHROPIC_CUSTOM_HEADERS` the profile sets (`custom_headers_secret`). Streaming answers are
   replayed only when the 429 arrived before the first byte, which is where the API sends it.
4. **Dollars in the gateway.** The batch passes the fallback model's price from `prices` (input, output, cache read,
   cache write per million tokens). The gateway prices each fallback response from its `usage`, as `ApiFallback.Spent`
   does with `total_cost_usd`. A model with no price in `prices` gets no fallback: the batch refuses at its start, as a
   missing cap does today (`CheckApiFallback`).
5. **The cap is a reservation.** At batch start, the server reserves an allowance from `ApiFallback`: what is left of
   today's cap, divided by the batches that hold a reservation, plus one. The ledger records it under the batch id, so
   two batches cannot both spend the whole remainder. In the gateway:
   - A fallback request is checked against the allowance before it is sent.
   - Past the allowance, the 429 goes back to the session as it does today, and the task ends `rate_limited`.
   - A fallback answer of 402 (the gateway's own budget) closes the fallback for the rest of the batch.
6. **Settling.** The gateway writes one log line per fallback call: run id, tokens, dollars and status, never text.
   At teardown, the server reads them from the egress log the way #650 reads refusals. It adds the dollars to
   `ApiFallback.Spent`, calls `ApiFallback.Refused` on a 402, records the reset with `ApiFallback.Limited`, and
   releases the rest of the reservation. A log that cannot be read counts the whole reservation as spent: the cap errs
   low, never high. After a server restart, a batch that resumes (`BatchNetwork.EnsureAsync`) keeps its reservation
   from the ledger.
7. **What the result says.** The batch result's `credential_delivery` stays `token_exchange`. The result gains
   `fallback: {usd, calls, refused}`, and the task records gain the same per task. ADR 0039's amendment changes to "a
   batch switches to the API key only inside its egress gateway, within the reserved part of the fallback cap".

## Consequences

- A batch stops on a subscription limit only when today's fallback dollars run out, or when the gateway's budget
  refuses.
- Prompt caching does not carry over: a session that switches pays one cache write at API prices on its next request.
  That is part of the cost the cap is for.
- Buffering request bodies costs the egress container memory. A long-context request is a few MiB; 64 concurrent
  connections at 64 MiB each is the theoretical worst case, and the container's limit must allow for it. A real batch
  should be measured before this ships.
- The spend in `api-fallback.json` now has two sources, worker turns and batches, and one cap. A batch's spend reaches
  the ledger only when it ends, so the console and `doctor` show reservations as "held".
- `BatchMachineTests.An_unavailable_provider_stops_the_batch_without_fallback` stays true for a batch without a
  fallback, and gains a counterpart for one with it.

### Acceptance checks

Each is a test that must pass before this ADR is accepted:

- `ModelGatewayTests.A_subscription_limit_is_replayed_on_the_fallback_key_and_the_session_sees_200`: a fake upstream
  answers 429 with the limit body. The gateway resends the same body to the fallback host with `x-api-key` and no OAuth
  beta, and the client gets the fallback's answer.
- `ModelGatewayTests.A_budget_refusal_is_passed_through_and_never_replayed`: the client gets a 402, or a 429 naming a
  budget, unchanged.
- `ModelGatewayTests.Past_the_allowance_the_limit_reaches_the_session`: priced usage passes the allowance, and the next
  limit comes back as a 429.
- `ModelGatewayTests.After_the_reset_the_first_request_goes_on_the_subscription`.
- `ApiFallbackTests.Two_batches_reserve_parts_of_the_remainder_and_settle_what_they_spent`: the ledger after both
  teardowns equals the logged dollars, and nothing stays reserved.
- `ApiFallbackTests.An_unreadable_egress_log_counts_the_whole_reservation`.
- `DrivenRunTests.A_subscription_batch_with_a_fallback_finishes_its_task_through_a_limit`: the counterpart of the
  stop-without-fallback test.
- `RunnerTests.The_runner_accepts_only_the_profiles_fallback_host_for_the_model_endpoint`.
- One live run on the VPS: a batch started with the subscription exhausted completes on the fallback, and its spend
  appears in `api-fallback.json`. Recorded in the pull request that ships this.

## Reopen if

- The API stops sending the limit before the first streamed byte, so replay would have to cut a stream mid-answer.
- The egress container's memory, measured with buffered bodies, does not fit the batch's limit.
- Anthropic's terms forbid one process holding both a subscription token and an API key for the same work.
- Option 2's lost turn turns out cheap: if most limits land between turns, not inside one, the simpler per-task retry
  wins.
