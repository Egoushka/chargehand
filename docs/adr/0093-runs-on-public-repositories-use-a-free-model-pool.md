# 0093. Runs on public repositories use a free-model pool

- Status: proposed
- Date: 2026-11-04

## Context

The owner wants more free model credit and does not mind where the data goes as long as work stays separate (2026-11-04). Free tiers
(OpenRouter `:free`, Mistral, Cloudflare, Groq) may train on prompts or have unknown terms, so they are fit only for code that
is already public. The gateway the owner runs will issue one virtual key, `public-free`, limited to those providers and to no other
model. The pool is free models plus `mistral-free` on a paid key; the gateway holds a monthly cap on that key, so a run can never spend past it. This ADR is the client side: when does a run use that key, and how can it never be used for anything else.

The rule that matters is a negative one: a private repository, or one whose visibility we could not read, must never reach the pool,
whatever the caller asks for. So the choice of the pool rests on one fact, read from GitHub by the server, and every doubt means no.

Assumptions: the gateway speaks the Anthropic API to the `claude_code` runtime (the runtime already takes a base URL and a key per
credential, ADR 0053); a free pool has quota limits we cannot predict (starting values come from the gateway owner).

## Prior art

Not searched: the credential half is an account's own model credential (ADR 0053) and the setting is the one-field file of ADR 0092.

## Options

1. A `public` label or flag on the request. The caller's own claim; a mistaken or hostile caller sends it on a private repository.
2. A list of repositories in the profile. Safe, but the owner keeps a second list that drifts from GitHub (a repository made private
   later stays on it).
3. Read the repository's visibility from GitHub at the moment of the run, and let only a `public` answer in.

## Decision

Option 3.

**The pool.** The profile has an optional `public_free` block: `api_key_secret` (the secret-store item holding the gateway's
`public-free` virtual key, never the key), `base_url` (the gateway), `preset` (default `public-free`), `replaces` (the presets whose
runs on a public repository move to the pool; none listed: no automatic routing), `deny_path_segments` (default `["work"]`),
`intake_model` (the free model a pool run's intake uses; a pool run never uses the profile's own `intake_model`) and `github_token_secret`
(optional secret item for the visibility lookup; without it the lookup is unauthenticated, and GitHub's unauthenticated rate limit is
low: set it, because only a positive answer is cached and every non-public repository asks again on each run).
Model ids are not in the block: the `public-free` preset names placeholder models that the profile's `models` map resolves, as for
every preset. A pool model id, in `models` and in `intake_model`, is `<provider>/<model>` as the gateway names it, split at the first
slash only (`openrouter-free/google/gemma-4-26b-a4b-it:free` is provider `openrouter-free`, model `google/gemma-4-26b-a4b-it:free`),
and the pool runtime hands the CLI the whole id, provider included, because the gateway routes by that prefix. Any other runtime
still sends the model segment alone. A secret item is read through the profile's secret source; with the environment source the item `some-item` is the
variable `SOME_ITEM` (upper case, `-` becomes `_`), so the gateway key's variable name decides the item name. A missing secret
fails the run with a message that names the item, never the value, and the key is never logged or printed (the credential's
`ToString` shows only whether it is a subscription). The block needs the `claude_code` runtime and is refused at startup without it. Personal values live in the gitignored
`profiles/local.*`; `profiles/example.json` carries placeholders.

**The only way in.** A repository qualifies only when all of these hold; any other outcome, including an error, does not:

1. The request resolves to a checkout the server knows, and that checkout has a forge name (`owner/name`) from the server's own
   mapping (a managed clone or the GitHub intake map). The caller's text names a checkout; it never supplies the forge name.
   The forge name is then checked against the checkout itself: its git `origin` must name the same `owner/name` (any URL form,
   any letter case). A mapping that points at a checkout whose origin is another repository, is not on GitHub, or has no origin
   is a mistaken mapping, and the checkout does not qualify.
2. No directory of the checkout's path is named in `deny_path_segments`.
3. `GET /repos/{owner}/{name}` on GitHub answers 200 with `"visibility": "public"`, `"private": false` and a `full_name` equal to
   the name asked for (a repository that was renamed or transferred answers 301 or names its new owner, and is `Unknown`: the
   name we hold may now belong to something else). A 404 (what GitHub
   answers for a private repository the token cannot see), 403, 5xx, a timeout, an unreadable body, `private`, `internal` or any
   value we do not know is `Unknown` or its own non-public value, and does not qualify.

A positive answer is remembered for 10 minutes (a starting value); no other answer is remembered. A name that an owner later
frees and a different public repository takes cannot be told apart by name alone; the origin check narrows it to checkouts the
owner cloned from that name, and the rest is accepted.

**What the selector does** (`PublicFreePool.Decide`, a pure function):

- Routing is on, the repository qualifies, the preset is in `replaces`: the run's preset becomes the pool preset.
- A request that names the pool preset itself, on a repository that does not qualify, or while routing is off, is refused with
  `public_free_refused` and the reason. It is never downgraded to another preset and never run.
