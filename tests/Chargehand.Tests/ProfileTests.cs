using Chargehand.Config;

namespace Chargehand.Tests;

/// <summary>Profile is fully optional (ADR 0026): Load defaults an absent file, and secrets try an ordered list.</summary>
public class ProfileTests
{
    [Fact]
    public void Load_returns_defaults_when_the_file_is_absent()
    {
        using var dir = new TempDir();

        var profile = Profile.Load(System.IO.Path.Combine(dir.Path, "missing.json"));

        Assert.Equal("profile/v1", profile.Schema);
        Assert.Equal(Profile.DefaultWorkerRoot, profile.WorkerRoot);
        Assert.Equal("cheap", profile.DefaultPreset);
        Assert.Null(profile.IntakeModel);
        Assert.Null(profile.Prices);
        Assert.Null(profile.Secrets);
        Assert.Equal(1.00m, profile.RunCapUsd);
    }

    [Fact]
    public void Default_worker_root_is_outside_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(Profile.DefaultWorkerRoot.StartsWith(home, StringComparison.Ordinal));
    }

    [Fact]
    public void A_profile_file_may_omit_the_now_optional_fields()
    {
        using var dir = new TempDir();
        var path = dir.Write("local.json", """{"schema":"profile/v1","opencode":{"url":"http://127.0.0.1:1","password_secret":"p","version":"1"}}""");

        var profile = Profile.Load(path);

        Assert.Equal(Profile.DefaultWorkerRoot, profile.WorkerRoot);
        Assert.Equal("cheap", profile.DefaultPreset);
        Assert.Null(profile.IntakeModel);
    }

    [Fact]
    public void With_no_repository_roots_the_cli_allows_its_launch_directory()
    {
        var profile = new Profile("profile/v1").WithLaunchDirectory("/launch/dir");

        Assert.Equal([Profile.DefaultWorkerRoot, "/launch/dir"], profile.Roots);
    }

    [Fact]
    public void Explicit_repository_roots_win_over_the_launch_directory()
    {
        var profile = new Profile("profile/v1", RepositoryRoots: ["/repos"]).WithLaunchDirectory("/launch/dir");

        Assert.Equal(["/repos"], profile.Roots);
    }

    [Fact]
    public void With_no_secrets_configured_env_is_the_only_source()
    {
        var profile = new Profile("profile/v1");
        const string item = "profile-tests-only-env";
        var name = item.ToUpperInvariant().Replace('-', '_');
        Environment.SetEnvironmentVariable(name, "s3cr3t");
        try
        {
            Assert.Equal("s3cr3t", profile.Secret(item));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Secret_sources_are_tried_in_order_first_success_wins()
    {
        const string item = "profile-tests-command-item";
        var profile = new Profile("profile/v1", Secrets:
        [
            new SecretSource(Env: true),
            new SecretSource(Command: ["sh", "-c", "printf %s from-command-$0", "{item}"]),
        ]);

        // The env source is tried first and finds nothing (the var is unset), so the command source wins.
        Assert.Equal($"from-command-{item}", profile.Secret(item));
    }

    [Fact]
    public void Secret_command_runs_as_argv_no_shell()
    {
        var profile = new Profile("profile/v1", Secrets: [new SecretSource(Command: ["printf", "%s", "{item}-value"])]);

        Assert.Equal("db-password-value", profile.Secret("db-password"));
    }

    [Fact]
    public void Secret_throws_when_every_source_fails()
    {
        var profile = new Profile("profile/v1", Secrets: [new SecretSource(Command: ["sh", "-c", "exit 1"])]);

        Assert.Throws<InvalidOperationException>(() => profile.Secret("whatever"));
    }
}
