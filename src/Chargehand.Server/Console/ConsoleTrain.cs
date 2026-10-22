using Chargehand.Modules.Accounts.Api;

namespace Chargehand.Server;

/// <summary>A place in a repository's merge train. <paramref name="After"/> holds the positions of the earlier cars whose tasks changed some of the same paths: once they merge, this
/// one's branch needs bringing up to date, and may conflict.</summary>
/// <param name="IntegratorMode"><c>merge</c> (the integrator merges it itself once its checks pass), <c>ask</c> (a person merges it) or <c>off</c>.</param>
public sealed record TrainCarBody(int Position, ConflictPullBody Pull, string IntegratorMode, bool AwaitingPerson, string? AwaitingReason, IReadOnlyList<int> After, int FilesSharedWithEarlier);

public sealed record TrainBody(string Repository, IReadOnlyList<TrainCarBody> Cars);

/// <param name="Reason"><c>partial_draft</c> (unfinished work, ADR 0044) or <c>test_integrity</c> (ADR 0046): never merged, whatever the policy.</param>
public sealed record HeldPullBody(ConflictPullBody Pull, string Reason);

/// <summary><c>train/v1</c> (CHARGEHAND-216): the order to merge the open pull requests chargehand opened in, per repository, oldest first, as a merge queue would. Read only: the integrator
/// still merges each pull request on its own when its task ends.</summary>
public sealed record TrainsBody(string ContractVersion, DateTimeOffset GeneratedAt, IReadOnlyList<TrainBody> Trains, IReadOnlyList<HeldPullBody> Held, IReadOnlyList<string> NotRecorded);

internal static class ConsoleTrain
{
    public const string Contract = "train/v1";

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "The order is a proposal, oldest first: the integrator does not follow it, and merges a pull request of a merge repository as soon as its own checks pass.",
        "A car depends on an earlier one when their tasks' latest runs changed a path in common (see conflicts/v1); a pull request whose changed paths are not recorded depends on none.",
    ];

    public static async Task<TrainsBody> BuildAsync(ConsoleContext c, Principal? who, CancellationToken ct)
    {
        var open = await ConsoleConflicts.ReadAsync(c, who, ct);
        List<HeldPullBody> held = [.. open.Where(p => Hold(p.Pull) is not null).Select(p => new HeldPullBody(p.Body, Hold(p.Pull)!))];
        List<TrainBody> trains = [];
        foreach (var group in open.Where(p => Hold(p.Pull) is null).GroupBy(p => p.Pull.Repository!, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var cars = group.OrderBy(p => p.Pull.OpenedAt ?? DateTimeOffset.MaxValue).ThenBy(p => p.Pull.Url, StringComparer.Ordinal).ToList();
            trains.Add(new(group.Key, [.. cars.Select((car, i) =>
            {
                var earlier = cars.Take(i).Select((e, j) => (Position: j + 1, Shared: Shared(e.Files, car.Files))).Where(e => e.Shared.Count > 0).ToList();
                return new TrainCarBody(i + 1, car.Body, car.Pull.IntegratorMode, car.Pull.AwaitingPerson, car.Pull.AwaitingReason, [.. earlier.Select(e => e.Position)],
                    earlier.SelectMany(e => e.Shared).Distinct(StringComparer.Ordinal).Count());
            })]));
        }
        return new(Contract, c.Now, trains, held, NotRecorded);
    }

    private static string? Hold(PullRequestBody pull) => pull.TestIntegrityFlagged ? "test_integrity" : pull.Partial ? "partial_draft" : null;

    private static List<string> Shared(HashSet<string>? a, HashSet<string>? b) => a is null || b is null ? [] : [.. a.Intersect(b, StringComparer.Ordinal)];
}
