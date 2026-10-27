using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Chargehand.Kernel;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Economics.Api;

/// <summary>How a pull request stands now. <see cref="Unknown"/>: the forge could not be asked and no outcome was recorded.</summary>
public enum PullRequestState { Merged, Closed, Open, Unknown }

/// <summary>One pull request (or GitLab merge request) on a forge.</summary>
public sealed partial record PullRequestKey(RemoteRepository Remote, int Number)
{
    /// <summary>The pull request a URL names (<c>https://host/o/r/pull/7</c>, or GitLab's <c>/-/merge_requests/7</c>); null for any other URL.</summary>
    public static PullRequestKey? Parse(string? url)
    {
        if (url is null)
            return null;
        if (GitHub().Match(url) is { Success: true } hub)
            return new(new RemoteRepository(hub.Groups["o"].Value, hub.Groups["r"].Value), int.Parse(hub.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture));
        return GitLab().Match(url) is { Success: true } lab
            ? new(new RemoteRepository(lab.Groups["o"].Value, lab.Groups["r"].Value, ForgeKind.GitLab, lab.Groups["h"].Value.ToLowerInvariant()), int.Parse(lab.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    [GeneratedRegex(@"^https://github\.com/(?<o>[A-Za-z0-9_.-]+)/(?<r>[A-Za-z0-9_.-]+)/pull/(?<n>\d{1,9})(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHub();

    [GeneratedRegex(@"^https://(?<h>[^/]+)/(?<o>[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*)/(?<r>[A-Za-z0-9_.-]+)/-/merge_requests/(?<n>\d{1,9})(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitLab();
}

/// <summary>Where the report learns what became of a pull request.</summary>
public interface IPullRequestStates
{
    Task<PullRequestState> StateOfAsync(PullRequestKey pull, CancellationToken ct);
}

/// <summary>Asks the forge, read-only, and remembers the answer: a merged or closed pull request for good (it cannot change), an open one for <paramref name="openFor"/>, so a console that is
/// reloaded does not read every open pull request each time. A forge that cannot be reached falls back to <paramref name="recorded"/>, then to <see cref="PullRequestState.Unknown"/>
/// (and that is not remembered).</summary>
public sealed class ForgePullRequestStates(Func<RemoteRepository, IIntegrationHost?> hostFor, IPullRequestStates? recorded = null, TimeSpan? openFor = null, TimeProvider? clock = null) : IPullRequestStates
{
    private readonly ConcurrentDictionary<PullRequestKey, (PullRequestState State, DateTimeOffset At)> _seen = [];

    public async Task<PullRequestState> StateOfAsync(PullRequestKey pull, CancellationToken ct)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (_seen.TryGetValue(pull, out var kept) && (kept.State != PullRequestState.Open || now - kept.At < (openFor ?? TimeSpan.FromMinutes(10))))
            return kept.State;
        var state = await ReadAsync(pull, ct);
        if (state != PullRequestState.Unknown)
            _seen[pull] = (state, now);
        return state;
    }

    private async Task<PullRequestState> ReadAsync(PullRequestKey pull, CancellationToken ct)
    {
        if (hostFor(pull.Remote) is { } host)
        {
            try
            {
                var snapshot = await host.GetAsync(pull.Remote, pull.Number, ct);
                return snapshot.Merged ? PullRequestState.Merged : snapshot.Closed ? PullRequestState.Closed : PullRequestState.Open;
            }
            catch (Exception e) when (e is IntegrationException or HttpRequestException or ChargehandException || e is TaskCanceledException && !ct.IsCancellationRequested)
            {
                // The forge is down, the credential is gone or the pull request is: fall back to what was recorded.
            }
        }

        return recorded is null ? PullRequestState.Unknown : await recorded.StateOfAsync(pull, ct);
    }
}

/// <summary>The outcomes the server's tracker wrote to the learning records (ADR 0045): no credential and no network, so the CLI can read them. Only driven tasks have one, and a pull request
/// the tracker has not looked at since it was opened reads as open.</summary>
public sealed class RecordedPullRequestStates(IEnumerable<LearningEntry> entries) : IPullRequestStates
{
    private readonly Dictionary<PullRequestKey, PullRequestState> _states = Index(entries);

    public Task<PullRequestState> StateOfAsync(PullRequestKey pull, CancellationToken ct) =>
        Task.FromResult(_states.TryGetValue(pull, out var state) ? state : PullRequestState.Unknown);

    private static Dictionary<PullRequestKey, PullRequestState> Index(IEnumerable<LearningEntry> entries)
    {
        Dictionary<PullRequestKey, PullRequestState> states = [];
        foreach (var entry in entries.OrderBy(e => e.Record.At))
        {
            if (entry.Record.Pr is not { } pr)
                continue;
            states[new PullRequestKey(pr.Remote, pr.Number)] = entry.PrOutcome?.Outcome switch
            {
                PrOutcomes.MergedUnchanged or PrOutcomes.MergedEdited or PrOutcomes.Merged => PullRequestState.Merged,
                PrOutcomes.Closed => PullRequestState.Closed,
                _ => PullRequestState.Open,
            };
        }

        return states;
    }
}
