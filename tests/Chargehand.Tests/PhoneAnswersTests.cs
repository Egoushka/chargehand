using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chargehand.Automation;
using Chargehand.Contracts;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0077: a question or partial stop goes to a phone with its answer options, and the signed answer that comes back continues the task.</summary>
public class PhoneAnswersTests
{
    private const string Key = "test-approval-key";

    private static readonly NotifyApprovals Approvals = new("https://approvals.example.test", "approvals-token", "approvals-key", "https://chargehand.example.test/v1/answers/approvals");

    // Signatures made by Node's crypto over JSON.stringify, as the approvals server signs: escapes, non-ASCII, U+2028 and a lone surrogate stay as JavaScript writes them.
    [Theory]
    [InlineData("01JB0000000000000000000010", "chargehand:question:run-1:t1", "answer", "Use the second option.\nKeep \"both\" tests\\ as they are — дякую 🙂\u0001",
        "810378ec7adf776076bd86c52787601bbdf4af6bd5cef45ddaeb3f0cec262272")]
    [InlineData("01JB0000000000000000000011", "chargehand:partial:run-1:t2", "continue", null, "d0cbd914fd25aa642edab90be6433ad593e1d47297353a4289fa375dec34da37")]
    public void A_signature_made_the_way_the_approvals_server_signs_verifies_and_a_changed_field_does_not(string decision, string id, string action, string? answer, string sig)
    {
        var edited = new Dictionary<string, string>(StringComparer.Ordinal);
        if (answer is not null)
            edited["body.answer"] = answer;
        var at = new DateTimeOffset(2026, 10, 22, 8, 12, 3, TimeSpan.Zero);
        var signed = new ApprovalDecision(decision, id, action, edited, at, "2026-10-22T08:12:03.000Z", sig);

        Assert.True(signed.Verify(Key, at.AddMinutes(5)));
        Assert.False(signed.Verify("another-key", at));
        Assert.False((signed with { Action = "drop" }).Verify(Key, at));
        Assert.False(signed.Verify(Key, at + ApprovalDecision.MaxSkew + TimeSpan.FromSeconds(1)));
    }

    // xUnit cannot carry a lone surrogate in theory data, so this vector is built here.
    [Fact]
    public void A_lone_surrogate_and_a_line_separator_are_written_as_JavaScript_writes_them()
    {
        var edited = new Dictionary<string, string>(StringComparer.Ordinal) { ["body.z"] = "z", ["body.answer"] = "a\tb\u2028c\ud800" };
        var at = new DateTimeOffset(2026, 10, 22, 8, 12, 3, TimeSpan.Zero);

        Assert.Equal("{\"body.answer\":\"a\\tb\u2028c\\ud800\",\"body.z\":\"z\"}", ApprovalDecision.Canonical(edited));
        Assert.True(new ApprovalDecision("01JB0000000000000000000012", "chargehand:question.1:run-1:t1", "answer", edited, at, "2026-10-22T08:12:03.000Z",
            "e394787874cce7dc65a51873b5791540cd7c6404a36c99531bb12c3ca31abf03").Verify(Key, at));
    }

    [Fact]
    public void An_item_id_names_its_kind_batch_and_task_and_another_sources_does_not()
    {
        Assert.Equal(("question", "run-1", "t1"), ApprovalDecision.Parse("chargehand", "chargehand:question.2:run-1:t1"));
        Assert.Equal(("partial", "run-1", "t:2"), ApprovalDecision.Parse("chargehand", "chargehand:partial:run-1:t:2"));
        Assert.Null(ApprovalDecision.Parse("chargehand", "outreach:question:run-1:t1"));
        Assert.Null(ApprovalDecision.Parse("chargehand", "chargehand:other:run-1:t1"));
    }

    private static QuestionNotice Question(string id = "question:run-1:t1", DateTimeOffset? at = null) =>
        new(id, QuestionKinds.Question, "run-1", "t1", "/srv/repo", "Add the export", ["Which format?", "Keep the old one?"], at ?? ConsoleRig.Now);

