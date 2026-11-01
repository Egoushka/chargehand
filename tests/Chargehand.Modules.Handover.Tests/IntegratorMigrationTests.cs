using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Handover.Tests;

/// <summary>A pull request that adds <c>db/migrations/*.sql</c> is never merged or auto-merged, whatever the policy and however green its checks: it is marked ready, commented on once and left for the owner.</summary>
public class IntegratorMigrationTests
{
    private static readonly RemoteRepository Repo = new("o", "r");

    private static readonly CheckStatus[] Green = [new("ci-gate", true, CheckOutcome.Passed, 1)];

    private static Integrator Make(FakeIntegrationHost host) =>
        new(new IntegratorPolicy(new DrivenIntegrator(1, 2, "k", new Dictionary<string, string> { ["o/r"] = "merge" })), host, delay: (_, _) => Task.CompletedTask);

    private static IntegrationTarget Target() => new(Repo, 7, "chargehand/run1", false, "Add a column");

    private static FakeIntegrationHost Host(string statusAfterReady) =>
        new() { ChangedPaths = ["src/Store.cs", "db/migrations/0042_add_column.sql"], StatusAfterReady = statusAfterReady, Current = FakeIntegrationHost.Snapshot(checks: Green) };

    [Theory]
    [InlineData("CLEAN")]
    [InlineData("BLOCKED")]
    public async Task A_migration_with_green_checks_is_neither_merged_nor_set_to_auto_merge(string statusAfterReady)
    {
        var host = Host(statusAfterReady);

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.Asked, result.Action);
        Assert.Contains("db/migrations/0042_add_column.sql", result.Detail);
        Assert.Equal(["get", "ready", "get", "comment"], host.Calls);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("merge", StringComparison.Ordinal) || c.StartsWith("auto-merge", StringComparison.Ordinal));
        Assert.False(host.Current.Merged);
        Assert.False(host.Current.AutoMerge);
        Assert.Contains("migration", Assert.Single(host.Comments));
    }

    [Fact]
    public async Task The_migration_is_commented_once_across_repeated_passes()
    {
        var host = Host("CLEAN");

        await Make(host).IntegrateAsync(Target(), _ => { }, default);
        await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Single(host.Comments);
    }

    [Fact]
    public async Task A_pull_request_without_a_migration_still_merges()
    {
        var host = new FakeIntegrationHost { ChangedPaths = ["src/Store.cs", "docs/db/migrations.md", "db/seeds/0001.sql"], StatusAfterReady = "CLEAN" };

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.Merged, result.Action);
    }

    [Fact]
    public async Task A_pull_request_whose_files_cannot_be_read_is_not_merged()
    {
        var host = new FakeIntegrationHost { ChangedPathsFail = new IntegrationException("files unavailable"), StatusAfterReady = "CLEAN" };

        var result = await Make(host).IntegrateAsync(Target(), _ => { }, default);

        Assert.Equal(IntegrationAction.Failed, result.Action);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("merge", StringComparison.Ordinal) || c.StartsWith("auto-merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_migration_that_a_fix_pushed_after_the_checks_started_ends_the_watch_unmerged()
    {
        var host = new FakeIntegrationHost { Current = FakeIntegrationHost.Snapshot(draft: false, status: "CLEAN", checks: Green), ChangedPaths = ["db/migrations/0042_add_column.sql"] };

        var result = await Make(host).FollowAsync(Target(), (_, _) => throw new InvalidOperationException("no fix expected"), _ => { }, default);

        Assert.Equal(IntegrationAction.Asked, result.Action);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("merge", StringComparison.Ordinal) || c.StartsWith("auto-merge", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("db/migrations/0001_init.sql", true)]
    [InlineData("server/db/migrations/0001_init.SQL", true)]
    [InlineData("db/migrations/0001_init.down.sql", true)]
    [InlineData("db/migrations/README.md", false)]
    [InlineData("db/migrations.sql", false)]
    [InlineData("db/seeds/0001.sql", false)]
    [InlineData("migrations/0001.sql", false)]
    public void Only_a_sql_file_in_a_db_migrations_folder_is_a_migration(string path, bool expected) => Assert.Equal(expected, MigrationPaths.IsMigration(path));
}
