using System.Security.Cryptography;
using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>Goal 0.8 (ADR 0036): canonical form, ES256 signing and offline verification of a result.</summary>
public class ResultSignatureTests
{
    private static string Canon(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ResultSignature.Canonicalize(doc.RootElement);
    }

    [Fact]
    public void Canonical_form_matches_the_RFC_8785_example()
    {
        const string input = "{\"numbers\":[333333333.33333329,1E30,4.50,2e-3,0.000000000000000000000000001],\"string\":\"\\u20ac$\\u000F\\u000aA'\\u0042\\u0022\\u005c\\\\\\\"/\",\"literals\":[null,true,false]}";
        const string expected = "{\"literals\":[null,true,false],\"numbers\":[333333333.3333333,1e+30,4.5,0.002,1e-27],\"string\":\"€$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}";
        Assert.Equal(expected, Canon(input));
    }

    [Theory]
    [InlineData("0.5", "0.5")]
    [InlineData("100", "100")]
    [InlineData("0.002506", "0.002506")]
    [InlineData("1e-7", "1e-7")]
    [InlineData("-0.0", "0")]
    [InlineData("1000000", "1000000")]
    [InlineData("123456789012345680000", "123456789012345680000")]
    [InlineData("1e21", "1e+21")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("12.50", "12.5")]
    public void Numbers_take_the_ECMAScript_form(string input, string expected) => Assert.Equal(expected, Canon($"[{input}]").Trim('[', ']'));

    [Fact]
    public void Keys_sort_by_code_unit_and_the_signature_member_is_left_out()
    {
        Assert.Equal("{\"a\":1,\"b\":{\"x\":2,\"y\":3},\"é\":4}", Canon("{\"é\":4,\"b\":{\"y\":3,\"x\":2},\"signature\":{\"alg\":\"ES256\"},\"a\":1}"));
    }

    private static ResultContract Result(string summary = "A summary.", decimal? usd = 0.002506m) =>
        new("result/v1", "run-1", "run", new string('a', 32), new PromptChain([], new AsSent("2.0.16", "build", "p/m", "2026-09-30")), ResultStatus.Completed, summary,
            [new Claim("A claim.", ["e1"], 0.9, ClaimSupport.Supported)], [new Evidence("e1", EvidenceKind.File, "README.md:1")], [], ["Open."], 0.9,
            new Usage(1000, 100, 0, 4000, usd));

    private static readonly JsonSerializerOptions Indented = new(ContractJson.Options) { WriteIndented = true };

    private static string Json(ResultContract r, bool indent = false) => JsonSerializer.Serialize(r, indent ? Indented : ContractJson.Options);

    [Fact]
    public void A_signed_result_verifies_and_carries_a_key_id()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = ResultSignature.Sign(Result(), key);
        Assert.Equal("ES256", signed.Signature!.Alg);
        Assert.Matches("^[0-9a-f]{16}$", signed.Signature.KeyId);
        Assert.Equal(ResultSignature.KeyId(key), signed.Signature.KeyId);
        Assert.Equal(new SignatureCheck(true, "valid"), ResultSignature.Verify(Json(signed), key));
        using var doc = JsonDocument.Parse(Json(signed));
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Result, doc.RootElement));
    }

    [Fact]
    public void Verification_works_with_the_public_half_alone()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var publicOnly = ECDsa.Create();
        publicOnly.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);
        Assert.True(ResultSignature.Verify(Json(ResultSignature.Sign(Result(), key)), publicOnly).Valid);
    }

    [Fact]
    public void Reformatting_and_reordering_keys_do_not_break_a_signature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = Json(ResultSignature.Sign(Result(), key), indent: true);
        using var doc = JsonDocument.Parse(text);
        var reordered = "{" + string.Join(",", doc.RootElement.EnumerateObject().Reverse().Select(p => $"\"{p.Name}\":{p.Value.GetRawText()}")) + "}";
        Assert.True(ResultSignature.Verify(text, key).Valid);
        Assert.True(ResultSignature.Verify(reordered, key).Valid);
    }

    [Fact]
    public void A_null_cost_signs_and_verifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(ResultSignature.Verify(Json(ResultSignature.Sign(Result(usd: null), key)), key).Valid);
    }

    [Theory]
    [InlineData("A summary.", "A summary!")]
    [InlineData("\"confidence\":0.9,", "\"confidence\":1,")]
    [InlineData("\"usd\":0.002506", "\"usd\":null")]
    [InlineData("\"support\":\"supported\"", "\"support\":\"partial\"")]
    [InlineData("\"open_questions\":[\"Open.\"]", "\"open_questions\":[]")]
    public void Any_change_to_the_result_fails_verification(string from, string to)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = Json(ResultSignature.Sign(Result(), key));
        Assert.Contains(from, text, StringComparison.Ordinal);
        var check = ResultSignature.Verify(text.Replace(from, to, StringComparison.Ordinal), key);
        Assert.False(check.Valid);
        Assert.Contains("does not match", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_key_an_unsigned_result_and_a_bad_algorithm_are_refused_with_reasons()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = Json(ResultSignature.Sign(Result(), key));
        Assert.Contains("different key", ResultSignature.Verify(text, other).Reason, StringComparison.Ordinal);
        Assert.Contains("unsigned", ResultSignature.Verify(Json(Result()), key).Reason, StringComparison.Ordinal);
        Assert.Contains("unsupported algorithm", ResultSignature.Verify(text.Replace("ES256", "HS256", StringComparison.Ordinal), key).Reason, StringComparison.Ordinal);
        Assert.False(ResultSignature.Verify("not json", key).Valid);
    }

    [Fact]
    public void Support_and_signature_are_optional_in_the_schema()
    {
        using var doc = JsonDocument.Parse(Json(Result() with { Claims = [new Claim("c", ["e1"], 0.5)] }));
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Result, doc.RootElement));
        Assert.DoesNotContain("support", Json(Result() with { Claims = [new Claim("c", ["e1"], 0.5)] }), StringComparison.Ordinal);
    }
}
