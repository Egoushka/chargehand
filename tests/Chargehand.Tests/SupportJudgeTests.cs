using Chargehand.Contracts;
using Chargehand.Runtime;
using Chargehand.Verification;

namespace Chargehand.Tests;

/// <summary>Goal 0.8 (ADR 0036): the support judge's prompt, parse and retry.</summary>
public class SupportJudgeTests
{
    private static CitedClaim Claim(int index, string text, string cited, bool truncated = false) =>
        new(index, new Claim(text, ["e1"], 0.9), cited, truncated);

    private static readonly CitedClaim[] Two = [Claim(0, "It retries.", "[e1] file a.cs:1\n1: retry(3)"), Claim(2, "It logs.", "[e2] file b.cs:5\n5: // nothing", truncated: true)];

    private static bool TryParse(string text, out IReadOnlyList<ClaimVerdict> verdicts, out string? error) => SupportJudge.Parse(text, Two, out verdicts, out error);

    [Fact]
    public void A_valid_object_parses_and_maps_numbers_to_claim_indexes()
    {
        Assert.True(TryParse("""{"verdicts":[{"claim":1,"verdict":"supported","reason":"retry(3)"},{"claim":2,"verdict":"unsupported","reason":"a comment"}]}""", out var v, out _));
        Assert.Equal([new ClaimVerdict(0, SupportVerdict.Supported, "retry(3)"), new ClaimVerdict(2, SupportVerdict.Unsupported, "a comment")], v);
    }

    [Fact]
    public void Prose_around_the_object_capitals_and_string_numbers_are_accepted()
    {
        Assert.True(TryParse("""Here you go: {"verdicts":[{"claim":"1","verdict":"Partial"},{"claim":"C2","verdict":" SUPPORTED "}]} done""", out var v, out _));
        Assert.Equal([SupportVerdict.Partial, SupportVerdict.Supported], v.Select(x => x.Verdict));
    }

    [Theory]
    [InlineData("no json here")]
    [InlineData("""{"verdicts":[{"claim":1,"verdict":"supported"}]}""")]
    [InlineData("""{"verdicts":[{"claim":1,"verdict":"supported"},{"claim":3,"verdict":"supported"}]}""")]
    [InlineData("""{"verdicts":[{"claim":1,"verdict":"maybe"},{"claim":2,"verdict":"supported"}]}""")]
    [InlineData("""{"other":[]}""")]
    public void A_reply_that_does_not_cover_every_claim_with_a_known_verdict_is_refused(string text)
    {
        Assert.False(TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void The_prompt_holds_every_claim_its_cited_text_and_the_truncation_note()
    {
        var prompt = SupportJudge.Prompt(Two);
        Assert.Contains("Claim 1: It retries.", prompt, StringComparison.Ordinal);
        Assert.Contains("1: retry(3)", prompt, StringComparison.Ordinal);
        Assert.Contains("Claim 2: It logs.", prompt, StringComparison.Ordinal);
        Assert.Contains("cut for length", prompt, StringComparison.Ordinal);
        Assert.Contains("do not use your own knowledge", prompt, StringComparison.Ordinal);
    }

    private sealed class ReplyRuntime(params string[] replies) : IWorkerRuntime
    {
        private readonly Queue<string> _replies = new(replies);
        public List<string> Prompts { get; } = [];

        public Task<string> GenerateAsync(ModelRef? model, string prompt, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(_replies.Dequeue());
        }

        public Task<WorkerSession> CreateAsync(NodeSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task SetInstructionAsync(string sessionId, string key, string value, CancellationToken ct) => throw new NotSupportedException();
        public Task SubmitAsync(string sessionId, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdleOutcome> AwaitIdleAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task InterruptAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkerMessage>> ReadMessagesAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PermissionRequest>> PendingPermissionsAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task AnswerPermissionAsync(string sessionId, string requestId, PermissionDecision decision, string? message, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkerSession> ForkAsync(string sessionId, string? beforeMessageId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompactAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileDiff>> DiffAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_bad_reply_gets_one_retry_that_names_the_error()
    {
        var runtime = new ReplyRuntime("nope", """{"verdicts":[{"claim":1,"verdict":"supported"},{"claim":2,"verdict":"supported"}]}""");
        var verdicts = await SupportJudge.JudgeAsync(runtime, null, Two, default);
        Assert.Equal(2, verdicts.Count);
        Assert.Equal(2, runtime.Prompts.Count);
        Assert.Contains("previous output was invalid: no JSON object", runtime.Prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_bad_replies_throw()
    {
        var runtime = new ReplyRuntime("nope", "still nope");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => SupportJudge.JudgeAsync(runtime, null, Two, default));
        Assert.Contains("after one retry", e.Message, StringComparison.Ordinal);
    }
}
