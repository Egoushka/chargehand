using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-241: GitHub intake with no repository list (<c>discover</c>), its managed clones, the trigger label it creates, and the per-repository switch the console sets.</summary>
public class IntakeSourcesTests
{
    private const string Other = "octo/other";

    private static AutomationGitHub Discovering(IReadOnlyDictionary<string, AutomationRepository>? listed = null) => new(listed, [IntakeRig.Author], Discover: true);

    /// <summary>Managed clones under a temporary root, with the given repositories already cloned so no git runs.</summary>
    private static ManagedClones Clones(TempDir dir, params string[] cloned)
    {
        var clones = new ManagedClones(Path.Combine(dir.Path, "repos"), (_, _, _) => Task.FromResult("ghs_secrettoken"));
        foreach (var repository in cloned)
            Directory.CreateDirectory(Path.Combine(clones.PathOf(repository), ".git"));
        return clones;
    }

    [Fact]
    public async Task With_discover_every_repository_the_App_sees_is_watched_from_its_managed_clone_and_an_archived_one_is_not()
    {
        using var dir = new TempDir();
        var clones = Clones(dir, Other);
        using var rig = new IntakeRig(Discovering(), clones: clones);
        rig.GitHub.Installed.AddRange([new(Other, "https://github.example.test/octo/other.git", false), new("octo/old", "https://github.example.test/octo/old.git", true)]);
        rig.Labeled(7);
        await rig.Poll();

        var request = Assert.Single(rig.Starter.Started);
        Assert.Equal(clones.PathOf(Other), request.Context.Repository!.Path);
        var watched = Assert.Single(rig.Intake.Watched);
        Assert.Equal((Other, true), (watched.Key, watched.Value.Discovered));
        Assert.Empty(rig.Warnings);
    }

    [Fact]
    public async Task A_listed_repository_keeps_its_own_checkout_and_a_failed_listing_keeps_the_last_one()
    {
        using var dir = new TempDir();
        using var rig = new IntakeRig(Discovering(new Dictionary<string, AutomationRepository> { [Other] = new("/srv/repos/other") }), clones: Clones(dir, "octo/third"));
        rig.GitHub.Installed.AddRange([new(Other, "https://github.example.test/octo/other.git", false), new("octo/third", "https://github.example.test/octo/third.git", false)]);
        await rig.Poll();
        Assert.Equal([(Other, "/srv/repos/other", false), ("octo/third", Path.Combine(dir.Path, "repos", "octo", "third"), true)],
            rig.Intake.Watched.OrderBy(w => w.Key).Select(w => (w.Key, w.Value.Path, w.Value.Discovered)));

        rig.GitHub.InstalledFails = true;
        await rig.Poll();
        Assert.Equal(2, rig.Intake.Watched.Count);
        Assert.Contains(rig.Warnings, w => w.Contains("could not be listed"));
    }

    [Fact]
    public void Discover_needs_no_repository_list_and_puts_the_clones_among_the_repository_roots()
    {
        using var dir = new TempDir();
        var profile = Profile.Load(dir.Write("local.json", """
            {"schema":"profile/v1","worker_root":"/var/tmp/ch","repository_roots":["/srv/repos"],
             "driven":{"enabled":true,"integrator":{"app_id":1,"private_key_secret":"app-key"}},
             "automation":{"github":{"discover":true,"allowed_authors":["alice"]}}}
            """));

        Assert.True(profile.Automation!.Github!.Discover);
        Assert.Equal(["/srv/repos", Path.Combine("/var/tmp/ch", "repos")], profile.Roots);
    }

    [Fact]
    public async Task The_trigger_label_is_created_once_when_a_repository_is_first_watched()
    {
        using var rig = new IntakeRig();
        rig.GitHub.LabelledRepositories.Clear();
        await rig.Poll();
        await rig.Poll();
        Assert.Equal([$"{IntakeRig.Repo}:chargehand"], rig.GitHub.LabelsCreated);

        rig.Restart();
        rig.GitHub.LabelledRepositories.Clear();
        await rig.Poll();
        Assert.Single(rig.GitHub.LabelsCreated);
        Assert.Contains(IntakeRig.Repo, rig.State.Labeled);
    }

