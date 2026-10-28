using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Conversations.Api;

namespace Chargehand.Modules.Conversations.Tests;

/// <summary>The pause on a runtime's credential: a conversation the provider rejected pauses the key, a new conversation on it is refused, and one is let through after the retry to find out.</summary>
public sealed class ConversationCredentialTests : IAsyncLifetime
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Key = "fake-key";
    private const string Rejection = "session/prompt failed with code -32000: authentication_error: invalid x-api-key";

    private readonly string _root = Directory.CreateTempSubdirectory("chargehand-conversations-").FullName;
    private readonly ManualClock _clock = new(Events.Start);
    private readonly CredentialHealth _health = new();
    private readonly List<(string Key, string? Error)> _reports = [];
    private readonly FakeConversationRuntime _fake = new() { Credential = Key };
    private readonly List<ConversationService> _services = [];

    private ConversationService Service => _services[0];

    public Task InitializeAsync()
    {
        _services.Add(new ConversationService(_fake, Path.Combine(_root, "conversations"), new ConversationSettings(), clock: _clock, credentials: Credentials()));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var service in _services)
            await service.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private ConversationCredentials Credentials() => new(_health, (key, error, ct) =>
    {
        _reports.Add((key, error));
        if (error is null)
            _health.Succeed(key);
        else
            _health.Fail(key, error, _clock.GetUtcNow());
        return Task.CompletedTask;
    }, _clock);

    private Task<ConversationOutcome<ConversationView>> Start() =>
        Service.StartAsync(null, new StartConversation("acme/widgets", Commit, FakeConversationRuntime.Name), default);

    private async Task<string> Rejected(string message = Rejection)
    {
        var id = (await Start()).Value!.Id;
        _fake.Emit(id, new Emitted(new ErrorData(message, false)));
        await Wait(id, e => ConversationJson.Parse(e) is ErrorData);
        return id;
    }

    private async Task Wait(string id, Func<ConversationEvent, bool> match)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var e in Service.Events(null, id, 0, cts.Token)!)
            if (match(e))
                return;
    }

    [Fact]
    public async Task A_conversation_the_provider_rejected_pauses_the_key_and_a_new_one_is_refused_credential_paused_503()
    {
        await Rejected();

        var refused = await Start();

        Assert.Equal(ProblemKind.Refused, refused.Problem!.Kind);
        Assert.Equal(("credential_paused", 503), (refused.Problem.Refusal!.Code, refused.Problem.Refusal.Status));
        Assert.Contains(Key, refused.Problem.Errors[0], StringComparison.Ordinal);
        Assert.Equal([(Key, Rejection)], _reports);
    }

    [Fact]
    public async Task One_conversation_is_let_through_each_retry_and_a_turn_that_works_resumes_the_key()
    {
        await Rejected();
        _clock.Advance(ConversationCredentials.Retry - TimeSpan.FromSeconds(1));
        Assert.Equal(ProblemKind.Refused, (await Start()).Problem!.Kind);
        _clock.Advance(TimeSpan.FromSeconds(1));

        var trial = await Start();

        Assert.True(trial.Ok);
        Assert.Equal(ProblemKind.Refused, (await Start()).Problem!.Kind);
        await Service.SendAsync(null, trial.Value!.Id, "hello", default);
        await Wait(trial.Value.Id, e => ConversationJson.Parse(e) is StateData { StopReason: "end_turn" });
        Assert.Null(_health.Paused(Key));
        Assert.Equal([(Key, Rejection), (Key, (string?)null)], _reports);
        Assert.True((await Start()).Ok);
    }

    [Fact]
    public async Task A_trial_that_is_rejected_again_keeps_the_key_paused_for_another_retry()
    {
        await Rejected();
        _clock.Advance(ConversationCredentials.Retry);
        var trial = (await Start()).Value!.Id;
        _fake.Emit(trial, new Emitted(new ErrorData(Rejection, false)));
        await Wait(trial, e => ConversationJson.Parse(e) is ErrorData);

        Assert.Equal(ProblemKind.Refused, (await Start()).Problem!.Kind);
        _clock.Advance(ConversationCredentials.Retry);
        Assert.True((await Start()).Ok);
    }

    [Fact]
    public async Task An_error_that_is_not_a_rejected_credential_pauses_nothing()
    {
        await Rejected("the agent could not read the repository");

        Assert.True((await Start()).Ok);
        Assert.Empty(_reports);
    }

    [Fact]
    public async Task A_runtime_with_no_credential_is_never_refused_for_a_paused_key()
    {
        _health.Fail(Key, Rejection, _clock.GetUtcNow());
        var keyless = new ConversationService(new FakeConversationRuntime(), Path.Combine(_root, "keyless"), new ConversationSettings(), clock: _clock, credentials: Credentials());
        _services.Add(keyless);

        var started = await keyless.StartAsync(null, new StartConversation("acme/widgets", Commit, FakeConversationRuntime.Name), default);

        Assert.True(started.Ok);
    }
}
