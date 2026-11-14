using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Oversight.Tests;

/// <summary>Issue #452: the four kinds of human fix the analysis of chargehand's own merged pull requests found (lint, missing imports, unbounded input, CI config), classified from
/// fixture commits alone, no forge and no checkout needed.</summary>
public class HumanEditClassifierTests
{
    [Theory]
    [InlineData("style: fix lint warnings")]
    [InlineData("chore: run dotnet format")]
    [InlineData("fix trailing whitespace")]
    [InlineData("remove unused using")]
    public void A_formatting_commit_message_is_lint(string message) => Assert.Equal(HumanEditCategory.Lint, HumanEditClassifier.Classify(message, ["src/Foo.cs"]));

    [Theory]
    [InlineData("fix: add missing using for System.Linq")]
    [InlineData("add missing import for the json namespace")]
    [InlineData("fix unresolved reference in RunService")]
    public void A_missing_import_commit_message_is_missing_import(string message) => Assert.Equal(HumanEditCategory.MissingImport, HumanEditClassifier.Classify(message, ["src/Foo.cs"]));

    [Theory]
    [InlineData("fix: bound the input before writing it to the log")]
    [InlineData("add a max length check for the uploaded body")]
    [InlineData("truncate the untrusted input before storing it")]
    public void An_unbounded_input_commit_message_is_unbounded_input(string message) => Assert.Equal(HumanEditCategory.UnboundedInput, HumanEditClassifier.Classify(message, ["src/Foo.cs"]));

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/actions/setup/action.yml")]
    [InlineData(".gitlab-ci.yml")]
    [InlineData("azure-pipelines.yml")]
    [InlineData(".circleci/config.yml")]
    [InlineData("Jenkinsfile")]
    public void A_changed_ci_path_is_ci_config_whatever_the_message(string path) => Assert.Equal(HumanEditCategory.CiConfig, HumanEditClassifier.Classify("fix the build", [path]));

    [Fact]
    public void A_ci_path_wins_over_a_lint_sounding_message() =>
        Assert.Equal(HumanEditCategory.CiConfig, HumanEditClassifier.Classify("style: format the workflow", [".github/workflows/ci.yml"]));

    [Fact]
    public void Classification_is_case_insensitive() => Assert.Equal(HumanEditCategory.Lint, HumanEditClassifier.Classify("STYLE: FIX LINT", ["src/Foo.cs"]));

    [Theory]
    [InlineData("rename a variable for clarity")]
    [InlineData("tidy up the comment")]
    [InlineData("")]
    public void An_unrecognised_commit_message_is_other(string message) => Assert.Equal(HumanEditCategory.Other, HumanEditClassifier.Classify(message, ["src/Foo.cs"]));
}
