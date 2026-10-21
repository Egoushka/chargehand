using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Tests;

/// <summary>This repository's own rules for a driven session's check gate (<see cref="SessionChecks.RulesFile"/>): they parse, pass on the tree as it is, and fail on a file over the budget.</summary>
public class RepositoryChecksTests
{
    private static IReadOnlyList<string> Rules() => SessionChecks.ParseRules(File.ReadAllText(Path.Combine(Repo.Root, SessionChecks.RulesFile)));

    [Fact]
    public async Task The_repositorys_rules_pass_on_its_own_tree()
    {
        Assert.Contains("scripts/check-file-lines.sh 400", Rules());
        var results = await SessionChecks.RunAllAsync(Rules(), null, Repo.Root, 1, [], default);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.Command}: {r.OutputTail}"));
    }

    [Fact]
    public async Task The_line_budget_rule_names_a_module_file_over_400_lines()
    {
        using var dir = new TempDir();
        dir.Write("scripts/check-file-lines.sh", File.ReadAllText(Path.Combine(Repo.Root, "scripts/check-file-lines.sh")));
        dir.Write("src/Modules/M/Long.cs", string.Concat(Enumerable.Repeat("// line\n", 401)));
        dir.Write("src/Modules/M/Short.cs", "// one\n");
        foreach (var folder in new[] { "src/Shared", "src/Runtimes", "src/Hosts" })
            Directory.CreateDirectory(Path.Combine(dir.Path, folder));
        var result = Assert.Single(await SessionChecks.RunAllAsync(["sh scripts/check-file-lines.sh 400"], null, dir.Path, 1, [], default));
        Assert.False(result.Passed);
        Assert.Contains("src/Modules/M/Long.cs: 401 lines", result.OutputTail);
        Assert.DoesNotContain("Short.cs", result.OutputTail);
    }
}
