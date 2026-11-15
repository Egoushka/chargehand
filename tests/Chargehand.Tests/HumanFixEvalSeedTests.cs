using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Evals;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary>Issue #453: each commit issue #452 classified becomes an eval item for the cell whose step should have caught it, so
/// review/worker, code/writer and driven/session stop running with 0 items.</summary>
public class HumanFixEvalSeedTests
{
    private static readonly IReadOnlyList<EvalCell> Cells = EvalCell.Load(Repo.Path("evals", "cells.json"));

    private static HumanEditCommit Commit(string category, string message = "a fix") =>
        new(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), "run-1", "https://github.com/Egoushka/chargehand/pull/1",
            new string('a', 40), category, message);

    [Fact]
    public void A_lint_commit_seeds_a_code_writer_item()
    {
        var item = HumanFixEvalSeed.Seed(Cells, [Commit(HumanEditCategory.Lint)], "/srv/chargehand/work/chargehand").Single();
        Assert.Equal("code", item.Request.Context.Preset);
        Assert.NotEmpty(item.Expected.Facts!);
        Assert.Equal("human-fix", item.Metadata!["source"]);
        Assert.Equal(HumanEditCategory.Lint, item.Metadata!["category"]);
    }

    [Fact]
    public void A_missing_import_commit_seeds_a_code_writer_item()
    {
        var item = HumanFixEvalSeed.Seed(Cells, [Commit(HumanEditCategory.MissingImport)], "/srv/chargehand/work/chargehand").Single();
        Assert.Equal("code", item.Request.Context.Preset);
    }

    [Fact]
    public void An_unbounded_input_commit_seeds_a_review_worker_item()
    {
        var item = HumanFixEvalSeed.Seed(Cells, [Commit(HumanEditCategory.UnboundedInput)], "/srv/chargehand/work/chargehand").Single();
        Assert.Equal("review", item.Request.Context.Preset);
    }

    [Fact]
    public void A_ci_config_commit_seeds_a_driven_session_item()
    {
        var item = HumanFixEvalSeed.Seed(Cells, [Commit(HumanEditCategory.CiConfig)], "/srv/chargehand/work/chargehand").Single();
        Assert.Equal("driven", item.Request.Context.Preset);
    }

    [Fact]
    public void An_other_commit_seeds_nothing() =>
        Assert.Empty(HumanFixEvalSeed.Seed(Cells, [Commit(HumanEditCategory.Other)], "/srv/chargehand/work/chargehand"));

    [Fact]
    public void Each_seeded_item_carries_the_commit_as_an_input_and_cites_its_pull_request()
    {
        var commit = Commit(HumanEditCategory.Lint, "style: fix lint warnings");
        var item = HumanFixEvalSeed.Seed(Cells, [commit], "/srv/chargehand/work/chargehand").Single();
        var input = Assert.Single(item.Request.Inputs!);
        Assert.Equal(commit.Message, input.Text);
        Assert.Equal(commit.PrUrl, input.SourceUrl);
        Assert.Equal($"human-fix-{commit.Sha}", item.Id);
        Assert.Equal(commit.RunId, item.Metadata!["run_id"]);
        Assert.Equal(commit.PrUrl, item.Metadata!["pr_url"]);
    }

    [Fact]
    public void A_seeded_items_request_validates_as_request_v1()
    {
        foreach (var category in new[] { HumanEditCategory.Lint, HumanEditCategory.MissingImport, HumanEditCategory.UnboundedInput, HumanEditCategory.CiConfig })
        {
            var item = HumanFixEvalSeed.Seed(Cells, [Commit(category)], "/srv/chargehand/work/chargehand").Single();
            Assert.Empty(ContractSchemas.Validate(ContractSchemas.Request, JsonSerializer.SerializeToElement(item.Request, ContractJson.Options)));
        }
    }

    [Fact]
    public void A_cell_missing_from_the_caller_s_cells_seeds_nothing_for_its_category() =>
        Assert.Empty(HumanFixEvalSeed.Seed([], [Commit(HumanEditCategory.Lint)], "/srv/chargehand/work/chargehand"));
}
