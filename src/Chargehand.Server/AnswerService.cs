using Chargehand.Automation;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Runs the question notifier while the server runs (ADR 0077), started with the host after <see cref="BatchResumer"/> and stopped with it.</summary>
internal sealed class AnswerService(QuestionNotifier notifier) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    public Task StartAsync(CancellationToken ct)
    {
        _loop = Task.Run(() => notifier.RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _stop.CancelAsync();
        await _loop.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose() => _stop.Dispose();
}

/// <summary>The open questions and partial stops of the console's inbox (the owner's view: every account), as notices.</summary>
internal sealed class ServerQuestionSource(Func<ConsoleContext> context) : IQuestionSource
{
    public async Task<IReadOnlyList<QuestionNotice>> OpenAsync(CancellationToken ct)
    {
        var c = context();
        var view = await ConsoleInbox.ViewAsync(c, null, c.Now - ConsoleInbox.DefaultWindow, ct);
        return [.. view.Open.Select(e => e.Item).Where(i => i.BatchRunId is not null && i.TaskId is not null).Select(Notice).OfType<QuestionNotice>()];
    }

    private static QuestionNotice? Notice(InboxItem item) => ConsoleAnswers.KindOf(item) is { } kind
        ? new QuestionNotice(item.Id, kind, item.BatchRunId!, item.TaskId!, item.Repository, item.Title, item.Questions, item.At, item.PullRequest?.Url)
        : null;
}
