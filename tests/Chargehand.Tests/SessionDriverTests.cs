using System.Diagnostics;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>The in-container driver (ADR 0039): runs headless Claude Code, ends it when it stalls, and leaves a bundle, a report and an outcome.</summary>
public class SessionDriverTests
{
    private const string Canary = "canary-secret-value-123";
    private static readonly string AwsKey = "AKIA" + "IOSFODNN7EXAMPLE";

    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static SessionTask Task(int noProgressSeconds = 60, long? maxTokens = null) =>
        new("run1", "Add a retry to the fetch client", "chargehand/run1", MaxTurns: 80, MaxMinutes: 5, NoProgressSeconds: noProgressSeconds, MaxTokens: maxTokens);

    private sealed class Workspace : IDisposable
    {
        public TempDir Dir { get; } = new();
        public string Work => Path.Combine(Dir.Path, "work");
        public string Out => Path.Combine(Dir.Path, "out");

        public Workspace(bool commit = true)
        {
            Directory.CreateDirectory(Work);
            Directory.CreateDirectory(Out);
            Git("init", "-q", "-b", "main");
            Git("config", "user.email", "t@example.test");
            Git("config", "user.name", "t");
            File.WriteAllText(Path.Combine(Work, "a.txt"), "a");
            Git("add", "-A");
            Git("commit", "-q", "-m", "base");
            if (commit)
            {
                Git("checkout", "-q", "-b", "chargehand/run1");
                File.WriteAllText(Path.Combine(Work, "b.txt"), "b");
                Git("add", "-A");
                Git("commit", "-q", "-m", "feat: the change");
            }
        }

        public string Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = Work, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
            return output;
        }