- Anything else runs as before, on its own preset and credential.

**Where the key attaches.** Only to a run whose preset is the pool preset, and the Orchestrator asks the same guard again when it
chooses the run's runtime, so a run started by the CLI or by any path that skipped the server's selection cannot carry the key onto a
repository that does not qualify. The pool is a worker runtime over the `public_free` credential, built on the first run.

**The gateway (read from config, not tested live).** The facts below come from the gateway session's reading of the homelab pull
request 215 config and the Bifrost documentation. The keys and the deploy do not exist yet, so none of it has been exercised.

- Endpoint: `base_url` ends in `/anthropic`; the client appends `/v1/messages`. The profile holds the address the machine running the
  server reaches the gateway at, so it is not written in this repository (`profiles/example.json` keeps a placeholder).
- Key: the virtual key travels as the header `x-bf-vk: <key>`. The pool runtime sets it through the CLI's `ANTHROPIC_CUSTOM_HEADERS`
  (the mechanism `claude_code.custom_headers_secret` already uses), from the same secret as `api_key_secret`; the CLI also sends the key
  in its own `x-api-key`. The variable is replaced, never inherited, and the runtime keeps the key and the header value out of every
  failure text it builds (it removes the exact values, as it does for a service grant's). A key with whitespace or a line break is refused
  without being named, so it cannot add a header.
  Checked: the installed CLI (2.1.303) contains the string `ANTHROPIC_CUSTOM_HEADERS`. UNKNOWN: that it sends exactly the header we set to this gateway.
- Models: `<provider>/<model>` with the provider as the gateway names it: `openrouter-free/google/gemma-4-26b-a4b-it:free` (tool-call support unchecked), `mistral-free/mistral-medium-latest`,
  `cloudflare/@cf/openai/gpt-oss-20b`, `groq/openai/gpt-oss-20b`. `openrouter-free` accepts names matching `regex:.*:free$` (RE2, whole name).
  UNKNOWN: that the CLI passes a model with slashes or `@` to the gateway unchanged.
  The gateway may add providers later (a Gemini provider returns as its own pull request); the profile's `models` map is where the ids are listed.

One risk only the first live call settles:

1. **Provider names register at config load.** If the custom providers (`openrouter-free`, `mistral-free`, `cloudflare`, `groq`) are not registered, the call goes to the
   gateway's default provider, which may be paid.

**Free means free.** The pool has no second credential. There is no paid fallback: the `public-free` run never uses the server's
Anthropic credential, an account's key or the API fallback of ADR 0088. When the gateway answers that the free quota is gone (429 or
a quota error), the run fails with the existing retryable `rate_limited` error (no new error code, so `result/v1` does not change) and says so; the caller may start
it again later or on another preset. No fallback is possible by construction: a run's worker runtime is fixed when it starts.
A pool run uses no memory: it neither recalls facts (which may come from other repositories) into its prompt nor retains its
claims, because its prompt goes to free providers. The pool is not used for driven batches (their runner has its own routing); a driven request never selects it and naming the pool
preset in one is refused.

**The console setting.** `PUT /v1/limits/public-free` takes `{"enabled": true}` or `{"enabled": false}`; `GET` reads it with
`configured` (a `public_free` block exists) and the preset. Both are owner-only. It is kept in `limits/public-free.json` beside the
run log, effective at once, and written whole through a temporary file like ADR 0092. The web console's switch for it is a follow-up; the endpoint is the setting.
**Default: off.** A fresh or missing file means off: free models are weaker than the owner's usual ones, and silently moving
every public run onto them is a change the owner should make on purpose, once the gateway key exists. Off turns the whole pool off,
including a request that names the pool preset.

## Acceptance checks

Machine-run; `scripts/check.sh` runs all of them and must exit 0 with `Passed!  - Failed:     0`.

