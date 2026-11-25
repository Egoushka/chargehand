using Chargehand.Modules.Accounts.Api;

namespace Chargehand.Modules.Accounts.Tests;

/// <summary>Spec 2026-11-25, F1 to F5: the list, the route, the tier and the fence, pinned to the refusals of the maintainer's router. The list here is synthetic.</summary>
public class FreeRouteTests
{
    private const string List = """
        # synthetic list for tests
        [providers.free]
        name = "pool"
        key_file = "/nowhere"

        [[model]]
        id = "zen/alpha-free"          # private tier, zero retention
        tier = "private"
        tasks = { ask = "ok", mechanical = "good", critic = "ok" }

        [[model]]
        id = "zen/beta-free"
        tier = "private"
        tasks = { ask = "good", mechanical = "ok", edit = "ok", review = "no" }

        [[model]]
        id = "zen/gamma-free"
        tier = "public"
        tasks = { ask = "good", mechanical = "good" }

        [[model]]
        id = "pool/vendor/delta:free"
        tier = "public"
        tasks = { ask = "good" }

        [[model]]
        id = "zen/ungraded-free"
        tier = "private"
        """;

    private static readonly FreeModelCatalog Catalog = FreeModelCatalog.Parse(List);
    private static readonly string[] Zen = ["zen"];
    private static readonly string[] ZenAndPool = ["zen", "pool"];

    [Fact]
    public void The_list_reads_tiers_grades_and_order()
    {
        Assert.Equal(["zen/alpha-free", "zen/beta-free", "zen/gamma-free", "pool/vendor/delta:free", "zen/ungraded-free"], Catalog.Models.Select(m => m.Id));
        Assert.Equal("pool", Catalog.Models[3].Provider);
        Assert.Equal(FreeTier.Public, Catalog.Models[2].Tier);
        Assert.False(Catalog.Models[1].Grades.ContainsKey(FreeTask.Review));   // "no" is not a grade that routes
    }

    [Theory]
    [InlineData("[[model]]\nid = \"zen/x\"\ntier = \"secret\"")]
    [InlineData("[[model]]\nid = \"zen/x\"\ntier = \"private\"\ntasks = { ask = \"great\" }")]
    [InlineData("[[model]]\nid = \"zen/x\"\ntier = \"private\"\ntasks = { dream = \"ok\" }")]
    [InlineData("[[model]]\nid = \"nosplit\"\ntier = \"private\"")]
    [InlineData("[[model]]\nid = \"zen/x\"\ntier = \"private\"\ntasks = ok")]
    [InlineData("[[model]]\nid = \"zen/x\" trailing\ntier = \"private\"")]
    [InlineData("[[other]]")]
    public void A_malformed_list_fails_closed(string toml) => Assert.Throws<FormatException>(() => FreeModelCatalog.Parse(toml));

    [Fact]
    public void A_missing_or_broken_file_offers_no_model_and_says_why()
    {
        using var dir = new TempDir();
        var (missing, why) = FreeModelCatalog.Load(Path.Combine(dir.Path, "none.toml"));
        Assert.Empty(missing.Models);
        Assert.NotNull(why);
        var bad = Path.Combine(dir.Path, "bad.toml");
        File.WriteAllText(bad, "[[model]]\nid = 3");
        Assert.Empty(FreeModelCatalog.Load(bad).Catalog.Models);
    }

    [Fact]
    public void Good_comes_before_ok_then_file_order_and_ungraded_never_routes()
    {
        Assert.Equal(["zen/beta-free", "zen/alpha-free"], FreeRoute.Models(Catalog, FreeTier.Private, FreeTask.Ask, Zen));
        Assert.Equal(["zen/alpha-free", "zen/beta-free"], FreeRoute.Models(Catalog, FreeTier.Private, FreeTask.Mechanical, Zen));
        Assert.Empty(FreeRoute.Models(Catalog, FreeTier.Private, FreeTask.Review, Zen));   // "no" and ungraded
        Assert.Empty(FreeRoute.Models(Catalog, FreeTier.Public, FreeTask.Plan, ZenAndPool));
    }

