# 0062. A gateway's headers reach Claude Code workers as a secret

- Status: accepted
- Date: 2026-10-14

## Context

`claude_code.base_url` points the workers at an Anthropic-compatible gateway. A gateway that identifies its callers by a virtual
key in a header (Bifrost's `/anthropic` passthrough wants `x-bf-vk: <key>`, else 401) cannot be used: Claude Code sends only
`Authorization` (the subscription token, forwarded to Anthropic unchanged) and its own headers. The CLI reads extra headers from
`ANTHROPIC_CUSTOM_HEADERS` (`Name: value`, newline-separated); checked on 2026-10-14 against the gateway with `claude -p`.

Driven batches never read `claude_code.base_url`: in subscription mode the egress container swaps a per-task token for the real
one toward `api.anthropic.com` only, over public addresses (ADR 0039, decision 9). Measured from the code, not by a run.

## Options

1. A profile field holding the header lines inline. The virtual key would sit in the profile file, which the repo's rule (secrets by item name) forbids.
2. `claude_code.custom_headers_secret`: a secret item holding the lines. Same path as the other credentials; the value stays out of the profile.
3. Teach the egress endpoint to add the header and to reach an internal upstream, for driven sessions too. The endpoint refuses non-public upstreams on purpose; relaxing that is its own decision.

## Decision

Option 2, for plain workers. The runtime sets `ANTHROPIC_CUSTOM_HEADERS` from the secret (trimmed, one `Name: value` per line) and
removes an inherited one, as it does for the base URL and auth token. A profile that names the secret without `base_url` is refused:
the headers are for the gateway, not Anthropic. Each header value joins the credentials a driven transcript is scanned for.
Driven sessions are unchanged until option 3 is decided.

## Consequences

- Workers can use a gateway that needs a virtual key; nothing changes for a profile without the field.
- A driven batch on the subscription still goes direct to Anthropic and bypasses any gateway the plain workers use.

## Reopen if

A driven batch must pass through the same gateway (option 3).
