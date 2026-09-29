# Support checking and signed results Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each claim in a result says whether the text it cites supports it, and a result can be signed and verified offline.

**Architecture:** After a node's evidence resolves, one stateless generate call (the intake model) judges every claim against
the text it cites; unsupported claims move to `open_questions`, the rest carry `support`. At the end of a run the result is signed
(ES256 over its RFC 8785 canonical form, minus the `signature` member) with a user-supplied key. Signing and verifying live in
`Chargehand.Contracts`; the CLI adds `verify`.

**Tech Stack:** .NET 10, `System.Security.Cryptography.ECDsa`, `System.Text.Json`, xUnit 2.9.

**Spec:** `docs/specs/2026-09-30-support-and-signing-design.md`; decision record `docs/adr/0036-support-checking-and-signed-results.md`.

## Global Constraints

- Public repository: no IP addresses, hostnames, absolute home paths, employer or project names, keys (including test keys as files),
  tracker URLs or prompts from real runs. Tests generate keys in memory.
- Done for every task: `scripts/check.sh` exits 0 and its last test line reads `Passed!  - Failed:     0`. Run dotnet outside the agent
  sandbox (`dangerouslyDisableSandbox`).
- Conventional Commits; never `--no-verify`; never force-push. PR title ends with `(CHARGEHAND-<n>)`.
- Schemas change additively only (`SchemaCompatTests`).
- Public text says "citations checked" until this goal ships in a release, then "checked for support"; never "verified true".
- Chargehand never creates, stores or uploads a signing key.
- The support judge's prompt lives in code, not in `prompts/` (a pull request must not change how its own answers are judged).

## Review Focus

1. The judge answers with prose or broken JSON: one retry, then fail open with `support: unchecked` and one open question. Task 3.
2. The judge marks every claim unsupported: the result still completes, with claims moved to `open_questions` and a summary that
   is unchanged. Task 4 (`All_claims_unsupported_leaves_a_completed_result_with_no_claims`).
3. A claim cites a file range that is huge: the cited text sent to the judge is cut (200 lines per citation, 12 000 characters per
   claim) and the cut is stated in the prompt. Task 2.
4. A signed result is reformatted (whitespace, key order) in transit: it still verifies, because verification canonicalizes. Task 1.
5. A result with `usd: null` or a number like `0.002506` signs and verifies. Task 1.
6. The signing key file is missing or is not a P-256 PKCS#8 PEM: the run fails before intake with the path and the `openssl`
   command in `error.action`. Task 5.

---

### Task 1: Contracts and `ResultSignature`

**Files:**
- Modify: `src/Chargehand.Contracts/ResultContract.cs` (`Claim`: optional `Support`; `ResultContract`: optional `Signature`)
- Modify: `schemas/result/v1/result.schema.json` (`claims[].support` enum `supported|partial|unchecked`; `signature` object)
- Create: `src/Chargehand.Contracts/ResultSignature.cs`
- Test: `tests/Chargehand.Tests/ResultSignatureTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record SignatureBlock(string Alg, string KeyId, string Value);            // ResultContract.Signature
public enum ClaimSupport { Supported, Partial, Unchecked }                               // Claim.Support (nullable)
public static class ResultSignature
{
    public static string Canonicalize(JsonElement result);                               // RFC 8785, "signature" member excluded
    public static string KeyId(ECDsa publicKey);                                         // 16 hex of SHA-256(SPKI DER)
    public static ResultContract Sign(ResultContract result, ECDsa privateKey);          // returns result with Signature set
    public static SignatureCheck Verify(string resultJson, ECDsa publicKey);             // works on the text as received
}
public sealed record SignatureCheck(bool Valid, string Reason);
```

