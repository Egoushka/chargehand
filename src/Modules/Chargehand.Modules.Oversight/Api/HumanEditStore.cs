using System.Text.Json;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>One human commit a pull request's branch carries after chargehand's own last push, classified (ADR 0045's <c>merged_edited</c>; issue #452). <paramref name="RunId"/> and
/// <paramref name="PrUrl"/> trace it back to the task and pull request it came from. <paramref name="ChangedPaths"/> is the commit's own files, kept so a reader can say which files
/// humans fix most (issue #455), not just which category.</summary>
public sealed record HumanEditCommit(DateTimeOffset At, string RunId, string PrUrl, string Sha, string Category, string Message, IReadOnlyList<string> ChangedPaths = null!)
{
    /// <summary>Never null: a line written before issue #455 added this field reads back with no files, not a missing property.</summary>
    public IReadOnlyList<string> ChangedPaths { get; init; } = ChangedPaths ?? [];

    /// <summary>By value, not by the list's own type: a JSON round trip reads <see cref="ChangedPaths"/> back as a different <see cref="IReadOnlyList{T}"/> implementation than the
    /// one it was written with, and the record's own generated equality would treat that as a different commit.</summary>
    public bool Equals(HumanEditCommit? other) =>
        other is not null && At == other.At && RunId == other.RunId && PrUrl == other.PrUrl && Sha == other.Sha && Category == other.Category && Message == other.Message
        && ChangedPaths.SequenceEqual(other.ChangedPaths);

    public override int GetHashCode() => HashCode.Combine(At, RunId, PrUrl, Sha, Category, Message);
}

/// <summary>The human commits <see cref="HumanEditClassifier"/> has already classified, by pull request outcome (ADR 0045). The server owns the file; a reader only sums them up.</summary>
public interface IHumanEditStore
{
    Task AppendAsync(HumanEditCommit commit, CancellationToken ct);

    /// <summary>Every commit read so far, oldest first, one per sha: a sha already held is not written again.</summary>
    Task<IReadOnlyList<HumanEditCommit>> ReadAsync(CancellationToken ct);
}

/// <summary>One JSON object per line, append-only, written without reflection (ADR 0060). A reader skips a last line with no newline yet, and a line it cannot read, and takes the first
/// commit it reads for a sha (the tracker appends each sha once, but a store that already holds one is not asked to hold it twice).</summary>
public sealed class JsonlHumanEditStore(string path) : IHumanEditStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task AppendAsync(HumanEditCommit commit, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var line = JsonSerializer.Serialize(commit, HumanEditJsonContext.Default.HumanEditCommit) + "\n";
            await File.AppendAllTextAsync(path, line, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyList<HumanEditCommit>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];
        var text = await File.ReadAllTextAsync(path, ct);
        Dictionary<string, HumanEditCommit> bySha = [];
        foreach (var line in text[..(text.LastIndexOf('\n') + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (JsonSerializer.Deserialize(line, HumanEditJsonContext.Default.HumanEditCommit) is { } commit)
                    bySha.TryAdd(commit.Sha, commit);
            }
            catch (JsonException)
            {
                // a torn or corrupt line is skipped; the rest of the file still reads.
            }
        }
        return [.. bySha.Values.OrderBy(c => c.At)];
    }
}
