using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <summary>Everything <c>automation.notify</c> sets up (ADR 0077): the sinks, the state the notifier and the answer route share, and the key decisions are signed with.</summary>
/// <param name="key">The approvals server's signing key, read when a decision comes in; null without <c>approvals</c>.</param>
public sealed class PhoneAnswers(AutomationNotify settings, NotifyState state, IReadOnlyList<IQuestionSink> sinks, Func<string>? key)
{
    public AutomationNotify Settings => settings;

    public NotifyState State => state;

    public IReadOnlyList<IQuestionSink> Sinks => sinks;

    /// <exception cref="InvalidOperationException">The secret did not resolve, or there is no approvals server.</exception>
    public string Key() => key?.Invoke() ?? throw new InvalidOperationException("automation.notify has no approvals server");

    public QuestionNotifier Notifier(IQuestionSource source, TimeProvider? clock = null, Action<string>? warn = null) => new(source, sinks, state, clock, warn);

    /// <param name="secret">Resolves a secret item by name.</param>
    public static PhoneAnswers Create(AutomationNotify settings, string statePath, Func<string, string> secret, IScheduleNotifier ntfy)
    {
        List<IQuestionSink> sinks = [];
        if (settings.Approvals is { } approvals)
            sinks.Add(new ApprovalsSink(approvals, () => secret(approvals.TokenSecret!), settings.ConsoleUrl));
        if (settings.NtfyUrl is { } url)
            sinks.Add(new NtfyQuestionSink(url, settings.ConsoleUrl, ntfy));
        return new PhoneAnswers(settings, NotifyState.Load(statePath), sinks, settings.Approvals is { KeySecret: { } item } ? () => secret(item) : null);
    }
}
