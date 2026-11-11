using Chargehand.Config;
using Chargehand.Evals;

namespace Chargehand.Tests;

/// <summary>Token-saving levers over the eval cells (issue #780).</summary>
public class LeverBenchTests
{
    private static readonly EvalCell Cell = new("cheap/worker", "d", "worker", "cheap", ["prompts/core/worker.md", "presets/cheap.yaml"], 0.10, 0.30, MinItems: 2);

    [Fact]
    public void The_shipped_levers_each_build_a_root_whose_presets_and_prompts_still_load()
    {
        using var dir = new TempDir();
        var levers = Lever.Load(Repo.Path("evals", "levers.json"));
        Assert.Equal(["prompt", "effort", "compaction"], levers.Select(l => l.Kind).ToArray());
        foreach (var lever in levers)
        {
            var root = LeverBench.Apply(lever, Repo.Root, Path.Combine(dir.Path, lever.Id), Repo.Path("evals"));
            var cheap = Preset.Load(Path.Combine(root, "presets"), "cheap").NodeKinds["worker"];
            var worker = new Chargehand.Modules.Research.Api.PromptRegistry(Path.Combine(root, "prompts")).Get("core/worker");
            switch (lever.Kind)
            {
                case "prompt":
                    Assert.StartsWith("Worker node.", worker.Text, StringComparison.Ordinal);
                    Assert.Equal("0.3.0", worker.Version); // the base's front matter stays
                    break;
                case "effort":
                    Assert.Equal("provider/small-model#low", cheap.Model);
                    Assert.Contains("model: provider/critic-model }", File.ReadAllText(Path.Combine(root, "presets", "strict.yaml")), StringComparison.Ordinal);
                    break;
                case "compaction":
                    Assert.Equal(30000, cheap.Compaction!.TriggerTokens);
                    break;
            }
            foreach (var preset in Directory.EnumerateFiles(Path.Combine(root, "presets"), "*.yaml"))
                Preset.Load(Path.Combine(root, "presets"), Path.GetFileNameWithoutExtension(preset));
        }
    }

    [Fact]
    public void A_row_reports_tokens_and_pass_rate_of_both_arms_and_keeps_a_lever_that_saves_without_a_block()
    {
        Pair P(double b, double c, long bt, long ct) => new("i", b, c, 0.01m, 0.01m, bt, ct);
        var row = LeverBench.Row(Cell, "effort-low", [P(0.8, 0.8, 1000, 600), P(0.9, 0.7, 1000, 700), P(0.5, 0.75, 1000, 500)], 0.7);

        Assert.Equal(3000, row.BaseTokens);
        Assert.Equal(1800, row.LeverTokens);
        Assert.Equal(-0.4, row.TokenChange!.Value, 6);
        Assert.Equal(2 / 3.0, row.BasePass, 6);
        Assert.Equal(1.0, row.LeverPass, 6);
        Assert.True(row.Keep, row.Verdict!.Reason);
        Assert.Contains("| cheap/worker | cheap | effort-low | 3 | 3000 → 1800 | -40% | 67% → 100% |", LeverBench.Table([row]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_lever_that_saves_tokens_but_drops_quality_beyond_the_gate_is_not_kept()
    {
        var pairs = Enumerable.Range(0, 8).Select(i => new Pair($"i{i}", 0.9, 0.4 + (i % 2 * 0.02), 0.01m, 0.005m, 1000, 500)).ToList();
        var row = LeverBench.Row(Cell, "terse", pairs, 0.7);
        Assert.True(row.Verdict!.Blocked);
        Assert.False(row.Keep);
    }

    [Fact]
    public async Task A_lever_that_changes_none_of_a_cells_files_is_skipped_without_a_run()
    {
        using var dir = new TempDir();
        var runner = new EvalRunner(_ => throw new InvalidOperationException("a skipped lever runs nothing"), null!, null, null!, null, () => { }, TextWriter.Null);
        var draft = Cell with { Name = "draft/draft", Preset = "draft", Files = ["prompts/core/draft.md"] };
        var rows = await LeverBench.RunAsync(runner, [draft], [new Lever("c", "compaction", Value: "30000")], Repo.Path("evals"),
            _ => throw new InvalidOperationException("no items read"), Repo.Root, dir.Path, "t", 0.7, CancellationToken.None);
        Assert.StartsWith("changes none of", Assert.Single(rows).Skipped, StringComparison.Ordinal);
    }
}
