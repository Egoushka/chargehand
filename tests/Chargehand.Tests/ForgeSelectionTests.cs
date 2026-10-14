using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Tests;

/// <summary>Which forge a remote URL is on (ADR 0047): the profile's <c>driven.forge</c> if it names one, else the host. A host nobody recognises is GitHub, as before.</summary>
public class ForgeSelectionTests
{
    [Theory]
    [InlineData("https://gitlab.com/group/project.git", "group", "project", "gitlab.com")]
    [InlineData("https://gitlab.com/group/sub/deeper/project", "group/sub/deeper", "project", "gitlab.com")]
    [InlineData("git@gitlab.com:group/project.git", "group", "project", "gitlab.com")]
    [InlineData("git@gitlab.example.test:group/sub/project.git", "group/sub", "project", "gitlab.example.test")]
    [InlineData("ssh://git@gitlab.example.test/group/sub/project.git", "group/sub", "project", "gitlab.example.test")]
    [InlineData("ssh://git@gitlab.example.test:2222/group/sub/project", "group/sub", "project", "gitlab.example.test")]
    [InlineData("https://oauth2:token@gitlab.example.test/group/project/", "group", "project", "gitlab.example.test")]
    [InlineData("https://GitLab.Example.test/group/project.git", "group", "project", "gitlab.example.test")]
    public void A_gitlab_host_is_read_with_its_nested_groups_without_a_profile_entry(string url, string owner, string name, string host) =>
        Assert.Equal(new RemoteRepository(owner, name, ForgeKind.GitLab, host), RemoteRepository.Parse(url, null));

    [Theory]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
    [InlineData("git@github.com:owner/repo.git", "owner", "repo")]
    [InlineData("ssh://git@github.com/owner/repo", "owner", "repo")]
    [InlineData("https://git.example.test/owner/repo.git", "owner", "repo")]
    [InlineData("https://ghe.example.test/owner/repo", "owner", "repo")]
    [InlineData("git@code.example.test:group/repo.git", "group", "repo")]
    public void Any_other_host_is_github_exactly_as_before(string url, string owner, string name)
    {
        var parsed = RemoteRepository.Parse(url, null);
        Assert.Equal(new RemoteRepository(owner, name), parsed);
        Assert.Equal(RemoteRepository.Parse(url), parsed);
        Assert.Equal(ForgeKind.GitHub, parsed!.Forge);
    }

    [Theory]
    [InlineData("https://git.example.test/group/sub/project.git", "group/sub", "project", "git.example.test")]
    [InlineData("git@code.example.test:group/sub/project.git", "group/sub", "project", "code.example.test")]
    [InlineData("https://github.com/group/sub/project.git", "group/sub", "project", "github.com")]
    public void The_profile_can_say_a_host_is_gitlab(string url, string owner, string name, string host) =>
        Assert.Equal(new RemoteRepository(owner, name, ForgeKind.GitLab, host), RemoteRepository.Parse(url, new DrivenForge("gitlab")));

    [Fact]
    public void The_profile_can_say_a_gitlab_looking_host_is_github()
    {
        Assert.Equal(new RemoteRepository("owner", "repo"), RemoteRepository.Parse("https://gitlab.example.test/owner/repo.git", new DrivenForge("github")));
        Assert.Null(RemoteRepository.Parse("https://gitlab.example.test/group/sub/project.git", new DrivenForge("github")));
    }

    [Theory]
    [InlineData("https://example.test/only-one")]
    [InlineData("https://gitlab.com/only-one")]
    [InlineData("file:///tmp/x.git")]
    [InlineData("/tmp/x.git")]
    [InlineData("")]
    public void Anything_else_is_on_no_forge_we_open_requests_on(string url)
    {
        Assert.Null(RemoteRepository.Parse(url, null));
        Assert.Null(RemoteRepository.Parse(url, new DrivenForge("gitlab")));
    }