        public void Dispose() => Dir.Dispose();
    }

    private static SessionOptions Options(Workspace w, string script, SessionTask task, string[]? secrets = null) =>
        new(w.Work, w.Out, task, new SessionCommand("/bin/sh", [w.Dir.Write("claude.sh", script)]), secrets ?? [Canary], PromptText: "system prompt", McpUrl: null, McpToken: null);

    private const string Init = """{"type":"system","subtype":"init","session_id":"s"}""";

    private static string Assistant(string id, string text, string usage = """{"input_tokens":100,"output_tokens":20}""") =>
        JsonSerializer.Serialize(new { type = "assistant", message = new { id, content = new[] { new { type = "text", text } }, usage = JsonDocument.Parse(usage).RootElement } });

    private static string Result(string text = "done") =>
        JsonSerializer.Serialize(new
        {
            type = "result",
            is_error = false,
            result = text,
            total_cost_usd = 0.12,
            usage = new { input_tokens = 150, output_tokens = 40, cache_read_input_tokens = 500 },
        });

    private static string Emit(params string[] lines) => string.Join("\n", lines.Select(l => $"cat <<'EOF_LINE'\n{l}\nEOF_LINE"));

    [Fact]
    public async Task A_finished_session_leaves_a_bundle_a_report_and_a_completed_outcome()
    {
        using var w = new Workspace();
        const string report = """{"summary":"added a retry","claims":[{"text":"the client retries","evidence":["src/a.cs:1-3"]}]}""";
        var script = Emit(Init, Assistant("m1", "working"), Assistant("m2", "All done.\n```json\n" + report + "\n```"), Result());
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task()), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(2, outcome.Turns);
        Assert.Equal(150, outcome.InputTokens);
        Assert.Equal(40, outcome.OutputTokens);
        Assert.Equal(0.12m, outcome.CostUsd);
        Assert.True(outcome.BundleWritten);
        w.Git("bundle", "verify", Path.Combine(w.Out, "chargehand.bundle"));
        Assert.Contains("chargehand/run1", w.Git("bundle", "list-heads", Path.Combine(w.Out, "chargehand.bundle")));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(w.Out, "driven-report.json")));
        Assert.Equal("added a retry", doc.RootElement.GetProperty("summary").GetString());
        Assert.True(File.Exists(Path.Combine(w.Out, "session-outcome.json")));
        Assert.Contains("\"type\":\"result\"", File.ReadAllText(Path.Combine(w.Out, "stream.jsonl")));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(w.Out, "stream.jsonl")))), outcome.StreamSha256);
    }

    [Fact]
    public async Task The_outcome_names_the_session_its_model_and_the_tokens_of_each_model_it_called()
    {
        using var w = new Workspace();
        var result = JsonSerializer.Serialize(new
        {
            type = "result",
            session_id = "cc-1",
            is_error = false,
            result = "done",
            total_cost_usd = 0.05,
            usage = new { input_tokens = 150, output_tokens = 40, cache_read_input_tokens = 500, cache_creation_input_tokens = 70 },
            modelUsage = new Dictionary<string, object>
            {
                ["claude-sonnet-5-5"] = new { inputTokens = 120, outputTokens = 30, cacheReadInputTokens = 500, cacheCreationInputTokens = 70, costUSD = 0.04 },
                ["claude-haiku-5-5"] = new { inputTokens = 30, outputTokens = 10, cacheReadInputTokens = 0, cacheCreationInputTokens = 0, costUSD = 0.01 },
            },
        });
        var outcome = await SessionDriver.RunAsync(Options(w, Emit("""{"type":"system","subtype":"init","session_id":"cc-1","model":"claude-sonnet-5-5"}""", Assistant("m1", "hi"), result), Task()), default);

        Assert.Equal(("cc-1", "claude-sonnet-5-5", 70L), (outcome.SessionId, outcome.Model, outcome.CacheWriteTokens));
        Assert.Equal(new TokenCounts(120, 30, 0, 500, 70), outcome.ModelUsage!["claude-sonnet-5-5"]);
        Assert.Equal(new TokenCounts(30, 10, 0, 0, 0), outcome.ModelUsage["claude-haiku-5-5"]);
        // The host reads the outcome from the volume: the new fields survive the file, model ids as written.
        var read = JsonSerializer.Deserialize<SessionOutcome>(File.ReadAllText(Path.Combine(w.Out, "session-outcome.json")), Snake)!;
        Assert.Equal(outcome.ModelUsage["claude-haiku-5-5"], read.ModelUsage!["claude-haiku-5-5"]);
        Assert.Equal("cc-1", read.SessionId);
    }

    [Fact]
    public async Task A_session_that_prints_nothing_is_stopped_as_stalled_and_leaves_no_process()
    {
        using var w = new Workspace();
        var sw = Stopwatch.StartNew();
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init) + "\nsleep 60", Task(noProgressSeconds: 1)), default);
        Assert.Equal(SessionStatus.Stalled, outcome.Status);
        Assert.Equal("no_progress", outcome.Reason);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
        Assert.True(outcome.BundleWritten, "work so far is bundled for the handover to decide on");
    }

    [Fact]
    public async Task Needs_input_ends_the_session_with_the_questions()
    {
        using var w = new Workspace();
        var script = Emit(Init, Assistant("m1", "I cannot continue.\nNEEDS_INPUT:\n1. Which database?\n2. Keep the old API?"), Result("stopped"));
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task()), default);
        Assert.Equal(SessionStatus.NeedsInput, outcome.Status);
        Assert.Equal(["Which database?", "Keep the old API?"], outcome.Questions);
    }

    [Fact]
    public async Task A_secret_never_reaches_the_stored_stream_or_the_outcome()
    {
        using var w = new Workspace();
        var script = Emit(Init, Assistant("m1", $"the token is {Canary} oops"), Result($"and again {Canary}"));
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task()), default);
        foreach (var file in Directory.GetFiles(w.Out))
            Assert.DoesNotContain(Canary, File.ReadAllText(file));
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Contains("[redacted]", File.ReadAllText(Path.Combine(w.Out, "stream.jsonl")));
    }

    [Fact]
    public async Task A_nonzero_exit_with_no_result_is_failed()
    {
        using var w = new Workspace();
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init) + "\necho boom >&2\nexit 3", Task()), default);
        Assert.Equal(SessionStatus.Failed, outcome.Status);
        Assert.Equal(3, outcome.ExitCode);
    }

    [Fact]
    public async Task No_branch_means_no_bundle()
    {
        using var w = new Workspace(commit: false);
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init, Assistant("m1", "hi"), Result()), Task()), default);
        Assert.False(outcome.BundleWritten);
        Assert.False(File.Exists(Path.Combine(w.Out, "chargehand.bundle")));
    }

    [Fact]
    public async Task A_stopped_session_has_its_uncommitted_files_committed_to_the_branch_before_it_is_bundled()
    {
        using var w = new Workspace();
        File.WriteAllText(Path.Combine(w.Work, "b.txt"), "changed");
        File.WriteAllText(Path.Combine(w.Work, "new.txt"), "untracked");
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init) + "\nsleep 60", Task(noProgressSeconds: 1)), default);

        Assert.Equal("no_progress", outcome.Reason);
        Assert.Equal("WIP: session stopped by no_progress after 0 turns", w.Git("log", "-1", "--format=%s").Trim());
        Assert.Equal("", w.Git("status", "--porcelain"));
        Assert.Equal("changed", w.Git("show", "chargehand/run1:b.txt"));
        Assert.Equal("untracked", w.Git("show", "chargehand/run1:new.txt"));
        Assert.True(outcome.BundleWritten);
        Assert.Equal(1, outcome.Commits);
    }

    [Fact]
    public async Task A_stop_with_a_clean_tree_adds_no_commit_and_a_branch_the_session_never_made_is_created_for_the_files()
    {
        using var clean = new Workspace();
        var cleanOutcome = await SessionDriver.RunAsync(Options(clean, Emit(Init) + "\nsleep 60", Task(noProgressSeconds: 1)), default);
        Assert.Equal("feat: the change", clean.Git("log", "-1", "--format=%s").Trim());
        Assert.Equal(0, cleanOutcome.Commits);

        using var bare = new Workspace(commit: false);
        File.WriteAllText(Path.Combine(bare.Work, "late.txt"), "written, never committed");
        var outcome = await SessionDriver.RunAsync(Options(bare, Emit(Init) + "\nsleep 60", Task(noProgressSeconds: 1)), default);
        Assert.True(outcome.BundleWritten);
        Assert.Equal("written, never committed", bare.Git("show", "chargehand/run1:late.txt"));
    }

    [Fact]
    public async Task Commits_are_counted_from_the_commit_the_workspace_started_at_not_from_what_a_remote_has()
    {
        using var w = new Workspace(commit: false);
        // The base is on no remote here, as when the pinned commit is on no branch: `--not --remotes` would count it, and the session's own commit, as two.
        var commit = "git checkout -q -b chargehand/run1 && echo b > b.txt && git add -A && git -c user.name=t -c user.email=t@example.test commit -q -m feat\n";
        var outcome = await SessionDriver.RunAsync(Options(w, commit + Emit(Init, Assistant("m1", "done"), Result()), Task()), default);
        Assert.Equal(1, outcome.Commits);

        using var seeded = new Workspace(commit: false);
        seeded.Git("checkout", "-q", "-b", "chargehand/run1");
        File.WriteAllText(Path.Combine(seeded.Work, "b.txt"), "b");
        seeded.Git("add", "-A");
        seeded.Git("commit", "-q", "-m", "earlier session");
        var seed = Path.Combine(seeded.Dir.Path, "seed.bundle");
        seeded.Git("bundle", "create", seed, "chargehand/run1", "--not", "main");
        seeded.Git("checkout", "-q", "-B", "chargehand/run1", "main");
        File.Copy(seed, Path.Combine(seeded.Out, "seed.bundle"));
        var next = await SessionDriver.RunAsync(Options(seeded, Emit(Init, Assistant("m1", "done"), Result()), Task()), default);
        Assert.Equal(1, next.Commits);
    }

    [Fact]
    public async Task Files_over_the_size_limit_stay_out_of_the_snapshot_and_the_commit_names_them()
    {
        using var w = new Workspace();
        File.WriteAllBytes(Path.Combine(w.Work, "big.bin"), new byte[(int)SessionDriver.SnapshotMaxFileBytes + 1]);
        File.WriteAllText(Path.Combine(w.Work, "small.txt"), "kept");
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init) + "\nsleep 60", Task(noProgressSeconds: 1)), default);

        Assert.Equal("kept", w.Git("show", "chargehand/run1:small.txt"));
        Assert.DoesNotContain("big.bin", w.Git("ls-tree", "-r", "--name-only", "chargehand/run1"));
        Assert.Contains("big.bin", w.Git("log", "-1", "--format=%B"));
        Assert.Equal("not committed (over 5 MiB): big.bin", outcome.SnapshotNote);
    }

    [Fact]
    public async Task A_stale_index_lock_is_removed_and_a_live_one_skips_the_snapshot_with_a_note()
    {
        using var stale = new Workspace();
        File.WriteAllText(Path.Combine(stale.Work, "late.txt"), "written");
        File.WriteAllText(Path.Combine(stale.Work, ".git", "index.lock"), "");
        var cleared = await SessionDriver.RunAsync(Options(stale, Emit(Init) + "\nsleep 60", Task(noProgressSeconds: 1)) with { GitRunning = () => false }, default);
        Assert.False(File.Exists(Path.Combine(stale.Work, ".git", "index.lock")));
        Assert.Equal("written", stale.Git("show", "chargehand/run1:late.txt"));
        Assert.Null(cleared.SnapshotNote);

        using var live = new Workspace();
        File.WriteAllText(Path.Combine(live.Work, "late.txt"), "written");
        File.WriteAllText(Path.Combine(live.Work, ".git", "index.lock"), "");
        var skipped = await SessionDriver.RunAsync(Options(live, Emit(Init) + "\nsleep 60", Task(noProgressSeconds: 1)) with { GitRunning = () => true }, default);
        Assert.True(File.Exists(Path.Combine(live.Work, ".git", "index.lock")));
        Assert.Equal("feat: the change", live.Git("log", "-1", "--format=%s").Trim());
        Assert.Contains("index.lock exists and a git process is running", skipped.SnapshotNote);
        Assert.True(skipped.BundleWritten, "the commits the session made are still bundled");
    }

    [Fact]
    public async Task A_git_that_hangs_skips_the_snapshot_instead_of_hanging_the_session()
    {
        using var w = new Workspace();
        File.WriteAllText(Path.Combine(w.Work, "late.txt"), "written");
        var sw = Stopwatch.StartNew();
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init) + "\nsleep 60", Task(noProgressSeconds: 1)) with { GitTimeout = TimeSpan.FromMilliseconds(1) }, default);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
        Assert.Contains("timed out", outcome.SnapshotNote);
        Assert.Equal("feat: the change", w.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task A_session_that_finishes_on_its_own_gets_no_snapshot_commit()
    {
        using var w = new Workspace();
        File.WriteAllText(Path.Combine(w.Work, "scratch.txt"), "left over");
        await SessionDriver.RunAsync(Options(w, Emit(Init, Assistant("m1", "done"), Result()), Task()), default);
        Assert.Equal("feat: the change", w.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task A_continuation_starts_from_the_seed_bundle_of_the_previous_session()
    {
        // The previous session left a WIP commit on the branch and bundled it; the new workspace is the base on a fresh branch, as the workspace helper makes it.
        using var w = new Workspace(commit: false);
        w.Git("checkout", "-q", "-b", "chargehand/run1");
        File.WriteAllText(Path.Combine(w.Work, "b.txt"), "b");
        File.WriteAllText(Path.Combine(w.Work, "c.txt"), "from the first session");
        w.Git("add", "-A");
        w.Git("commit", "-q", "-m", "WIP: session stopped by turn_limit after 80 turns");
        var seed = Path.Combine(w.Dir.Path, "seed.bundle");
        w.Git("bundle", "create", seed, "chargehand/run1", "--not", "main");
        w.Git("checkout", "-q", "-B", "chargehand/run1", "main");
        Assert.False(File.Exists(Path.Combine(w.Work, "c.txt")));
        File.Copy(seed, Path.Combine(w.Out, "seed.bundle"));

        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init, Assistant("m1", "continuing"), Result()), Task()), default);

        Assert.True(File.Exists(Path.Combine(w.Work, "c.txt")));
        Assert.True(File.Exists(Path.Combine(w.Work, "b.txt")));
        Assert.Equal("WIP: session stopped by turn_limit after 80 turns", w.Git("log", "-1", "--format=%s").Trim());
        Assert.True(outcome.BundleWritten);
    }

    [Fact]
    public async Task Passing_the_token_cap_stops_the_session()
    {
        using var w = new Workspace();
        var script = Emit(Init, Assistant("m1", "big", """{"input_tokens":900,"output_tokens":300}""")) + "\nsleep 60";
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task(maxTokens: 1000)), default);
        Assert.Equal(SessionStatus.TokenCap, outcome.Status);
        Assert.True(outcome.InputTokens + outcome.OutputTokens >= 1000);
    }

    [Fact]
    public async Task The_running_token_tally_is_in_the_usage_file_while_the_session_works()
    {
        using var w = new Workspace();
        var seen = await UsageSeen(w, Task(), "tokens.: 350", Assistant("m1", "step", """{"input_tokens":300,"output_tokens":50}"""));
        Assert.Equal(350, seen.GetProperty("tokens").GetInt64());
    }

    [Fact]
    public async Task The_usage_file_also_says_how_many_turns_and_which_step_the_session_is_on()
    {
        using var w = new Workspace();
        var edit = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { id = "m2", content = new object[] { new { type = "tool_use", id = "tu1", name = "Edit", input = new { file_path = "/work/a.txt" } } }, usage = new { input_tokens = 10, output_tokens = 5 } },
        });
        // The stage, not the bare word: "write" is also in the file's cache_write key from the first write on.
        var seen = await UsageSeen(w, Task(), "stage.: .write", Assistant("m1", "looking"), edit);
        Assert.Equal(135, seen.GetProperty("tokens").GetInt64());
        Assert.Equal(2, seen.GetProperty("turns").GetInt32());
        Assert.Equal("write", seen.GetProperty("stage").GetString());
    }

    private static readonly IReadOnlyDictionary<string, ModelPrice> Prices = new Dictionary<string, ModelPrice> { ["provider/claude-sonnet-5-5"] = new(3m, 15m, 0.3m, 3.75m) };

    private static string Sonnet(string id, string usage) =>
        JsonSerializer.Serialize(new { type = "assistant", message = new { id, model = "claude-sonnet-5-5", content = new[] { new { type = "text", text = "step" } }, usage = JsonDocument.Parse(usage).RootElement } });

    /// <summary>Runs a session that holds after its messages until the usage file shows <paramref name="until"/>, copies the file, then ends with a result.</summary>
    private static async Task<JsonElement> UsageSeen(Workspace w, SessionTask task, string until, params string[] messages)
    {
        var usage = Path.Combine(w.Out, "session-usage.json");
        var script = Emit([Init, .. messages]) + $"\nfor i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do grep -q '{until}' '{usage}' 2>/dev/null && break; sleep 0.2; done\n"
            + $"cp '{usage}' '{Path.Combine(w.Dir.Path, "seen.json")}'\n" + Emit(Result());
        await SessionDriver.RunAsync(Options(w, script, task), default);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(w.Dir.Path, "seen.json"))).RootElement.Clone();
    }

    [Fact]
    public async Task The_usage_file_counts_cache_per_message_and_prices_it_by_model_before_the_result_arrives()
    {
        using var w = new Workspace();
        var seen = await UsageSeen(w, Task() with { Prices = Prices }, "usd_estimate.: 0.04",
            Sonnet("m1", """{"input_tokens":1000,"output_tokens":100,"cache_read_input_tokens":60000,"cache_creation_input_tokens":2000}"""),
            Sonnet("m2", """{"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":40000}"""));
        Assert.Equal(1100, seen.GetProperty("tokens").GetInt64());
        Assert.Equal(100_000, seen.GetProperty("cache_read").GetInt64());
        Assert.Equal(2000, seen.GetProperty("cache_write").GetInt64());
        // 1000 in at 3, 100 out at 15, 100000 cache reads at 0.3, 2000 cache writes at 3.75, per million.
        Assert.Equal(0.042m, seen.GetProperty("usd_estimate").GetDecimal());
    }

    [Fact]
    public async Task Without_prices_or_with_an_unpriced_model_the_estimate_is_null_not_zero()
    {
        using var w = new Workspace();
        var message = Sonnet("m1", """{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":700}""");
        var unpriced = await UsageSeen(w, Task(), "cache_read.: 700", message);
        Assert.Equal(700, unpriced.GetProperty("cache_read").GetInt64());
        Assert.Equal(JsonValueKind.Null, unpriced.GetProperty("usd_estimate").ValueKind);
        using var other = new Workspace();
        var unknown = await UsageSeen(other, Task() with { Prices = new Dictionary<string, ModelPrice> { ["p/other"] = new(1m, 1m, 1m, 1m) } }, "cache_read.: 700", message);
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("usd_estimate").ValueKind);
    }

    [Fact]
    public async Task The_outcome_keeps_the_estimate_and_the_per_message_cache_when_the_session_never_printed_a_result()
    {
        using var w = new Workspace();
        var script = Emit(Init, Sonnet("m1", """{"input_tokens":1000,"output_tokens":100,"cache_read_input_tokens":60000}""")) + "\nexit 1";
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task() with { Prices = Prices }), default);
        Assert.Equal((60_000L, 0.0225m), (outcome.CacheReadTokens, outcome.UsdEstimate));
    }

    [Fact]
    public async Task A_result_that_says_usage_limit_reached_ends_the_session_rate_limited_with_its_reset_time()
    {
        using var w = new Workspace();
        var result = JsonSerializer.Serialize(new { type = "result", is_error = true, result = "Claude AI usage limit reached|1760281200" });
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init, Assistant("m1", "hi"), result), Task()), default);
        Assert.Equal((SessionStatus.RateLimited, "rate_limited"), (outcome.Status, outcome.Reason));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760281200), outcome.RateLimitResetsAt);
        var read = JsonSerializer.Deserialize<SessionOutcome>(File.ReadAllText(Path.Combine(w.Out, "session-outcome.json")), Snake)!;
        Assert.Equal(outcome.RateLimitResetsAt, read.RateLimitResetsAt);
    }

    [Fact]
    public async Task A_result_with_a_429_error_is_rate_limited_without_a_reset_time_and_other_errors_still_fail()
    {
        using var w = new Workspace();
        var limited = JsonSerializer.Serialize(new { type = "result", is_error = true, result = """API Error: 429 {"type":"error","error":{"type":"rate_limit_error","message":"This request would exceed your rate limit."}}""" });
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init, Assistant("m1", "hi"), limited), Task()), default);
        Assert.Equal(SessionStatus.RateLimited, outcome.Status);
        Assert.Null(outcome.RateLimitResetsAt);

        using var other = new Workspace();
        var broken = JsonSerializer.Serialize(new { type = "result", is_error = true, result = "API Error: 500 internal error" });
        var failed = await SessionDriver.RunAsync(Options(other, Emit(Init, Assistant("m1", "hi"), broken), Task()), default);
        Assert.Equal((SessionStatus.Failed, "session_error"), (failed.Status, failed.Reason));
    }

    [Theory]
    [InlineData("Claude AI usage limit reached|1760281200", true, 1760281200L)]
    [InlineData("5-hour usage limit reached", true, null)]
    [InlineData("API Error: 429 Too Many Requests", true, null)]
    [InlineData("""API Error: 429 {"type":"error"}""", true, null)]
    [InlineData("request failed with status 429", true, null)]
    [InlineData("HTTP 429", true, null)]
    [InlineData("Rate limit exceeded", true, null)]
    [InlineData("API Error: 500 internal error", false, null)]
    [InlineData("the tests exceed the limit of 5 seconds", false, null)]
    [InlineData("NullReferenceException at Retry.cs:line 429", false, null)]
    [InlineData("Build failed: src/Client.cs(429,13): error CS1002", false, null)]
    public void The_text_of_a_result_names_a_usage_or_rate_limit_and_maybe_when_it_lifts(string text, bool limited, long? resets)
    {
        Assert.Equal(limited, UsageLimit.Matches(text));
        Assert.Equal(resets is { } r ? DateTimeOffset.FromUnixTimeSeconds(r) : null, UsageLimit.ResetsAt(text));
    }

    [Fact]
    public void The_shared_rate_limit_check_does_not_learn_the_driven_only_usage_limit_wording()
    {
        Assert.False(ChargehandException.RateLimited("usage limit reached"));
        Assert.True(ChargehandException.RateLimited("Rate limit exceeded"));
    }

    [Fact]
    public async Task A_failed_session_whose_error_text_has_429_as_a_line_number_is_failed_not_rate_limited()
    {
        using var w = new Workspace();
        var result = JsonSerializer.Serialize(new { type = "result", is_error = true, result = "Error: NullReferenceException at Retry.cs:line 429" });
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init, Assistant("m1", "hi"), result), Task()), default);
        Assert.Equal(SessionStatus.Failed, outcome.Status);
    }

    [Fact]
    public async Task A_message_whose_model_has_no_price_makes_the_estimate_partial_not_null()
    {
        using var w = new Workspace();
        var other = JsonSerializer.Serialize(new { type = "assistant", message = new { id = "m2", model = "claude-fable-9", content = new[] { new { type = "text", text = "x" } }, usage = new { input_tokens = 500, output_tokens = 5 } } });
        var seen = await UsageSeen(w, Task() with { Prices = Prices }, "usd_estimate_partial.: true",
            Sonnet("m1", """{"input_tokens":1000,"output_tokens":100,"cache_read_input_tokens":60000,"cache_creation_input_tokens":2000}"""), other);
        Assert.Equal(0.03m, seen.GetProperty("usd_estimate").GetDecimal());     // m1 alone: 3000 + 1500 + 18000 + 7500 per million
        Assert.True(seen.GetProperty("usd_estimate_partial").GetBoolean());
    }

    [Fact]
    public void A_task_without_prices_leaves_them_out_of_task_json_so_an_older_session_image_still_parses_it()
    {
        var plain = JsonSerializer.Serialize(Task(), SessionCli.Json);
        Assert.DoesNotContain("prices", plain);
        var priced = JsonSerializer.Serialize(Task() with { Prices = Prices }, SessionCli.Json);
        Assert.Equal(Prices, JsonSerializer.Deserialize<SessionTask>(priced, SessionCli.Json)!.Prices);
        Assert.Contains("\"cache_read\":0.3", priced);
    }

    [Fact]
    public void The_claude_command_line_is_headless_bounded_and_carries_no_secret()
    {
        var args = SessionDriver.ClaudeArgs(Task(maxTokens: 500_000) with { Model = "claude-sonnet-5-5", MaxUsd = 2.5m }, "/opt/plugin", "/tmp/mcp.json", "/tmp/prompt.md");
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
        Assert.Contains("-p", args);
        Assert.StartsWith("/chargehand:change ", After("-p"));
        Assert.Contains("Add a retry to the fetch client", After("-p"));
        Assert.Equal("/opt/plugin", After("--plugin-dir"));
        Assert.Equal("dontAsk", After("--permission-mode"));
        Assert.Equal("stream-json", After("--output-format"));
        Assert.Contains("--verbose", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("/tmp/mcp.json", After("--mcp-config"));
        Assert.Equal("/tmp/prompt.md", After("--append-system-prompt-file"));
        Assert.Equal("claude-sonnet-5-5", After("--model"));
        Assert.Equal("2.5", After("--max-budget-usd"));
        Assert.Contains("mcp__chargehand__orchestrate", After("--allowedTools"));
        Assert.DoesNotContain("--bare", args);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.DoesNotContain(args, a => a.Contains("Bearer"));
    }

    [Fact]
    public void Without_a_dollar_cap_or_model_those_flags_are_absent()
    {
        var args = SessionDriver.ClaudeArgs(Task(), "/opt/plugin", "/tmp/mcp.json", "/tmp/prompt.md");
        Assert.DoesNotContain("--max-budget-usd", args);
        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public void A_goal_starting_with_a_slash_command_cannot_replace_the_skill()
    {
        var args = SessionDriver.ClaudeArgs(Task() with { Goal = "/login now" }, "/opt/plugin", "/tmp/mcp.json", "/tmp/prompt.md");
        Assert.StartsWith("/chargehand:change ", args[args.ToList().IndexOf("-p") + 1]);
    }

    [Fact]
    public void The_mcp_config_names_the_chargehand_server_over_http_with_the_run_token()
    {
        var json = SessionDriver.McpConfig("http://chargehand.internal:4300/v1/mcp", "run-token-1");
        using var doc = JsonDocument.Parse(json);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("chargehand");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://chargehand.internal:4300/v1/mcp", server.GetProperty("url").GetString());
        Assert.Equal("Bearer run-token-1", server.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    private static string ToolCall(string id, string command) =>
        JsonSerializer.Serialize(new { type = "assistant", message = new { id = $"m-{id}", content = new object[] { new { type = "tool_use", id, name = "Bash", input = new { command } } } } });

    private static string ToolResult(string id, string output) =>
        JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = id, content = output, is_error = true } } } });

    [Fact]
    public async Task A_governed_session_logs_its_decisions_redacted_and_acts_on_none_in_observe_mode()
    {
        using var w = new Workspace();
        var command = $"make CANARY={Canary}";
        var script = Emit([Init, .. Enumerable.Range(1, 4).SelectMany(i => new[] { ToolCall($"c{i}", command), ToolResult($"c{i}", "make: *** [all] Error 1") }), Result()]);
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task() with { Governor = new SessionGovernor(SizeClass: "M") }), default);

        Assert.Equal(SessionStatus.Completed, outcome.Status);
        var log = File.ReadAllText(Path.Combine(w.Out, SessionDriver.GovernorFile));
        Assert.DoesNotContain(Canary, log);
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // The third failure warns (repeated_failure); the fourth identical pair is a second signal, so a checkpoint.
        Assert.Equal(["warn", "checkpoint"], lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("action").GetString()));
        Assert.All(lines, l => Assert.False(JsonDocument.Parse(l).RootElement.GetProperty("acted").GetBoolean()));
        Assert.Equal(2, outcome.Governor!.Count);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(w.Out, SessionDriver.GovernorFile)))), outcome.GovernorLogSha256);
        Assert.Contains("\"governor_log_sha256\"", File.ReadAllText(Path.Combine(w.Out, "session-outcome.json")));
        Assert.False(File.Exists(Path.Combine(w.Out, "handoff.json")));       // nothing stopped it, so no handoff
    }

    [Fact]
    public async Task A_session_with_the_governor_off_or_absent_writes_no_log()
    {
        foreach (var governor in new[] { null, new SessionGovernor(GovernorMode.Off) })
        {
            using var w = new Workspace();
            var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init, ToolCall("c1", "make"), ToolResult("c1", "x"), Result()), Task() with { Governor = governor }), default);
            Assert.False(File.Exists(Path.Combine(w.Out, SessionDriver.GovernorFile)));
            Assert.Null(outcome.Governor);
            Assert.Null(outcome.GovernorLogSha256);
        }
    }

    private static string FailingFourTimes(string command) =>
        Emit([Init, .. Enumerable.Range(1, 4).SelectMany(i => new[] { ToolCall($"c{i}", command), ToolResult($"c{i}", "make: *** [all] Error 1") })]) + "\nsleep 60";

    private static List<JsonElement> GovernorLines(Workspace w) =>
        [.. File.ReadAllLines(Path.Combine(w.Out, SessionDriver.GovernorFile)).Select(l => JsonDocument.Parse(l).RootElement)];

    [Fact]
    public async Task In_enforce_mode_a_checkpoint_ends_the_session_as_checkpoint_stuck_and_only_that_decision_is_acted_on()
    {
        using var w = new Workspace();
        var sw = Stopwatch.StartNew();
        var outcome = await SessionDriver.RunAsync(Options(w, FailingFourTimes("make"), Task() with { Governor = new SessionGovernor(GovernorMode.Enforce, SizeClass: "M") }), default);

        Assert.Equal((SessionStatus.Stalled, "checkpoint_stuck"), (outcome.Status, outcome.Reason));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
        var lines = GovernorLines(w);
        Assert.Equal([("warn", false), ("checkpoint", true)], lines.Select(l => (l.GetProperty("action").GetString()!, l.GetProperty("acted").GetBoolean())));
        Assert.Equal("enforce", lines[1].GetProperty("mode").GetString());
        Assert.Equal([false, true], outcome.Governor!.Select(d => d.Acted));
        Assert.True(File.Exists(Path.Combine(w.Out, "handoff.json")));
        Assert.Contains("stopped by checkpoint stuck", File.ReadAllText(Path.Combine(w.Out, "progress.md")));
    }

    [Fact]
    public async Task In_enforce_mode_the_checkpoint_reason_follows_the_signal()
    {
        using var w = new Workspace();
        var script = Emit(Init, Sonnet("m1", """{"input_tokens":200000,"output_tokens":10}""")) + "\nsleep 60";
        var context = await SessionDriver.RunAsync(Options(w, script, Task() with { Governor = new SessionGovernor(GovernorMode.Enforce) }), default);
        Assert.Equal((SessionStatus.Stalled, "checkpoint_context"), (context.Status, context.Reason));

        using var spend = new Workspace();
        var priced = Emit(Init, Sonnet("m1", """{"input_tokens":10,"output_tokens":200000}""")) + "\nsleep 60";      // 3.00 dollar equivalent
        var outcome = await SessionDriver.RunAsync(Options(spend, priced, Task() with { Prices = Prices, Governor = new SessionGovernor(GovernorMode.Enforce, BudgetUsd: 3.3m) }), default);
        Assert.Equal((SessionStatus.Stalled, "checkpoint_spend"), (outcome.Status, outcome.Reason));
        Assert.Equal([("checkpoint", true)], GovernorLines(spend).Select(l => (l.GetProperty("action").GetString()!, l.GetProperty("acted").GetBoolean())));
    }

    [Fact]
    public async Task In_enforce_mode_a_spent_budget_ends_the_session_as_budget_spent_and_the_stop_outranks_the_checkpoint_beside_it()
    {
        using var w = new Workspace();
        var script = Emit(Init, Sonnet("m1", """{"input_tokens":10,"output_tokens":200000}""")) + "\nsleep 60";
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task() with { Prices = Prices, Governor = new SessionGovernor(GovernorMode.Enforce, BudgetUsd: 2m) }), default);

        Assert.Equal((SessionStatus.Stalled, "budget_spent"), (outcome.Status, outcome.Reason));
        Assert.True(outcome.BundleWritten);
        Assert.Equal([("checkpoint", false), ("stop", true)], GovernorLines(w).Select(l => (l.GetProperty("action").GetString()!, l.GetProperty("acted").GetBoolean())));
    }

    [Fact]
    public async Task A_stopped_session_leaves_a_handoff_built_from_git_and_the_stream_with_secrets_redacted_and_a_secret_looking_call_dropped()
    {
        using var w = new Workspace();
        File.WriteAllText(Path.Combine(w.Work, "new.txt"), "untracked");
        var todos = new { todos = new[] { new { content = "finish the retry", status = "in_progress", activeForm = "Finishing" } } };
        var todo = JsonSerializer.Serialize(new { type = "assistant", message = new { id = "m-todo", content = new object[] { new { type = "tool_use", id = "todo1", name = "TodoWrite", input = todos } } } });
        var script = Emit(Init, ToolCall("t1", "dotnet test"), ToolResult("t1", "Exit code 1\nFailed!"), ToolCall("c1", $"make CANARY={Canary}"), ToolResult("c1", "ok"),
            ToolCall("k1", $"echo {AwsKey}"), ToolResult("k1", "ok"), todo) + "\nsleep 60";
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task(noProgressSeconds: 1)), default);

        Assert.Equal("no_progress", outcome.Reason);
        var json = File.ReadAllText(Path.Combine(w.Out, "handoff.json"));
        var progress = File.ReadAllText(Path.Combine(w.Out, "progress.md"));
        Assert.DoesNotContain(Canary, json + progress);
        Assert.DoesNotContain(AwsKey, json + progress);
        var doc = JsonDocument.Parse(json).RootElement;
        Assert.Equal("no_progress", doc.GetProperty("reason").GetString());
        Assert.Equal(w.Git("rev-parse", "chargehand/run1").Trim(), doc.GetProperty("head_commit").GetString());
        Assert.Equal(w.Git("rev-parse", "HEAD~1").Trim(), doc.GetProperty("base_commit").GetString());
        Assert.Equal(["WIP: session stopped by no_progress after 4 turns"], doc.GetProperty("commits").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("new.txt", doc.GetProperty("changes")[0].GetProperty("path").GetString());
        Assert.Equal(("dotnet test", 1, true), (doc.GetProperty("last_test").GetProperty("command").GetString(), doc.GetProperty("last_test").GetProperty("exit_code").GetInt32(), doc.GetProperty("last_test").GetProperty("is_error").GetBoolean()));
        Assert.Equal("finish the retry", doc.GetProperty("todos")[0].GetProperty("text").GetString());
        Assert.Equal(["dotnet test", "make CANARY=[redacted]"], doc.GetProperty("calls").EnumerateArray().Where(c => c.GetProperty("name").GetString() == "Bash").Select(c => c.GetProperty("arguments").GetProperty("command").GetString()));
        Assert.Equal([("aws", "tool call 3")], doc.GetProperty("findings").EnumerateArray().Select(f => (f.GetProperty("kind").GetString()!, f.GetProperty("item").GetString()!)));
        Assert.Contains("- new.txt (+1 -0)", progress);
        Assert.Contains("matched a secret pattern", progress);
    }

    [Fact]
    public async Task Every_stop_the_driver_makes_writes_a_handoff_the_turn_limit_included()
    {
        using var w = new Workspace();
        var script = Emit(Init, Assistant("m1", "one"), Assistant("m2", "two"), Assistant("m3", "three")) + "\nsleep 60";
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task() with { MaxTurns = 1 }), default);

        Assert.Equal("turn_limit", outcome.Reason);
        Assert.Equal("turn_limit", JsonDocument.Parse(File.ReadAllText(Path.Combine(w.Out, "handoff.json"))).RootElement.GetProperty("reason").GetString());
        Assert.StartsWith("# Handoff from the previous session", File.ReadAllText(Path.Combine(w.Out, "progress.md")));
    }

    [Fact]
    public async Task A_session_that_finishes_on_its_own_leaves_no_handoff()
    {
        using var w = new Workspace();
        await SessionDriver.RunAsync(Options(w, Emit(Init, Assistant("m1", "done"), Result()), Task()), default);
        Assert.False(File.Exists(Path.Combine(w.Out, "handoff.json")));
        Assert.False(File.Exists(Path.Combine(w.Out, "progress.md")));
    }
}
