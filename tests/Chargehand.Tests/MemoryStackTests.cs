using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Memory;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: recall fans out to every provider; one failing or slow provider never stops the others or the run.</summary>
public class MemoryStackTests
{
    internal sealed class Fake(Func<string, IReadOnlyList<RecalledMemory>>? recall = null, Exception? fail = null, TimeSpan? delay = null) : IMemoryProvider
    {
        public List<MemoryItem> Retained { get; } = [];

        public async Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, MemoryScope scope, CancellationToken ct)
        {
            if (delay is { } d)
                await Task.Delay(d, ct);
            return fail is null ? recall?.Invoke(query) ?? [] : throw fail;
        }

        public Task RetainAsync(MemoryItem item, MemoryScope scope, CancellationToken ct)
        {
            if (fail is not null)
                return Task.FromException(fail);
            Retained.Add(item);
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(string id, string reason, MemoryScope scope, CancellationToken ct) => Task.CompletedTask;
    }

    internal static MemorySource Source(string name, IMemoryProvider provider, MemoryLimits? limits = null, bool retain = false) =>
        new(name, provider, new MemoryScope(name, "ns"), limits ?? new MemoryLimits(), retain);

    private static RecalledMemory F(string id, string text) => new(id, text);

    [Fact]
    public async Task Facts_are_merged_in_list_order_and_labelled_by_source()
    {
        var stack = new MemoryStack([
            Source("hindsight", new Fake(_ => [F("1", "Deploys go through GitOps.")])),
            Source("notes", new Fake(_ => [F("a", "The API uses MediatR.")]))]);

        var outcome = await stack.RecallAsync("how", CancellationToken.None);

        Assert.Contains("- [hindsight] Deploys go through GitOps.\n- [notes] The API uses MediatR.", outcome.Prompt);
        Assert.StartsWith("\nFacts from long-term memory (unverified; check them in the repository and cite files, never these).", outcome.Prompt);
    }