    [Fact]
    public void A_github_remote_with_extra_segments_is_still_not_a_remote_we_open_pull_requests_on() =>
        Assert.Null(RemoteRepository.Parse("https://github.com/o/r/extra/segments", null));

    [Fact]
    public void The_policy_names_a_nested_project_by_its_whole_path()
    {
        var policy = new IntegratorPolicy(new DrivenIntegrator(Repositories: new Dictionary<string, string> { ["group/sub/project"] = "merge" }));
        Assert.Equal(IntegrationMode.Merge, policy.Resolve(RemoteRepository.Parse("git@gitlab.example.test:group/sub/project.git", null)!));
        Assert.Equal(IntegrationMode.Off, policy.Resolve(RemoteRepository.Parse("git@gitlab.example.test:group/other/project.git", null)!));
    }

    [Fact]
    public void The_forge_block_loads_and_the_integrator_needs_no_app_for_gitlab()
    {
        using var dir = new TempDir();
        var driven = Profile.Load(dir.Write("p.json", """
            {"schema":"profile/v1","driven":{"enabled":true,"forge":{"kind":"gitlab","base_url":"https://git.example.test","token_secret":"gitlab-token"},
              "integrator":{"repositories":{"group/sub/project":"merge"}}}}
            """)).Driven!;
        Assert.Equal(new DrivenForge("gitlab", "https://git.example.test", "gitlab-token"), driven.Forge);
        Assert.Null(driven.Integrator!.AppId);
        Assert.Equal("merge", driven.Integrator.Repositories!["group/sub/project"]);
        Assert.Null(Profile.Load(dir.Write("none.json", """{"schema":"profile/v1","driven":{"enabled":true}}""")).Driven!.Forge);
    }

    [Theory]
    [InlineData("""{"kind":"bitbucket"}""", "kind")]
    [InlineData("""{"kind":"gitlab","base_url":"http://git.example.test"}""", "base_url")]
    [InlineData("""{"kind":"gitlab","base_url":"https://user:pw@git.example.test"}""", "base_url")]
    [InlineData("""{"kind":"gitlab","base_url":"git.example.test"}""", "base_url")]
    [InlineData("""{"kind":"gitlab","token_secret":""}""", "token_secret")]
    [InlineData("""{"kind":"github","token_secret":"t"}""", "token_secret")]
    public void A_forge_value_that_cannot_work_is_refused_when_the_profile_loads(string block, string named)
    {
        using var dir = new TempDir();
        var path = dir.Write("p.json", $$$"""{"schema":"profile/v1","driven":{"enabled":true,"forge":{{{block}}}}}""");
        var e = Assert.Throws<ChargehandException>(() => Profile.Load(path));
        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains(named, e.Message);
    }

    [Theory]
    [InlineData("""{"app_id":7}""", "private_key_secret")]
    [InlineData("""{"private_key_secret":"k"}""", "app_id")]
    [InlineData("""{"installation_id":8,"private_key_secret":"k"}""", "app_id")]
    [InlineData("""{"app_id":7,"installation_id":8}""", "private_key_secret")]
    public void The_app_id_and_the_key_are_both_set_or_both_left_out(string block, string named)
    {
        using var dir = new TempDir();
        var path = dir.Write("p.json", $$$"""{"schema":"profile/v1","driven":{"enabled":true,"integrator":{{{block}}}}}""");
        Assert.Contains(named, Assert.Throws<ChargehandException>(() => Profile.Load(path)).Message);
    }

    [Fact]
    public void The_installation_id_is_optional_because_the_installation_is_looked_up_per_owner()
    {
        using var dir = new TempDir();
        var integrator = Profile.Load(dir.Write("p.json", """{"schema":"profile/v1","driven":{"enabled":true,"integrator":{"app_id":7,"private_key_secret":"k","repositories":{"o/r":"merge"}}}}""")).Driven!.Integrator!;
        Assert.Equal((7L, null), (integrator.AppId, integrator.InstallationId));
    }
}
