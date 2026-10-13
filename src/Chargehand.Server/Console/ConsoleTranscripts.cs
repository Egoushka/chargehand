using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;

namespace Chargehand.Server;

/// <summary>Which session a transcript is of.</summary>
public sealed record TranscriptSessionBody(string RunId, string Role, string? Runner, DateTimeOffset? Started, DateTimeOffset? Ended, string? Status);

/// <summary><c>transcript/v1</c>: the end of one session's transcript (ADR 0059). <paramref name="Available"/> is false when there is none to show and <paramref name="Reason"/> says why: <c>session_gone</c> (it left none,
/// or its output volume was removed), <c>runner_unreachable</c>, <c>runner_too_old</c>, <c>runner_error</c>, <c>runner_unknown</c> or <c>driven_off</c>.</summary>
/// <param name="Lines">Whole lines, oldest first, each one <c>stream-json</c> event as the session wrote it with secrets redacted, or a <c>chargehand_cut</c> stub for one too long to show. Text to render as text, never as markup.</param>
/// <param name="Bytes">What the runner returned; <paramref name="MaxBytes"/> is the window asked for.</param>
/// <param name="Truncated">Earlier lines exist.</param>
/// <param name="Redacted">The kinds of secret that were replaced; never a value.</param>
/// <param name="Cut">Lines replaced by a stub for their length.</param>
public sealed record TranscriptBody(string ContractVersion, string BatchRunId, string TaskId, TranscriptSessionBody Session, bool Available, string? Reason, int Bytes, int MaxBytes, bool Truncated, int Cut,
    IReadOnlyList<string> Redacted, IReadOnlyList<string> Lines, IReadOnlyList<string> NotRecorded);

/// <summary>Builds <c>transcript/v1</c>. The batch is the caller's own (an account) or any (the owner), as for every batch route, and the session must be one the task view lists, so a run id the caller guessed
/// reaches no volume. The transcript is read on request from where the session wrote it and is not stored, logged or sent in an event.</summary>
internal static class ConsoleTranscripts
{
    public const string Contract = "transcript/v1";

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "Only the end of a transcript is served, up to 256 KiB, and only while the session's output volume exists: the last session of a task, and an earlier one until a later one has its bundle.",
        "Secrets are redacted by pattern (cloud, GitHub, Anthropic and Slack tokens, private keys) and by the credentials this server holds; a secret it cannot recognise, such as a password in a repository file the model read, is not.",
        "A line over 32 KiB is replaced by a stub that gives its size.",
    ];

    /// <returns>Null when the batch, the task or the session is not the caller's to read, or does not exist.</returns>
    public static async Task<TranscriptBody?> BuildAsync(ConsoleContext c, Principal? who, string batchRunId, string taskId, string sessionRunId, int bytes, CancellationToken ct)
    {
        if (await ConsoleBatches.TaskAsync(c, who, batchRunId, taskId, 0, ct) is not { } detail
            || detail.Task.Sessions.FirstOrDefault(s => s.RunId == sessionRunId) is not { } session)
            return null;
        // A session recorded before runners had names says nothing: the saved state of a batch still running may.
        var runner = session.Runner ?? c.Driven?.LiveBatches().FirstOrDefault(b => b.RunId == batchRunId)?.Tasks.FirstOrDefault(t => t.Id == taskId)?.Runner;
        var read = c.Driven is { } driven
            ? await driven.ReadTranscriptAsync(sessionRunId, runner, bytes, detail.Actor, ct)
            : new TranscriptRead(null, "driven_off");
        var shown = read.View;
        return new TranscriptBody(Contract, batchRunId, taskId, new TranscriptSessionBody(session.RunId, session.Role, session.Runner ?? (string.IsNullOrEmpty(runner) ? null : runner), session.Started, session.Ended, session.Status),
            shown is not null, read.Unavailable, shown?.Bytes ?? 0, bytes, shown?.Truncated ?? false, shown?.Cut ?? 0, shown?.Redacted ?? [], shown?.Lines ?? [], NotRecorded);
    }
}
