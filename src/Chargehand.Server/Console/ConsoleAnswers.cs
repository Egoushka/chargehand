using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <summary>The answer an approvals server posts back (ADR 0077, docs/guide/phone-answers.md): <c>POST /v1/answers/approvals</c>. It carries no key: the body's HMAC signature with the approvals key is
/// the credential, and the key middleware lets this one path through to be checked here. A valid answer to an open question or partial stop starts a batch of that one task with <c>continue_from</c>
/// and the answer, as the account that started it; <c>drop</c> dismisses the inbox item. Every reply is <c>{ok, message}</c>, which the approvals server shows on the phone. A decision is acted on
/// once: sent again, it answers <c>409</c>, which the approvals server reads as already handled.</summary>
internal static class ConsoleAnswers
{
    public const string Path = "/v1/answers/approvals";

    public const string Actor = "approvals";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Map(WebApplication app, Func<HttpContext, ConsoleContext> context, PhoneAnswers phone) =>
        app.MapPost(Path, async (HttpContext ctx) =>
        {
            await Gate.WaitAsync(ctx.RequestAborted);
            try
            {
                return await AnswerAsync(context(ctx), phone, ctx);
            }
            finally
            {
                Gate.Release();
            }
        });

    private static IResult Reply(bool ok, string message, int status) => ChargehandServer.Json(new { ok, message }, status);

    private static async Task<IResult> AnswerAsync(ConsoleContext c, PhoneAnswers phone, HttpContext http)
    {
        ApprovalDecision? decision;
        string? problem;
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            (decision, problem) = ApprovalDecision.Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            (decision, problem) = (null, "the body is not JSON");
        }
        if (decision is null)
            return Reply(false, problem!, StatusCodes.Status400BadRequest);
        string key;
        try
        {
            key = phone.Key();
        }
        catch (InvalidOperationException)
        {
            return Reply(false, "chargehand cannot read its approvals key", StatusCodes.Status503ServiceUnavailable);
        }
        if (!decision.Verify(key, c.Now))
            return Reply(false, "the signature does not match or the decision is too old", StatusCodes.Status401Unauthorized);
        if (ApprovalDecision.Parse(phone.Settings.Approvals!.Source, decision.Id) is not var (kind, batch, task))
            return Reply(false, $"{decision.Id} is not a chargehand question", StatusCodes.Status400BadRequest);
        if (phone.State.Read(s => s.Decisions.GetValueOrDefault(decision.Decision)) is { } handled)
            return Reply(true, $"Already handled: {handled.Outcome}", StatusCodes.Status409Conflict);
        if (!AnswerActions.For(kind).Contains(decision.Action))
            return Reply(false, $"{decision.Action} is not an answer to a {kind}", StatusCodes.Status400BadRequest);
        var view = await ConsoleInbox.ViewAsync(c, null, c.Now - ConsoleInbox.DefaultWindow, http.RequestAborted);
        if (view.Open.Select(e => e.Item).FirstOrDefault(i => i.BatchRunId == batch && i.TaskId == task && KindOf(i) == kind) is not { } item)
            return Reply(true, "Already dealt with elsewhere; nothing started", StatusCodes.Status409Conflict);
        return await ActAsync(c, phone, decision, item, http);
    }

    /// <summary>The notice kind an inbox item is sent as, or null when it is not sent.</summary>
    public static string? KindOf(InboxItem item) => item switch
    {
        { Kind: "question" } => QuestionKinds.Question,
        { Kind: "pull_request", Reason: "partial_draft" } or { Kind: "failure", Reason: "partial", Subject: "task" } => QuestionKinds.Partial,
        _ => null,
    };

    private static async Task<IResult> ActAsync(ConsoleContext c, PhoneAnswers phone, ApprovalDecision decision, InboxItem item, HttpContext http)
    {
        void Handled(string outcome) => phone.State.Change(c.Now, s => s.Decisions[decision.Decision] = new HandledDecision(outcome, c.Now));
        switch (decision.Action)
        {
            case AnswerActions.Drop:
                if (c.Dismissals is not { } store)
                    return Reply(false, "this server keeps no dismissals; drop it on the console", StatusCodes.Status503ServiceUnavailable);
                store.Add([item.Id], item.Id, Actor, c.Now);
                Handled("dropped");
                return Reply(true, "Dropped; nothing started", StatusCodes.Status200OK);
            case AnswerActions.Answer when decision.Answer is null:
                // The approvals server cannot reopen a decided item: the question goes out again under a new id.
                phone.State.SendAgain(item.Id, "approvals", c.Now);
                Handled("sent again");
                return Reply(true, "Nothing was typed, so nothing started; the question comes again", StatusCodes.Status200OK);
        }
        IReadOnlyList<string>? answers = decision.Action switch
        {
            AnswerActions.Answer => [decision.Answer!],
            AnswerActions.Decide => [AnswerActions.DecideText],
            _ => decision.Answer is { } note ? [note] : null,
        };
        var (run, refusal, status) = await ContinueAsync(c, item.BatchRunId!, item.TaskId!, answers, http.RequestAborted);
        if (run is null)
            return Reply(false, refusal!, status);
        Handled($"started run {run}");
        ConsoleActions.Tell(c, null, item.BatchRunId!, item.TaskId, $"answered from {Actor}: continued as run {run}");
        return Reply(true, $"Started run {run}", StatusCodes.Status200OK);
    }

    /// <summary>A batch of the one task continuing it with <paramref name="answers"/>, started as the account that started the batch.</summary>
    private static async Task<(string? Run, string? Refusal, int Status)> ContinueAsync(ConsoleContext c, string batch, string task, IReadOnlyList<string>? answers, CancellationToken ct)
    {
        var index = await c.IndexAsync(ct);
        if (!index.Starts.TryGetValue(batch, out var start) || start.Request.Driven?.Tasks.FirstOrDefault(t => t.Id == task) is not { } asked)
            return (null, "the batch's request is not in the run log", StatusCodes.Status409Conflict);
        if (c.Runs.Halted)
            return (null, RunService.HaltedMessage, StatusCodes.Status503ServiceUnavailable);
        var who = start.Actor is { } actor && actor != AccountSettings.Owner ? new Principal(actor, c.Settings.Accounts?.FirstOrDefault(a => a.Name == actor)) : Principal.Owner;
        var again = asked with { Goal = null, Ref = null, DependsOn = null, ContinueFrom = new ContinueFrom(batch, task), Answers = answers };
        var request = start.Request with { Text = $"Answer to {batch}/{task}.", Driven = start.Request.Driven! with { Tasks = [again], MaxParallel = 1 } };
        var admitted = await c.Runs.AdmitAsync(who, request, ct);
        if (admitted.Refusal is { } refusal)
            return (null, refusal.Message, refusal.Status);
        return c.Runs.Start(admitted.Request, null, null, start.Actor) is { } run
            ? (run.Id, null, StatusCodes.Status200OK)
            : (null, $"{RunService.MaxUnfinished} runs are unfinished; retry later", StatusCodes.Status429TooManyRequests);
    }
}