    [Fact]
    public void The_public_tier_adds_the_public_models_and_the_private_tier_never_has_them()
    {
        Assert.Equal(["zen/beta-free", "zen/gamma-free", "pool/vendor/delta:free", "zen/alpha-free"], FreeRoute.Models(Catalog, FreeTier.Public, FreeTask.Ask, ZenAndPool));
        Assert.DoesNotContain("zen/gamma-free", FreeRoute.Models(Catalog, FreeTier.Private, FreeTask.Ask, ZenAndPool));
        Assert.DoesNotContain("pool/vendor/delta:free", FreeRoute.Models(Catalog, FreeTier.Public, FreeTask.Ask, Zen));   // the server does not hold that provider
    }

    [Theory]
    [InlineData(RepositoryVisibility.Public, FreeTier.Public)]
    [InlineData(RepositoryVisibility.Private, FreeTier.Private)]
    [InlineData(RepositoryVisibility.Internal, FreeTier.Private)]
    [InlineData(RepositoryVisibility.Unknown, FreeTier.Private)]
    public void Only_a_public_answer_from_the_forge_gets_the_public_tier(RepositoryVisibility seen, FreeTier tier) => Assert.Equal(tier, FreeRoute.TierFor(seen));

    [Theory]
    [InlineData("cheap", FreeTaskClass.SmallSlot, false, FreeTask.Mechanical)]
    [InlineData("prior-art", FreeTaskClass.SmallSlot, false, FreeTask.Mechanical)]
    [InlineData("draft", FreeTaskClass.SmallSlot, false, FreeTask.Mechanical)]
    [InlineData("thorough", FreeTaskClass.CriticSlot, false, FreeTask.Critic)]
    [InlineData("review", FreeTaskClass.WorkerSlot, false, FreeTask.Review)]
    [InlineData("design", FreeTaskClass.WorkerSlot, false, FreeTask.Plan)]
    [InlineData("split", FreeTaskClass.WorkerSlot, false, FreeTask.Plan)]
    [InlineData("default", FreeTaskClass.WorkerSlot, false, FreeTask.Ask)]
    [InlineData("incident", FreeTaskClass.WorkerSlot, false, FreeTask.Ask)]
    [InlineData("code", FreeTaskClass.WorkerSlot, true, FreeTask.Edit)]
    [InlineData("driven", FreeTaskClass.WorkerSlot, true, FreeTask.Edit)]
    public void A_node_kind_maps_to_its_task_class(string preset, string slot, bool writes, FreeTask expected) => Assert.Equal(expected, FreeTaskClass.For(preset, slot, writes));

    [Fact]
    public void A_slot_the_map_does_not_know_has_no_class() => Assert.Null(FreeTaskClass.For("public-free", "provider/free-model", false));

    private sealed class Seen(RepositoryVisibility answer) : IRepositoryVisibility
    {
        public Task<RepositoryVisibility> LookupAsync(string forge, CancellationToken ct) => Task.FromResult(answer);
    }

    private static readonly FreeFirstSettings Settings = new("/config/free-models.toml", null, ["zen", "pool"]);

    [Fact]
    public async Task An_offer_for_a_private_repository_lists_private_models_only()
    {
        using var repo = new TempDir();
        var offer = await FreeRoute.OfferAsync(Catalog, Settings, new Seen(RepositoryVisibility.Private), repo.Path, "o/r", FreeTask.Ask, new HashSet<string>(), CancellationToken.None);
        Assert.Equal(FreeTier.Private, offer.Tier);
        Assert.Equal(["zen/beta-free", "zen/alpha-free"], offer.Models);
    }

    [Fact]
    public async Task An_unverified_forge_name_is_the_private_tier()
    {
        using var repo = new TempDir();
        var offer = await FreeRoute.OfferAsync(Catalog, Settings, new Seen(RepositoryVisibility.Public), repo.Path, null, FreeTask.Ask, new HashSet<string>(), CancellationToken.None);
        Assert.Equal(FreeTier.Private, offer.Tier);
    }

    [Fact]
    public async Task Edit_and_a_missing_class_or_repository_offer_nothing()
    {
        using var repo = new TempDir();
        var visible = new Seen(RepositoryVisibility.Public);
        var none = new HashSet<string>();
        Assert.Empty((await FreeRoute.OfferAsync(Catalog, Settings, visible, repo.Path, "o/r", FreeTask.Edit, none, CancellationToken.None)).Models);
        Assert.Empty((await FreeRoute.OfferAsync(Catalog, Settings, visible, repo.Path, "o/r", null, none, CancellationToken.None)).Models);
        Assert.Empty((await FreeRoute.OfferAsync(Catalog, Settings, visible, null, "o/r", FreeTask.Ask, none, CancellationToken.None)).Models);
    }

