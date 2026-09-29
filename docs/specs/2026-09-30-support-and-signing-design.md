# Claims checked for support, and signed results (goal 0.8)

- Status: draft for the maintainer's review. Every decision below is the proposer's; the maintainer accepts or changes them in review.
- Date: 2026-09-30

## Goal

A result says, per claim, whether the text it cites supports it, and can be signed so that anyone with the public key can
check offline that this exact result came from the holder of the private key.

Done when: (1) a claim whose cited lines do not say what it claims is moved out of `claims` into `open_questions`, shown by a
scripted-runtime test and measured on a labelled set; (2) every remaining claim carries `support` (`supported`, `partial` or
`unchecked`); (3) a result signed with a user-supplied key verifies with `chargehand verify` on a machine with no network,
and any change to the JSON makes it fail; (4) the measured accuracy of the support check on the labelled set is in the guide, with
its size and its misses.

## Where it stands

Evidence: `path:line` at commit `185f7e3` (main, 0.6.1 plus goal 0.7 groundwork).

1. **Citations are checked to exist, not to say anything.** `GitEvidenceResolver` confirms a path and line range at the commit, a
   commit, a diff hunk, an input id (`GitEvidenceResolver.cs:12-31`). `ResultAssembler.MoveUnresolved` moves a claim whose evidence
   did not resolve to `open_questions` (`ResultAssembler.cs:89-111`). Nothing compares the cited lines with the claim text.
   `docs/guide/capabilities.md` says so (Support checking).
2. **A judge pattern exists.** `FactJudge` makes one stateless generate call on the intake model with its prompt in the trusted
   build, parses a JSON verdict, retries once, and fails the gate if it still cannot parse (`Evals/FactJudge.cs`).
3. **Results are unsigned.** `ResultContract` has no signature field; `result.schema.json` has `additionalProperties: false`, so
   a signature needs an additive optional property.
4. **The contracts package is the natural home for offline verification.** `Chargehand.Contracts` embeds the schemas and depends
   only on `System.Text.Json` and a schema library; a verifier should not need the whole tool.
5. **.NET has ECDSA, not Ed25519, in the base library** (`System.Security.Cryptography.ECDsa`); ES256 (P-256, SHA-256) is what
   JWS defines, and `openssl` can produce and read such keys.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Who judges support? | A model, the intake model, one stateless generate call per node with all its claims (as `FactJudge`). The prompt lives in the trusted build. Not a string match: a claim paraphrases its citation. |
| 2 | What is stated about it? | That it is a model's check, not a proof, and its measured accuracy on a labelled set (decision 9). Docs and result text say "checked for support", never "verified true". |
| 3 | Which claims are checked? | Claims whose evidence includes at least one `file`, `diff` or `input` citation: the cited text is fetched (file lines at the pinned commit, diff hunk from the session diff, input text). Claims resting only on `url`, `session_message` or `commit` are `unchecked`. |
| 4 | Verdicts | `supported`, `partial` (the text supports part of the claim or a weaker one), `unsupported`. |
| 5 | What happens to each? | `supported`: kept, `support: supported`. `partial`: kept, `support: partial`, confidence halved, the reason added to `open_questions`. `unsupported`: moved out of `claims` (its now-unused evidence dropped) to `open_questions` as "Unsupported by its citation: <claim> (<reason>)", as `MoveUnresolved` does for unresolved evidence. |
| 6 | When the judge fails | Fail open, as memory does (ADR 0008): claims stay, `support: unchecked`, one open question "Support check unavailable: <reason>". A failed judge never fails or empties a run. |
| 7 | On by default? | Yes: profile `support_check` (default true). It costs one small call per node. `false` restores today's behaviour. |
| 8 | Signature | ES256: ECDSA P-256 with SHA-256, IEEE P1363 encoding, over the canonical form of the result without its `signature` member (RFC 8785 JSON Canonicalization: keys sorted, no whitespace, numbers in ECMAScript form). `signature: { alg: "ES256", key_id, value }`, `key_id` = first 16 hex characters of the SHA-256 of the public key's SPKI DER, `value` base64url. |
| 9 | Keys | The user supplies a private key (PKCS#8 PEM) through profile `signing.key_file` or `CHARGEHAND_SIGNING_KEY_FILE`. Chargehand never creates, stores or uploads a key; the guide shows the `openssl` command. Without a key, results are unsigned as today. |
| 10 | Verification | `Chargehand.Contracts` gains `ResultSignature` (`Sign`, `Verify`, `Canonicalize`); the CLI gains `chargehand verify <result.json> --public-key <pem>` (exit 0 valid, 1 invalid or unsigned, 2 usage). Offline: no network, no run log. |
| 11 | What a signature means | The holder of the key produced exactly this JSON. It says nothing about whether the claims are true or the support check was right. Trust in the key is the verifier's decision. |
| 12 | Measurement | A labelled set of at least 30 claim and citation pairs from this repository's own code (supported, partial, unsupported), checked in as synthetic examples, and a script that runs the judge over it and reports agreement and the misses. Run live once; the numbers go in the guide with the model. |
| 13 | Schema | Additive only: `claims[].support` (optional enum), `signature` (optional object). `preset/v1` and `request/v1` unchanged. |
| 14 | Retention and memory | Unchanged: only `supported` and `unchecked` claims with resolved citations are retained; a `partial` claim is not. |

## Components

| Unit | Responsibility |
|---|---|
| `Contracts/ResultSignature` | Canonicalize, sign, verify (decision 8, 10). |
| `Verification/CitedText` | The text each cited evidence points at (decision 3). |
| `Verification/SupportJudge` | The prompt, the call, the parse, one retry (decisions 1, 4). |
| `Verification/SupportCheck` | Applies verdicts to a contract (decisions 5, 6). |
| `Orchestrator` | Runs the check after a node's evidence resolves when `support_check`; signs the final result when a key is configured. |
| CLI `verify` | Decision 10. |

## Errors

No new error codes. A signing key that cannot be read fails the run before intake (`invalid_request`, action: the path and the
`openssl` command), so a run never returns a result the caller believed would be signed and is not.

## Testing

- Unit: canonicalization against RFC 8785 examples (key order, number forms, escapes), sign then verify, tamper of any member
  fails, wrong key fails, unsigned fails, `key_id` derivation.
- Support: verdict parsing (bad JSON, out-of-range numbers, string numbers), each verdict's effect on a contract, fail-open, cited
  text for each evidence kind, claims with only `url` evidence unchecked.
- Orchestrator: scripted runtime whose intake generate call returns verdicts; a whole run with an unsupported claim; a run with the
  check off; a signed run whose result verifies and whose modified copy does not.
- Measurement: the labelled set and `scripts/support-eval.sh`, run live once.

## Out of scope for 0.8

Timestamping, transparency logs, certificate chains, key rotation, revocation, multiple signatures, signing artifacts or the
run log separately, and a support check that runs code or fetches URLs.

## Open items

- Whether the intake model is good enough as a judge. The measurement decides; if it is not, the profile gets a `support_model`.
- JCS number formatting of `usd` decimals: the implementation writes them through `double`; a result whose `usd` does not
  survive that round trip exactly is a known edge, tested with the values chargehand produces.
