using System.Text.Json;

namespace Chargehand.Server;

/// <summary>One inbox item a person dismissed (ADR 0064).</summary>
/// <param name="Shown">The id of the item as the inbox showed it: a collapsed failure stands for several, and undoing it restores them all.</param>
/// <param name="By">The account that dismissed it; null: the owner.</param>
/// <param name="Kept">Not a dismissal: a person undid what a rule resolved (ADR 0085), so the rules leave the item open.</param>
public sealed record InboxDismissal(DateTimeOffset At, string Shown, string? By, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool Kept = false);

/// <summary>The inbox items people dismissed, by item id, in one JSON file beside the run log that only the server writes, whole, through a temporary file (as <c>accounts-use.json</c> and intake's
/// state are). A file that cannot be read reads as empty, so the items come back rather than the inbox failing.</summary>
internal sealed class InboxDismissals(string path)
{
    private readonly object _lock = new();

    public IReadOnlyDictionary<string, InboxDismissal> Read()
    {
        lock (_lock)
            return Load();
    }

    /// <summary>Dismisses <paramref name="ids"/> as the item <paramref name="shown"/>; returns those that were not dismissed already.</summary>
    public IReadOnlyList<string> Add(IEnumerable<string> ids, string shown, string? by, DateTimeOffset at)
    {
        lock (_lock)
        {
            var all = Load();
            var added = ids.Where(id => !all.TryGetValue(id, out var was) || was.Kept).ToList();
            foreach (var id in added)
                all[id] = new InboxDismissal(at, shown, by);
            if (added.Count > 0)
                Save(all);
            return added;
        }
    }

    /// <summary>Keeps <paramref name="id"/>, which a rule resolved, open; false when it was kept already.</summary>
    public bool Keep(string id, string? by, DateTimeOffset at)
    {
        lock (_lock)
        {
            var all = Load();
            if (!all.TryAdd(id, new InboxDismissal(at, id, by, Kept: true)))
                return false;
            Save(all);
            return true;
        }
    }

    /// <summary>Undoes the dismissal of the item <paramref name="shown"/> and of everything dismissed with it; returns the ids that come back.</summary>
    public IReadOnlyList<string> Remove(string shown)
    {
        lock (_lock)
        {
            var all = Load();
            var removed = all.Where(e => !e.Value.Kept && (e.Key == shown || e.Value.Shown == shown)).Select(e => e.Key).ToList();
            foreach (var id in removed)
                all.Remove(id);
            if (removed.Count > 0)
                Save(all);
            return removed;
        }
    }

    private Dictionary<string, InboxDismissal> Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, InboxDismissal>>(File.ReadAllText(path), Contracts.ContractJson.Options) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, InboxDismissal> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(file, all, Contracts.ContractJson.Options);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
