using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>Turning a request's <c>driven</c> block into resolved tasks and limits (ADR 0039).</summary>
public class DrivenBatchTests
{
    private sealed class FakeSource(Func<string, string> answer) : ITaskSource
    {
        public List<string> Asked { get; } = [];

        public Task<string> ResolveAsync(string @ref, CancellationToken ct)
        {
            Asked.Add(@ref);
            return Task.FromResult(answer(@ref));
        }
    }

    private static RunRequest Request(RequestDriven driven) =>
        new("request/v1", "batch", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/r", "abc1234")), Driven: driven);

    private static readonly DrivenSettings On = new(Enabled: true, MaxParallel: 2, MaxParallelTotal: 4);
    private static readonly DrivenPreset Preset = new(MaxTokens: 1_500_000);

    [Fact]
    public async Task Goals_pass_through_and_refs_are_resolved_in_order()
    {
        var source = new FakeSource(r => $"goal for {r}");
        var request = Request(new RequestDriven([new DrivenTask("a", null, "Add a retry"), new DrivenTask("b", "ITEM-12", null), new DrivenTask("c", "ITEM-13", "Own text wins")]));
        var (ready, failed) = await DrivenBatch.PrepareAsync(request, On, source, default);
        Assert.Empty(failed);
        Assert.Equal([new ResolvedTask("a", null, "Add a retry"), new ResolvedTask("b", "ITEM-12", "goal for ITEM-12"), new ResolvedTask("c", "ITEM-13", "Own text wins")], ready);
        Assert.Equal(["ITEM-12"], source.Asked);                    // a task with both keeps its goal and does not ask the source
    }

    [Fact]
    public async Task A_ref_the_source_cannot_resolve_fails_that_task_only()
    {
        var source = new FakeSource(r => r == "BAD-1" ? throw new TaskSourceException("no item BAD-1") : $"goal for {r}");
        var request = Request(new RequestDriven([new DrivenTask("a", "OK-1", null), new DrivenTask("b", "BAD-1", null), new DrivenTask("c", null, "g")]));
        var (ready, failed) = await DrivenBatch.PrepareAsync(request, On, source, default);
        Assert.Equal(["a", "c"], ready.Select(t => t.Id));
        var failure = Assert.Single(failed);
        Assert.Equal("b", failure.Id);
        Assert.Equal(TaskState.Failed, failure.State);
        Assert.Equal(ErrorCode.InvalidRequest, failure.Error);
        Assert.Contains("no item BAD-1", failure.Detail);
    }

    [Fact]
    public async Task A_ref_with_no_task_source_configured_refuses_the_request_with_the_action()
    {
        var request = Request(new RequestDriven([new DrivenTask("a", "ITEM-1", null)]));
        var e = await Assert.ThrowsAsync<ChargehandException>(() => DrivenBatch.PrepareAsync(request, On, null, default));
        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains("driven.task_source", e.Action);
        // goals alone need no source
        var (ready, _) = await DrivenBatch.PrepareAsync(Request(new RequestDriven([new DrivenTask("a", null, "g")])), On, null, default);
        Assert.Single(ready);
    }

    [Fact]
    public async Task Duplicate_ids_and_a_disabled_profile_refuse_the_request()
    {
        var duplicate = Request(new RequestDriven([new DrivenTask("a", null, "g"), new DrivenTask("a", null, "h")]));
        Assert.Equal(ErrorCode.InvalidRequest, (await Assert.ThrowsAsync<ChargehandException>(() => DrivenBatch.PrepareAsync(duplicate, On, null, default))).Code);
        var off = await Assert.ThrowsAsync<ChargehandException>(() => DrivenBatch.PrepareAsync(Request(new RequestDriven([new DrivenTask("a", null, "g")])), new DrivenSettings(), null, default));
        Assert.Contains("driven.enabled", off.Action);
        Assert.Equal(ErrorCode.InvalidRequest, off.Code);
    }

    [Fact]
    public void Parallelism_is_the_smaller_of_the_request_and_the_profile_and_never_above_the_ceiling()
    {
        static long Parallel(int? requested, int profile) =>
            DrivenBatch.Limits(Request(new RequestDriven([new DrivenTask("a", null, "g")], MaxParallel: requested, MaxTokensTotal: 1)), On with { MaxParallel = profile }, Preset, 2m, priced: false, null).MaxParallel;
        Assert.Equal(2, Parallel(null, 2));
        Assert.Equal(1, Parallel(1, 3));
        Assert.Equal(3, Parallel(4, 3));
        Assert.Equal(4, Parallel(4, 8));
    }

    [Fact]
    public void A_subscription_batch_needs_a_token_cap_and_a_priced_one_a_dollar_cap()
    {
        var noCap = Request(new RequestDriven([new DrivenTask("a", null, "g")]));
        var tokens = Assert.Throws<ChargehandException>(() => DrivenBatch.Limits(noCap, On, Preset, 2m, priced: false, null));
        Assert.Contains("max_tokens_total", tokens.Message);
        var usd = Assert.Throws<ChargehandException>(() => DrivenBatch.Limits(noCap, On, Preset, 2m, priced: true, null));
        Assert.Contains("max_usd_total", usd.Message);

        var withTokens = DrivenBatch.Limits(Request(new RequestDriven([new DrivenTask("a", null, "g")], MaxTokensTotal: 4_000_000)), On, Preset, 2m, priced: false, null);
        Assert.Equal(4_000_000, withTokens.MaxTokensTotal);
        Assert.Null(withTokens.MaxUsdTotal);
        Assert.Equal(new TaskLimits(1_500_000, null), withTokens.PerTask);          // subscription: no dollar cap per task either

        var withUsd = DrivenBatch.Limits(Request(new RequestDriven([new DrivenTask("a", null, "g")], MaxUsdTotal: 6m, MaxTokensTotal: 9_000_000)), On, Preset, 2m, priced: true, null);
        Assert.Equal(6m, withUsd.MaxUsdTotal);
        Assert.Equal(9_000_000, withUsd.MaxTokensTotal);                             // the token cap still applies when given
        Assert.Equal(new TaskLimits(1_500_000, 2m), withUsd.PerTask);
    }
}
