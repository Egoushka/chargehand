using System.Text.Json;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-216: the merge train orders the open pull requests of each repository oldest first, names the earlier cars each shares paths with, and holds those never merged.</summary>
public class ConsoleTrainTests
{
    private static SeedTask Changed(params string[] paths) => new("t1", Artifacts:
        [ConsoleSeed.Inline("changes", new { @base = ConsoleRig.Commit, files_total = paths.Length, files = paths.Select(p => new { path = p, added = 1, removed = 0, binary = false }), diff_truncated = true })]);

    private static async Task OpenAsync(ConsoleRig rig, string batch, int number, int hoursAgo, string repository = "o/r", string outcome = LearningOutcome.Completed, bool integrity = false,
        params string[] paths)
    {
        await ConsoleSeed.BatchAsync(rig.Log, batch, null, ConsoleRig.Now.AddHours(-hoursAgo - 1), Changed(paths));
        var (owner, name) = (repository.Split('/')[0], repository.Split('/')[1]);
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned(batch, "t1", outcome, repository: repository, at: ConsoleRig.Now.AddHours(-hoursAgo), pr: ConsoleSeed.Pull(number, name, owner),
            integrityFlag: integrity));
    }

    [Fact]
    public async Task Each_repository_gets_a_train_oldest_first_with_the_earlier_cars_it_shares_paths_with()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await OpenAsync(rig, "b-3", 3, 1, paths: ["src/A.cs", "src/B.cs"]);
        await OpenAsync(rig, "b-1", 1, 3, paths: ["src/A.cs"]);
        await OpenAsync(rig, "b-2", 2, 2, paths: ["src/B.cs", "docs/x.md"]);
        await OpenAsync(rig, "b-other", 9, 5, repository: "o/other", paths: ["src/A.cs"]);
        await OpenAsync(rig, "b-partial", 4, 4, outcome: LearningOutcome.Partial, paths: ["src/A.cs"]);
        await OpenAsync(rig, "b-flagged", 5, 4, integrity: true, paths: ["tests/T.cs"]);

        var body = await ConsoleRig.Get(rig.Owner, "/v1/pulls/train");
        Assert.Equal("train/v1", body.GetProperty("contract_version").GetString());
        var trains = body.GetProperty("trains").EnumerateArray().ToList();
        Assert.Equal(["o/other", "o/r"], trains.Select(t => t.GetProperty("repository").GetString()));
        var cars = trains[1].GetProperty("cars").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], cars.Select(c => c.GetProperty("pull").GetProperty("number").GetInt32()));
        Assert.Equal([1, 2, 3], cars.Select(c => c.GetProperty("position").GetInt32()));
        Assert.Empty(cars[1].GetProperty("after").EnumerateArray());
        Assert.Equal([1, 2], cars[2].GetProperty("after").EnumerateArray().Select(a => a.GetInt32()));
        Assert.Equal(2, cars[2].GetProperty("files_shared_with_earlier").GetInt32());
        Assert.Equal("off", cars[0].GetProperty("integrator_mode").GetString());

        Assert.Equal([(4, "partial_draft"), (5, "test_integrity")], body.GetProperty("held").EnumerateArray()
            .Select(h => (h.GetProperty("pull").GetProperty("number").GetInt32(), h.GetProperty("reason").GetString()!)).OrderBy(h => h.Item1));
    }

    [Fact]
    public async Task A_merged_pull_request_leaves_the_train()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await OpenAsync(rig, "b-1", 1, 3, paths: ["src/A.cs"]);
        await OpenAsync(rig, "b-2", 2, 2, paths: ["src/A.cs"]);
        await ConsoleSeed.OutcomeAsync(rig, ConsoleSeed.Learned("b-1", "t1", pr: ConsoleSeed.Pull(1)), PrOutcomes.MergedUnchanged);

        var car = Assert.Single((await ConsoleRig.Get(rig.Owner, "/v1/pulls/train")).GetProperty("trains")[0].GetProperty("cars").EnumerateArray());
        Assert.Equal((2, 1), (car.GetProperty("pull").GetProperty("number").GetInt32(), car.GetProperty("position").GetInt32()));
        Assert.Empty(car.GetProperty("after").EnumerateArray());
    }
}
