using Chargehand.Contracts;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;
using Chargehand.Workspace;

namespace Chargehand.Tests;

/// <summary>Callers name a repository as they know it; a name that is not a checkout on the server resolves through the profile, and a miss lists the known names.</summary>
public class RepositoryNamesTests
{
    [Theory]
    [InlineData("Egoushka/chargehand")]
    [InlineData("https://github.com/Egoushka/chargehand")]
    [InlineData("https://github.com/Egoushka/chargehand.git")]
    [InlineData("git@github.com:Egoushka/chargehand.git")]
    public async Task An_owner_name_or_GitHub_url_resolves_through_the_intake_map(string given)
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var profile = Runs.Profile(root.Path) with
        {
            Automation = new AutomationSettings(new AutomationGitHub(new Dictionary<string, AutomationRepository> { ["Egoushka/chargehand"] = new(repo.Path) }, ["octo"])),
        };
        var orchestrator = new Orchestrator(profile, runtime, "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>());

        var r = await orchestrator.RunAsync(Runs.CheapRequest(repo with { Path = given }), CancellationToken.None);

        Assert.NotEqual(ResultStatus.Failed, r.Status);
        Assert.Single(runtime.Created);
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("personal/repo")]
    [InlineData("/workspace/repo")]
    [InlineData("repo/.git")]
    public async Task A_folder_name_resolves_to_the_checkout_under_the_roots(string given)
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        var r = await Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Runs.CheapRequest(repo with { Path = given }), CancellationToken.None);

        Assert.NotEqual(ResultStatus.Failed, r.Status);
        Assert.Single(runtime.Created);
    }

    [Fact]
    public async Task An_unknown_name_fails_with_the_names_the_server_knows()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        var r = await Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Runs.CheapRequest(repo with { Path = "someone/elsewhere" }), CancellationToken.None);

        Assert.Equal(ErrorCode.CheckoutInvalid, r.Error?.Code);
        Assert.Contains("known: repo", r.Summary, StringComparison.Ordinal);
        Assert.Contains("repositories tool", r.Error!.Action, StringComparison.Ordinal);
        Assert.Empty(runtime.Created);
    }

    [Fact]
    public void Two_checkouts_with_one_folder_name_resolve_only_by_forge_name()
    {
        KnownRepository[] known = [new("app", "/a/app", "one/app"), new("app", "/b/app", null)];

        Assert.Null(RepositoryNames.Resolve("app", known));
        Assert.Equal("/a/app", RepositoryNames.Resolve("one/app", known));
        Assert.Null(RepositoryNames.Resolve(".", known));
    }

    [Fact]
    public void Managed_clones_are_known_by_owner_and_name()
    {
        using var root = new TempDir();
        var profile = Runs.Profile(root.Path) with { RepositoryRoots = [], Automation = new AutomationSettings(new AutomationGitHub(AllowedAuthors: ["octo"], Discover: true)) };
        var clone = Path.Combine(profile.ClonesRoot, "acme");
        Directory.CreateDirectory(clone);
        Runs.GitRepo(clone);

        var known = Assert.Single(RepositoryNames.List(profile));

        Assert.Equal("acme/repo", known.Forge);
        Assert.Equal(known.Path, RepositoryNames.Resolve("https://github.com/acme/repo", [known]));
    }

    [Fact]
    public async Task Heads_carry_the_commit_each_checkout_has_checked_out()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);

        var head = Assert.Single(await RepositoryNames.HeadsAsync(RepositoryNames.List(Runs.Profile(root.Path)), CancellationToken.None));

        Assert.Equal(repo.Commit, head.Commit);
        Assert.Equal("repo", head.Name);
    }
}
