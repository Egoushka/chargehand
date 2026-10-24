using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;

namespace Chargehand.Tests;

/// <summary>The benchmark's arm on a driven request (ADR 0079): which values the server accepts, and that the field is additive on the wire.</summary>
public class DrivenArmTests
{
    private static RequestDriven Driven(string? arm, bool? bench) => new([new DrivenTask("t", Goal: "g")], Bench: bench, Arm: arm);

    [Theory]
    [InlineData(null, null)]
    [InlineData("chargehand", null)]
    [InlineData("chargehand", true)]
    [InlineData("bare", true)]
    public void An_arm_the_server_knows_is_accepted(string? arm, bool? bench) => DrivenRun.CheckArm(Driven(arm, bench));

    [Theory]
    [InlineData("bare", null, "needs driven.bench")]
    [InlineData("bare", false, "needs driven.bench")]
    [InlineData("plain", true, "is not an arm")]
    public void A_bare_arm_outside_a_benchmark_and_an_unknown_arm_are_refused(string arm, bool? bench, string why)
    {
        var e = Assert.Throws<ChargehandException>(() => DrivenRun.CheckArm(Driven(arm, bench)));
        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains(why, e.Message);
    }

    [Fact]
    public void The_arm_is_on_the_wire_only_when_it_is_set()
    {
        Assert.DoesNotContain("arm", JsonSerializer.Serialize(Driven(null, true), ContractJson.Options));
        var json = JsonSerializer.Serialize(Driven("bare", true), ContractJson.Options);
        Assert.Contains("\"arm\":\"bare\"", json);
        Assert.DoesNotContain("is_bare", json);
        Assert.True(JsonSerializer.Deserialize<RequestDriven>(json, ContractJson.Options)!.IsBare);
    }
}
