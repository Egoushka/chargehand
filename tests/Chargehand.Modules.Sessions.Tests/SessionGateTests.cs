using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Sessions.Api;
using static Chargehand.Modules.Sessions.Tests.SessionDriverTests;

namespace Chargehand.Modules.Sessions.Tests;

/// <summary>The check gate: a finished session's branch runs the repository's checks and the task's acceptance criteria, and a failure goes back to the session as its next turn.</summary>
public class SessionGateTests
{
    private static readonly string Report = "```json\n" + """{"summary":"s","claims":[]}""" + "\n```";

    /// <summary>A Claude Code stand-in: a first run emits a finished session; a resumed run (<c>--resume</c>) saves the prompt it got, runs <paramref name="onResume"/> and finishes again.
    /// Every run is counted in <c>runs</c>.</summary>
    private static string Script(Workspace w, string onResume = "", string first = "") => $"""
        echo x >> '{w.Out}/runs'
        case " $* " in
          *" --resume "*)
            printf '%s' "$2" > '{w.Out}/feedback.txt'
            {onResume}
        {Emit(Assistant("m2", "fixed\n" + Report), Result())}
            ;;
          *)
            {first}
        {Emit(Init, Assistant("m1", "done\n" + Report), Result())}
            ;;
        esac
        """;

    private static void CommitRules(Workspace w, string rules)
    {
        Directory.CreateDirectory(Path.Combine(w.Work, ".chargehand"));
        File.WriteAllText(Path.Combine(w.Work, SessionChecks.RulesFile), rules);
        w.Git("add", "-A");
        w.Git("commit", "-q", "-m", "rules");
    }

    private static int Runs(Workspace w) => File.ReadAllLines(Path.Combine(w.Out, "runs")).Length;

