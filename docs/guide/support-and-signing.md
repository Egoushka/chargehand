# Support checking and signed results

On main, not yet released. [ADR 0036](../adr/0036-support-checking-and-signed-results.md) has the decisions and their reasons.

## Claims checked for support

Until now chargehand checked that a citation exists at the pinned commit (a path and line range, a diff hunk, an input id). It
did not compare the cited text with the claim, so a claim could cite a real line that says something else. Now, after a node's
evidence resolves, one call on the intake model reads every claim beside the text it cites and returns a verdict per claim:

| verdict | what the result does |
|---|---|
| `supported` | the claim stays, with `"support": "supported"` |
| `partial` | the claim stays at half its confidence, with `"support": "partial"` and the reason in `open_questions` ("Partly supported by its citation: ...") |
| `unsupported` | the claim leaves `claims` for `open_questions` ("Unsupported by its citation: ..."), and evidence no other claim cites is dropped |
| not checked | `"support": "unchecked"`: the claim cites only a URL, a commit or a session message, or the check could not run |

The run's status and summary do not change. If the judge cannot answer (a provider error, or two replies that are not valid
verdicts) the claims stay, every one is `unchecked`, and one open question says why: a failed check never fails a run.
`support_check: false` in the profile turns the check off and removes the extra call. The judge's call is not in the result's
`usage`; it is one small call per node on the intake model. A `partial` claim is not kept for memory.

### How accurate it is

`scripts/support-eval.sh` runs the judge over `evals/support-examples.jsonl`: 30 claims, ten each labelled `supported`, `partial`
and `unsupported` against ten snippets of this repository's own code, three claims per snippet, labelled by the author of the set.
Run on 2026-09-30 through the signed-in Claude Code:

| judge | agreement | supported | partial | unsupported | supported claims dropped | unsupported claims kept as supported |
|---|---|---|---|---|---|---|
| Claude Haiku | 25 of 30 | 10 of 10 | 5 of 10 | 10 of 10 | 0 | 0 |
| Claude Sonnet | 25 of 30 | 10 of 10 | 5 of 10 | 10 of 10 | 0 | 0 |

Every miss was a `partial` claim judged `unsupported`: a claim whose first half the text says and whose second half it does not
(for example, "the commit is made with hooks off and is signed with the chargehand key", against code that switches signing
off). Both models put such claims in the open questions instead of keeping them, which is the safe direction, but it means the
check does not tell "half right" from "wrong" reliably. Thirty claims from one repository, labelled by one person, is a small
sample: it shows the judge is not obviously broken and where it errs, not a rate to rely on.

### What it does not do

- It is a model's opinion about the cited text, not a proof that the claim is true. A claim can be `supported` by text that is itself wrong.
- It reads only what the citation points at, cut to 200 lines per citation and 12 000 characters per claim; a claim that depends on
  code outside the cited range can be judged unsupported.
- It does not check claims that cite only a URL, a commit or a session message.

## Signed results

A run signs its result when the profile names a key. Chargehand creates and stores no key; make one yourself:

```sh
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out signing-key.pem
openssl pkey -in signing-key.pem -pubout -out signing-key.pub.pem
```

Set `signing.key_file` in the profile, or `CHARGEHAND_SIGNING_KEY_FILE` (which wins). A key file that is missing, is not PEM or is
not on curve P-256 fails the run before intake with `invalid_request`, so a caller never gets an unsigned result it expected signed.

The result then has a `signature` member: `{ "alg": "ES256", "key_id": "<16 hex>", "value": "<base64url>" }`. `value` is ECDSA P-256
with SHA-256 (IEEE P1363, 64 bytes) over the RFC 8785 canonical form of the result without its `signature` member; `key_id` is
the first 16 hex characters of the SHA-256 of the public key's SubjectPublicKeyInfo DER. Check a result anywhere, with no network
and no profile:

```sh
chargehand verify result.json --public-key signing-key.pub.pem
```

Exit 0 means valid, 1 means the signature is missing or does not match, 2 means a usage error or an unreadable file. Reformatting
the JSON or reordering its keys does not break a signature; changing a value does. `Chargehand.Contracts` has the same check as
`ResultSignature.Verify` for code that does not want the CLI.

On 2026-09-30 a real run (`cheap` preset on a two-line repository, Claude Code with Haiku, a throwaway `openssl` key) returned three
claims, all `supported`, and a signature; `chargehand verify` exited 0 on it and 1 after one word of the result was changed.

### What a signature means

It says that the holder of the key produced exactly this JSON. It does not say the claims are true, that the support check was
right, or that the key belongs to who you think: trusting a key is the verifier's decision. There is no timestamp, key rotation,
revocation or certificate chain.
