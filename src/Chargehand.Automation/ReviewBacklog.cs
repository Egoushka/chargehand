using System.Globalization;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <summary>The review-backlog check of the intake governor (ADR 0084): how many pull requests chargehand opened in a repository wait for someone to review them. Read once per poll and repository.</summary>
internal sealed class ReviewBacklog(AutomationGitHub settings, IIntakeHost host)
{
    private readonly Dictionary<string, int> _counts = [];

    public void NewPoll() => _counts.Clear();

    /// <summary>Why a new start must wait, or null: the limit is off or not reached.</summary>
    public async Task<string?> HoldAsync(string repository, CancellationToken ct)
    {
        if (settings.MaxReviewBacklog <= 0)
            return null;
        if (!_counts.TryGetValue(repository, out var count))
            _counts[repository] = count = await CountAsync(repository, ct);
        return count >= settings.MaxReviewBacklog ? $"{count.ToString(CultureInfo.InvariantCulture)} pull requests await review (limit {settings.MaxReviewBacklog.ToString(CultureInfo.InvariantCulture)})" : null;
    }

    /// <summary>Open, chargehand's own branch, and no review yet from an allowed author.</summary>
    private async Task<int> CountAsync(string repository, CancellationToken ct)
    {
        var count = 0;
        foreach (var pull in await host.OpenPullsAsync(repository, ct))
        {
            if (!GitHubIntake.IsChargehandBranch(pull.HeadBranch, pull.SameRepository))
                continue;
            if ((await host.ReviewersAsync(repository, pull.Number, ct)).Any(login => settings.AllowedAuthors?.Contains(login, StringComparer.OrdinalIgnoreCase) == true))
                continue;
            count++;
        }
        return count;
    }
}