    [Fact]
    public async Task A_branch_that_passes_the_repository_rules_completes_with_the_rules_listed()
    {
        using var w = new Workspace();
        CommitRules(w, "# the repository's own rule\ntest -f a.txt\n");
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w), Task()), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        var check = Assert.Single(outcome.Checks!);
        Assert.Equal(("rule", "test -f a.txt", true, 1), (check.Kind, check.Command, check.Passed, check.Attempt));
        Assert.Equal(1, Runs(w));
    }

    [Fact]
    public async Task The_declared_check_runs_first_in_the_gate_as_the_driver_s_own_run()
    {
        using var w = new Workspace();
        CommitRules(w, "test -f a.txt\n");
        var task = Task() with { Check = ["sh", "-c", "echo 'the check ran' && test -f a.txt"] };
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w), task), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(["check", "rule"], outcome.Checks!.Select(c => c.Kind));
        var check = outcome.Checks![0];
        Assert.Equal(("sh -c 'echo '\\''the check ran'\\'' && test -f a.txt'", true), (check.Command, check.Passed));
        Assert.Contains("the check ran", check.OutputTail);
    }

    [Fact]
    public async Task A_failing_declared_check_goes_back_to_the_session_like_a_rule()
    {
        using var w = new Workspace();
        var task = Task() with { Check = ["sh", "-c", "test -f fixed.txt"] };
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w, onResume: "touch fixed.txt && git add -A && git commit -q -m 'fix: the check'"), task), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(2, Runs(w));
        Assert.Contains("## check: `sh -c 'test -f fixed.txt'`", File.ReadAllText(Path.Combine(w.Out, "feedback.txt")));
    }

    [Fact]
    public async Task A_failed_criterion_goes_back_to_the_session_which_fixes_it_and_completes()
    {
        using var w = new Workspace();
        var task = Task() with { Acceptance = [new AcceptanceCheck("cat done.txt", Output: "ready")] };
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w, onResume: "echo ready > done.txt && git add -A && git commit -q -m 'fix: the criterion'"), task), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(2, Runs(w));
        var feedback = File.ReadAllText(Path.Combine(w.Out, "feedback.txt"));
        Assert.Contains("`cat done.txt`", feedback);
        Assert.Contains("output containing `ready`", feedback);
        Assert.Contains("done.txt", feedback);
        var check = Assert.Single(outcome.Checks!);
        Assert.Equal(("acceptance", true, 2, 0), (check.Kind, check.Passed, check.Attempt, check.ExitCode));
        // Both processes' results count: tokens and dollars are the session's, not the last process's.
        Assert.Equal(300, outcome.InputTokens);
        Assert.Equal(0.24m, outcome.CostUsd);
    }

    [Fact]
    public async Task A_branch_that_keeps_failing_ends_checks_failed_after_its_retries()
    {
        using var w = new Workspace();
        var task = Task() with { Acceptance = [new AcceptanceCheck("echo nope; exit 3")], CheckRetries = 1 };
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w), task), default);
        Assert.Equal((SessionStatus.Failed, SessionDriver.ChecksFailed), (outcome.Status, outcome.Reason));
        Assert.Equal(2, Runs(w));
        var check = Assert.Single(outcome.Checks!);
        Assert.Equal((false, 3, 2), (check.Passed, check.ExitCode, check.Attempt));
        Assert.Contains("nope", check.OutputTail);
        using var written = JsonDocument.Parse(File.ReadAllText(Path.Combine(w.Out, "session-outcome.json")));
        Assert.False(written.RootElement.GetProperty("checks")[0].GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task The_rules_are_the_base_commits_so_the_session_cannot_weaken_them()
    {
        using var w = new Workspace();
        CommitRules(w, "false\n");
        var weaken = $"echo true > {SessionChecks.RulesFile}; git add -A; git commit -q -m weaken";
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w, first: weaken), Task() with { CheckRetries = 0 }), default);
        Assert.Equal(SessionDriver.ChecksFailed, outcome.Reason);
        Assert.Equal("false", Assert.Single(outcome.Checks!).Command);
    }

    [Fact]
    public async Task A_branch_that_edits_ci_configuration_goes_back_to_the_session_which_reverts_it_and_completes()
    {
        using var w = new Workspace();
        var first = "mkdir -p .github/workflows && echo bad > .github/workflows/ci.yml && git add -A && git commit -q -m 'feat: add a workflow'";
        var onResume = "git rm -q -r .github/workflows && git commit -q -m 'revert: drop the workflow'";
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w, onResume: onResume, first: first), Task()), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(2, Runs(w));
        var feedback = File.ReadAllText(Path.Combine(w.Out, "feedback.txt"));
        Assert.Contains("## ci: `.github/workflows/ci.yml`", feedback);
        Assert.Contains("CI configuration", feedback);
    }

    [Fact]
    public async Task A_branch_that_keeps_editing_ci_configuration_ends_checks_failed_after_its_retries()
    {
        using var w = new Workspace();
        var first = "mkdir -p .github/workflows && echo bad > .github/workflows/ci.yml && git add -A && git commit -q -m 'feat: add a workflow'";
        var task = Task() with { CheckRetries = 0 };
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w, first: first), task), default);
        Assert.Equal((SessionStatus.Failed, SessionDriver.ChecksFailed), (outcome.Status, outcome.Reason));
        Assert.Equal(1, Runs(w));
        var check = Assert.Single(outcome.Checks!);
        Assert.Equal(("ci", false), (check.Kind, check.Passed));
        Assert.Contains(".github/workflows/ci.yml", check.Command);
    }

    [Fact]
    public async Task A_ci_edit_that_was_never_committed_is_still_caught_before_the_driver_s_own_commit()
    {
        using var w = new Workspace();
        var first = "mkdir -p .github/workflows && echo bad > .github/workflows/ci.yml";
        var onResume = "rm -rf .github";
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w, onResume: onResume, first: first), Task()), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(2, Runs(w));
        Assert.Contains(".github/workflows/ci.yml", File.ReadAllText(Path.Combine(w.Out, "feedback.txt")));
    }

    [Fact]
    public async Task Without_rules_or_criteria_no_check_runs()
    {
        using var w = new Workspace();
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w), Task()), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Null(outcome.Checks);
        Assert.Equal(1, Runs(w));
    }

    /// <summary>A session that finishes with prose only; a resumed run saves its prompt and answers with <paramref name="onResume"/> (a report, or more prose).</summary>
    private static string ProseOnly(Workspace w, string onResume) => $"""
        echo x >> '{w.Out}/runs'
        case " $* " in
          *" --resume "*)
            printf '%s' "$2" > '{w.Out}/feedback.txt'
        {onResume}
            ;;
          *)
        {Emit(Init, Assistant("m1", "All done, the change is committed."), Result())}
            ;;
        esac
        """;

    [Fact]
    public async Task A_finished_session_with_no_report_is_asked_once_for_the_report_and_completes_with_it()
    {
        using var w = new Workspace();
        var outcome = await SessionDriver.RunAsync(Options(w, ProseOnly(w, Emit(Assistant("m2", "here\n" + Report), Result())), Task()), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(2, Runs(w));
        Assert.Contains("no JSON report", File.ReadAllText(Path.Combine(w.Out, "feedback.txt")));
        using var written = JsonDocument.Parse(File.ReadAllText(Path.Combine(w.Out, "driven-report.json")));
        Assert.Equal("s", written.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task A_session_that_still_prints_no_report_is_not_asked_again()
    {
        using var w = new Workspace();
        var outcome = await SessionDriver.RunAsync(Options(w, ProseOnly(w, Emit(Assistant("m2", "sorry, no report"), Result())), Task()), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(2, Runs(w));
        Assert.False(File.Exists(Path.Combine(w.Out, "driven-report.json")));
    }

    [Fact]
    public async Task A_report_without_a_summary_counts_as_no_report()
    {
        using var w = new Workspace();
        var first = Emit(Init, Assistant("m1", "done\n```json\n{\"claims\":[]}\n```"), Result());
        var script = $"""
            echo x >> '{w.Out}/runs'
            case " $* " in
              *" --resume "*)
            {Emit(Assistant("m2", Report), Result())}
                ;;
              *)
            {first}
                ;;
            esac
            """;
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task()), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(2, Runs(w));
    }

    [Fact]
    public async Task A_session_that_asks_a_question_is_not_asked_for_a_report()
    {
        using var w = new Workspace();
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init, Assistant("m1", "NEEDS_INPUT:\n1. Which database?"), Result()) + $"\necho x >> '{w.Out}/runs'", Task()), default);
        Assert.Equal(SessionStatus.NeedsInput, outcome.Status);
        Assert.Equal(1, Runs(w));
    }

    [Fact]
    public async Task A_finished_session_that_left_its_change_uncommitted_is_asked_once_to_commit_it()
    {
        using var w = new Workspace();
        var script = Script(w, onResume: "git add -A && git commit -q -m 'fix: the change'", first: "echo edited > b.txt");
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task()), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(2, Runs(w));
        Assert.Contains("not committed", File.ReadAllText(Path.Combine(w.Out, "feedback.txt")));
        Assert.Equal("edited", w.Git("show", "chargehand/run1:b.txt").Trim());
        Assert.Equal(1, outcome.Commits);
    }

    [Fact]
    public async Task A_continuation_that_left_its_change_uncommitted_is_asked_although_its_seed_has_commits()
    {
        using var w = new Workspace(commit: false);
        w.Git("checkout", "-q", "-b", "chargehand/run1");
        File.WriteAllText(Path.Combine(w.Work, "b.txt"), "from the first session");
        w.Git("add", "-A");
        w.Git("commit", "-q", "-m", "WIP: session stopped by turn_limit after 80 turns");
        var seed = Path.Combine(w.Dir.Path, "seed.bundle");
        w.Git("bundle", "create", seed, "chargehand/run1", "--not", "main");
        w.Git("checkout", "-q", "-B", "chargehand/run1", "main");
        File.Copy(seed, Path.Combine(w.Out, "seed.bundle"));
        var script = Script(w, onResume: "git add -A && git commit -q -m 'fix: finish it'", first: "echo finished > b.txt");
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task()), default);
        Assert.Equal(2, Runs(w));
        Assert.Equal("finished", w.Git("show", "chargehand/run1:b.txt").Trim());
        Assert.Equal(2, outcome.Commits);
    }

    [Fact]
    public async Task A_session_that_committed_its_change_is_not_asked_about_files_it_left()
    {
        using var w = new Workspace();
        var first = "echo c > c.txt && git add -A && git commit -q -m 'feat: c' && echo tmp > scratch.txt";
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w, first: first), Task()), default);
        Assert.Equal(1, Runs(w));
        Assert.Equal("feat: c", w.Git("log", "-1", "--format=%s").Trim());
        Assert.Contains("scratch.txt", w.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task A_session_that_still_does_not_commit_is_not_asked_again()
    {
        using var w = new Workspace();
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w, first: "echo edited > b.txt"), Task()), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(2, Runs(w));
        Assert.Equal(0, outcome.Commits);
    }

    [Fact]
    public async Task A_check_output_is_redacted_before_it_is_stored_or_sent_back()
    {
        using var w = new Workspace();
        var task = Task() with { Acceptance = [new AcceptanceCheck($"echo {Canary}; exit 1")], CheckRetries = 1 };
        var outcome = await SessionDriver.RunAsync(Options(w, Script(w), task), default);
        Assert.DoesNotContain(Canary, Assert.Single(outcome.Checks!).OutputTail);
        Assert.DoesNotContain(Canary, File.ReadAllText(Path.Combine(w.Out, "feedback.txt")));
        Assert.DoesNotContain(Canary, File.ReadAllText(Path.Combine(w.Out, "session-outcome.json")));
    }

    [Fact]
    public void Rules_skip_blank_lines_and_comments()
    {
        Assert.Equal(["dotnet test", "./lint.sh"], SessionChecks.ParseRules("# rules\n\n  dotnet test  \r\n./lint.sh\n# end"));
    }

    [Fact]
    public void The_resumed_command_line_carries_the_feedback_and_the_session_id()
    {
        var args = SessionDriver.ClaudeArgs(Task(), "/opt/plugin", "/tmp/mcp.json", "/tmp/prompt.md", "/tmp/settings.json", ("cc-1", "fix the check"));
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
        Assert.Equal("fix the check", After("-p"));
        Assert.Equal("cc-1", After("--resume"));
        Assert.Equal("/tmp/settings.json", After("--settings"));
    }
}
