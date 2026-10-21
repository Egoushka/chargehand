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
