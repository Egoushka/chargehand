using Chargehand.Modules.Economics.Api;

namespace Chargehand.Modules.Economics.Tests;

/// <summary>The data-class gate of the model router (ADR 0095): only public and owner-private data may reach a model that trains on prompts; everything else, and anything nobody classified, reaches only the models
/// a pool lists as not training. A tier with none moves the call up, and with none anywhere the call is blocked, not waited on.</summary>
public class DataClassRoutingTests
{
    private static readonly PoolSettings Free = new("free", PoolKinds.Free, ["free/small"]);
    private static readonly PoolSettings Subscription = new("subscription", PoolKinds.Weekly, ["paid/small", "paid/big"], RefillWeekday: "sunday", NoTrainingModels: ["*"]);
    private static readonly PoolSettings Credit = new("credit", PoolKinds.Monthly, ["paid/small", "paid/big"], TotalUsd: 200, NoTrainingModels: ["*"]);
    private static readonly PoolSettings Other = new("other", PoolKinds.Fixed, ["other/small", "other/big"], TotalUsd: 50, Start: new DateOnly(2026, 11, 1), SpreadDays: 30);

    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static PoolsSettings Settings(params PoolSettings[] order) => new(Enabled: true, DefaultTier: "cheap",
        Tiers: [new("cheap", ["free/small", "paid/small", "other/small"]), new("strong", ["paid/big", "other/big"])],
        Order: order);

    private static RouteDecision Choose(int tier, DataClass dataClass, PoolsSettings settings, params string[] refused) =>
        new ModelRouter(settings).Choose(tier, dataClass, [], Now, refused.ToHashSet());

    [Fact]
    public void An_unset_class_is_the_restricted_one()
    {
        Assert.Equal(DataClass.Restricted, default);
        Assert.False(default(DataClass).MayTrain());
    }

    [Theory]
    [InlineData(DataClass.Public, true)]
    [InlineData(DataClass.OwnerPrivate, true)]
    [InlineData(DataClass.Restricted, false)]
    public void Only_public_and_owner_private_data_may_reach_a_training_model(DataClass dataClass, bool mayTrain) => Assert.Equal(mayTrain, dataClass.MayTrain());

    [Theory]
    [InlineData(DataClass.Public)]
    [InlineData(DataClass.OwnerPrivate)]
    public void Public_and_owner_private_data_drain_the_free_pool_first(DataClass dataClass)
    {
        var choice = Assert.IsType<RouteChoice>(Choose(0, dataClass, Settings(Free, Subscription, Credit, Other)));

        Assert.Equal(("free", "free/small"), (choice.Pool, choice.Model));
    }

    [Fact]
    public void Restricted_data_skips_the_pools_that_may_train_and_takes_the_first_that_does_not()
    {
        var choice = Assert.IsType<RouteChoice>(Choose(0, DataClass.Restricted, Settings(Free, Subscription, Credit, Other)));

        Assert.Equal(("subscription", "paid/small"), (choice.Pool, choice.Model));
    }

    [Fact]
    public void Restricted_data_falls_through_a_refused_pool_to_the_next_that_does_not_train()
    {
        var choice = Assert.IsType<RouteChoice>(Choose(0, DataClass.Restricted, Settings(Free, Subscription, Credit, Other), "subscription"));

        Assert.Equal(("credit", "paid/small"), (choice.Pool, choice.Model));
    }

    [Fact]
    public void Restricted_data_waits_for_a_refused_pool_rather_than_reach_a_pool_that_trains()
    {
        var wait = Assert.IsType<RouteWait>(Choose(0, DataClass.Restricted, Settings(Free, Credit, Other), "credit"));

        Assert.Equal(("cheap", "credit"), (wait.Tier, Assert.Single(wait.Standings).Pool));
    }

    [Fact]
    public void A_pool_lists_the_models_that_do_not_train_and_the_rest_of_its_models_are_for_open_data()
    {
        var partial = Other with { NoTrainingModels = ["other/big"] };
        var settings = Settings(Free, partial);

        var restricted = Assert.IsType<RouteChoice>(Choose(0, DataClass.Restricted, settings));
        var open = Assert.IsType<RouteChoice>(Choose(0, DataClass.Public, Settings(partial)));

        Assert.Equal(("other", "other/big", "strong"), (restricted.Pool, restricted.Model, restricted.Tier));
        Assert.Equal(("other", "other/small", "cheap"), (open.Pool, open.Model, open.Tier));
    }

