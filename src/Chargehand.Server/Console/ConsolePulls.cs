using Chargehand.Driven;

namespace Chargehand.Server;

/// <summary><c>pulls/v1</c>: the pull requests chargehand opened for the tasks the caller may see.</summary>
public sealed record PullsBody(string ContractVersion, DateTimeOffset GeneratedAt, IReadOnlyList<PullRequestBody> Pulls, IReadOnlyList<string> NotRecorded);

internal static class ConsolePulls
{
    public const string Contract = "pulls/v1";

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "The state is the last outcome the server read back from the forge (about hourly, for 14 days after the task ended); 'open' also covers a pull request whose outcome was never read.",
        "A pull request's CI checks and the CI fixer's attempts are seen only while the integrator's step runs; they are not stored. 'integrator_last' is the last integrated event of a run this process still holds.",
    ];

    /// <param name="repository">Exact <c>owner/name</c> as the learning record names it.</param>
    /// <param name="state">One of the outcomes: open, merged_unchanged, merged_edited, merged, closed.</param>
    /// <param name="awaiting">Only the pull requests that wait for a person.</param>
    public static async Task<PullsBody> BuildAsync(ConsoleContext c, Principal? who, string? repository, string? state, bool awaiting, DateTimeOffset? since, int limit, CancellationToken ct)
    {
        var learning = (await c.LearningAsync(ct)).Where(e => Principal.Sees(who, e.Record.Actor) && e.Record.Pr is not null && (since is not { } from || e.Record.At >= from));
        var rows = learning.OrderByDescending(e => e.Record.At).Select(e => ConsoleBatches.PullRequestOf(c, e)!)
            .Where(p => (repository is null || p.Repository == repository) && (state is null || p.State == state) && (!awaiting || p.AwaitingPerson))
            .Take(Math.Clamp(limit, 1, 500)).ToList();
        return new PullsBody(Contract, c.Now, rows, NotRecorded);
    }

    public static readonly IReadOnlyList<string> States = [PrOutcomes.Open, PrOutcomes.MergedUnchanged, PrOutcomes.MergedEdited, PrOutcomes.Merged, PrOutcomes.Closed];
}
