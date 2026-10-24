using System.Diagnostics;
using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>2026-10-21: the server's checkouts were mounted read-only, the fetch before a start failed on <c>.git/FETCH_HEAD</c>, and the batch started from whatever commit the checkout last had.
/// A checkout intake cannot write is not fetched into: the repository's managed clone is (ADR 0023: a source is only read).</summary>
public class ReadOnlyCheckoutTests
{
    private const string Url = "https://github.example.test/octo/repo.git";

    private static ManagedClones Clones(TempDir dir, bool cloned = true)
    {
        var clones = new ManagedClones(Path.Combine(dir.Path, "repos"), (_, _, _) => Task.FromResult("ghs_secrettoken"));
        if (cloned)
            Directory.CreateDirectory(Path.Combine(clones.PathOf(IntakeRig.Repo), ".git"));
        return clones;
    }

    [Fact]
    public async Task A_checkout_git_cannot_write_says_so_with_its_remote_and_the_commit_it_has_and_without_clones_its_commit_is_used()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var origin = new TempDir();
        using var checkout = new TempDir();
        using var dir = new TempDir();
        Git(origin.Path, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(origin.Path, "a.txt"), "one");
        Git(origin.Path, "add", ".");
        Git(origin.Path, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "one");
        Git(checkout.Path, "clone", "-q", origin.Path, ".");
        var stale = Git(checkout.Path, "rev-parse", "origin/HEAD");
        Git(origin.Path, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "--allow-empty", "-m", "two");
        var git = Path.Combine(checkout.Path, ".git");
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var e = await Assert.ThrowsAsync<ReadOnlyCheckoutException>(() => new GitRepositoryHeads(clones: Clones(dir)).CurrentCommitAsync(checkout.Path, default));
            Assert.Equal((origin.Path, stale), (e.OriginUrl, e.Stale));

