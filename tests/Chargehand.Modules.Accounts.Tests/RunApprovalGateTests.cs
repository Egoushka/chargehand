using Chargehand.Modules.Accounts.Api;

namespace Chargehand.Modules.Accounts.Tests;

/// <summary>ADR 0092: the threshold and the held requests survive a restart.</summary>
public class RunApprovalGateTests
{
    [Fact]
    public void The_threshold_defaults_to_five_and_a_new_process_reads_the_one_set()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "limits", "run-approval.json");
        Assert.Equal(5m, new RunApprovalGate(path).Limit.LimitUsd);

        new RunApprovalGate(path).Limit.Set(12.5m);
        Assert.Equal(12.5m, new RunApprovalGate(path).Limit.LimitUsd);

        new RunApprovalGate(path).Limit.Set(null);
        Assert.Null(new RunApprovalGate(path).Limit.LimitUsd);
    }

    [Fact]
    public void A_pending_request_survives_a_restart_and_an_approval_is_used_once()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "limits", "run-approval.json");
        var now = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
        var gate = new RunApprovalGate(path);
        var (asked, first) = gate.Check("fp", "alice", "owner/repo", "Draft a note.", 8m, RunApprovalGate.BudgetBasis, now);
        Assert.Equal(RunApprovalGate.Verdict.Held, first);

        gate = new RunApprovalGate(path);
        Assert.Equal(asked.Id, Assert.Single(gate.Pending()).Id);
        Assert.Equal(RunApprovalGate.Verdict.Held, gate.Check("fp", "alice", "owner/repo", "Draft a note.", 8m, RunApprovalGate.BudgetBasis, now).Verdict);
        Assert.Single(gate.Pending());

        Assert.NotNull(gate.Decide(asked.Id, approve: true, now));
        Assert.Null(gate.Decide(asked.Id, approve: true, now));
        Assert.Equal(RunApprovalGate.Verdict.Start, gate.Check("fp", "alice", "owner/repo", "Draft a note.", 8m, RunApprovalGate.BudgetBasis, now).Verdict);
        Assert.Equal(RunApprovalGate.Verdict.Held, gate.Check("fp", "alice", "owner/repo", "Draft a note.", 8m, RunApprovalGate.BudgetBasis, now).Verdict);
    }
}
