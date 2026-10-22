namespace Chargehand.Automation;

/// <summary>Sends each new question and partial stop of the inbox to every sink, once, and takes it back from them when it leaves the inbox (ADR 0077). It wakes every minute. Its first wake records the
/// moment notices begin: what was open before is not sent. A sink that fails is tried again at the next wake; the other sinks are not held up by it.</summary>
public sealed class QuestionNotifier(IQuestionSource source, IReadOnlyList<IQuestionSink> sinks, NotifyState state, TimeProvider? clock = null, Action<string>? warn = null)
{
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private readonly Action<string> _warn = warn ?? (message => Console.Error.WriteLine($"chargehand: {message}"));

    /// <summary>Wakes until <paramref name="ct"/> ends. A failed wake is reported and tried again at the next one.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _warn($"notify: wake failed: {e.Message}");
            }
            try
            {
                await Task.Delay(Tick, _time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var since = state.Change(now, s => s.Since ??= now);
        var open = await source.OpenAsync(ct);
        foreach (var notice in open.Where(n => n.At >= since).OrderBy(n => n.At))
            await SendAsync(notice, ct);
        var openIds = open.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var sent in state.Read(s => s.Sent.Values.Where(x => !openIds.Contains(x.Notice.Id)).ToList()))
            await WithdrawAsync(sent, ct);
    }

    private async Task SendAsync(QuestionNotice notice, CancellationToken ct)
    {
        var (reached, round) = state.Read(s => s.Sent.GetValueOrDefault(notice.Id) is { } sent ? (sent.Sinks.ToList(), sent.Notice.Round) : ([], 0));
        notice = notice with { Round = round };
        foreach (var sink in sinks.Where(k => !reached.Contains(k.Name)))
        {
            if (!await TryAsync(() => sink.SendAsync(notice, ct), $"{sink.Name}: {notice.Id} was not sent"))
                continue;
            state.Change(_time.GetUtcNow(), s =>
            {
                if (!s.Sent.TryGetValue(notice.Id, out var entry))
                    s.Sent[notice.Id] = entry = new SentNotice(notice, []);
                else
                    s.Sent[notice.Id] = entry = entry with { Notice = notice };
                entry.Sinks.Add(sink.Name);
                return true;
            });
        }
    }

    private async Task WithdrawAsync(SentNotice sent, CancellationToken ct)
    {
        foreach (var sink in sinks.Where(k => sent.Sinks.Contains(k.Name)))
            if (await TryAsync(() => sink.WithdrawAsync(sent.Notice, ct), $"{sink.Name}: {sent.Notice.Id} was not taken back"))
                state.Change(_time.GetUtcNow(), s => s.Sent.GetValueOrDefault(sent.Notice.Id)?.Sinks.Remove(sink.Name) ?? false);
        // A sink no longer configured cannot take it back; the entry goes once the configured ones have.
        state.Change(_time.GetUtcNow(), s => s.Sent.GetValueOrDefault(sent.Notice.Id) is { } entry && !sinks.Any(k => entry.Sinks.Contains(k.Name)) && s.Sent.Remove(sent.Notice.Id));
    }

    /// <summary>A sink's failure, or a secret that does not resolve, is a warning: the step is tried again at the next wake.</summary>
    private async Task<bool> TryAsync(Func<Task> step, string what)
    {
        try
        {
            await step();
            return true;
        }
        catch (Exception e) when (e is IntakeException or InvalidOperationException)
        {
            _warn($"notify: {what}: {e.Message}");
            return false;
        }
    }
}
