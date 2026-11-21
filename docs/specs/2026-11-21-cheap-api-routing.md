# Route grunt work to a cheap pay-per-token model

- Status: proposed 2026-11-21. Verdict pending the paid replay (R1 to R5 below).

## Problem

The subscription caps every week. Mechanical and bulk builder tasks could run on DeepSeek V4.1 Flash
(native Anthropic endpoint `https://api.deepseek.com/anthropic`; starting values, off-peak: $0.15 input,
$0.003 cache read, $0.60 output per 1M tokens; source: owner's price notes of 2026-11-21). Quality is lower
(Terminal-Bench 4.1: Flash 38.5, Kimi K3.1 Pro 59.7, Sonnet 5.5 63.9), so this adds to Claude and replaces nothing.

## Route: two options, one chosen

The brief assumed a LiteLLM gateway. The homelab runs Bifrost (`stacks/bifrost/config.json`); LiteLLM is gone, only
stale env-var names remain. So:

| Route | Verdict |
|---|---|
| (a) `claude` CLI against the vendor's Anthropic endpoint, as an account with `model.base_url` (ADR 0053, 0062) | **Chosen.** Exists today, profile only. No translation layer, so `cache_control` is untouched and the vendor caches by prefix. The key is a secret item, the cap is the account's `max_usd_per_day` plus the prepaid balance. |
| (b) Bifrost virtual key with `max_limit` per key | Not now. Bifrost has no direct DeepSeek provider (only OpenRouter models); a new `env.NAME` must exist in `.env.enc` before merge, so "only the key missing" cannot hold. Its translation path is reported to drop `cache_control`: a risk with no gain over (a). Reopen if one key must serve several callers. |

Not routed: driven batches never read `base_url` (ADR 0062), so this covers plain workers and an account's read-only runs only.

## Design

An account in the owner's gitignored profile (placeholders only here):

```json
{ "name": "flash", "max_usd_per_day": 3, "max_concurrent_runs": 2,
  "repositories": ["/path/to/personal/repo-a"],
  "model": { "api_key_secret": "deepseek-api-key", "base_url": "https://api.deepseek.com/anthropic", "third_party": true } }
```

New: `model.third_party` (default false). With it the profile is refused unless the account has `max_usd_per_day` and a
`repositories` list with no `work` path segment. D-48 keeps work text off these providers; an allowlist fails closed where a
deny-list would not. Touch points: `AccountModel`, `AccountSettings.ThirdPartyProblems`, `profiles/profile.schema.json`.

The flag is the account itself: no account, no route. Only the key is missing.

## Acceptance checks (machine-run)

| # | Check | How it runs |
|---|---|---|
| G1 | A `third_party` account without `max_usd_per_day`, without `repositories`, or with one under a `work` directory is a profile problem; a complete one and a non-third-party account are not | `AccountModelTests` |
| G2 | `scripts/check.sh` (or the devbox form in CLAUDE.md) passes | CI |
| R0 | Day one, 3 tasks: the response usage shows non-zero `cache_read_input_tokens` on turn 2 onward | replay script, reads the run's usage |
| R1 | 30 builder tasks (synthetic or `personal/` repos, never `work/`) replayed on DeepSeek Flash and on Sonnet 5.5 with the same presets; acceptance = the task's own check passes (`scripts/check.sh` or the item's verify command) | `eval` cell, one account per model |
| R2 | Flash acceptance rate / Sonnet acceptance rate | computed from R1 |
| R3 | Flash cost per accepted task = billed dollars / accepted tasks | from run usage |
| R4 | Flash cache reads / input tokens | from run usage |

**Stop if:** R2 below 0.50, R3 above $3, or R4 below 0.80. Route off. Otherwise route on, with the account cap.
(The earlier portfolio plan used 60% and $1.50; the brief's thresholds govern, and the plan's tighter ones are noted for the retro.)

## Replay cost estimate (to state before the paid run)

Starting value, not measured: 30 tasks at about 400k input tokens (mostly cache reads) and 20k output each on Flash is
about 30 x (400k x 0.003 + 20k x 0.60) / 1M, plus uncached first turns, so a few dollars at most; the brief expects under $5.
Sonnet's side runs on the subscription, so it costs no dollars.

## Value hypothesis and cheapest test

Hypothesis: a share of builder turns can move off the weekly cap at under $3 per accepted task without losing acceptance.
Cheapest test: R0 on 3 tasks (cents) before the 30-task replay.

## Results

Pending: needs the DeepSeek account and key (owner's step).

## Reopen if

Flash's acceptance on the replay passes but the vendor's terms change on orchestrators, or a pool of several callers needs one key (route b).
