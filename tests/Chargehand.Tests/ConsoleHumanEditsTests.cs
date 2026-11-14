using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary>Issue #455: the console's human-fix heatmap, built on issue #452's classification. Which files and rule categories humans fix most after a chargehand pull request, per repository.</summary>
public class ConsoleHumanEditsTests
{
    private static Task EditAsync(ConsoleRig rig, string runId, string sha, string category, params string[] paths) =>
        rig.Driven.HumanEdits!.AppendAsync(new HumanEditCommit(ConsoleRig.Now.AddHours(-1), runId, $"https://example.test/o/r/pull/1", sha, category, $"fix {category}", paths), default);

    [Fact]
    public async Task Files_and_categories_are_grouped_per_repository_most_touched_file_first()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await ConsoleSeed.BatchAsync(rig, "b-1", null, new SeedTask("t1"));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-1", "t1", repository: "o/r", pr: ConsoleSeed.Pull(1)));
        await ConsoleSeed.BatchAsync(rig, "b-2", null, new SeedTask("t1"));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-2", "t1", repository: "o/other", pr: ConsoleSeed.Pull(2, "other")));

        await EditAsync(rig, "b-1-t1", "sha1", HumanEditCategory.Lint, "src/A.cs", "src/B.cs");
        await EditAsync(rig, "b-1-t1", "sha2", HumanEditCategory.Lint, "src/A.cs");
        await EditAsync(rig, "b-1-t1", "sha3", HumanEditCategory.MissingImport, "src/A.cs");
        await EditAsync(rig, "b-2-t1", "sha4", HumanEditCategory.CiConfig, "src/C.cs");

        var body = await ConsoleRig.Get(rig.Owner, "/v1/human-edits");
        Assert.Equal("human-edits/v1", body.GetProperty("contract_version").GetString());
        var repos = body.GetProperty("repositories").EnumerateArray().ToList();
        Assert.Equal(["o/other", "o/r"], repos.Select(r => r.GetProperty("repository").GetString()));

        var r = repos[1];
        Assert.Equal(["src/A.cs", "src/B.cs"], r.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal([HumanEditCategory.Lint, HumanEditCategory.MissingImport, HumanEditCategory.UnboundedInput, HumanEditCategory.CiConfig, HumanEditCategory.Other],
            r.GetProperty("categories").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(3, r.GetProperty("commits").GetInt32());
        var cells = r.GetProperty("cells").EnumerateArray()
            .Select(c => (c.GetProperty("file").GetString(), c.GetProperty("category").GetString(), c.GetProperty("count").GetInt32())).ToList();
        Assert.Contains(("src/A.cs", HumanEditCategory.Lint, 2), cells);
        Assert.Contains(("src/B.cs", HumanEditCategory.Lint, 1), cells);
        Assert.Contains(("src/A.cs", HumanEditCategory.MissingImport, 1), cells);

        var other = repos[0];
        Assert.Equal(["src/C.cs"], other.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(1, other.GetProperty("commits").GetInt32());
    }

    [Fact]
    public async Task A_commit_whose_run_has_no_learning_record_is_left_out()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await EditAsync(rig, "no-such-run", "sha1", HumanEditCategory.Lint, "src/A.cs");

        var body = await ConsoleRig.Get(rig.Owner, "/v1/human-edits");
        Assert.Empty(body.GetProperty("repositories").EnumerateArray());
    }
}
