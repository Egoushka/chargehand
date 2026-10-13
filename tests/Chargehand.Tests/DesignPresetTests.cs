using System.Diagnostics;
using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-189: the read-only preset that drafts an ADR from the code at the pinned commit.</summary>
public class DesignPresetTests
{
    private const string AdrReply = """
        ```json
        {"status":"completed",
         "summary":"# NNNN. Greeting\n\n- Status: proposed\n- Date: YYYY-MM-DD\n\n## Context\n\nThe README greets (README.md:1).\n\n## Options\n\n1. Keep it.\n2. Change it.\n\n## Decision\n\nKeep it.\n\n## Consequences\n\nNone.\n\n## Reopen if\n\nThe greeting changes.",
         "claims":[{"text":"The README says hello.","evidence":["e1"],"confidence":0.9}],
         "evidence":[{"id":"e1","kind":"file","locator":"README.md:1"}],
         "artifacts":[],"open_questions":[],"confidence":0.8}
        ```
        """;

    private const string UnresolvedReply = """
        ```json
        {"status":"completed","summary":"# NNNN. Greeting",
         "claims":[{"text":"The README has a license section.","evidence":["e1"],"confidence":0.9}],
         "evidence":[{"id":"e1","kind":"file","locator":"README.md:99"}],
         "artifacts":[],"open_questions":[],"confidence":0.8}
        ```
        """;

    private static RunRequest Design(RepositoryRef repo) =>
        new("request/v1", "Draft an ADR: should the README greet by name?", new RequestContext(false, "design", Repository: repo));

    private static async Task<ResultContract> RunAsync(string reply, TempDir root, RepositoryRef repo) =>
        await Runs.Orchestrator(new ScriptedRuntime(reply), root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Design(repo), CancellationToken.None);

    [Fact]
    public void Is_read_only_and_answers_only()
    {
        var preset = Preset.Load(Repo.Path("presets"), "design");
        Assert.Equal(["answer"], preset.AllowedActions);
        var worker = preset.NodeKinds["worker"];
        var rules = worker.Permissions.Select(p => $"{p.Action} {p.Resource} {p.Effect}").ToList();
        Assert.Contains("edit * deny", rules);
        Assert.Contains("shell * deny", rules);
        Assert.Contains("subagent * deny", rules);
        Assert.Null(worker.Services);
        Assert.False(worker.Writes);
    }

    [Fact]
    public void The_prompt_asks_for_the_sections_of_the_repositorys_ADR_template()
    {
        var headings = Regex.Matches(File.ReadAllText(Repo.Path("docs", "adr", "template.md")), @"(?m)^## (.+)$").Select(m => m.Groups[1].Value).ToList();
        var prompt = File.ReadAllText(Repo.Path("prompts", "preset", "design.md"));
        Assert.NotEmpty(headings);
        Assert.All(headings, h => Assert.Contains($"\"## {h}\"", prompt));
        Assert.Contains("Status", prompt);
        Assert.Contains("Date", prompt);
    }

    [Fact]
    public async Task The_summary_is_the_draft_its_facts_are_claims_that_resolve_and_nothing_is_written()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);

        var r = await RunAsync(AdrReply, root, repo);

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.StartsWith("# NNNN.", r.Summary, StringComparison.Ordinal);
        Assert.Contains("## Options", r.Summary, StringComparison.Ordinal);
        Assert.Single(r.Claims);
        Assert.Contains(r.Evidence, e => (e.Kind, e.Locator) == (EvidenceKind.File, "README.md:1"));
        Assert.DoesNotContain(r.OpenQuestions, q => q.Contains("Unverified:", StringComparison.Ordinal));
        Assert.Empty(Status(repo.Path));
    }

    [Fact]
    public async Task A_claim_whose_citation_does_not_resolve_leaves_the_claims()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);

        var r = await RunAsync(UnresolvedReply, root, repo);

        Assert.Empty(r.Claims);
        Assert.Contains(r.OpenQuestions, q => q.Contains("Unverified:", StringComparison.Ordinal));
    }

    private static string Status(string dir)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true };
        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add("--porcelain");
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