    [Fact]
    public async Task The_approvals_item_carries_the_questions_one_editable_answer_the_options_and_the_callback()
    {
        var handler = new Recording();
        var sink = new ApprovalsSink(Approvals, () => "the-token", "https://console.example.test/console/", new HttpClient(handler), new FixedClock(ConsoleRig.Now));

        await sink.SendAsync(Question(), default);

        var (method, url, auth, body) = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Put, "https://approvals.example.test/items/chargehand%3Aquestion%3Arun-1%3At1", "Bearer the-token"), (method, url, auth));
        var item = JsonNode.Parse(body!)!;
        Assert.Equal(("chargehand", "chargehand:question:run-1:t1", "decision"), (item["source"]!.GetValue<string>(), item["id"]!.GetValue<string>(), item["kind"]!.GetValue<string>()));
        Assert.Equal("1. Which format?\n2. Keep the old one?", item["body"]!["questions"]!.GetValue<string>());
        Assert.Equal("", item["body"]!["answer"]!.GetValue<string>());
        Assert.Equal(["body.answer"], item["editable"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Equal(["answer", "decide", "drop"], item["actions"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()));
        Assert.Equal("https://console.example.test/console/runs/run-1/tasks/t1", item["links"]![0]!["url"]!.GetValue<string>());
        Assert.Equal(Approvals.CallbackUrl, item["callback"]!.GetValue<string>());
        Assert.Equal(ConsoleRig.Now.AddDays(7), DateTimeOffset.Parse(item["expires_at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_refused_item_fails_without_naming_the_server_and_a_decided_one_counts_as_sent()
    {
        var handler = new Recording { Status = HttpStatusCode.Unauthorized };
        var sink = new ApprovalsSink(Approvals, () => "the-token", null, new HttpClient(handler));

        var e = await Assert.ThrowsAsync<IntakeException>(() => sink.SendAsync(Question(), default));
        Assert.DoesNotContain("example.test", e.Message);
        handler.Status = HttpStatusCode.Conflict;
        await sink.SendAsync(Question(), default);
        handler.Status = HttpStatusCode.NotFound;
        await sink.WithdrawAsync(Question(), default);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Each_new_notice_is_sent_once_to_each_sink_a_failed_sink_is_tried_again_and_a_closed_one_is_taken_back()
    {
        var clock = new ManualTimeProvider();
        clock.Advance(ConsoleRig.Now - clock.GetUtcNow());
        var source = new Source { Open = [Question("old", ConsoleRig.Now.AddHours(-1))] };
        var (good, bad) = (new Sink("good"), new Sink("bad") { Fail = true });
        var warnings = new List<string>();
        var notifier = new QuestionNotifier(source, [good, bad], new NotifyState(), clock, warnings.Add);

        await notifier.TickAsync(default);
        Assert.Empty(good.Sent);   // open before the first wake: not sent

        source.Open = [.. source.Open, Question("new", ConsoleRig.Now.AddMinutes(1))];
        await notifier.TickAsync(default);
        await notifier.TickAsync(default);
        Assert.Equal(["new"], good.Sent.Select(n => n.Id));
        Assert.Equal(2, warnings.Count(w => w.Contains("bad: new was not sent", StringComparison.Ordinal)));

        bad.Fail = false;
        await notifier.TickAsync(default);
        Assert.Equal(["new"], bad.Sent.Select(n => n.Id));

        source.Open = [];
        await notifier.TickAsync(default);
        await notifier.TickAsync(default);
        Assert.Equal(["new"], good.Withdrawn.Select(n => n.Id));
        Assert.Equal(["new"], bad.Withdrawn.Select(n => n.Id));
    }

    [Fact]
    public async Task A_notice_sent_again_goes_to_that_sink_one_round_on_under_a_new_item_id()
    {
        var state = new NotifyState();
        var source = new Source { Open = [Question()] };
        var (approvals, ntfy) = (new Sink("approvals"), new Sink("ntfy"));
        var notifier = new QuestionNotifier(source, [approvals, ntfy], state, new FixedClock(ConsoleRig.Now.AddMinutes(-1)));
        await notifier.TickAsync(default);

        state.SendAgain("question:run-1:t1", "approvals", ConsoleRig.Now);
        await notifier.TickAsync(default);

        Assert.Equal([0, 1], approvals.Sent.Select(n => n.Round));
        Assert.Single(ntfy.Sent);
        Assert.Equal("chargehand:question.1:run-1:t1", ApprovalsSink.ItemId("chargehand", approvals.Sent[1]));
    }

    [Fact]
    public void The_profile_needs_a_sink_driven_sessions_and_an_https_callback()
    {
        var e = Assert.Throws<Chargehand.Kernel.ChargehandException>(() => new AutomationSettings(Notify: new AutomationNotify()).Validate(false, null));
        Assert.Contains("needs driven.enabled", e.Message);
        Assert.Contains("set approvals or ntfy_url", e.Message);
        e = Assert.Throws<Chargehand.Kernel.ChargehandException>(() => new AutomationSettings(Notify: new AutomationNotify(Approvals with { CallbackUrl = "http://plain.example.test/x" })).Validate(true, null));
        Assert.Contains("callback_url must be an https URL", e.Message);
        new AutomationSettings(Notify: new AutomationNotify(Approvals, "https://ntfy.example.test/topic")).Validate(true, null);
    }

    // The answer route, on a batch seeded in the run log

    private static async Task<(ConsoleRig Rig, PhoneAnswers Phone, Sink Sink)> RigAsync()
    {
        var sink = new Sink("approvals");
        var phone = new PhoneAnswers(new AutomationNotify(Approvals), new NotifyState(), [sink], () => Key);
        var rig = await ConsoleRig.StartAsync(phone: phone);
        await ConsoleSeed.BatchAsync(rig, "run-b1", null, new SeedTask("t1", "needs_input", Questions: ["Which format?"]),
            new SeedTask("t2", "failed", PrUrl: "https://example.test/o/r/pull/5", Artifacts: [ConsoleSeed.Inline("kept-work", new { @base = ConsoleRig.Commit, commits = 2, bundle = "t2.bundle" })]));
        // A partial stop opens a draft pull request: the inbox lists it as partial_draft.
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("run-b1", "t2", LearningOutcome.Partial, pr: ConsoleSeed.Pull(5)));
        return (rig, phone, sink);
    }

    private static JsonContent Decision(string id, string action, string? answer = null, string decision = "01JB0000000000000000000099", DateTimeOffset? at = null, string key = Key)
    {
        var edited = answer is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["body.answer"] = answer };
        var atText = (at ?? ConsoleRig.Now).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        var message = $"{decision}|{id}|{action}|{atText}|{ApprovalDecision.EditedHash(edited)}";
        var sig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message)));
        return JsonContent.Create(new { decision, id, action, edited, at = atText, sig });
    }

    private static async Task<(HttpStatusCode Status, bool Ok, string Message)> Post(ConsoleRig rig, JsonContent body)
    {
        using var anonymous = new HttpClient { BaseAddress = rig.Server.BaseAddress };
        var res = await anonymous.PostAsync("/v1/answers/approvals", body);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (res.StatusCode, json.GetProperty("ok").GetBoolean(), json.GetProperty("message").GetString()!);
    }

    private static async Task<StartRecord?> StartedAsync(ConsoleRig rig, string message)
    {
        var run = message["Started run ".Length..];
        for (var i = 0; i < 200; i++)
        {
            if ((await rig.Log.ReadAsync(run, default)).Start is { } start)
                return start;
            await Task.Delay(25);
        }
        return null;
    }

    [Fact]
    public async Task A_signed_answer_continues_the_task_with_it_once_and_the_same_decision_again_is_already_handled()
    {
        var (rig, phone, _) = await RigAsync();
        await using var _rig = rig;

        var first = await Post(rig, Decision("chargehand:question:run-b1:t1", "answer", "  CSV, and drop the old one.  "));
        var again = await Post(rig, Decision("chargehand:question:run-b1:t1", "answer", "CSV, and drop the old one."));

        Assert.Equal((HttpStatusCode.OK, true), (first.Status, first.Ok));
        var start = await StartedAsync(rig, first.Message);
        var task = Assert.Single(start!.Request.Driven!.Tasks);
        Assert.Equal((new ContinueFrom("run-b1", "t1"), "CSV, and drop the old one."), (task.ContinueFrom, Assert.Single(task.Answers!)));
        Assert.Equal("Answer to run-b1/t1.", start.Request.Text);
        Assert.Equal((HttpStatusCode.Conflict, true), (again.Status, again.Ok));
        Assert.Contains("Already handled", again.Message);
        Assert.Single(phone.State.Read(s => s.Decisions.Values.ToList()));
    }

    [Fact]
    public async Task Your_call_and_continue_start_a_continuation_and_drop_dismisses_the_item()
    {
        var (rig, _, _) = await RigAsync();
        await using var _rig = rig;

        var decide = await Post(rig, Decision("chargehand:question:run-b1:t1", "decide", decision: "d1"));
        var resume = await Post(rig, Decision("chargehand:partial:run-b1:t2", "continue", decision: "d2"));

        Assert.Equal(AnswerActions.DecideText, Assert.Single(Assert.Single((await StartedAsync(rig, decide.Message))!.Request.Driven!.Tasks).Answers!));
        var continued = Assert.Single((await StartedAsync(rig, resume.Message))!.Request.Driven!.Tasks);
        Assert.Equal((new ContinueFrom("run-b1", "t2"), (IReadOnlyList<string>?)null), (continued.ContinueFrom, continued.Answers));

        var (rig2, _, _) = await RigAsync();
        await using var _rig2 = rig2;
        var drop = await Post(rig2, Decision("chargehand:question:run-b1:t1", "drop", decision: "d3"));
        Assert.Equal((HttpStatusCode.OK, "Dropped; nothing started"), (drop.Status, drop.Message));
        var inbox = await ConsoleRig.Get(rig2.Owner, "/v1/inbox");
        Assert.DoesNotContain(inbox.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == "question:run-b1:t1");
    }

    [Fact]
    public async Task An_answer_with_nothing_typed_starts_nothing_and_the_question_is_sent_again()
    {
        var (rig, phone, _) = await RigAsync();
        await using var _rig = rig;
        phone.State.Change(ConsoleRig.Now, s => s.Sent["question:run-b1:t1"] = new SentNotice(Question("question:run-b1:t1"), ["approvals"]));

        var empty = await Post(rig, Decision("chargehand:question:run-b1:t1", "answer", "   "));

        Assert.Equal((HttpStatusCode.OK, true), (empty.Status, empty.Ok));
        Assert.Contains("comes again", empty.Message);
        var sent = phone.State.Read(s => s.Sent["question:run-b1:t1"]);
        Assert.Equal((1, 0), (sent.Notice.Round, sent.Sinks.Count));
    }

    [Fact]
    public async Task A_bad_signature_an_old_decision_a_wrong_action_and_an_unknown_task_start_nothing()
    {
        var (rig, _, _) = await RigAsync();
        await using var _rig = rig;

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(rig, Decision("chargehand:question:run-b1:t1", "decide", key: "wrong"))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(rig, Decision("chargehand:question:run-b1:t1", "decide", at: ConsoleRig.Now.AddHours(-1)))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(rig, Decision("chargehand:question:run-b1:t1", "continue"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(rig, Decision("outreach:question:run-b1:t1", "decide"))).Status);
        var gone = await Post(rig, Decision("chargehand:question:run-b9:t1", "decide"));
        Assert.Equal((HttpStatusCode.Conflict, true), (gone.Status, gone.Ok));
        Assert.Single((await rig.Log.ReadAllAsync(default)).Starts, s => s.ParentRunId is null);
    }

    [Fact]
    public async Task Without_approvals_the_route_needs_a_key_like_any_other()
    {
        await using var rig = await ConsoleRig.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = rig.Server.BaseAddress };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/v1/answers/approvals", Decision("chargehand:question:run-b1:t1", "decide"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Owner.PostAsync("/v1/answers/approvals", Decision("chargehand:question:run-b1:t1", "decide"))).StatusCode);
    }

    private sealed class Source : IQuestionSource
    {
        public IReadOnlyList<QuestionNotice> Open { get; set; } = [];

        public Task<IReadOnlyList<QuestionNotice>> OpenAsync(CancellationToken ct) => Task.FromResult(Open);
    }

    private sealed class Sink(string name) : IQuestionSink
    {
        public List<QuestionNotice> Sent { get; } = [];
        public List<QuestionNotice> Withdrawn { get; } = [];
        public bool Fail { get; set; }

        public string Name => name;

        public Task SendAsync(QuestionNotice notice, CancellationToken ct)
        {
            if (Fail)
                throw new IntakeException("the sink answered 503", 503);
            Sent.Add(notice);
            return Task.CompletedTask;
        }

        public Task WithdrawAsync(QuestionNotice notice, CancellationToken ct)
        {
            Withdrawn.Add(notice);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Recording : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth, string? Body)> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status);
        }
    }
}
