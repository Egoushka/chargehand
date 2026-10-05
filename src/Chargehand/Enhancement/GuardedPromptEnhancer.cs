namespace Chargehand.Enhancement;

/// <summary>
/// Makes any <see cref="IPromptEnhancer"/> safe to call (ADR 0040): the user's prompt is never worse off for asking. A late,
/// failing or nonsensical answer becomes the original prompt with a reason; only the caller's own cancellation escapes.
/// </summary>
/// <param name="deadline">How long to wait for an answer, whether or not the enhancer honours its token (default 1.5 s).</param>
/// <param name="time">The clock the deadline runs on; tests pass one they advance by hand.</param>
public sealed class GuardedPromptEnhancer(IPromptEnhancer inner, TimeSpan? deadline = null, TimeProvider? time = null) : IPromptEnhancer
{
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromMilliseconds(1500);

    private readonly TimeSpan _deadline = deadline ?? DefaultDeadline;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<Enhanced> EnhanceAsync(string prompt, EnhanceContext context, CancellationToken ct)
    {
        using var expiry = new CancellationTokenSource(_deadline, _time);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
        // Task.Run: an enhancer that throws or blocks before its first await must not escape the deadline either.
        var call = Task.Run(() => inner.EnhanceAsync(prompt, context, budget.Token), CancellationToken.None);
        try
        {
            await Task.WhenAny(call, Task.Delay(Timeout.Infinite, budget.Token));
        }
        catch (OperationCanceledException)
        {
            // The deadline or the caller; the caller's is rethrown below.
        }
        ct.ThrowIfCancellationRequested();
        if (!call.IsCompleted)
        {
            // Observe a late failure so it never surfaces as an unobserved task exception.
            _ = call.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return Enhanced.Unchanged(prompt, "enhancer did not answer in time");
        }
        try
        {
            return Sane(prompt, await call);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The type only: a message may quote the prompt.
            return Enhanced.Unchanged(prompt, $"enhancer failed ({e.GetType().Name})");
        }
    }

    /// <summary>Feedback is best effort: a failure here never reaches the run it reports on.</summary>
    public async Task FeedbackAsync(string requestId, EnhanceOutcome outcome, CancellationToken ct)
    {
        using var expiry = new CancellationTokenSource(_deadline, _time);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
        var call = Task.Run(() => inner.FeedbackAsync(requestId, outcome, budget.Token), CancellationToken.None);
        try
        {
            await Task.WhenAny(call, Task.Delay(Timeout.Infinite, budget.Token));
            if (call.IsCompleted)
                await call;
            else
                _ = call.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Nothing to do: the run being reported on has already finished.
        }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>A rewrite that is empty or the same text is no rewrite; the answer's original is always the caller's.</summary>
    private static Enhanced Sane(string prompt, Enhanced answer) =>
        answer.Rewrite is { } rewrite && !string.IsNullOrWhiteSpace(rewrite) && !string.Equals(rewrite, prompt, StringComparison.Ordinal)
            ? answer with { Original = prompt }
            : answer with { Original = prompt, Rewrite = null };
}
