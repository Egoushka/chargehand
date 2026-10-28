using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Conversations.Adapters;
using Chargehand.Modules.Conversations.Core;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Conversations.Api;

/// <summary>The conversations of one server (ADR 0086): start one, read it, stream its events, message it, answer its approvals and end it. Who may see what is the run rule (ADR 0050):
/// the owner and a call with no principal (the CLI) see every conversation, an account only its own, and another's is not found. The account's caps apply at the start and on each
/// message. Events are journaled in <paramref name="directory"/>; a conversation still open when the server stopped is lost when the next one starts.</summary>
/// <param name="directory">Where the journals live, <c>conversations/</c> beside the run log.</param>
/// <param name="log">The run log, for what an account's runs cost in a window; null: only its conversations count.</param>
/// <param name="credentials">The pause on a runtime's credential; null: a rejected credential is not remembered.</param>
public sealed partial class ConversationService(IConversationRuntime runtime, string directory, ConversationSettings settings, IRunLog? log = null, TimeProvider? clock = null,
    ConversationCredentials? credentials = null) : IAsyncDisposable
{
    /// <summary>The longest message taken, in characters.</summary>
    public const int MaxMessageChars = 100_000;

    private readonly ConcurrentDictionary<string, IConversationEntry> _entries = new();
    private readonly ConversationJournal _journal = new(directory);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The ids of journals <see cref="RecoverAsync"/> could not read; they are left as they are.</summary>
    public IReadOnlyList<string> Unreadable { get; private set; } = [];

    private ConversationCaps Caps => new(settings.MaxMessages);

    /// <summary>Takes up the journals of the earlier process. One whose conversation was still open ends as <c>lost</c>, with the event that says so.</summary>
    public Task RecoverAsync(CancellationToken ct)
    {
        List<string> unreadable = [];
        foreach (var id in _journal.Ids())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var events = _journal.Read(id);
                var snapshot = ConversationReducer.Fold(id, events);
                if (!snapshot.IsOver)
                {
                    var lost = ConversationJson.ToEvent(snapshot.LastEventId + 1, _clock.GetUtcNow(), new EndedData(EndedData.Lost));
                    using (var writer = _journal.Open(id))
                        writer.Append(lost);
                    snapshot = ConversationReducer.Apply(snapshot, id, lost);
                }

                _entries[id] = new ClosedConversation(snapshot, _journal);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                unreadable.Add(id);
            }
        }

        Unreadable = unreadable;
        return Task.CompletedTask;
    }

    public async Task<ConversationOutcome<ConversationView>> StartAsync(Principal? who, StartConversation request, CancellationToken ct)
    {
        if (Problems(request) is { Count: > 0 } errors)
            return ConversationOutcome.Fail<ConversationView>(ProblemKind.Invalid, [.. errors]);
        var key = runtime.CredentialKey(request.Runtime!);
        if (key is not null && credentials?.Refusal(key) is { } paused)
            return ConversationOutcome.Refuse<ConversationView>(paused);
        var now = _clock.GetUtcNow();
        var (day, month) = await SpendAsync(who, now, ct);
        if (ConversationAdmission.AtStart(who, request.Repository!, request.Commit!, request.Preset, OpenOf(who?.Name), day, month, now) is { } refusal)
            return ConversationOutcome.Refuse<ConversationView>(refusal);

        var id = ConversationIds.New(now);
        var repository = new RepositoryRef(request.Repository!, request.Commit!);
        var handle = ConversationHandle.Open(id, runtime, _journal, _clock,
            new StateData(ConversationState.Starting, Repository: repository, Runtime: request.Runtime, Model: request.Model, Preset: request.Preset, Actor: who?.Name),
            key is null ? null : credentials?.Watch(key));
        try
        {
            handle.Started(await runtime.StartAsync(new RuntimeStart(id, request.Runtime!, repository, request.Model, request.Preset), ct));
        }
        catch (Exception e)
        {
            handle.StartFailed(ChargehandException.Scrub(e.Message));
            _entries[id] = handle;
            if (e is OperationCanceledException)
                throw;
            return ConversationOutcome.Fail<ConversationView>(new ConversationProblem(ProblemKind.Unavailable, [$"the {request.Runtime} runtime did not start: {ChargehandException.Scrub(e.Message)}"], ConversationId: id));
        }

        _entries[id] = handle;
        return ConversationOutcome.Of(ViewOf(handle));
    }

    /// <summary>The conversation, or null when there is none or <paramref name="who"/> may not see it.</summary>
    public ConversationView? Find(Principal? who, string id) => Visible(who, id) is { } entry ? ViewOf(entry) : null;

    /// <summary>The events after <paramref name="after"/> (0: all), then those that follow until the conversation is over; null when there is no such conversation for <paramref name="who"/>.</summary>
    public IAsyncEnumerable<ConversationEvent>? Events(Principal? who, string id, long after, CancellationToken ct) =>
        Visible(who, id)?.Events(after, ct);

    public async Task<ConversationOutcome<MessageAccepted>> SendAsync(Principal? who, string id, string? text, CancellationToken ct)
    {
        if (Visible(who, id) is not { } entry)
            return ConversationOutcome.Fail<MessageAccepted>(ProblemKind.NotFound);
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxMessageChars)
            return ConversationOutcome.Fail<MessageAccepted>(ProblemKind.Invalid, $"text is required and takes at most {MaxMessageChars} characters");
        if (entry is not ConversationHandle handle || entry.Snapshot.IsOver)
            return Over<MessageAccepted>(entry);
        var now = _clock.GetUtcNow();
        var (day, month) = await SpendAsync(who, now, ct);
        if (ConversationAdmission.AtMessage(who, handle.Snapshot, handle.Queued, Caps, day, month, now) is { } refusal)
            return ConversationOutcome.Refuse<MessageAccepted>(refusal);
        return await handle.SendAsync(text) switch
        {
            SendResult.Over => Over<MessageAccepted>(entry),
            var sent => ConversationOutcome.Of(new MessageAccepted(id, sent == SendResult.Queued, handle.Snapshot.Messages + handle.Queued)),
        };
    }

    /// <param name="decision"><c>allow_once</c> or <c>reject</c>.</param>
    public async Task<ConversationOutcome<ApprovalAnswered>> AnswerAsync(Principal? who, string id, string callId, string? decision, CancellationToken ct)
    {
        if (Visible(who, id) is not { } entry)
            return ConversationOutcome.Fail<ApprovalAnswered>(ProblemKind.NotFound);
        if (decision is not (ApprovalAnswered.AllowOnce or ApprovalAnswered.Reject))
            return ConversationOutcome.Fail<ApprovalAnswered>(ProblemKind.Invalid, $"decision must be {ApprovalAnswered.AllowOnce} or {ApprovalAnswered.Reject}: a permission is never saved");
        if (entry is not ConversationHandle handle)
            return Over<ApprovalAnswered>(entry);
        var result = await handle.AnswerAsync(callId, decision);
        return result.Outcome switch
        {
            AnswerOutcome.Answered => ConversationOutcome.Of(new ApprovalAnswered(callId, decision)),
            AnswerOutcome.Unknown => ConversationOutcome.Fail<ApprovalAnswered>(ProblemKind.NotFound),
            AnswerOutcome.AlreadyAnswered => ConversationOutcome.Fail<ApprovalAnswered>(ProblemKind.Conflict, $"{callId} is answered already"),
            AnswerOutcome.RuntimeFailed => ConversationOutcome.Fail<ApprovalAnswered>(ProblemKind.Unavailable, result.Message ?? "the runtime did not take the answer"),
            _ => Over<ApprovalAnswered>(entry),
        };
    }

    /// <summary>Cancels the running turn and ends the conversation. One that is over stays as it is: ending it again is not an error.</summary>
    public async Task<ConversationOutcome<ConversationView>> EndAsync(Principal? who, string id)
    {
        if (Visible(who, id) is not { } entry)
            return ConversationOutcome.Fail<ConversationView>(ProblemKind.NotFound);
        if (entry is ConversationHandle handle)
            await handle.EndAsync();
        return ConversationOutcome.Of(ViewOf(entry));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var handle in _entries.Values.OfType<ConversationHandle>())
            await handle.DisposeAsync();
    }

    private IConversationEntry? Visible(Principal? who, string id) =>
        _entries.TryGetValue(id, out var entry) && Principal.Sees(who, entry.Snapshot.Actor) ? entry : null;

    private ConversationView ViewOf(IConversationEntry entry) => entry.Snapshot.ToView(Caps, entry.Queued);

    private static ConversationOutcome<T> Over<T>(IConversationEntry entry) =>
        ConversationOutcome.Fail<T>(ProblemKind.Conflict, $"conversation {entry.Snapshot.Id} is {entry.Snapshot.State.ToString().ToLowerInvariant()}; start another");

    /// <summary>An account's open conversations count against <c>max_concurrent_runs</c>, as its unfinished runs do.</summary>
    private int OpenOf(string? actor) =>
        actor is null ? 0 : _entries.Values.Count(e => e.Snapshot.Actor == actor && !e.Snapshot.IsOver);

    private List<string> Problems(StartConversation request)
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(request.Repository) || request.Repository.Length > 512)
            errors.Add("repository is required: the repository's path or owner/name, at most 512 characters");
        if (request.Commit is null || !CommitPattern().IsMatch(request.Commit))
            errors.Add("commit is required: the commit the checkout is pinned to, 7 to 40 lowercase hex digits");
        if (string.IsNullOrWhiteSpace(request.Runtime))
            errors.Add("runtime is required");
        else if (!runtime.Supports(request.Runtime))
            errors.Add($"unknown runtime '{request.Runtime}'");
        if (request.Model is { Length: > 200 })
            errors.Add("model takes at most 200 characters");
        if (request.Preset is { Length: > 64 })
            errors.Add("preset takes at most 64 characters");
        return errors;
    }

    [GeneratedRegex("^[0-9a-f]{7,40}$")]
    private static partial Regex CommitPattern();

    /// <summary>What <paramref name="who"/>'s account has spent today and this month: its runs in the run log, and its conversations.</summary>
    private async Task<(ActorSpend Day, ActorSpend Month)> SpendAsync(Principal? who, DateTimeOffset now, CancellationToken ct)
    {
        ActorSpend none = new(0, 0);
        if (who?.Account is not { } account)
            return (none, none);
        var day = AccountPolicy.UsesDay(account) ? await SpentAsync(account.Name, AccountPolicy.DayStart(now), ct) : none;
        var month = AccountPolicy.UsesMonth(account) ? await SpentAsync(account.Name, AccountPolicy.MonthStart(now), ct) : none;
        return (day, month);
    }

    private async Task<ActorSpend> SpentAsync(string actor, DateTimeOffset since, CancellationToken ct)
    {
        var runs = log is null ? new ActorSpend(0, 0) : await log.SpendAsync(actor, since, ct);
        var turns = _entries.Values.Select(e => e.Snapshot).Where(s => s.Actor == actor).SelectMany(s => s.UsageLog).Where(u => u.At >= since).ToList();
        var usd = turns.Sum(u => u.Usd ?? 0);
        return runs with { Usd = runs.Usd + usd, Tokens = runs.Tokens + turns.Sum(u => u.Tokens), UsdAtApiPrice = runs.UsdAtApiPrice + usd };
    }
}
