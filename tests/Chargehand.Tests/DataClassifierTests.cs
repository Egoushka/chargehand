using Chargehand.Config;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>The data class of a run comes from the repository the server itself maps (ADR 0095): public only on GitHub's word, owner-private only when the profile names it, everything else, and every doubt, restricted.</summary>
public class DataClassifierTests
{
    private const string Forge = "owner/open";

    private static readonly PoolSettings Credit = new("credit", PoolKinds.Monthly, ["p/small"], TotalUsd: 10, NoTrainingModels: ["*"]);

    private static PoolsSettings Pools(IReadOnlyList<string>? ownerPrivate = null, IReadOnlyList<string>? segments = null) =>
        new(Enabled: true, Tiers: [new("small", ["p/small"])], Order: [Credit], OwnerPrivateRepos: ownerPrivate, RestrictedPathSegments: segments);

    private static (DataClassifier Classifier, string Path) Build(TempDir root, IRepositoryVisibility? visibility, PoolsSettings? pools, string parent = "", string? origin = $"https://github.com/{Forge}.git", bool mapped = true)
    {
        var parentPath = parent.Length == 0 ? root.Path : Directory.CreateDirectory(Path.Combine(root.Path, parent)).FullName;
        var repo = Runs.GitRepo(parentPath);
        if (origin is not null)
            Runs.SetOrigin(repo, origin);
        var profile = Runs.Profile(root.Path) with
        {
            Pools = pools,
            Automation = mapped ? new AutomationSettings(new AutomationGitHub(new Dictionary<string, AutomationRepository> { [Forge] = new(repo.Path) }, ["octo"])) : null,
        };
        return (new DataClassifier(profile, visibility is null ? null : PublicFreePoolTests.Pool(visibility)), repo.Path);
    }

    private static IRepositoryVisibility Answers(RepositoryVisibility answer) => new PublicFreePoolTests.FakeVisibility(_ => answer);

    [Fact]
    public async Task A_repository_GitHub_reports_public_is_public()
    {
        using var root = new TempDir();
        var (classifier, path) = Build(root, Answers(RepositoryVisibility.Public), Pools());

        Assert.Equal(DataClass.Public, await classifier.ClassifyAsync(path, CancellationToken.None));
    }

    [Theory]
    [InlineData(nameof(RepositoryVisibility.Private))]
    [InlineData(nameof(RepositoryVisibility.Internal))]
    [InlineData(nameof(RepositoryVisibility.Unknown))]
    public async Task A_repository_that_is_not_public_and_not_named_is_restricted(string answer)
    {
        using var root = new TempDir();
        var (classifier, path) = Build(root, Answers(Enum.Parse<RepositoryVisibility>(answer)), Pools(["owner/other"]));

        Assert.Equal(DataClass.Restricted, await classifier.ClassifyAsync(path, CancellationToken.None));
    }

    [Theory]
    [InlineData("owner/open")]
    [InlineData("OWNER/Open")]
    [InlineData("owner/*")]
    public async Task A_private_repository_the_profile_names_is_owner_private(string entry)
    {
        using var root = new TempDir();
        var (classifier, path) = Build(root, Answers(RepositoryVisibility.Private), Pools([entry]));

        Assert.Equal(DataClass.OwnerPrivate, await classifier.ClassifyAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task A_checkout_under_a_work_directory_is_restricted_whatever_GitHub_and_the_profile_say()
    {
        using var root = new TempDir();
        var (classifier, path) = Build(root, Answers(RepositoryVisibility.Public), Pools([Forge]), parent: "work");

        Assert.Equal(DataClass.Restricted, await classifier.ClassifyAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task The_profile_names_the_restricted_directories_and_work_stops_being_one_when_it_does_not()
    {
        using var root = new TempDir();
        var (clients, clientsPath) = Build(root, Answers(RepositoryVisibility.Public), Pools(segments: ["clients"]), parent: "clients");
        using var other = new TempDir();
        var (work, workPath) = Build(other, Answers(RepositoryVisibility.Public), Pools(segments: ["clients"]), parent: "work");

        Assert.Equal(DataClass.Restricted, await clients.ClassifyAsync(clientsPath, CancellationToken.None));
        Assert.Equal(DataClass.Public, await work.ClassifyAsync(workPath, CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_public_lookup_no_repository_is_public_and_a_named_one_is_owner_private()
    {
        using var root = new TempDir();
        var (classifier, path) = Build(root, null, Pools([Forge]));
        using var other = new TempDir();
        var (unnamed, unnamedPath) = Build(other, null, Pools());

        Assert.Equal(DataClass.OwnerPrivate, await classifier.ClassifyAsync(path, CancellationToken.None));
        Assert.Equal(DataClass.Restricted, await unnamed.ClassifyAsync(unnamedPath, CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_lookup_is_not_public()
    {
        using var root = new TempDir();
        var (classifier, path) = Build(root, new PublicFreePoolTests.ThrowingVisibility(new HttpRequestException("boom")), Pools());

        Assert.Equal(DataClass.Restricted, await classifier.ClassifyAsync(path, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://github.com/owner/private-thing.git")]
    [InlineData("git@github.com:someone/else.git")]
    [InlineData(null)]
    public async Task A_mapped_name_the_checkouts_origin_does_not_match_is_restricted(string? origin)
    {
        using var root = new TempDir();
        var (classifier, path) = Build(root, Answers(RepositoryVisibility.Public), Pools([Forge]), origin: origin);

        Assert.Equal(DataClass.Restricted, await classifier.ClassifyAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task A_path_the_server_does_not_know_is_restricted()
    {
        using var root = new TempDir();
        var (classifier, path) = Build(root, Answers(RepositoryVisibility.Public), Pools([Forge]), mapped: false);

        Assert.Equal(DataClass.Restricted, await classifier.ClassifyAsync(path, CancellationToken.None));
        Assert.Equal(DataClass.Restricted, await classifier.ClassifyAsync(Path.Combine(root.Path, "elsewhere"), CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task No_repository_is_restricted(string? path)
    {
        using var root = new TempDir();
        var (classifier, _) = Build(root, Answers(RepositoryVisibility.Public), Pools([Forge]));

        Assert.Equal(DataClass.Restricted, await classifier.ClassifyAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task A_profile_without_a_pools_block_classifies_everything_as_restricted()
    {
        using var root = new TempDir();
        var (classifier, path) = Build(root, Answers(RepositoryVisibility.Public), null);

        Assert.Equal(DataClass.Restricted, await classifier.ClassifyAsync(path, CancellationToken.None));
    }

    [Theory]
    [InlineData(nameof(RepositoryVisibility.Public), DataClass.Public)]
    [InlineData(nameof(RepositoryVisibility.Private), DataClass.Restricted)]
    [InlineData(nameof(RepositoryVisibility.Unknown), DataClass.Restricted)]
    public async Task Another_persons_repository_cloned_for_reading_is_public_only_on_GitHubs_word(string answer, DataClass expected)
    {
        using var root = new TempDir();
        var (classifier, _) = Build(root, Answers(Enum.Parse<RepositoryVisibility>(answer)), Pools([Forge]));

        Assert.Equal(expected, await classifier.ClassifyAsync("gh:someone/else", CancellationToken.None));
    }

    [Fact]
    public async Task A_peer_clone_with_no_public_lookup_is_restricted()
    {
        using var root = new TempDir();
        var (classifier, _) = Build(root, null, Pools([Forge]));

        Assert.Equal(DataClass.Restricted, await classifier.ClassifyAsync("gh:someone/else", CancellationToken.None));
    }
}
