using System.Text.Json;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-216: the conflict radar pairs open pull requests of one repository whose tasks changed the same paths.</summary>
public class ConsoleConflictsTests
{
    private static AccountSettings Alice() => new("alice", Driven: true, AllowServerCredential: true);

    private static SeedTask Changed(string task, int total, params string[] paths) => new(task, Artifacts:
        [ConsoleSeed.Inline("changes", new { @base = ConsoleRig.Commit, files_total = total, files = paths.Select(p => new { path = p, added = 1, removed = 0, binary = false }), diff_truncated = true })]);

    private static async Task OpenAsync(ConsoleRig rig, string batch, string? actor, int number, string repository, params string[] paths)
    {
        await ConsoleSeed.BatchAsync(rig.Log, batch, actor, ConsoleRig.Now.AddHours(-3), Changed("t1", paths.Length, paths));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned(batch, "t1", actor: actor, repository: repository, pr: ConsoleSeed.Pull(number, repository.Split('/')[1], repository.Split('/')[0])));
    }

    [Fact]
    public async Task Open_pull_requests_of_one_repository_that_change_the_same_paths_are_paired_and_the_rest_are_not()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        await OpenAsync(rig, "b-a", null, 1, "o/r", "src/A.cs", "src/B.cs");
        await OpenAsync(rig, "b-b", "alice", 2, "o/r", "src/B.cs", "docs/x.md", "src/A.cs");
        await OpenAsync(rig, "b-c", null, 3, "o/r", "src/C.cs");
        await OpenAsync(rig, "b-other", null, 4, "o/other", "src/B.cs");
        await OpenAsync(rig, "b-merged", null, 5, "o/r", "src/A.cs");
        await ConsoleSeed.OutcomeAsync(rig, ConsoleSeed.Learned("b-merged", "t1", pr: ConsoleSeed.Pull(5)), PrOutcomes.MergedUnchanged);
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-gone", "t1", pr: ConsoleSeed.Pull(6)));

        var body = await ConsoleRig.Get(rig.Owner, "/v1/pulls/conflicts");
        Assert.Equal(("conflicts/v1", 5), (body.GetProperty("contract_version").GetString(), body.GetProperty("open_pulls").GetInt32()));
        var conflict = Assert.Single(body.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(("o/r", 2), (conflict.GetProperty("repository").GetString(), conflict.GetProperty("files_shared").GetInt32()));
        Assert.Equal(["src/A.cs", "src/B.cs"], conflict.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal([1, 2], conflict.GetProperty("pulls").EnumerateArray().Select(p => p.GetProperty("number").GetInt32()).Order());
        Assert.True(conflict.GetProperty("pulls")[0].GetProperty("files_listed").GetBoolean());
        var unread = Assert.Single(body.GetProperty("unread").EnumerateArray());
        Assert.Equal(6, unread.GetProperty("number").GetInt32());

        using var alice = rig.As("alice");
        var own = await ConsoleRig.Get(alice, "/v1/pulls/conflicts");
        Assert.Equal(1, own.GetProperty("open_pulls").GetInt32());
        Assert.Empty(own.GetProperty("conflicts").EnumerateArray());
    }

    [Fact]
    public async Task A_pull_request_whose_result_kept_fewer_paths_than_it_changed_says_so()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await ConsoleSeed.BatchAsync(rig.Log, "b-big", null, ConsoleRig.Now.AddHours(-3), Changed("t1", 900, "src/A.cs"));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-big", "t1", pr: ConsoleSeed.Pull(1)));
        await OpenAsync(rig, "b-small", null, 2, "o/r", "src/A.cs");

        var pulls = (await ConsoleRig.Get(rig.Owner, "/v1/pulls/conflicts")).GetProperty("conflicts")[0].GetProperty("pulls").EnumerateArray().ToList();
        var big = pulls.Single(p => p.GetProperty("number").GetInt32() == 1);
        Assert.Equal((900, false), (big.GetProperty("files_total").GetInt32(), big.GetProperty("files_listed").GetBoolean()));
    }
}
