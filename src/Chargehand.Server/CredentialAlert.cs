using Chargehand.Automation;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Server;

/// <summary>Where the "runs paused" alert goes: the schedules' <c>notify_url</c>, else the phone-answers <c>ntfy_url</c>; null when neither is set.</summary>
internal static class CredentialAlert
{
    public static Func<string, string, CancellationToken, Task>? Notify(AutomationSettings? automation, IScheduleNotifier? notifier = null) =>
        (automation?.Schedules?.NotifyUrl ?? automation?.Notify?.NtfyUrl) is { } url
            ? (title, body, ct) => (notifier ?? new HttpScheduleNotifier()).PostAsync(url, title, body, ct)
            : null;
}
