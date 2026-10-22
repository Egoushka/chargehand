using System.Globalization;
using System.Text;

namespace Chargehand.Automation;

/// <summary>Sends a notice as plain text to an ntfy-style URL (the notifier schedules use): the questions and a link to the task, where it is answered. No buttons: ntfy has nowhere to send an answer back
/// that this server would trust. A withdrawal sends nothing.</summary>
public sealed class NtfyQuestionSink(string url, string? consoleUrl, IScheduleNotifier notifier) : IQuestionSink
{
    public string Name => "ntfy";

    public Task SendAsync(QuestionNotice notice, CancellationToken ct) => notifier.PostAsync(url, Title(notice), Text(notice, consoleUrl), ct);

    public Task WithdrawAsync(QuestionNotice notice, CancellationToken ct) => Task.CompletedTask;

    public static string Title(QuestionNotice notice) => notice.Kind == QuestionKinds.Question ? $"chargehand asks: {notice.Title}" : $"chargehand stopped early: {notice.Title}";

    public static string Text(QuestionNotice notice, string? consoleUrl)
    {
        var text = new StringBuilder($"{notice.Repository ?? "no repository"}, task {notice.TaskId}.");
        if (notice.Kind == QuestionKinds.Question)
            foreach (var (question, i) in notice.Questions.Select((q, i) => (q, i)))
                text.Append(CultureInfo.InvariantCulture, $"\n{i + 1}. {question}");
        else
            text.Append("\nThe session stopped before it finished; its work so far is kept and can be continued.");
        if (notice.PullRequest is { } pr)
            text.Append(CultureInfo.InvariantCulture, $"\nPull request: {pr}");
        if (consoleUrl is not null)
            text.Append(CultureInfo.InvariantCulture, $"\nAnswer: {ApprovalsSink.TaskUrl(consoleUrl, notice)}");
        return ScheduleResults.Cut(text.ToString());
    }
}
