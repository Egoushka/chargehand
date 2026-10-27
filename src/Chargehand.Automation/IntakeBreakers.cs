using System.Globalization;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <summary>The safety nets of the intake governor that replaced the daily count of starts (ADR 0084). Both hold only new starts from issues and comments; running batches, rounds and answers go on. Both read
/// <see cref="IntakeState.Tasks"/>, so a restart changes nothing.</summary>
internal sealed class IntakeBreakers(AutomationGitHub settings, IntakeState state, TimeProvider time, Action<string> warn)
{
    public const int FailuresInARow = 3;

    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly HashSet<string> _warned = [];

    /// <summary>Why a new start in <paramref name="repository"/> must wait, or null.</summary>
    public string? Hold(string repository)
    {
        var now = time.GetUtcNow();
        return Rate(now) ?? Failures(repository, now);
    }

    /// <summary>More starts in the last hour than <c>max_starts_per_hour</c>: a loop, not a budget.</summary>
    private string? Rate(DateTimeOffset now)
    {
        var limit = settings.MaxStartsPerHour;
        var starts = state.Tasks.Count(t => now - t.Started < Window);
        if (limit <= 0 || starts < limit)
        {
            _warned.Remove("rate");
            return null;
        }
        var why = $"{starts.ToString(CultureInfo.InvariantCulture)} starts in the last hour (limit {limit.ToString(CultureInfo.InvariantCulture)})";
        if (_warned.Add("rate"))
            warn($"intake: start-rate breaker: {why}; new starts wait until the hour passes");
        return why;
    }

    /// <summary>The last three batches of the repository ended in failure: its starts wait <c>breaker_cooldown_minutes</c> after the last one. After that one start may try; a success ends the streak, a failure holds again.</summary>
    private string? Failures(string repository, DateTimeOffset now)
    {
        var last = state.Tasks.Where(t => t.Repository == repository && t.Phase != IntakePhase.Running).OrderByDescending(t => t.FailedAt ?? t.Started).Take(FailuresInARow).ToList();
        if (last.Count < FailuresInARow || last.Any(t => t.FailedAt is null))
            return null;
        var until = last[0].FailedAt!.Value + TimeSpan.FromMinutes(settings.BreakerCooldownMinutes);
        if (now >= until)
            return null;
        var why = $"{FailuresInARow.ToString(CultureInfo.InvariantCulture)} failed batches in a row; held until {until.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}";
        if (_warned.Add($"failures:{repository}:{until:O}"))
            warn($"intake: failure breaker: {repository}: {why}");
        return why;
    }
}
