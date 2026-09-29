using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>Goal 0.7 (ADR 0035): the additive contract changes a writing preset relies on.</summary>
public class WritingContractsTests
{
    private const string WritesBlock = "    writes: true\n    verify: { timeout_seconds: 30, max_fix_rounds: 1 }\n";

    [Fact]
    public void A_writing_node_kind_loads_with_its_verify_settings()
    {
        var kind = PresetRoot.Load("writer-test", WritesBlock).NodeKinds["worker"];
        Assert.True(kind.Writes);
        Assert.Equal(new VerifySettings(30, 1), kind.Verify);
    }

    [Fact]
    public void A_node_kind_without_them_does_not_write()
    {
        var kind = PresetRoot.Load("plain-test", "").NodeKinds["worker"];
        Assert.False(kind.Writes);
        Assert.Null(kind.Verify);
        Assert.Equal(new VerifySettings(), new VerifySettings(600, 2));
    }

    [Fact]
    public void A_request_may_carry_a_verify_command()
    {
        const string json = """{"contract_version":"request/v1","text":"t","context":{"interactive":false,"preset":"code","verify":["dotnet","test"]}}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Request, doc.RootElement));
        var request = doc.RootElement.Deserialize<RunRequest>(ContractJson.Options)!;
        Assert.Equal(["dotnet", "test"], request.Context.Verify);
    }

    [Fact]
    public void An_empty_verify_command_is_refused()
    {
        const string json = """{"contract_version":"request/v1","text":"t","context":{"interactive":false,"preset":"code","verify":[]}}""";
        using var doc = JsonDocument.Parse(json);
        Assert.NotEmpty(ContractSchemas.Validate(ContractSchemas.Request, doc.RootElement));
    }

    [Theory]
    [InlineData(ErrorCode.SandboxUnavailable, "sandbox_unavailable")]
    [InlineData(ErrorCode.VerificationFailed, "verification_failed")]
    public void The_new_error_codes_serialise_and_validate(ErrorCode code, string wire)
    {
        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(code, ContractJson.Options));
        var error = JsonSerializer.SerializeToElement(new ResultError(code, "m", false, "a"), ContractJson.Options);
        Assert.Equal(wire, error.GetProperty("code").GetString());
        var schema = JsonDocument.Parse(ContractSchemas.Text(ContractSchemas.Result)).RootElement;
        var allowed = schema.GetProperty("properties").GetProperty("error").GetProperty("properties").GetProperty("code").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString());
        Assert.Contains(wire, allowed);
    }
}