| # | Check | Test |
|---|---|---|
| 1 | A private, internal, unknown or errored lookup never qualifies, for an automatic or a named pool preset | `PublicFreePoolTests.NonPublicNeverSelectsThePool` |
| 2 | A lookup that throws or times out is `Unknown` | `GitHubRepositoryVisibilityTests.ErrorsAndOddAnswersAreUnknown` |
| 3 | Only `visibility: public` with `private: false` is `Public` | `GitHubRepositoryVisibilityTests.OnlyPublicAndNotPrivateIsPublic` |
| 4 | No forge name, or a denied path segment, never qualifies even when GitHub says public | `PublicFreePoolTests.WithoutAForgeNameOrUnderWorkNothingQualifies` |
| 5 | Routing off refuses even a named pool preset; the default setting is off | `PublicFreeSettingTests.DefaultIsOffAndIsKept` |
| 6 | The Orchestrator does not attach the pool runtime to a repository that does not qualify | `PublicFreeRuntimeTests.TheKeyDoesNotReachAPrivateRepository` |
| 7 | An exhausted pool fails with `rate_limited` and never uses another runtime | `PublicFreeRuntimeTests.ExhaustedPoolFailsWithoutFallback` |
| 8 | The profile block names secrets, not keys, and `profiles/example.json` validates | `PublicFreeProfileTests.PublicFreeBlockIsPlaceholdersAndSecretNames` |
| 9 | A checkout whose git origin is not the mapped forge name never gets the pool; the same name in another case or URL form does | `PublicFreeRuntimeTests.AMappedNameThatTheCheckoutsOriginDoesNotMatchNeverGetsThePool`, `AnOriginThatNamesTheSameRepositoryInAnotherCaseOrFormStillQualifies` |
| 10 | A redirect, a renamed repository or a body without the asked-for `full_name` is `Unknown` | `GitHubRepositoryVisibilityTests.ARedirectOrARenamedRepositoryIsUnknown` |
| 11 | Admission: a paid preset on a non-public repository stays put; the pool preset named where it may not run is refused; switch off or `work` keeps the preset | `PublicFreeServerTests` |
| 12 | Orchestrator: switch off, unmapped checkout and `work` path never get the pool runtime | `PublicFreeRuntimeTests.APublicAnswerIsNotEnoughWhenTheSwitchIsOffTheCheckoutIsUnmappedOrUnderWork` |
| 13 | The endpoint: default off, owner only (403 for an account key, 401 without a key), 400 for a body that is not an explicit boolean, 409 without a `public_free` block | `PublicFreeServerTests` |
| 14 | A pool run is not held for paid-spend approval, the same run on a private repository is | `PublicFreeServerTests.APoolRunIsNotHeldForPaidSpendApprovalButTheSameRunOnAPrivateRepositoryIs` |
| 15 | A pool run does not recall from or retain into memory | `PublicFreeRuntimeTests.APoolRunNeitherRecallsFromMemoryNorRetainsIntoIt` |
| 16 | The credential and a missing-secret error never show the key | `PublicFreeRuntimeTests.TheCredentialAndAMissingSecretNeverShowTheKey` |

Added by the gateway change (these run in `scripts/check.sh`):

| # | Check | Test |
|---|---|---|
| 17 | A pool id with a second slash splits at the first only and resolves through the `models` map | `PublicFreeGatewayTests.A_pool_model_id_is_split_at_the_first_slash_only_and_resolves_through_the_models_map` |
| 18 | The pool sends the whole provider-prefixed id to the CLI; another runtime sends the model alone | `PublicFreeGatewayTests.The_pool_sends_the_whole_provider_prefixed_id_to_the_cli_and_other_runtimes_send_the_model_alone` |
| 19 | The key rides in `x-bf-vk` and replaces an inherited header list | `PublicFreeGatewayTests.The_pool_carries_its_key_in_the_x_bf_vk_header_and_replaces_an_inherited_list` |
| 20 | Neither the key nor the header value reaches a failure text; a key that is not one token is refused without being named | `PublicFreeGatewayTests` (three failure-text and key-shape tests) |

**Live acceptance (NOT DONE; needs the gateway deployed and the keys in place).** The first live call after homelab pull request 215
deploys must show, from the gateway's own logs or usage and the run's result:

- (a) a run on a public repository reaches a free provider (the one named by the model's prefix) and not the gateway's default provider;
- (b) an `/anthropic` request type that the gateway refuses comes back as a clear failed run that says the gateway refused it, and not as a silent
  retry on another provider or a paid one. If it falls back, the pool is not free and stays off until the gateway's list is fixed.

Until both hold, the switch (`PUT /v1/limits/public-free`) stays off.

## Consequences

- Turning the switch on moves the listed presets, on public repositories only, to free models; quality may drop and a run can be
  refused when the quota is spent. The owner sees `rate_limited` and chooses.
- A repository that is public on GitHub but sits outside the server's known checkouts, or has no forge name, uses the owner's usual
  preset. The pool is opt-in per known repository, not a guess from a URL.
- Each new run makes one GitHub call (none within 10 minutes of a positive answer for that repository), unauthenticated unless the
  profile's GitHub token secret is set. Hitting GitHub's rate limit means `Unknown`, so the run is not moved: the failure is safe, but
  it silently turns the pool off, which is why the token secret is recommended.
- A repository made private moves off the pool within 10 minutes. A run already underway keeps the key until it ends and visibility
  is not checked again mid-run (accepted: the quota is free and the data was public when the run started).
- Free providers may train on what they see. The pool is for public code, and `deny_path_segments` plus the GitHub answer are the
  separation; an owner who clones a private repository under a managed clone folder still gets GitHub's answer for its name.

## What the gateway owner must provide

The virtual key `public-free` with only the pool providers (free models, and `mistral-free` on a paid key under a monthly cap), the model ids for the `models` map, and the per-provider limits. The list is in
the pull request.

## Reopen if

Free quotas run out within the day (then add a queue that waits instead of failing), a provider changes its terms so a public
repository's content is no longer fit for it, or a second consumer wants the same visibility check (then lift it out of this
module).