    [Fact]
    public async Task A_repository_switched_off_is_not_read_and_its_trigger_waits_until_it_is_switched_on_again_after_a_restart()
    {
        using var rig = new IntakeRig();
        Assert.True(rig.Intake.Sources.Set(IntakeRig.Repo, false));
        rig.Labeled(7);
        await rig.Poll();
        Assert.Empty(rig.Starter.Started);
        Assert.Empty(rig.GitHub.LabelsCreated);

        rig.Restart();
        await rig.Poll();
        Assert.Empty(rig.Starter.Started);
        Assert.False(rig.Sources.IsOn(IntakeRig.Repo));

        Assert.True(rig.Intake.Sources.Set(IntakeRig.Repo, true));
        Assert.False(rig.Intake.Sources.Set(IntakeRig.Repo, true));
        await rig.Poll();
        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public async Task A_batch_of_a_repository_switched_off_still_reports_how_it_ended()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7);
        await rig.Poll();
        rig.Intake.Sources.Set(IntakeRig.Repo, false);
        rig.Log.End("run-1", IntakeRig.Pr());
        await rig.Poll();
        Assert.Contains(rig.GitHub.Posted, p => p.Body.Contains("draft pull request https://github.example.test/octo/repo/pull/9"));
    }

    [Fact]
    public async Task A_managed_clone_takes_the_token_as_a_header_in_the_environment_and_never_a_URL_with_a_credential()
    {
        using var dir = new TempDir();
        var clones = Clones(dir);
        var env = await clones.AuthAsync(clones.PathOf(Other), default);

        Assert.Equal("http.extraHeader", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal($"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:ghs_secrettoken"))}", env["GIT_CONFIG_VALUE_0"]);
        Assert.True(clones.Owns(clones.PathOf(Other)));
        Assert.False(clones.Owns("/srv/repos/other"));
        foreach (var url in new[] { "https://x-access-token:t@github.example.test/octo/other.git", "file:///srv/repos/other", "git@github.example.test:octo/other.git" })
            await Assert.ThrowsAsync<IntakeException>(() => clones.EnsureAsync(Other, url, default));
        Assert.False(Directory.Exists(clones.PathOf(Other)));
    }

    [Fact]
    public async Task The_console_lists_the_switches_and_sets_them_for_watched_repositories_only()
    {
        using var rig = new IntakeRig();
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), intake: rig.Intake);

        var listed = await server.Http.GetFromJsonAsync<JsonElement>("/v1/intake/sources");
        Assert.Equal("intake-sources/v1", listed.GetProperty("contract_version").GetString());
        var source = Assert.Single(listed.GetProperty("sources").EnumerateArray());
        Assert.Equal((IntakeRig.Repo, true, "listed"), (source.GetProperty("repository").GetString(), source.GetProperty("enabled").GetBoolean(), source.GetProperty("origin").GetString()));

        var put = await server.Http.PutAsJsonAsync("/v1/intake/sources", new { sources = new[] { new { repository = IntakeRig.Repo, enabled = false } } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False((await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sources")[0].GetProperty("enabled").GetBoolean());
        Assert.False(rig.Sources.IsOn(IntakeRig.Repo));

        Assert.Equal(HttpStatusCode.NotFound, (await server.Http.PutAsJsonAsync("/v1/intake/sources", new { sources = new[] { new { repository = "octo/typo", enabled = false } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Http.PutAsJsonAsync("/v1/intake/sources", new { sources = new[] { new { repository = IntakeRig.Repo, enabled = "no" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Http.PutAsJsonAsync("/v1/intake/sources", new { ids = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_Plane_project_switched_off_is_not_read_and_its_batch_still_reports_how_it_ended()
    {
        using var rig = new PlaneRig();
        rig.Item(1);
        await rig.Poll();
        Assert.True(rig.Intake.Sources.Set(PlaneRig.Project, false));
        rig.Item(2);
        rig.Log.End("run-1", PlaneRig.Pr(1));
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Contains(rig.Plane.Comments, c => c.Item == "item-1" && c.Text.Contains("draft pull request"));

        rig.Restart();
        Assert.False(rig.Sources.IsOn(PlaneRig.Project));
        rig.Intake.Sources.Set(PlaneRig.Project, true);
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);
    }

    [Fact]
    public async Task The_console_lists_each_source_with_its_queue_and_sets_Plane_projects()
    {
        using var github = new IntakeRig(new AutomationGitHub(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: 1));
        github.Labeled(7);
        github.Labeled(8, eventId: 2);
        await github.Poll();
        using var plane = new PlaneRig();
        plane.Item(3);
        await plane.Poll();
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), intake: github.Intake, plane: plane.Intake,
            tweak: (_, s) => s with { IntakeStatePath = github.StateFile, PlaneStatePath = plane.StateFile });

        var listed = await server.Http.GetFromJsonAsync<JsonElement>("/v1/intake/sources");
        var source = Assert.Single(listed.GetProperty("sources").EnumerateArray());
        Assert.Equal((1, 1), (source.GetProperty("running").GetInt32(), source.GetProperty("queued").GetInt32()));
        var recent = Assert.Single(source.GetProperty("recent").EnumerateArray());
        Assert.Equal(("#7", "running"), (recent.GetProperty("item").GetString(), recent.GetProperty("phase").GetString()));
        var planes = listed.GetProperty("plane");
        Assert.True(planes.GetProperty("configured").GetBoolean());
        var project = Assert.Single(planes.GetProperty("projects").EnumerateArray());
        Assert.Equal((PlaneRig.Project, true, "octo/repo", 1), (project.GetProperty("project").GetString(), project.GetProperty("enabled").GetBoolean(),
            project.GetProperty("remote").GetString(), project.GetProperty("running").GetInt32()));
        Assert.Equal("ABC-3", project.GetProperty("recent")[0].GetProperty("item").GetString());

        var put = await server.Http.PutAsJsonAsync("/v1/intake/sources", new { plane_projects = new[] { new { project = PlaneRig.Project, enabled = false } } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False((await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plane").GetProperty("projects")[0].GetProperty("enabled").GetBoolean());
        Assert.False(plane.Sources.IsOn(PlaneRig.Project));
        Assert.True(github.Sources.IsOn(IntakeRig.Repo));

        Assert.Equal(HttpStatusCode.NotFound, (await server.Http.PutAsJsonAsync("/v1/intake/sources", new { plane_projects = new[] { new { project = "proj-typo", enabled = false } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Http.PutAsJsonAsync("/v1/intake/sources", new { plane_projects = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Http.PutAsJsonAsync("/v1/intake/sources", new { sources = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task A_switch_of_an_intake_the_profile_does_not_have_is_a_conflict()
    {
        using var plane = new PlaneRig();
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), plane: plane.Intake);

        var listed = await server.Http.GetFromJsonAsync<JsonElement>("/v1/intake/sources");
        Assert.False(listed.GetProperty("configured").GetBoolean());
        Assert.Single(listed.GetProperty("plane").GetProperty("projects").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await server.Http.PutAsJsonAsync("/v1/intake/sources", new { sources = new[] { new { repository = IntakeRig.Repo, enabled = false } } })).StatusCode);
    }
}
