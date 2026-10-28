using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Conversations.Adapters;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Conversations.Tests;

/// <summary>The service over the scripted agent: what a person can start, say, approve and end, who sees it, what it costs, and what a restart leaves.</summary>
public sealed class ConversationServiceTests : IAsyncLifetime
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("chargehand-conversations-").FullName;
    private readonly List<ConversationService> _services = [];
    private readonly ManualClock _clock = new(Events.Start);
    private readonly FakeConversationRuntime _fake = new();

    private string Dir => Path.Combine(_root, "conversations");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var service in _services)
            await service.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private ConversationService Service(IConversationRuntime? runtime = null, ConversationSettings? settings = null, IRunLog? log = null)
    {
        var service = new ConversationService(runtime ?? _fake, Dir, settings ?? new ConversationSettings(), log, _clock);
        _services.Add(service);
        return service;
    }

    private static async Task<ConversationView> Started(ConversationService service, Principal? who = null, string repository = "acme/widgets")
    {
        var started = await service.StartAsync(who, new StartConversation(repository, Commit, FakeConversationRuntime.Name), default);
        Assert.True(started.Ok, string.Join("; ", started.Problem?.Errors ?? []));
        return started.Value!;
    }

    private static string Kind(ConversationEvent e) => ConversationJson.Parse(e) switch
    {
        StateData s => $"state:{s.State.ToString().ToLowerInvariant()}",
        MessageChunk m => $"message:{m.Role}",
        EndedData x => $"ended:{x.Reason}",
        var data => data!.Type,
    };

    private static string[] Kinds(IEnumerable<ConversationEvent> events) => [.. events.Select(Kind)];

    private static T Data<T>(ConversationEvent e) where T : ConversationEventData => Assert.IsType<T>(ConversationJson.Parse(e));

    /// <summary>Reads the stream from after <paramref name="after"/> until <paramref name="done"/> says enough, or the conversation is over.</summary>
    private static async Task<List<ConversationEvent>> Read(ConversationService service, string id, Func<IReadOnlyList<ConversationEvent>, bool> done, long after = 0, Principal? who = null)
    {
        using var cts = new CancellationTokenSource(Patience);
        List<ConversationEvent> seen = [];
        await foreach (var e in service.Events(who, id, after, cts.Token)!)
        {
            seen.Add(e);
            if (done(seen))
                break;
        }

        return seen;
    }

    private static Task<List<ConversationEvent>> UntilIdle(ConversationService service, string id, long after, Principal? who = null) =>
        Read(service, id, seen => seen.Any(e => Kind(e) == "state:idle"), after, who);

    /// <summary>Sends <paramref name="text"/> and returns the events up to the end of its turn.</summary>
    private static async Task<List<ConversationEvent>> Say(ConversationService service, string id, string text, Principal? who = null)
    {
        var before = service.Find(who, id)!.LastEventId;
        var sent = await service.SendAsync(who, id, text, default);
        Assert.True(sent.Ok, string.Join("; ", sent.Problem?.Errors ?? []));
        return await UntilIdle(service, id, before, who);
    }

    private void Journal(string id, Events events)
    {
        using var writer = new ConversationJournal(Dir).Open(id);
        foreach (var e in events.List)
            writer.Append(e);
    }

    [Fact]
    public async Task A_started_conversation_is_idle_with_the_runtime_s_model_and_opens_with_starting_then_idle()
    {
        var service = Service();

        var view = await Started(service);

        Assert.Equal((ConversationView.Version, ConversationState.Idle, "fake-1", FakeConversationRuntime.Name, 0, 2L), (view.ContractVersion, view.State, view.Model, view.Runtime, view.Messages, view.LastEventId));
        Assert.Equal(new ConversationCaps(200), view.Caps);
        Assert.Matches("^conv-[0-9]{8}-[0-9]{6}-[0-9a-f]{6}$", view.Id);
        var events = await Read(service, view.Id, seen => seen.Count == 2);
        Assert.Equal(["state:starting", "state:idle"], Kinds(events));
        Assert.Equal([1L, 2L], events.Select(e => e.Id));
        var opened = Data<StateData>(events[0]);
        Assert.Equal(("acme/widgets", Commit, "fake"), (opened.Repository!.Path, opened.Repository.Commit, opened.Runtime));
    }

    [Fact]
    public async Task A_message_runs_a_turn_whose_events_arrive_in_order_and_whose_usage_adds_up()
    {
        var service = Service();
        var view = await Started(service);

        var events = await Say(service, view.Id, "hello");

        Assert.Equal(["message:user", "state:running", "message:assistant", "message:assistant", "usage", "state:idle"], Kinds(events));
        Assert.Equal("end_turn", Data<StateData>(events[^1]).StopReason);
        Assert.Equal("You said: hello", string.Concat(events.Where(e => Kind(e) == "message:assistant").Select(e => Data<MessageChunk>(e).Text)));
        var after = service.Find(null, view.Id)!;
        Assert.Equal((ConversationState.Idle, 1, 0, 8L), (after.State, after.Messages, after.Queued, after.LastEventId));
        Assert.Equal(new ConversationUsage(5, 15, null), after.Usage);
    }

    [Fact]
    public async Task A_message_sent_during_a_turn_waits_and_goes_to_the_agent_when_the_turn_ends()
    {
        var service = Service();
        var view = await Started(service);
        await service.SendAsync(null, view.Id, "hold:first", default);
        await Read(service, view.Id, seen => seen.Any(e => Kind(e) == "message:assistant"));

        var second = await service.SendAsync(null, view.Id, "second", default);

        Assert.True(second.Value!.Queued);
        Assert.Equal(1, service.Find(null, view.Id)!.Queued);
        Assert.Equal(["hold:first"], _fake.Prompts(view.Id));
        _fake.Release(view.Id);
        var events = await Read(service, view.Id, seen => seen.Count(e => Kind(e) == "state:idle") == 3);
        Assert.Equal(["hold:first", "second"], _fake.Prompts(view.Id));
        Assert.Equal(["message:user", "message:user"], Kinds(events.Where(e => Kind(e) == "message:user")));
        var done = service.Find(null, view.Id)!;
        Assert.Equal((2, 0, ConversationState.Idle), (done.Messages, done.Queued, done.State));
    }

    [Theory]
    [InlineData("allow_once", "completed", "Edited README.md.")]
    [InlineData("reject", "failed", "Left README.md alone.")]
    public async Task An_approval_the_agent_asks_for_is_answered_once_and_the_turn_goes_on(string decision, string status, string said)
    {
        var service = Service();
        var view = await Started(service);
        var before = view.LastEventId;
        await service.SendAsync(null, view.Id, "approve:edit", default);
        var asked = await Read(service, view.Id, seen => seen.Any(e => e.Type == ConversationEventTypes.ApprovalRequested), before);

        var request = Data<ApprovalRequested>(asked[^1]);
        Assert.Equal(("call-1", "edit", "README.md"), (request.CallId, request.Kind, request.Locations![0].Path));
        Assert.Equal(["call-1"], service.Find(null, view.Id)!.PendingApprovals);
        var answered = await service.AnswerAsync(null, view.Id, "call-1", decision, default);
        Assert.Equal(decision, answered.Value!.Decision);

        var rest = await UntilIdle(service, view.Id, asked[^1].Id);
        Assert.Equal(["approval_answered", "tool_call_update", "message:assistant", "usage", "state:idle"], Kinds(rest));
        Assert.Equal(status, Data<ToolCallUpdate>(rest[1]).Status);
        Assert.Equal(said, Data<MessageChunk>(rest[2]).Text);
        Assert.Empty(service.Find(null, view.Id)!.PendingApprovals);
    }

    [Fact]
    public async Task An_approval_that_is_unknown_wrong_or_answered_already_is_refused_with_its_own_kind()
    {
        var service = Service();
        var view = await Started(service);
        await service.SendAsync(null, view.Id, "approve:edit", default);
        await Read(service, view.Id, seen => seen.Any(e => e.Type == ConversationEventTypes.ApprovalRequested));

        Assert.Equal(ProblemKind.NotFound, (await service.AnswerAsync(null, view.Id, "call-9", ApprovalAnswered.AllowOnce, default)).Problem!.Kind);
        Assert.Equal(ProblemKind.Invalid, (await service.AnswerAsync(null, view.Id, "call-1", "always", default)).Problem!.Kind);
        Assert.True((await service.AnswerAsync(null, view.Id, "call-1", ApprovalAnswered.AllowOnce, default)).Ok);
        Assert.Equal(ProblemKind.Conflict, (await service.AnswerAsync(null, view.Id, "call-1", ApprovalAnswered.Reject, default)).Problem!.Kind);
    }

    [Fact]
    public async Task Ending_a_conversation_during_a_turn_writes_ended_user_and_ends_the_stream()
    {
        var service = Service();
        var view = await Started(service);
        await service.SendAsync(null, view.Id, "hold:first", default);
        await Read(service, view.Id, seen => seen.Any(e => Kind(e) == "message:assistant"));
        var tail = Read(service, view.Id, _ => false, after: 5);

        var ended = await service.EndAsync(null, view.Id);

        Assert.Equal((ConversationState.Ended, "user"), (ended.Value!.State, ended.Value.EndedReason));
        Assert.Equal(["ended:user"], Kinds(await tail));
        Assert.Equal(["state:starting", "state:idle", "message:user", "state:running", "message:assistant", "ended:user"], Kinds(await Read(service, view.Id, _ => false)));
        Assert.Equal(ProblemKind.Conflict, (await service.SendAsync(null, view.Id, "more", default)).Problem!.Kind);
        Assert.True((await service.EndAsync(null, view.Id)).Ok);
        Assert.Equal(ConversationState.Ended, service.Find(null, view.Id)!.State);
    }

    [Fact]
    public async Task A_stream_resumes_after_an_event_without_losing_or_repeating_one()
    {
        var service = Service();
        var view = await Started(service);
        var live = Read(service, view.Id, seen => seen.Any(e => Kind(e) == "state:idle"), after: 2);
        await service.SendAsync(null, view.Id, "hello", default);

        Assert.Equal([3L, 4L, 5L, 6L, 7L, 8L], (await live).Select(e => e.Id));
        Assert.Equal([6L, 7L, 8L], (await Read(service, view.Id, seen => seen.Count == 3, after: 5)).Select(e => e.Id));
        Assert.Equal(8, (await Read(service, view.Id, seen => seen.Count == 8)).Count);
    }

    [Fact]
    public async Task A_conversation_still_open_when_the_process_stopped_is_lost_when_the_next_one_starts()
    {
        Journal("conv-old-1", Events.Opened().Add(new MessageChunk("user", "hi")));
        Journal("conv-old-2", Events.Opened().Add(new EndedData(EndedData.User)));
        Journal("conv-bad-1", new Events().Add(new MessageChunk("user", "no start")));
        var service = Service();

        await service.RecoverAsync(default);

        var lost = service.Find(null, "conv-old-1")!;
        Assert.Equal((ConversationState.Lost, "lost", 4L, 1), (lost.State, lost.EndedReason, lost.LastEventId, lost.Messages));
        Assert.Equal(["state:starting", "state:idle", "message:user", "ended:lost"], Kinds(await Read(service, "conv-old-1", _ => false)));
        var ended = service.Find(null, "conv-old-2")!;
        Assert.Equal((ConversationState.Ended, "user", 3L), (ended.State, ended.EndedReason, ended.LastEventId));
        Assert.Equal(["conv-bad-1"], service.Unreadable);
        Assert.Null(service.Find(null, "conv-bad-1"));
        Assert.True((await service.SendAsync(null, "conv-old-1", "again", default)).Problem is { Kind: ProblemKind.Conflict });
        Assert.NotNull((await service.StartAsync(null, new StartConversation("acme/widgets", Commit, "fake"), default)).Value);
    }

    [Fact]
    public async Task Stopping_the_server_ends_an_open_conversation_as_lost_once()
    {
        var first = new ConversationService(_fake, Dir, new ConversationSettings(), null, _clock);
        var view = await Started(first);
        await first.DisposeAsync();
        var next = Service();

        await next.RecoverAsync(default);

        Assert.Equal(["state:starting", "state:idle", "ended:lost"], Kinds(await Read(next, view.Id, _ => false)));
        Assert.Equal(ConversationState.Lost, next.Find(null, view.Id)!.State);
    }

    [Fact]
    public async Task An_account_sees_its_own_conversations_and_the_owner_and_the_cli_see_all()
    {
        var service = Service();
        var (alice, bob) = (People.Account("alice"), People.Account("bob"));
        var view = await Started(service, alice);

        Assert.Equal("alice", view.Actor);
        Assert.Null(service.Find(bob, view.Id));
        Assert.Null(service.Events(bob, view.Id, 0, default));
        Assert.Equal(ProblemKind.NotFound, (await service.SendAsync(bob, view.Id, "hi", default)).Problem!.Kind);
        Assert.Equal(ProblemKind.NotFound, (await service.AnswerAsync(bob, view.Id, "call-1", ApprovalAnswered.Reject, default)).Problem!.Kind);
        Assert.Equal(ProblemKind.NotFound, (await service.EndAsync(bob, view.Id)).Problem!.Kind);
        Assert.Equal(ConversationState.Idle, service.Find(alice, view.Id)!.State);
        Assert.NotNull(service.Find(Principal.Owner, view.Id));
        Assert.NotNull(service.Find(null, view.Id));
    }

    [Fact]
    public async Task A_conversation_takes_at_most_max_messages_and_a_queued_one_counts()
    {
        var service = Service(settings: new ConversationSettings(MaxMessages: 2));
        var view = await Started(service);
        Assert.Equal(new ConversationCaps(2), view.Caps);
        await service.SendAsync(null, view.Id, "hold:first", default);
        await service.SendAsync(null, view.Id, "second", default);

        var third = await service.SendAsync(null, view.Id, "third", default);

        Assert.Equal(ProblemKind.Refused, third.Problem!.Kind);
        Assert.Equal(("conversation_cap_exceeded", AccountRefusal.TooManyRequests), (third.Problem.Refusal!.Code, third.Problem.Refusal.Status));
        _fake.Release(view.Id);
    }

    [Fact]
    public async Task An_account_s_spend_counts_its_runs_and_its_conversations_against_its_cap()
    {
        List<DateTimeOffset> windows = [];
        var log = new SpendLog((_, since) =>
        {
            windows.Add(since);
            return new ActorSpend(0.5m, 0);
        });
        var service = Service(new FakeConversationRuntime(usdPerTurn: 0.6m), log: log);
        var alice = People.Account("alice", usdPerDay: 1m);
        var view = await Started(service, alice);
        await Say(service, view.Id, "first", alice);

        var refused = await service.SendAsync(alice, view.Id, "second", default);

        var refusal = refused.Problem!.Refusal!;
        Assert.Equal(("budget_exceeded", AccountRefusal.TooManyRequests, "day", "usd", 1.1m), (refusal.Code, refusal.Status, refusal.Window, refusal.Unit, refusal.Spent));
        Assert.All(windows, since => Assert.Equal(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero), since));
        Assert.Equal(1, service.Find(alice, view.Id)!.Messages);
    }

    [Fact]
    public async Task An_account_holds_at_most_max_concurrent_runs_conversations_open()
    {
        var service = Service();
        var alice = People.Account("alice", concurrent: 1);
        var first = await Started(service, alice);

        var second = await service.StartAsync(alice, new StartConversation("acme/widgets", Commit, "fake"), default);

        Assert.Equal(("concurrency_exceeded", AccountRefusal.TooManyRequests), (second.Problem!.Refusal!.Code, second.Problem.Refusal.Status));
        await service.EndAsync(alice, first.Id);
        Assert.True((await service.StartAsync(alice, new StartConversation("acme/widgets", Commit, "fake"), default)).Ok);
    }

    [Fact]
    public async Task An_account_with_repositories_may_start_only_inside_them()
    {
        var service = Service();
        var alice = People.Account("alice", repositories: ["/srv/allowed"]);

        var outside = await service.StartAsync(alice, new StartConversation("acme/widgets", Commit, "fake"), default);

        Assert.Equal(("repository_not_allowed", AccountRefusal.Forbidden), (outside.Problem!.Refusal!.Code, outside.Problem.Refusal.Status));
        Assert.True((await service.StartAsync(alice, new StartConversation("/srv/allowed/widgets", Commit, "fake"), default)).Ok);
    }

    [Fact]
    public async Task A_start_with_a_bad_field_is_invalid_and_names_each_field()
    {
        var service = Service();

        var bad = await service.StartAsync(null, new StartConversation("", "HEAD", "nope"), default);

        Assert.Equal(ProblemKind.Invalid, bad.Problem!.Kind);
        Assert.Equal(3, bad.Problem.Errors.Count);
        Assert.Contains(bad.Problem.Errors, e => e.StartsWith("repository", StringComparison.Ordinal));
        Assert.Contains(bad.Problem.Errors, e => e.StartsWith("commit", StringComparison.Ordinal));
        Assert.Contains(bad.Problem.Errors, e => e.Contains("'nope'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_message_without_text_is_invalid(string text)
    {
        var service = Service();
        var view = await Started(service);

        Assert.Equal(ProblemKind.Invalid, (await service.SendAsync(null, view.Id, text, default)).Problem!.Kind);
        Assert.Equal(ProblemKind.Invalid, (await service.SendAsync(null, view.Id, new string('x', ConversationService.MaxMessageChars + 1), default)).Problem!.Kind);
        Assert.Equal(ProblemKind.NotFound, (await service.SendAsync(null, "conv-nope", "hi", default)).Problem!.Kind);
    }

    [Fact]
    public async Task A_runtime_that_does_not_start_leaves_a_failed_conversation_the_caller_can_read()
    {
        _fake.StartFailure = new InvalidOperationException("no agent on this host");
        var service = Service();

        var started = await service.StartAsync(null, new StartConversation("acme/widgets", Commit, "fake"), default);

        Assert.Equal(ProblemKind.Unavailable, started.Problem!.Kind);
        var id = started.Problem.ConversationId!;
        var failed = service.Find(null, id)!;
        Assert.Equal((ConversationState.Failed, "error"), (failed.State, failed.EndedReason));
        Assert.Equal(["state:starting", "error", "ended:error"], Kinds(await Read(service, id, _ => false)));
        Assert.True(Data<ErrorData>((await Read(service, id, seen => seen.Count == 2))[1]).Fatal);
    }

    [Fact]
    public async Task A_fatal_error_from_the_agent_fails_the_conversation_and_the_agent_closing_it_ends_it()
    {
        var service = Service();
        var broken = await Started(service);
        _fake.Emit(broken.Id, new Emitted(new ErrorData("the agent crashed", true)));
        var closed = await Started(service);
        _fake.Emit(closed.Id, new RuntimeEnded());

        Assert.Equal(["error", "ended:error"], Kinds(await Read(service, broken.Id, _ => false, after: 2)));
        Assert.Equal((ConversationState.Failed, "error"), (service.Find(null, broken.Id)!.State, service.Find(null, broken.Id)!.EndedReason));
        Assert.Equal(["ended:runtime_exited"], Kinds(await Read(service, closed.Id, _ => false, after: 2)));
        Assert.Equal((ConversationState.Ended, "runtime_exited"), (service.Find(null, closed.Id)!.State, service.Find(null, closed.Id)!.EndedReason));
    }

    [Fact]
    public void The_settings_are_valid_by_default_and_name_what_is_wrong()
    {
        Assert.Empty(new ConversationSettings().Problems());
        Assert.Equal(["runtime must name a runtime", "max_messages must be 1 to 10000"], new ConversationSettings(" ", 0).Problems());
    }
}