            List<string> warnings = [];
            Assert.Equal(stale, await new GitRepositoryHeads(warnings.Add).CurrentCommitAsync(checkout.Path, default));
            Assert.Contains(warnings, w => w.Contains("could not fetch", StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task A_read_only_checkout_is_replaced_by_the_managed_clone_which_later_starts_use_directly()
    {
        using var dir = new TempDir();
        var clones = Clones(dir);
        using var rig = new IntakeRig(new AutomationGitHub(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: 5), clones: clones);
        rig.Heads.ReadOnly["/srv/repos/repo"] = new ReadOnlyCheckoutException("cannot open '.git/FETCH_HEAD': Read-only file system", Url, "0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0");
        rig.Labeled(7);
        await rig.Poll();
        rig.Labeled(8, eventId: 2);
        await rig.Poll();

        Assert.Equal([new RepositoryRef(clones.PathOf(IntakeRig.Repo), rig.Heads.Commit), new RepositoryRef(clones.PathOf(IntakeRig.Repo), rig.Heads.Commit)],
            rig.Starter.Started.Select(r => r.Context.Repository));
        Assert.Equal(["/srv/repos/repo", clones.PathOf(IntakeRig.Repo), clones.PathOf(IntakeRig.Repo)], rig.Heads.Asked);
        Assert.Contains(rig.Infos, i => i.Contains("intake: octo/repo: /srv/repos/repo cannot be written", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_read_only_checkout_whose_remote_cannot_be_cloned_starts_from_the_commit_it_has_and_says_so()
    {
        using var dir = new TempDir();
        using var rig = new IntakeRig(clones: Clones(dir, cloned: false));
        rig.Heads.ReadOnly["/srv/repos/repo"] = new ReadOnlyCheckoutException("cannot open '.git/FETCH_HEAD': Read-only file system", "git@github.example.test:octo/repo.git", "0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0");
        rig.Labeled(7);
        await rig.Poll();

        Assert.Equal(new RepositoryRef("/srv/repos/repo", "0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0"), Assert.Single(rig.Starter.Started).Context.Repository);
        Assert.Contains(rig.Warnings, w => w.Contains("starts from the commit the checkout has", StringComparison.Ordinal));
    }

    private static AutomationGitHub Reviewing() =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: 5, ReviewPulls: true);

    private const string Sha = "1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Old = "0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0ld0";

    [Fact]
    public async Task A_pull_head_git_cannot_fetch_into_a_read_only_checkout_says_so_with_the_ref_a_host_job_kept_and_without_clones_that_ref_is_used()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var origin = new TempDir();
        using var checkout = new TempDir();
        using var dir = new TempDir();
        Git(origin.Path, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(origin.Path, "a.txt"), "one");
        Git(origin.Path, "add", ".");
        Git(origin.Path, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "one");
        var kept = Git(origin.Path, "rev-parse", "HEAD");
        Git(origin.Path, "update-ref", "refs/pull/5/head", kept);
        Git(checkout.Path, "clone", "-q", origin.Path, ".");
        Git(checkout.Path, "update-ref", "refs/pull/5/head", kept);
        var git = Path.Combine(checkout.Path, ".git");
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var e = await Assert.ThrowsAsync<ReadOnlyCheckoutException>(() => new GitRepositoryHeads(clones: Clones(dir)).FetchPullHeadAsync(checkout.Path, 5, default));
            Assert.Equal((origin.Path, kept), (e.OriginUrl, e.Stale));

            Assert.Equal(kept, await new GitRepositoryHeads().FetchPullHeadAsync(checkout.Path, 5, default));
        }
        finally
        {
            File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task A_pull_request_is_reviewed_from_the_managed_clone_when_the_checkout_cannot_be_written_and_later_ones_go_there_directly()
    {
        using var dir = new TempDir();
        var clones = Clones(dir);
        using var rig = new IntakeRig(Reviewing(), clones: clones);
        rig.Heads.ReadOnly["/srv/repos/repo"] = new ReadOnlyCheckoutException("cannot open '.git/FETCH_HEAD': Read-only file system", Url, Old);
        rig.OpenPull(5, Sha);
        await rig.Poll();
        rig.OpenPull(6, "2222222bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        await rig.Poll();

        Assert.Equal([new RepositoryRef(clones.PathOf(IntakeRig.Repo), Sha), new RepositoryRef(clones.PathOf(IntakeRig.Repo), "2222222bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")],
            rig.Starter.Started.Select(r => r.Context.Repository));
        Assert.Equal(["/srv/repos/repo", clones.PathOf(IntakeRig.Repo), clones.PathOf(IntakeRig.Repo)], rig.Heads.FetchedInto);
        Assert.Contains(rig.Infos, i => i.Contains("intake: octo/repo: /srv/repos/repo cannot be written", StringComparison.Ordinal));
        Assert.Empty(rig.Warnings);
    }

    [Fact]
    public async Task A_pull_request_whose_head_the_host_job_already_fetched_into_the_read_only_checkout_is_reviewed_there()
    {
        using var dir = new TempDir();
        using var rig = new IntakeRig(Reviewing(), clones: Clones(dir));
        rig.Heads.ReadOnly["/srv/repos/repo"] = new ReadOnlyCheckoutException("cannot open '.git/FETCH_HEAD': Read-only file system", Url, Sha);
        rig.OpenPull(5, Sha);
        await rig.Poll();

        Assert.Equal(new RepositoryRef("/srv/repos/repo", Sha), Assert.Single(rig.Starter.Started).Context.Repository);
        Assert.Equal(["/srv/repos/repo"], rig.Heads.FetchedInto);
    }

    [Fact]
    public async Task A_pull_request_whose_read_only_checkout_has_an_old_head_and_whose_remote_cannot_be_cloned_is_not_reviewed_and_says_why()
    {
        using var dir = new TempDir();
        using var rig = new IntakeRig(Reviewing(), clones: Clones(dir, cloned: false));
        rig.Heads.ReadOnly["/srv/repos/repo"] = new ReadOnlyCheckoutException("cannot open '.git/FETCH_HEAD': Read-only file system", "git@github.example.test:octo/repo.git", Old);
        rig.OpenPull(5, Sha);
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        Assert.Contains(rig.Warnings, w => w.Contains("octo/repo#5: review not started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scheduled_run_on_a_read_only_checkout_starts_from_the_managed_clone_at_a_fresh_commit()
    {
        using var dir = new TempDir();
        var clones = Clones(dir);
        using var rig = new ScheduleRig(ScheduleRig.Docs());
        rig.UseClones(clones);
        rig.Heads.ReadOnly["/srv/repos/repo"] = new ReadOnlyCheckoutException("cannot open '.git/FETCH_HEAD': Read-only file system", Url, Old);
        await rig.Tick();
        await rig.Wake(TimeSpan.FromHours(4));

        var request = Assert.Single(rig.Starter.Started);
        Assert.Equal(new RepositoryRef(clones.PathOf(IntakeRig.Repo), rig.Heads.Commit), request.Context.Repository);
        Assert.Equal(["/srv/repos/repo", clones.PathOf(IntakeRig.Repo)], rig.Heads.Asked);
    }

    [Fact]
    public async Task A_plane_item_on_a_read_only_checkout_starts_from_the_managed_clone_at_a_fresh_commit()
    {
        using var dir = new TempDir();
        var clones = Clones(dir);
        using var rig = new PlaneRig();
        rig.UseClones(clones);
        rig.Heads.ReadOnly["/srv/repos/repo"] = new ReadOnlyCheckoutException("cannot open '.git/FETCH_HEAD': Read-only file system", Url, Old);
        rig.Item(7);
        await rig.Poll();

        Assert.Equal(new RepositoryRef(clones.PathOf(IntakeRig.Repo), rig.Heads.Commit), Assert.Single(rig.Starter.Started).Context.Repository);
        Assert.Equal(["/srv/repos/repo", clones.PathOf(IntakeRig.Repo)], rig.Heads.Asked);
    }

    [Fact]
    public void The_clones_are_among_the_repository_roots_when_only_schedules_or_plane_intake_is_configured()
    {
        using var dir = new TempDir();
        var profile = Profile.Load(dir.Write("local.json", """{"schema":"profile/v1","worker_root":"/var/tmp/ch","repository_roots":["/srv/repos"]}"""));

        Assert.Equal(["/srv/repos"], profile.Roots);
        foreach (var automation in new[] { new AutomationSettings(Schedules: new AutomationSchedules()), new AutomationSettings(Plane: new AutomationPlane()) })
            Assert.Equal(["/srv/repos", Path.Combine("/var/tmp/ch", "repos")], (profile with { Automation = automation }).Roots);
    }

    [Fact]
    public void GitHub_intake_puts_its_clones_among_the_repository_roots_without_discover()
    {
        using var dir = new TempDir();
        var profile = Profile.Load(dir.Write("local.json", """
            {"schema":"profile/v1","worker_root":"/var/tmp/ch","repository_roots":["/srv/repos"],
             "driven":{"enabled":true,"integrator":{"app_id":1,"private_key_secret":"app-key"}},
             "automation":{"github":{"repositories":{"octo/repo":{"path":"/srv/repos/repo"}},"allowed_authors":["alice"]}}}
            """));

        Assert.Equal(["/srv/repos", Path.Combine("/var/tmp/ch", "repos")], profile.Roots);
    }

    private static string Git(string directory, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
        return output.Trim();
    }
}