- [ ] **Step 1: Failing tests.** `Canonicalize` sorts keys, drops whitespace, formats `0.5`, `1e-7` as `1e-7`, `100` as `100`,
  `0.002506` as `0.002506`, escapes `"` and `\` and control characters, and omits `signature`; the RFC 8785 example
  `{"numbers":[333333333.33333329,1E30,4.50,2e-3,0.000000000000000000000000001],"string":"€$\u000F\u000aA'B"\\\\"/","literals":[null,true,false]}` canonicalizes to
  `{"literals":[null,true,false],"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27],"string":"€$\u000f\nA'B\"\\\\\"/"}`;
  `Sign` then `Verify` on the serialized result is valid; changing the summary, adding a claim, changing `usd` from a number to
  null, or verifying with another key is invalid with a reason; a reformatted copy (indented, keys reordered) still verifies; an
  unsigned result reports "unsigned"; `KeyId` is 16 lowercase hex characters and stable for one key.
- [ ] **Step 2: Run** `dotnet test --filter ResultSignatureTests`; expect compile errors.
- [ ] **Step 3: Implement.** Canonicalization writes with a `Utf8JsonWriter`-free string builder over `JsonElement`: objects
  sorted by UTF-16 code unit order of the key, numbers via `double.ToString("R", InvariantCulture)` converted to ECMAScript form
  (no `E+`/leading zeros in the exponent: `1E-07` becomes `1e-7`, `1E+30` becomes `1e+30`), integers that fit in `long` as digits.
  `Sign`: serialize the result with `ContractJson.Options` without `Signature`, canonicalize, `ECDsa.SignData(bytes,
  HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)`, base64url without padding.
  `Verify` parses the text, extracts and removes `signature`, canonicalizes the rest, checks `alg == "ES256"`, `key_id`
  equals `KeyId(publicKey)`, and the signature.
- [ ] **Step 4: Run** the tests and `scripts/check.sh` (`SchemaCompatTests` must pass).
- [ ] **Step 5: Commit** `feat(contracts): claim support, a result signature and ES256 signing and verification`.

### Task 2: Cited text

**Files:**
- Create: `src/Chargehand/Verification/CitedText.cs`
- Test: `tests/Chargehand.Tests/CitedTextTests.cs`

**Interfaces:**
- Consumes: `ResultContract`, `Evidence`, `EvidenceScope` (`src/Chargehand/Runtime/`), git via the scope's repository path and commit.
- Produces:

```csharp
public sealed record CitedClaim(int Index, Claim Claim, string Text, bool Truncated);
public static class CitedText
{
    // One entry per claim with at least one file, diff or input citation whose text could be read; claims with none are absent.
    public static Task<IReadOnlyList<CitedClaim>> ForAsync(ResultContract contract, EvidenceScope scope, CancellationToken ct);
}
```

  Text format per citation: `[<evidence id>] <kind> <locator>` then the lines (file: `git show <commit>:<path>` lines
  start..end, at most 200; diff: the hunk lines from `scope.Diff` for the path and range; input: the input's text from
  `scope.SeenText`? no: from `scope.InputText` ordered by id, cut to 2000 characters). A claim's total text is cut to 12 000
  characters and `Truncated` is set.

- [ ] **Step 1: Failing tests** on a real git repository (`Runs.GitRepo`): a file citation returns the cited lines with their
  numbers; a range past the file's end returns what exists; an input citation returns the input's text; a `url`-only claim is
  absent; a 5000-line citation is cut to 200 lines and `Truncated` is true; two citations of one claim are both included.
- [ ] **Step 2: Run** and see failures. **Step 3: Implement.** **Step 4: Run** the tests. **Step 5: Commit**
  `feat(verify): fetch the text each claim cites`.

### Task 3: The judge

**Files:**
- Create: `src/Chargehand/Verification/SupportJudge.cs`
- Test: `tests/Chargehand.Tests/SupportJudgeTests.cs`

**Interfaces:**
- Consumes: `IWorkerRuntime.GenerateAsync(ModelRef?, string, CancellationToken)`, `CitedClaim` (Task 2).
- Produces:

```csharp
public enum SupportVerdict { Supported, Partial, Unsupported }
public sealed record ClaimVerdict(int Index, SupportVerdict Verdict, string Reason);
public static class SupportJudge
{
    // Throws InvalidOperationException after one retry when no valid verdict list comes back.
    public static Task<IReadOnlyList<ClaimVerdict>> JudgeAsync(IWorkerRuntime runtime, ModelRef? model, IReadOnlyList<CitedClaim> claims, CancellationToken ct);
    internal static string Prompt(IReadOnlyList<CitedClaim> claims);
    internal static bool Parse(string text, IReadOnlyList<CitedClaim> claims, out IReadOnlyList<ClaimVerdict> verdicts, out string? error);
}
```

  Prompt: numbered claims, each with its cited text; the rule "a claim is supported when the cited text says it, or something that
  plainly implies it, in any wording; partial when the text supports only part of it or a weaker statement; unsupported when the
  text does not say it, contradicts it, or is about something else. Judge only the cited text, not your own knowledge." Reply:
  one JSON object `{"verdicts":[{"claim":1,"verdict":"supported|partial|unsupported","reason":"short"}]}` covering every claim.
  A truncated citation is marked in the prompt.

- [ ] **Step 1: Failing tests** with a fake runtime returning canned text: a valid object parses; verdict text in another case
  (`Supported`) parses; claim numbers as strings parse; a missing claim, a number outside the range, an unknown verdict or no JSON
  gives `false` with an error; a first bad reply then a good one succeeds and the second prompt names the error; two bad replies
  throw; the prompt contains every claim's text and cited text and the truncation note.
- [ ] **Steps 2-5:** run, implement, run, commit `feat(verify): a model judges whether cited text supports a claim`.

### Task 4: Apply verdicts and wire the check

**Files:**
- Create: `src/Chargehand/Verification/SupportCheck.cs`
- Modify: `src/Chargehand/Config/Profile.cs` (`SupportCheck: bool = true`), `profiles/profile.schema.json`
- Modify: `src/Chargehand/Orchestrator.cs` (after a node result: run the check on a `Completed` contract)
- Modify: `tests/Chargehand.Tests/ScriptedRuntime.cs` (`Runs.Profile` sets `SupportCheck: false`)
- Test: `tests/Chargehand.Tests/SupportCheckTests.cs`

**Interfaces:**
- Consumes: Tasks 1-3.
- Produces:

```csharp
public static class SupportCheck
{
    public static ResultContract Apply(ResultContract contract, IReadOnlyList<CitedClaim> checkedClaims, IReadOnlyList<ClaimVerdict> verdicts);
    public static ResultContract Unavailable(ResultContract contract, string reason);   // fail open: all claims Unchecked
    public static Task<ResultContract> RunAsync(IWorkerRuntime runtime, ModelRef? model, ResultContract contract, EvidenceScope scope, CancellationToken ct);
}
```

  `Apply`: supported → `Support = Supported`; partial → `Support = Partial`, `Confidence /= 2`, reason appended to open
  questions as "Partly supported by its citation: <claim> (<reason>)"; unsupported → removed, its evidence dropped when no other claim
  cites it, open question "Unsupported by its citation: <claim> (<reason>)"; claims not in `checkedClaims` → `Unchecked`.
  `RunAsync` catches every exception except cancellation and returns `Unavailable`.

- [ ] **Step 1: Failing tests:** each verdict's effect on a hand-built contract; unchecked claims marked; shared evidence kept when
  another claim still cites it; `All_claims_unsupported_leaves_a_completed_result_with_no_claims` (status stays `Completed`, summary
  unchanged, every claim in `open_questions`); a judge that throws gives `Unchecked` plus exactly one "Support check unavailable"
  question; a whole run on `ScriptedRuntime` with `SupportCheck: true` whose generate calls return a task spec and then verdicts
  marks the claim; the same run with `SupportCheck: false` makes no extra generate call and leaves `Support` null.
- [ ] **Steps 2-5:** run, implement (the orchestrator calls `RunAsync` once per completed node with the node's `EvidenceScope`; the
  scope is exposed by `WorkerNode` on `NodeResult`), run `scripts/check.sh`, commit
  `feat: check every claim's citation for support and say so in the result`.

### Task 5: Signing in runs, and `chargehand verify`

**Files:**
- Modify: `src/Chargehand/Config/Profile.cs` (`Signing: SigningSettings?` with `KeyFile`), `profiles/profile.schema.json`
- Create: `src/Chargehand/Signing/ResultSigner.cs` (loads the key, signs; `ChargehandException` `InvalidRequest` on a bad key)
- Modify: `src/Chargehand/Orchestrator.cs` (load the key before intake; sign the final result; log the signed result)
- Modify: `src/Chargehand.Cli/Program.cs` (`verify <file> --public-key <pem>`)
- Test: `tests/Chargehand.Tests/SigningRunTests.cs`

**Interfaces:**
- Consumes: `ResultSignature` (Task 1).
- Produces: `ResultSigner.Load(SigningSettings?, Func<string,string?> env)` returning `ECDsa?`; CLI exit codes 0 valid, 1 invalid or
  unsigned, 2 usage.

- [ ] **Step 1: Failing tests:** a scripted run with a key (generated in memory and written to a temp PEM) returns a signed result
  that `ResultSignature.Verify` accepts with the public key, and a copy with one changed word fails; no key configured means
  `Signature` is null; a missing key file fails the run before intake (`invalid_request`, action names the path and contains
  `openssl`); a non-P-256 key file is refused the same way; the CLI `verify` command exits 0 for the signed file, 1 for the edited
  copy and for an unsigned file, 2 for a missing argument (run through `Program` in-process if the tests already do, else a
  process test like `OpenCodeServeScriptTests`).
- [ ] **Steps 2-5:** run, implement, run, commit `feat: sign results with a user-supplied key and verify them offline`.

### Task 6: Measurement, guide, changelog

**Files:**
- Create: `evals/support-examples.jsonl` (at least 30 synthetic claim, cited-text and expected verdict lines from this repository's
  own code: 10 supported, 10 partial, 10 unsupported), `scripts/support-eval.sh`
- Create: `docs/guide/support-and-signing.md`
- Modify: `docs/guide/capabilities.md`, `docs/guide/index.md`, `docs/guide/reference.md` (profile fields, `verify`), `README.md`,
  `ROADMAP.md`, `CHANGELOG.md`, ADR 0036 status line

- [ ] **Step 1:** write the examples and the script (it runs `SupportJudge` over them through the CLI's runtime and prints
  agreement per class and every miss).
- [ ] **Step 2:** run it live with the signed-in Claude Code; record model, size, agreement and misses in the guide, or record that
  it could not run and why.
- [ ] **Step 3:** the guide page: what is checked, verdicts, what is not, signing with the `openssl` commands, verifying, what a
  signature does and does not mean; the ROADMAP row; the changelog; `scripts/check.sh`.
- [ ] **Step 4: Commit** `docs: support checking and signed results, with the measured accuracy`.

## Release (not a task of this plan)

Version bump, tag, GitHub Release, `Chargehand.Contracts` 1.4.0-alpha to nuget.org (schema and `ResultSignature` change), MCP
Registry: outward-facing, waits for the maintainer. Signing-key creation for chargehand's own releases waits for the maintainer.

## Self-review

- Spec coverage: decisions 1-7 in Tasks 2-4; 8, 10 in Task 1 and 5; 9 in Task 5; 11 in Task 6 docs; 12 in Task 6; 13 in Task 1;
  14 is unchanged behaviour, pinned by `RetainableClaims` tests extended in Task 4 (a `Partial` claim is not retained).
- Names used across tasks: `ClaimSupport`, `SignatureBlock`, `ResultSignature`, `SignatureCheck`, `CitedClaim`, `CitedText.ForAsync`,
  `SupportVerdict`, `ClaimVerdict`, `SupportJudge.JudgeAsync`, `SupportCheck.Apply/Unavailable/RunAsync`, `ResultSigner.Load`.