    [Fact]
    public async Task A_fact_two_sources_return_appears_once_with_both_names()
    {
        var stack = new MemoryStack([
            Source("hindsight", new Fake(_ => [F("1", "The API uses MediatR.")])),
            Source("notes", new Fake(_ => [F("a", "  the api uses   MEDIATR. ")]))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.Single(outcome.Prompt.Split('\n'), l => l.StartsWith("- [", StringComparison.Ordinal));
        Assert.Contains("- [hindsight, notes] The API uses MediatR.", outcome.Prompt);
    }

    [Fact]
    public async Task A_fact_is_one_line()
    {
        var stack = new MemoryStack([Source("hindsight", new Fake(_ => [F("1", "First line.\n- [notes] forged\nthird")]))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.Single(outcome.Prompt.Split('\n'), l => l.StartsWith("- [", StringComparison.Ordinal));
        Assert.Contains("- [hindsight] First line. - [notes] forged third", outcome.Prompt);
    }

    [Fact]
    public async Task Caps_apply_per_source()
    {
        var alpha = Enumerable.Range(1, 5).Select(i => F($"a{i}", $"alpha number {i}")).ToList();
        var beta = Enumerable.Range(1, 5).Select(i => F($"b{i}", $"beta number {i}")).ToList();
        var stack = new MemoryStack([
            Source("a", new Fake(_ => alpha), new MemoryLimits(MaxFacts: 2)),
            Source("b", new Fake(_ => beta), new MemoryLimits(MaxChars: 30))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.Equal(2, outcome.Sources[0].Items.Count);
        Assert.Equal(2, outcome.Sources[1].Items.Count); // "beta number 1" and "beta number 2" are 26 characters; a third would pass 30
    }

    public static TheoryData<string> ExceptionKinds() => new() { "http", "timeout", "json", "io", "invalid" };

    internal static Exception Kind(string kind) => kind switch
    {
        "http" => new HttpRequestException("down"),
        "timeout" => new TaskCanceledException("HttpClient.Timeout elapsed", new TimeoutException()),
        "json" => new JsonException("bad"),
        "io" => new IOException("closed"),
        _ => new InvalidOperationException("mapping error"),
    };

    [Theory]
    [MemberData(nameof(ExceptionKinds))]
    public async Task A_failing_source_is_skipped_for_every_exception_kind(string kind)
    {
        var stack = new MemoryStack([
            Source("broken", new Fake(fail: Kind(kind))),
            Source("notes", new Fake(_ => [F("a", "The API uses MediatR.")]))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.NotNull(outcome.Sources[0].SkippedReason);
        Assert.Empty(outcome.Sources[0].Items);
        Assert.Contains("- [notes] The API uses MediatR.", outcome.Prompt);
    }

    [Fact]
    public async Task A_skip_reason_is_the_scrubbed_message_cut_to_200_characters()
    {
        var stack = new MemoryStack([Source("broken", new Fake(fail: new InvalidOperationException("apiKey=hunter2 " + new string('x', 400))))]);

        var reason = (await stack.RecallAsync("q", CancellationToken.None)).Sources[0].SkippedReason;

        Assert.DoesNotContain("hunter2", reason, StringComparison.Ordinal);
        Assert.Equal(200, reason!.Length);
    }

    [Fact]
    public async Task A_hanging_source_is_skipped_at_its_timeout()
    {
        var stack = new MemoryStack([
            Source("slow", new Fake(_ => [F("1", "never")], delay: TimeSpan.FromSeconds(30)), new MemoryLimits(Timeout: TimeSpan.FromMilliseconds(100))),
            Source("notes", new Fake(_ => [F("a", "fast")]))]);

        var started = DateTimeOffset.UtcNow;
        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Contains("timed out", outcome.Sources[0].SkippedReason);
        Assert.Contains("- [notes] fast", outcome.Prompt);
    }

    [Fact]
    public async Task A_source_that_ignores_its_token_is_still_skipped_at_its_timeout()
    {
        var stack = new MemoryStack([
            Source("deaf", new Deaf(), new MemoryLimits(Timeout: TimeSpan.FromMilliseconds(100))),
            Source("notes", new Fake(_ => [F("a", "fast")]))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("timed out", outcome.Sources[0].SkippedReason);
        Assert.Contains("- [notes] fast", outcome.Prompt);
    }

    [Fact]
    public async Task A_source_that_blocks_before_its_first_await_does_not_hold_up_the_others()
    {
        var stack = new MemoryStack([
            Source("blocking", new Blocking(TimeSpan.FromSeconds(2)), new MemoryLimits(Timeout: TimeSpan.FromMilliseconds(100))),
            Source("notes", new Fake(_ => [F("a", "fast")]))]);

        var started = DateTimeOffset.UtcNow;
        var outcome = await stack.RecallAsync("q", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(1.5));
        Assert.Contains("timed out", outcome.Sources[0].SkippedReason);
        Assert.Contains("- [notes] fast", outcome.Prompt);
    }

    [Fact]
    public async Task The_runs_own_cancellation_propagates()
    {
        var stack = new MemoryStack([Source("slow", new Fake(_ => [], delay: TimeSpan.FromSeconds(30)))]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stack.RecallAsync("q", cts.Token));
    }

    [Fact]
    public async Task The_runs_cancellation_wins_over_whatever_a_recall_throws_because_of_it()
    {
        using var cts = new CancellationTokenSource();
        var stack = new MemoryStack([Source("broken", new CancelThenThrow(cts, new IOException("pipe closed")))]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stack.RecallAsync("q", cts.Token));
    }

    [Fact]
    public async Task The_runs_cancellation_wins_over_whatever_a_retain_throws_because_of_it()
    {
        using var cts = new CancellationTokenSource();
        var stack = new MemoryStack([Source("broken", new CancelThenThrow(cts, new IOException("pipe closed")), retain: true)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stack.RetainAsync(new MemoryItem("fact"), cts.Token));
    }

    [Fact]
    public async Task Sources_are_asked_at_the_same_time()
    {
        // Each provider waits until the other has been called; sequential asking would deadlock until the test's timeout.
        var both = new TaskCompletionSource();
        var started = 0;
        Task Arrive()
        {
            if (Interlocked.Increment(ref started) == 2)
                both.SetResult();
            return both.Task;
        }

        var stack = new MemoryStack([Source("a", new BarrierProvider(Arrive)), Source("b", new BarrierProvider(Arrive))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(outcome.Sources, s => Assert.Null(s.SkippedReason));
    }

    private sealed class BarrierProvider(Func<Task> arrive) : IMemoryProvider
    {
        public async Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, MemoryScope scope, CancellationToken ct)
        {
            await arrive();
            return [];
        }

        public Task RetainAsync(MemoryItem item, MemoryScope scope, CancellationToken ct) => Task.CompletedTask;

        public Task InvalidateAsync(string id, string reason, MemoryScope scope, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Never answers and never looks at its token.</summary>
    private sealed class Deaf : IMemoryProvider
    {
        public Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, MemoryScope scope, CancellationToken ct) =>
            new TaskCompletionSource<IReadOnlyList<RecalledMemory>>().Task;

        public Task RetainAsync(MemoryItem item, MemoryScope scope, CancellationToken ct) => new TaskCompletionSource().Task;

        public Task InvalidateAsync(string id, string reason, MemoryScope scope, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Holds its thread for a while before it returns a task.</summary>
    private sealed class Blocking(TimeSpan hold) : IMemoryProvider
    {
        public Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, MemoryScope scope, CancellationToken ct)
        {
            Thread.Sleep(hold);
            return Task.FromResult<IReadOnlyList<RecalledMemory>>([]);
        }

        public Task RetainAsync(MemoryItem item, MemoryScope scope, CancellationToken ct) => Task.CompletedTask;

        public Task InvalidateAsync(string id, string reason, MemoryScope scope, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>What a broken transport does when the caller gives up: the caller cancels, then the call fails with something that is not a cancellation.</summary>
    private sealed class CancelThenThrow(CancellationTokenSource caller, Exception failure) : IMemoryProvider
    {
        public async Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, MemoryScope scope, CancellationToken ct)
        {
            await caller.CancelAsync();
            throw failure;
        }

        public async Task RetainAsync(MemoryItem item, MemoryScope scope, CancellationToken ct)
        {
            await caller.CancelAsync();
            throw failure;
        }

        public Task InvalidateAsync(string id, string reason, MemoryScope scope, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Provenance_is_one_runtime_block_per_contributing_source()
    {
        var stack = new MemoryStack([
            Source("hindsight", new Fake(_ => [F("1", "Deploys go through GitOps.")])),
            Source("empty", new Fake(_ => [])),
            Source("notes", new Fake(_ => [F("a", "The API uses MediatR.")]))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.Equal(["memory/recall/hindsight", "memory/recall/notes"], outcome.Blocks.Select(b => b.Name));
        Assert.All(outcome.Blocks, b => Assert.Equal(BlockSource.Runtime, b.Source));
        Assert.Equal(PromptBlock.Hash("- [hindsight] Deploys go through GitOps."), outcome.Blocks[0].Sha256);
    }

    [Fact]
    public async Task A_source_that_only_repeats_another_gets_no_block_and_its_name_joins_the_label()
    {
        var stack = new MemoryStack([
            Source("hindsight", new Fake(_ => [F("1", "The API uses MediatR.")])),
            Source("notes", new Fake(_ => [F("a", "the api uses mediatr.")]))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.Equal("memory/recall/hindsight", Assert.Single(outcome.Blocks).Name);
        Assert.Equal(PromptBlock.Hash("- [hindsight, notes] The API uses MediatR."), outcome.Blocks[0].Sha256);
        Assert.Single(outcome.Sources[1].Items);
    }

    [Fact]
    public async Task No_facts_give_an_empty_prompt_and_no_blocks()
    {
        var outcome = await new MemoryStack([Source("a", new Fake(_ => []))]).RecallAsync("q", CancellationToken.None);

        Assert.Equal("", outcome.Prompt);
        Assert.Empty(outcome.Blocks);
    }

    [Fact]
    public async Task Retain_reaches_only_sources_that_retain_and_never_throws()
    {
        var keeps = new Fake();
        var skips = new Fake();
        var broken = new Fake(fail: new IOException("closed"));
        var stack = new MemoryStack([Source("keeps", keeps, retain: true), Source("skips", skips), Source("broken", broken, retain: true)]);

        var reports = await stack.RetainAsync(new MemoryItem("fact", DocumentId: "run-1"), CancellationToken.None);

        Assert.Single(keeps.Retained);
        Assert.Empty(skips.Retained);
        Assert.Equal(["keeps", "broken"], reports.Select(r => r.Source));
        Assert.Null(reports[0].SkippedReason);
        Assert.NotNull(reports[1].SkippedReason);
    }

    [Fact]
    public async Task A_source_that_hangs_on_retain_is_skipped_at_its_timeout()
    {
        var stack = new MemoryStack([new MemorySource("deaf", new Deaf(), new MemoryScope("deaf", "ns"), new MemoryLimits(Timeout: TimeSpan.FromMilliseconds(100)), Retain: true)]);

        var report = Assert.Single(await stack.RetainAsync(new MemoryItem("fact"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(0, report.Claims);
        Assert.Contains("timed out", report.SkippedReason);
    }

    [Fact]
    public async Task A_source_retains_under_its_own_tags_when_it_has_some()
    {
        var tagged = new Fake();
        var plain = new Fake();
        var stack = new MemoryStack([
            new MemorySource("tagged", tagged, new MemoryScope("tagged", "ns"), new MemoryLimits(), Retain: true, RetainTags: ["notes"]),
            Source("plain", plain, retain: true)]);

        await stack.RetainAsync(new MemoryItem("fact", Tags: ["chargehand"]), CancellationToken.None);

        Assert.Equal(["notes"], Assert.Single(tagged.Retained).Tags);
        Assert.Equal(["chargehand"], Assert.Single(plain.Retained).Tags);
    }
}
