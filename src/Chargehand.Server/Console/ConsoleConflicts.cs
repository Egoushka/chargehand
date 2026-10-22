using System.Text.Json;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Server;

/// <summary>An open pull request of the radar. <paramref name="FilesTotal"/> is how many paths its task changed; <paramref name="FilesListed"/> false when the result kept fewer than that (it stays
/// within 64 KiB), so an overlap may be missed.</summary>
public sealed record ConflictPullBody(string Url, int? Number, string BatchRunId, string TaskId, string? Actor, DateTimeOffset? OpenedAt, int FilesTotal, bool FilesListed);

/// <summary>Two open pull requests of one repository whose tasks changed some of the same paths: merging one is likely to make the other conflict. <paramref name="Files"/> holds up to 50 of them.</summary>
public sealed record ConflictBody(string Repository, IReadOnlyList<ConflictPullBody> Pulls, int FilesShared, IReadOnlyList<string> Files);

/// <summary><c>conflicts/v1</c> (CHARGEHAND-216): the conflict radar. <paramref name="OpenPulls"/> is the open pull requests the caller may see; <paramref name="Unread"/> those whose changed paths the
/// run log does not hold, so they are left out of the pairs.</summary>
public sealed record ConflictsBody(string ContractVersion, DateTimeOffset GeneratedAt, int OpenPulls, IReadOnlyList<ConflictBody> Conflicts, IReadOnlyList<ConflictPullBody> Unread,
    IReadOnlyList<string> NotRecorded);

/// <summary>Pairs the open pull requests chargehand opened whose tasks changed the same paths, from each one's latest <c>changes</c> artifact. Read only: it says what may conflict, it changes nothing.</summary>
internal static class ConsoleConflicts
{
    public const string Contract = "conflicts/v1";

    private const int MaxPulls = 500;
    private const int MaxFiles = 50;

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "The paths are those the pull request's latest task run changed, as its result recorded them; a commit pushed to the branch by a person is not seen.",
        "A shared path is a likely conflict, not a certain one: two changes to different lines of a file can merge cleanly, and a conflict through a file only one of them touched is not found.",
        "Open is the last outcome the server read back from the forge (see pulls/v1).",
    ];

    /// <summary>An open pull request with the paths its latest task run changed; <c>Files</c> is null when the run log holds none.</summary>
    internal sealed record OpenPull(PullRequestBody Pull, ConflictPullBody Body, HashSet<string>? Files);

    /// <summary>The open pull requests the caller may see, each with its changed paths.</summary>
    internal static async Task<List<OpenPull>> ReadAsync(ConsoleContext c, Principal? who, CancellationToken ct)
    {
        var open = (await ConsolePulls.BuildAsync(c, who, null, PrOutcomes.Open, false, null, MaxPulls, ct)).Pulls
            .Where(p => p.Repository is not null).DistinctBy(p => p.Url).ToList();
        List<OpenPull> pulls = [];
        foreach (var pull in open)
        {
            var changes = pull.RunId is { } run ? ResultArtifacts.Content((await c.Log.ReadAsync(run, ct)).Run?.Result, "changes") : null;
            var files = Paths(changes);
            var total = ResultArtifacts.Int(changes, "files_total") ?? files?.Count ?? 0;
            pulls.Add(new(pull, new ConflictPullBody(pull.Url, pull.Number, pull.BatchRunId, pull.TaskId, pull.Actor, pull.OpenedAt, total, files is not null && files.Count >= total), files));
        }
        return pulls;
    }

    public static async Task<ConflictsBody> BuildAsync(ConsoleContext c, Principal? who, CancellationToken ct)
    {
        var all = await ReadAsync(c, who, ct);
        var read = all.Where(p => p.Files is not null).Select(p => (Pull: p.Body, Repository: p.Pull.Repository!, Files: p.Files!)).ToList();
        var unread = all.Where(p => p.Files is null).Select(p => p.Body).ToList();
        List<ConflictBody> conflicts = [];
        foreach (var group in read.GroupBy(r => r.Repository, StringComparer.OrdinalIgnoreCase))
        {
            var pulls = group.ToList();
            for (var i = 0; i < pulls.Count; i++)
                for (var j = i + 1; j < pulls.Count; j++)
                {
                    var shared = pulls[i].Files.Intersect(pulls[j].Files, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                    if (shared.Count > 0)
                        conflicts.Add(new(group.Key, [pulls[i].Pull, pulls[j].Pull], shared.Count, [.. shared.Take(MaxFiles)]));
                }
        }
        return new(Contract, c.Now, all.Count, [.. conflicts.OrderByDescending(x => x.FilesShared).ThenBy(x => x.Repository, StringComparer.Ordinal)], unread, NotRecorded);
    }

    /// <summary>The paths of a <c>changes</c> artifact; null when there is none.</summary>
    private static HashSet<string>? Paths(JsonElement? changes)
    {
        if (changes is not { ValueKind: JsonValueKind.Object } e || !e.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;
        return [.. files.EnumerateArray().Select(f => ResultArtifacts.String(f, "path")).OfType<string>()];
    }
}
