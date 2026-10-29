using System.Security.Cryptography;

namespace Chargehand.Tests;

/// <summary>
/// Stability plan Task 2 (docs/plans/2026-09-30-stability-1.0.md): a checked-in signature test vector for
/// <see cref="Chargehand.Contracts.ResultSignature"/> (ADR 0036). The private key that produced this vector was
/// generated once, at the keyboard, to sign the fixed result JSON below; it was never written to a file and is not
/// reproduced here — only its public half and the resulting signature are checked in, both of which are safe to
/// publish. If a future change to the canonical form or the signing routine ever makes this fixed signature fail to
/// verify against this fixed JSON, that is exactly the breaking change this test exists to catch; <see
/// cref="ResultSignatureTests"/> covers the RFC 8785 canonicalization rules in detail and generates a fresh key per
/// test, which this vector deliberately does not.
/// </summary>
public class ResultSignatureVectorTests
{
    private const string PublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEz/Om4SB60jVvq4UhQi4zf/QZZjYohk2VdXryhFrdQ9LeEc2t6rpT1FLGtv4v6xwIEjLpYu+frGif0E3AnxtSYw==";

    private const string KeyId = "830223d6129fbdae";

    private const string SignedResultJson =
        """{"contract_version":"result/v1","task_id":"task-1","node_id":"worker","trace_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","prompt_chain":{"blocks":[],"as_sent":{"opencode_version":"2.0.16","agent":"build","model":"p/m","date":"2026-09-30"}},"status":"completed","summary":"A fixed summary for the signature test vector.","claims":[{"text":"A fixed claim.","evidence":["e1"],"confidence":0.9,"support":"supported"}],"evidence":[{"id":"e1","kind":"file","locator":"README.md:1"}],"artifacts":[],"open_questions":["A fixed open question."],"confidence":0.9,"usage":{"input":1000,"output":100,"cache_read":0,"cache_write":4000,"usd":0.002506},"signature":{"alg":"ES256","key_id":"830223d6129fbdae","value":"HN03p2OdmqKExIrmYtYsNc-5qLXlbYwlU_zoUSoHHc2whxdWSvnxoozv0gsyuhsR8InK26zt-8spA6nbqSEk4g"}}""";

    private static ECDsa PublicKey()
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeySpki), out _);
        return key;
    }

    [Fact]
    public void The_fixed_public_key_derives_the_fixed_key_id()
    {
        using var key = PublicKey();
        Assert.Equal(KeyId, Chargehand.Contracts.ResultSignature.KeyId(key));
    }

    [Fact]
    public void The_fixed_signature_still_verifies_against_the_fixed_result_and_public_key()
    {
        using var key = PublicKey();
        var check = Chargehand.Contracts.ResultSignature.Verify(SignedResultJson, key);
        Assert.True(check.Valid, check.Reason);
    }

    [Fact]
    public void The_fixed_vector_still_validates_against_result_v1()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(SignedResultJson);
        Assert.Empty(Chargehand.Contracts.ContractSchemas.Validate(Chargehand.Contracts.ContractSchemas.Result, doc.RootElement));
    }
}
