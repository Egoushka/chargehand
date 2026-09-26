# 0013. Middleware policy

- Status: accepted
- Date: 2026-09-26

## Context

Two classes of middleware sit between the orchestrator and a model. Optimisation middleware (compression,
response caches, rewriters) is off by default and must be deterministic, fail open and show a net saving after
caching. Privacy middleware (the PII-masking proxy) must map the same value to the same placeholder on every
call and fail closed.

## Evidence

- Masking proxy, determinism (spike): a new person introduced in a later user turn, and in an instruction
  update, left every call's cache covering the whole previous prompt (all but the last 3 tokens) — placeholders
  stayed stable.
- Masking proxy, failure (source, v0.9.3): a detector error or timeout raises a detection error and the route
  answers **503 "Detection service unavailable"** without forwarding the request — fail closed, as required.
- Compression proxy (pre-spike measurement in the gateway): ~10% fewer input tokens, one model's cache hit rate
  84% → 50%, requests failed while it was down (fail closed).

## Decision

The masking proxy is the privacy middleware and stays mandatory in the owner's profile; the orchestrator never
calls a provider, the masking proxy or the gateway directly, only OpenCode (the gateway key lives in the OpenCode
server's config only). A 503 from the masking proxy is retried once (ADR 0011), then the node fails. Compression
stays out of v0–v1; if ever opted into, the orchestrator must fall back to the plain masking route by itself on
any compressor error.

## Reopen if

A compressor proves deterministic, fail-open and net-saving after caching.
