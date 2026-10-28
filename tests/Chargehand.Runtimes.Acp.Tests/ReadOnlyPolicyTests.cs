using AgentClientProtocol;
using Chargehand.Runtimes.Acp.Api;

namespace Chargehand.Runtimes.Acp.Tests;

public class ReadOnlyPolicyTests
{
    private static RequestPermissionRequest Ask(string? kind, params (string Id, string Kind)[] options) =>
        new("s1", new ToolCallUpdate("t1", Title: "Do it", Kind: kind), [.. options.Select(o => new PermissionOption(o.Id, o.Id, o.Kind))]);

    private static readonly (string, string)[] AllowAndReject = [("go", AcpPermissionKind.AllowOnce), ("no", AcpPermissionKind.RejectOnce)];

    [Theory]
    [InlineData(AcpToolKind.Edit)]
    [InlineData(AcpToolKind.Delete)]
    [InlineData(AcpToolKind.Move)]
    [InlineData(AcpToolKind.Execute)]
    [InlineData(AcpToolKind.Fetch)]
    [InlineData(AcpToolKind.SwitchMode)]
    [InlineData(AcpToolKind.Other)]
    [InlineData("something_new")]
    [InlineData(null)]
    public void A_request_that_is_not_a_read_is_rejected_with_the_agents_own_reject_option(string? kind)
    {
        var verdict = AcpReadOnlyPolicy.Decide(Ask(kind, AllowAndReject));

        Assert.True(verdict.Refused);
        Assert.Equal(RequestPermissionOutcome.Selected("no"), verdict.Outcome);
        Assert.Contains(kind ?? "unknown", verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AcpToolKind.Read)]
    [InlineData(AcpToolKind.Search)]
    [InlineData(AcpToolKind.Think)]
    public void A_read_is_allowed_once(string kind)
    {
        var verdict = AcpReadOnlyPolicy.Decide(Ask(kind, AllowAndReject));

        Assert.False(verdict.Refused);
        Assert.Equal(RequestPermissionOutcome.Selected("go"), verdict.Outcome);
    }

    [Fact]
    public void A_read_is_never_allowed_for_good()
    {
        var verdict = AcpReadOnlyPolicy.Decide(Ask(AcpToolKind.Read, ("always", AcpPermissionKind.AllowAlways), ("no", AcpPermissionKind.RejectOnce)));

        Assert.True(verdict.Refused);
        Assert.Equal(RequestPermissionOutcome.Selected("no"), verdict.Outcome);
    }

    [Fact]
    public void A_refusal_never_saves_a_rejection()
    {
        var verdict = AcpReadOnlyPolicy.Decide(Ask(AcpToolKind.Edit, ("always", AcpPermissionKind.AllowAlways), ("never", AcpPermissionKind.RejectAlways)));

        Assert.True(verdict.Refused);
        Assert.Equal(RequestPermissionOutcome.Cancelled, verdict.Outcome);
    }

    [Fact]
    public void A_request_with_no_options_is_cancelled()
    {
        var verdict = AcpReadOnlyPolicy.Decide(Ask(AcpToolKind.Execute));

        Assert.True(verdict.Refused);
        Assert.Equal(RequestPermissionOutcome.Cancelled, verdict.Outcome);
    }
}
