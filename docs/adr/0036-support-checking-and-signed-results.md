# 0036. Support checking and signed results

- Status: proposed
- Date: 2026-09-30

## Context

A `result/v1` claim cites evidence, and the resolver confirms the citation exists at the pinned commit (ADR 0009). It does not
compare the cited lines with the claim, so a claim can cite a real line that says something else, and the result cannot tell a
reader which claims survive that check. Results also carry no signature: a result copied out of a run cannot be told from an edited
one. Goal 0.8 asks for both (design spec, "Where it stands").

## Options

1. String or embedding similarity between claim and cited text. Cheap and deterministic; wrong in both directions for paraphrase and
   for negation ("does not retry" against a line that retries).
2. A model judges each claim against its cited text. Handles paraphrase; is itself fallible and costs a call.
3. Run the code or a test that encodes the claim. Strong; impossible for most claims.
For signing: (a) Ed25519, not in the .NET base library; (b) ES256, in it and in JWS; (c) a hosted signing service, which puts a
third party and a network in the loop.

## Decision

Option 2, one stateless call on the intake model per node, prompt in the trusted build, failing open. Unsupported claims move to
`open_questions`; partial ones stay marked and at half confidence. The result says what was checked (`support` per claim), never
that a claim is true. Signing is (b): ES256 over the RFC 8785 canonical result minus its `signature`, with a key the user supplies;
verification lives in `Chargehand.Contracts` and the CLI and needs no network. Chargehand creates no key.

## Consequences

- The support check is a model's opinion. Its accuracy is measured on a labelled set and published with the model's name; a reader
  is told "checked for support", not "verified".
- Every run costs one more small call per node unless `support_check` is false.
- A signature proves origin and integrity of the JSON for whoever trusts the key; it does not prove the answer right.
- A claim that cites only a URL, a commit or a session message is `unchecked`, so "every claim" means every claim that cites text
  chargehand can fetch.
- Adding fields to `result/v1` is additive, and `Chargehand.Contracts` needs a new alpha version when released.

## Reopen if

The measured accuracy is too low to publish; a second signature algorithm is asked for; a verifier outside .NET needs the
canonicalization specified beyond RFC 8785 plus the member exclusion.
