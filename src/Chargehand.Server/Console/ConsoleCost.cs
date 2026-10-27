using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Economics.Api;

namespace Chargehand.Server;

/// <summary><c>cost/v1</c> (CHARGEHAND-577): dollars per merged pull request per repository and preset over 7 and 30 days. The forge is asked as the integrator's App, read-only, and answers are
/// kept (<see cref="ForgePullRequestStates"/>); where it cannot be asked, the outcomes the tracker recorded stand in. An account sees its own runs.</summary>
internal static class ConsoleCost
{
    public static async Task<CostReport> BuildAsync(ConsoleContext c, IPullRequestStates states, Principal? who, CancellationToken ct)
    {
        var data = await c.IndexAsync(ct);
        return await CostReportBuilder.BuildAsync(data.Data, states, c.Now, start => Principal.Sees(who, start.Actor), ct: ct);
    }

    /// <summary>The states source for the server's lifetime: the forge, then the learning records.</summary>
    public static IPullRequestStates StatesFor(Chargehand.Driven.DrivenRun? driven) =>
        new ForgePullRequestStates(remote => driven?.ReadHost(remote), new LazyRecorded(driven));

    private sealed class LazyRecorded(Chargehand.Driven.DrivenRun? driven) : IPullRequestStates
    {
        public async Task<PullRequestState> StateOfAsync(PullRequestKey pull, CancellationToken ct) =>
            driven?.Learning is { } store ? await new RecordedPullRequestStates(await store.ReadAsync(ct)).StateOfAsync(pull, ct) : PullRequestState.Unknown;
    }
}