    [Fact]
    public async Task A_model_already_tried_is_not_offered_again()
    {
        using var repo = new TempDir();
        var offer = await FreeRoute.OfferAsync(Catalog, Settings, new Seen(RepositoryVisibility.Private), repo.Path, "o/r", FreeTask.Ask, new HashSet<string> { "zen/beta-free" }, CancellationToken.None);
        Assert.Equal(["zen/alpha-free"], offer.Models);
    }

    [Fact]
    public async Task A_repository_under_a_work_directory_is_refused_whatever_the_forge_says()
    {
        using var root = new TempDir();
        var repo = Directory.CreateDirectory(Path.Combine(root.Path, "work", "client", "app")).FullName;
        var offer = await FreeRoute.OfferAsync(Catalog, Settings, new Seen(RepositoryVisibility.Public), repo, "o/r", FreeTask.Ask, new HashSet<string>(), CancellationToken.None);
        Assert.Empty(offer.Models);
        Assert.NotNull(offer.Reason);
        var dotted = Directory.CreateDirectory(Path.Combine(root.Path, "x", "..", "work", "y")).FullName;
        Assert.NotNull(FreeFence.Refusal(dotted, FreeFence.DefaultDeny));
        Assert.NotNull(FreeFence.Refusal(Directory.CreateDirectory(Path.Combine(root.Path, ".devbox", "z")).FullName, FreeFence.DefaultDeny));
    }

    [Fact]
    public void A_symlinked_parent_that_leads_into_work_is_refused()
    {
        using var root = new TempDir();
        var inner = Directory.CreateDirectory(Path.Combine(root.Path, "work", "app")).FullName;
        var alias = Path.Combine(root.Path, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(root.Path, "work"));
        Assert.NotNull(FreeFence.Refusal(Path.Combine(alias, "app"), FreeFence.DefaultDeny));
        Assert.Null(FreeFence.Refusal(Directory.CreateDirectory(Path.Combine(root.Path, "ok")).FullName, FreeFence.DefaultDeny));
        Assert.True(Directory.Exists(inner));
    }

    [Fact]
    public void A_link_inside_the_repository_that_leads_to_work_or_holds_it_is_refused()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "work", "secret"));
        var repo = Directory.CreateDirectory(Path.Combine(root.Path, "repos", "app")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(repo, "direct"), Path.Combine(root.Path, "work", "secret"));
        Assert.NotNull(FreeFence.Refusal(repo, FreeFence.DefaultDeny));

        var holder = Directory.CreateDirectory(Path.Combine(root.Path, "repos", "app2")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(holder, "up"), root.Path);   // the directory that has work/ in it
        Assert.NotNull(FreeFence.Refusal(holder, FreeFence.DefaultDeny));
    }

    [Fact]
    public void A_link_to_a_harmless_directory_is_fine_and_a_link_chain_is_followed()
    {
        using var root = new TempDir();
        var shared = Directory.CreateDirectory(Path.Combine(root.Path, "shared")).FullName;
        var repo = Directory.CreateDirectory(Path.Combine(root.Path, "repos", "app")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(repo, "docs"), shared);
        Assert.Null(FreeFence.Refusal(repo, FreeFence.DefaultDeny));
        Directory.CreateDirectory(Path.Combine(root.Path, "work", "x"));
        Directory.CreateSymbolicLink(Path.Combine(shared, "hop"), Path.Combine(root.Path, "work", "x"));
        Assert.NotNull(FreeFence.Refusal(repo, FreeFence.DefaultDeny));
    }

    [Fact]
    public void A_settings_block_needs_the_list_and_takes_directory_names_only()
    {
        Assert.Contains(new FreeFirstSettings().Problems(), p => p.Contains("models_file", StringComparison.Ordinal));
        Assert.Contains(new FreeFirstSettings("/x", ["a/b"]).Problems(), p => p.Contains("deny_path_segments", StringComparison.Ordinal));
        Assert.Empty(new FreeFirstSettings("/x").Problems());
        Assert.Equal(["work", ".devbox"], new FreeFirstSettings("/x").Denied);
    }
}
