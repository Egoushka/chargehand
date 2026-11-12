using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Tests;

/// <summary>Blind tests (issue 789): an opted-in task gets a sibling task that writes acceptance tests from the issue alone.</summary>
public class BlindTaskPlanTests
{
    private static ResolvedTask Item(string id, bool blind = false, string[]? dependsOn = null) =>
        new(id, null, $"issue text of {id}", DependsOn: dependsOn, BlindTests: blind);

    private static PlanProposal Proposal(params ProposedTask[] tasks) => new(tasks, [], null, "run-plan-1");

    [Fact]
    public void An_opted_in_task_gets_a_sibling_with_no_dependencies_and_the_issue_text_as_its_goal()
    {
        var (tasks, plan) = PlanApplier.Apply([Item("a", blind: true, dependsOn: ["x"]), Item("x")], Proposal(), "request");

        var blind = Assert.Single(tasks, t => t.Id == "a.blind");
        Assert.Null(blind.DependsOn);
        Assert.Equal("a", blind.BlindFor);
        Assert.Equal("issue text of a", blind.Goal);
        Assert.False(blind.BlindTests);
        Assert.Contains(plan.Tasks, t => t.Id == "a.blind");
        Assert.Equal(3, tasks.Count);
    }

    [Fact]
    public void A_task_that_did_not_opt_in_adds_nothing()
    {
        var (tasks, plan) = PlanApplier.Apply([Item("a"), Item("b")], Proposal(), "request");

        Assert.Equal(["a", "b"], tasks.Select(t => t.Id));
        Assert.DoesNotContain(plan.Tasks, t => t.Id.EndsWith(".blind", StringComparison.Ordinal));
    }

    [Fact]
    public void A_split_task_gets_one_sibling_from_the_whole_issue_not_from_a_part()
    {
        var (tasks, _) = PlanApplier.Apply([Item("t", blind: true)], Proposal(new ProposedTask("t", "L", [new ProposedPart("first", "S"), new ProposedPart("second", "S")], null)), "profile");

        Assert.Equal(["t-1", "t.blind", "t"], tasks.Select(t => t.Id));
        Assert.Equal("issue text of t", tasks[1].Goal);
        Assert.Equal("t", tasks[1].BlindFor);
    }

    [Fact]
    public void The_sibling_counts_in_the_batch_cap_so_a_split_that_no_longer_fits_stays_whole()
    {
        var items = Enumerable.Range(0, PlanApplier.MaxTasks - 2).Select(i => Item($"t{i}")).Append(Item("a", blind: true)).ToList();
        var (tasks, plan) = PlanApplier.Apply(items, Proposal(new ProposedTask("a", "L", [new ProposedPart("p1", "S"), new ProposedPart("p2", "S")], null)), "request");

        Assert.Equal(PlanApplier.MaxTasks, tasks.Count);
        Assert.Contains("a.blind", tasks.Select(t => t.Id));
        Assert.DoesNotContain("a-1", tasks.Select(t => t.Id));
        Assert.Contains(plan.Dropped, d => d.Contains("a: not split", StringComparison.Ordinal));
    }

    [Fact]
    public void Adding_the_sibling_twice_changes_nothing_and_the_sibling_id_is_read_back_by_BlindIds()
    {
        var (once, _) = PlanApplier.Apply([Item("a", blind: true)], Proposal(), "request");
        Assert.Equal(once.Count, PlanApplier.AddBlind(once).Count);
        Assert.Equal("a", BlindIds.For("a.blind"));
        Assert.Null(BlindIds.For("a"));
    }
}
