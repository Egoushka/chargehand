using System.Text.Json;
using Chargehand.ClaudeCode;
using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>ADR 0098: how much of a subscription's usage windows a run had used, from the CLI's <c>rate_limit_event</c> to the run record and the run list. A share, never dollars,
/// and null, never zero, when the provider reported none.</summary>
public sealed class SubscriptionUsageTests : IDisposable
{
    private static readonly NodeSpec Spec = new("/w", "build", new ModelRef("anthropic", "claude-sonnet-5"), [], new Dictionary<string, string>());
    private static readonly DateTimeOffset FiveResets = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
    private static readonly DateTimeOffset SevenResets = DateTimeOffset.FromUnixTimeSeconds(1_790_400_000);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(0.2, 0.6, 0.6, "seven_day")]
    [InlineData(0.7, 0.3, 0.7, "five_hour")]
    [InlineData(0.5, 0.5, 0.5, "seven_day")]
    [InlineData(null, 0.4, 0.4, "seven_day")]
    [InlineData(0.4, null, 0.4, "five_hour")]
    public void Used_is_the_larger_window_and_the_weekly_one_wins_a_tie(double? five, double? seven, double used, string window)
    {
        var usage = SubscriptionUsage.Of(five is { } f ? new UsageWindow(f) : null, seven is { } s ? new UsageWindow(s) : null)!;
        Assert.Equal((used, window), (usage.Used, usage.Window));
    }

    [Fact]
    public void No_window_is_no_reading_and_not_a_zero()
    {
        Assert.Null(SubscriptionUsage.Of(null, null));
        Assert.Null(new SubscriptionUsage().Used);
        Assert.Null(new SubscriptionUsage().Window);
    }

    [Fact]
    public void A_newer_reading_is_laid_over_the_older_one_window_by_window()
    {
        var older = new SubscriptionUsage(new UsageWindow(0.3, FiveResets), new UsageWindow(0.5, SevenResets));
        var weekOnly = new SubscriptionUsage(null, new UsageWindow(0.6, SevenResets));

        Assert.Equal(new SubscriptionUsage(new UsageWindow(0.3, FiveResets), new UsageWindow(0.6, SevenResets)), SubscriptionUsage.Merge(older, weekOnly));
        Assert.Same(older, SubscriptionUsage.Merge(older, null));
        Assert.Equal(weekOnly, SubscriptionUsage.Merge(null, weekOnly));
        Assert.Null(SubscriptionUsage.Merge(null, null));
    }

    [Fact]
    public void Both_windows_are_read_with_their_reset_times()
    {
        var usage = Read("""
            {"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1790000000,"rateLimitType":"five_hour","utilization":0.42,
             "unifiedWindows":{"five_hour":{"utilization":0.42,"resetsAt":1790000000},"seven_day":{"utilization":0.7,"resetsAt":1790400000}}}}
            """);

        Assert.Equal(new SubscriptionUsage(new UsageWindow(0.42, FiveResets), new UsageWindow(0.7, SevenResets)), usage);
        Assert.Equal(("seven_day", 0.7), (usage!.Window, usage.Used));
    }

    [Theory]
    [InlineData("five_hour", true)]
    [InlineData("seven_day", false)]
    public void The_top_level_fields_stand_in_for_the_window_that_is_limiting(string type, bool five)
    {
        var usage = Read("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1790000000,"rateLimitType":"TYPE","utilization":0.55}}""".Replace("TYPE", type));

        var window = new UsageWindow(0.55, FiveResets);
        Assert.Equal(five ? new SubscriptionUsage(window, null) : new SubscriptionUsage(null, window), usage);
    }

    [Theory]
    [InlineData("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","rateLimitType":"seven_day_opus","utilization":0.9}}""")]
    [InlineData("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","utilization":0.9}}""")]
    [InlineData("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","rateLimitType":"five_hour"}}""")]
    [InlineData("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","unifiedWindows":{"seven_day_opus":{"utilization":0.9}}}}""")]
    [InlineData("""{"type":"rate_limit_event","rate_limit_info":"allowed"}""")]
    [InlineData("""{"type":"rate_limit_event"}""")]
    public void An_event_with_no_window_this_reads_is_no_reading(string json) => Assert.Null(Read(json));

    [Fact]
    public void A_utilization_that_is_not_a_share_is_dropped_window_by_window()
    {
        var onlyFive = Read("""{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":0.2},"seven_day":{"utilization":-0.1}}}}""");
        var none = Read("""{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":"0.2"},"seven_day":{"utilization":null}}}}""");

        Assert.Equal(new SubscriptionUsage(new UsageWindow(0.2), null), onlyFive);
        Assert.Null(none);
    }

    [Fact]
    public void A_utilization_past_one_is_kept_for_a_window_run_past_its_cap()
    {
        var usage = Read("""{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":1.03}}}}""");
        Assert.Equal((1.03, "five_hour"), (usage!.Used, usage.Window));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"soon\"")]
    [InlineData("9999999999999")]
    public void A_reset_time_that_is_no_date_drops_only_the_reset(string resetsAt)
    {
        var usage = Read("""{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":0.3,"resetsAt":RESETS}}}}""".Replace("RESETS", resetsAt));
        Assert.Equal(new SubscriptionUsage(new UsageWindow(0.3, null), null), usage);
    }

    [Fact]
    public void An_assistant_message_carries_the_reading_in_force_when_it_began()
    {
        var s = new ClaudeCodeWorkerRuntime.Session("s", Spec, null);
        Apply(s, Assistant("msg_1"));
        Apply(s, """{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":0.3,"resetsAt":1790000000}}}}""");
        Apply(s, Assistant("msg_2"));
        Apply(s, """{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"seven_day":{"utilization":0.5,"resetsAt":1790400000}}}}""");
        Apply(s, Assistant("msg_3"));

        Assert.Null(s.Messages[0].Subscription);
        Assert.Equal(new SubscriptionUsage(new UsageWindow(0.3, FiveResets), null), s.Messages[1].Subscription);
        // The weekly report does not drop the five-hour window the earlier one gave.
        Assert.Equal(new SubscriptionUsage(new UsageWindow(0.3, FiveResets), new UsageWindow(0.5, SevenResets)), s.Messages[2].Subscription);
    }

    [Fact]
    public void An_event_with_no_window_leaves_the_reading_alone_and_a_continued_message_keeps_its_stamp()
    {
        var s = new ClaudeCodeWorkerRuntime.Session("s", Spec, null);
        Apply(s, """{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":0.3}}}}""");
        Apply(s, Assistant("msg_1"));
        Apply(s, """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"}}""");
        Apply(s, Assistant("msg_1"));
        Apply(s, Assistant("msg_2"));

        var reading = new SubscriptionUsage(new UsageWindow(0.3), null);
        Assert.Equal([reading, reading], s.Messages.Select(m => m.Subscription));
    }

    [Fact]
    public void A_session_that_was_never_told_a_reading_stamps_none()
    {
        var s = new ClaudeCodeWorkerRuntime.Session("s", Spec, null);
        Apply(s, Assistant("msg_1"));
        Assert.Null(Assert.Single(s.Messages).Subscription);
    }

    [Fact]
    public void A_turn_on_the_fallback_key_carries_no_reading_and_a_stray_event_is_not_kept()
    {
        var s = new ClaudeCodeWorkerRuntime.Session("s", Spec, null) { OnFallback = true };
        Apply(s, """{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":0.9}}}}""");
        Apply(s, Assistant("msg_1"));

        Assert.Null(Assert.Single(s.Messages).Subscription);
        Assert.Null(s.Subscription);
    }

    [Fact]
    public async Task The_run_log_writes_the_reading_in_snake_case_and_reads_it_back()
    {
        var path = Path.Combine(_dir.Path, "log.jsonl");
        var log = new JsonlRunLog(path);
        var usage = new SubscriptionUsage(new UsageWindow(0.18, FiveResets), new UsageWindow(0.61, SevenResets));
        await log.AppendAsync(new RunRecord("r1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "draft", null, null, Runs.Completed() with { TaskId = "r1" }, Credential: "subscription", SubscriptionUsage: usage),
            CancellationToken.None);

        using var line = JsonDocument.Parse(File.ReadAllLines(path).Single());
        var written = line.RootElement.GetProperty("subscription_usage");
        Assert.Equal((0.61, "seven_day", 0.18, 0.61), (written.GetProperty("used").GetDouble(), written.GetProperty("window").GetString(),
            written.GetProperty("five_hour").GetProperty("utilization").GetDouble(), written.GetProperty("seven_day").GetProperty("utilization").GetDouble()));
        Assert.Equal(FiveResets, written.GetProperty("five_hour").GetProperty("resets_at").GetDateTimeOffset());
        Assert.Equal(usage, (await log.ReadAsync("r1", CancellationToken.None)).Run!.SubscriptionUsage);
    }

    [Fact]
    public async Task A_record_with_no_reading_writes_no_field_and_reads_back_as_none()
    {
        var path = Path.Combine(_dir.Path, "log.jsonl");
        var log = new JsonlRunLog(path);
        await log.AppendAsync(new RunRecord("r1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "draft", null, null, Runs.Completed() with { TaskId = "r1" }, Credential: "subscription"), CancellationToken.None);

        Assert.DoesNotContain("subscription_usage", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Null((await log.ReadAsync("r1", CancellationToken.None)).Run!.SubscriptionUsage);
    }

    [Fact]
    public async Task A_subscription_run_records_its_latest_reading_on_the_run_and_on_each_call_and_the_run_list_serves_it()
    {
        var usage = new SubscriptionUsage(new UsageWindow(0.18, FiveResets), new UsageWindow(0.61, SevenResets));
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Credential = "subscription", Subscription = usage };
        var log = new JsonlRunLog(Path.Combine(_dir.Path, "log.jsonl"));

        var result = await Runs.Orchestrator(runtime, _dir.Path, log).RunAsync(Runs.DraftRequest(), CancellationToken.None);

        var entry = await log.ReadAsync(result.TaskId, CancellationToken.None);
        Assert.Equal(("subscription", usage), (entry.Run!.Credential, entry.Run.SubscriptionUsage));
        Assert.All(entry.Calls.Where(c => c.Credential == "subscription"), c => Assert.Equal(usage, c.SubscriptionUsage));
        Assert.Contains(entry.Calls, c => c.SubscriptionUsage is not null);

        var row = Assert.Single(await log.ListAsync(new RunListQuery(), CancellationToken.None));
        Assert.Equal(("subscription", usage), (row.Credential, row.SubscriptionUsage));
        using var served = JsonDocument.Parse(JsonSerializer.Serialize(row, ContractJsonContext.Default.RunSummary));
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunSummary, served.RootElement));
        Assert.Equal(0.61, served.RootElement.GetProperty("subscription_usage").GetProperty("used").GetDouble());
    }

    [Fact]
    public async Task A_subscription_run_that_was_told_no_reading_records_none_and_not_a_zero()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Credential = "subscription" };
        var log = new JsonlRunLog(Path.Combine(_dir.Path, "log.jsonl"));

        var result = await Runs.Orchestrator(runtime, _dir.Path, log).RunAsync(Runs.DraftRequest(), CancellationToken.None);

        var entry = await log.ReadAsync(result.TaskId, CancellationToken.None);
        Assert.Equal("subscription", entry.Run!.Credential);
        Assert.Null(entry.Run.SubscriptionUsage);
        Assert.All(entry.Calls, c => Assert.Null(c.SubscriptionUsage));
        Assert.Null(Assert.Single(await log.ListAsync(new RunListQuery(), CancellationToken.None)).SubscriptionUsage);
    }

    [Fact]
    public async Task An_api_key_run_records_no_reading_even_when_a_message_carries_one()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Credential = "api_key", Subscription = new SubscriptionUsage(new UsageWindow(0.9), null) };
        var log = new JsonlRunLog(Path.Combine(_dir.Path, "log.jsonl"));

        var result = await Runs.Orchestrator(runtime, _dir.Path, log).RunAsync(Runs.DraftRequest(), CancellationToken.None);

        var entry = await log.ReadAsync(result.TaskId, CancellationToken.None);
        Assert.Equal("api_key", entry.Run!.Credential);
        Assert.Null(entry.Run.SubscriptionUsage);
        Assert.All(entry.Calls, c => Assert.Null(c.SubscriptionUsage));
    }

    private static SubscriptionUsage? Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return RateLimitEvent.Read(doc.RootElement);
    }

    private static string Assistant(string id) =>
        """{"type":"assistant","message":{"id":"ID","content":[{"type":"text","text":"x"}],"usage":{"input_tokens":1,"output_tokens":1,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""".Replace("ID", id);

    private static bool Apply(ClaudeCodeWorkerRuntime.Session s, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ClaudeCodeWorkerRuntime.Apply(s, doc.RootElement, compact: false);
    }
}