    [Fact]
    public void A_tier_with_no_model_that_may_see_the_data_moves_the_call_up_never_down()
    {
        var decision = Choose(0, DataClass.Restricted, Settings(Free, Other with { NoTrainingModels = ["other/big"] }));

        Assert.Equal("strong", Assert.IsType<RouteChoice>(decision).Tier);
        Assert.Equal("strong", Assert.IsType<RouteChoice>(Choose(1, DataClass.Restricted, Settings(Free, Other with { NoTrainingModels = ["other/big"] }))).Tier);
    }

    [Fact]
    public void Restricted_data_is_blocked_when_no_pool_anywhere_lists_a_model_that_does_not_train()
    {
        var blocked = Assert.IsType<RouteBlocked>(Choose(0, DataClass.Restricted, Settings(Free, Other)));

        Assert.Equal(("cheap", DataClass.Restricted), (blocked.Tier, blocked.DataClass));
        Assert.IsType<RouteChoice>(Choose(0, DataClass.Public, Settings(Free, Other)));
    }

    [Fact]
    public void A_blocked_call_is_not_a_wait_even_when_every_pool_is_refused()
    {
        var decision = Choose(0, DataClass.Restricted, Settings(Free, Other), "free", "other");

        Assert.IsType<RouteBlocked>(decision);
    }

    [Fact]
    public void The_wildcard_covers_every_model_of_the_pool()
    {
        var pool = Other with { NoTrainingModels = ["*"] };

        Assert.Equal("other/big", Assert.IsType<RouteChoice>(Choose(1, DataClass.Restricted, Settings(pool))).Model);
    }

    [Fact]
    public void An_enabled_block_with_no_pool_that_keeps_prompts_private_is_a_problem()
    {
        Assert.Contains(Settings(Free, Other).Problems(), p => p.StartsWith("no pool lists no_training_models", StringComparison.Ordinal));
        Assert.DoesNotContain(Settings(Free, Credit).Problems(), p => p.Contains("no_training_models", StringComparison.Ordinal));
    }

    [Fact]
    public void A_no_training_model_the_pool_does_not_pay_for_is_a_problem()
    {
        var problems = Settings(Free, Credit with { NoTrainingModels = ["paid/small", "paid/huge"] }).Problems().ToList();

        Assert.Contains("pool 'credit': no_training_models names 'paid/huge', which the pool does not pay for", problems);
        Assert.DoesNotContain(problems, p => p.Contains("'paid/small', which", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("owner/name", true)]
    [InlineData("owner/*", true)]
    [InlineData("owner", false)]
    [InlineData("/name", false)]
    [InlineData("owner/", false)]
    [InlineData("a/b/c", false)]
    public void The_owner_private_list_holds_owner_and_name_or_owner_and_a_star(string entry, bool valid)
    {
        var problems = (Settings(Free, Credit) with { OwnerPrivateRepos = [entry] }).Problems().Where(p => p.StartsWith("owner_private_repos", StringComparison.Ordinal)).ToList();

        Assert.Equal(valid, problems.Count == 0);
    }

    [Fact]
    public void An_owner_private_name_matches_the_repository_or_the_owners_whole_account_without_case()
    {
        var pools = Settings(Free, Credit) with { OwnerPrivateRepos = ["me/notes", "ME/*"] };

        Assert.True(pools.NamesOwnerPrivate("Me/Notes"));
        Assert.True(pools.NamesOwnerPrivate("me/anything"));
        Assert.False(pools.NamesOwnerPrivate("other/notes"));
        Assert.False(pools.NamesOwnerPrivate("me/"));
        Assert.False(Settings(Free, Credit).NamesOwnerPrivate("me/notes"));
    }

    [Fact]
    public void Employer_repositories_are_restricted_by_their_path_unless_the_profile_names_other_directories()
    {
        Assert.Equal(["work"], Settings(Free, Credit).RestrictedSegments);
        Assert.Equal(["clients"], (Settings(Free, Credit) with { RestrictedPathSegments = ["clients"] }).RestrictedSegments);
    }
}
