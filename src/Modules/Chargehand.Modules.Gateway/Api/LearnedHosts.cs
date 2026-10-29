using System.Text.Json;

namespace Chargehand.Modules.Gateway.Api;

/// <summary>A connection a batch's session was refused only because no allowlist entry names its host (ADR 0078, decision 4).</summary>
public sealed record EgressRefusal(string Host, DateTimeOffset At);

/// <summary>A host the sessions of one repository were refused, as the owner reviews it before approving it for that repository.</summary>
/// <param name="Refusals">Refused connections, over every batch.</param>
/// <param name="Batches">Batches that had at least one.</param>
/// <param name="Approved">When the owner approved it for the repository; null: not approved. An approved host is on the allowlist of the repository's later batches.</param>
public sealed record LearnedHost(string Host, int Refusals, int Batches, DateTimeOffset First, DateTimeOffset Last, string LastBatch, DateTimeOffset? Approved = null);

public static class EgressRefusals
{
    /// <summary>The refusals in the egress proxy's log that listing the host would have lifted. Other lines (allowed connections, the model endpoint's, text), and refusals no entry can lift
    /// (an IP literal, <c>localhost</c>, a port other than 443), are skipped.</summary>
    public static IReadOnlyList<EgressRefusal> Parse(string log)
    {
        List<EgressRefusal> refused = [];
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{'))
                continue;
            EgressLogLine? entry;
            try
            {
                entry = JsonSerializer.Deserialize(line, GatewayJsonContext.Default.EgressLogLine);
            }
            catch (JsonException)
            {
                continue;
            }
            // A model endpoint line has no host.
            if (entry is { Allowed: false, Host.Length: > 0 } && Listable(entry.Host, entry.Port))
                refused.Add(new EgressRefusal(entry.Host.Trim().TrimEnd('.').ToLowerInvariant(), entry.At));
        }
        return refused;
    }

    private static bool Listable(string host, int port)
    {
        try
        {
            return new AllowlistMatcher([host]).Check(host, port).Allowed;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>The refused hosts per repository (<c>owner/name</c>), in one JSON file in the state directory that only the server writes, whole, through a temporary file. A file that cannot be read
/// reads as empty: what is lost is a hint, and the next refusal brings the host back.</summary>
public sealed class LearnedHosts(string path)
{
    /// <summary>Hosts kept per repository, the most recently refused first; a session that tries hundreds of hosts does not grow the file without end.</summary>
    public const int MaxPerRepository = 100;

    private readonly object _lock = new();

    public void Record(string repository, string batchId, IReadOnlyList<EgressRefusal> refused)
    {
        if (refused.Count == 0)
            return;
        lock (_lock)
        {
            var all = Load();
            var hosts = all.TryGetValue(repository, out var known) ? known.ToDictionary(h => h.Host, StringComparer.Ordinal) : new Dictionary<string, LearnedHost>(StringComparer.Ordinal);
            foreach (var group in refused.GroupBy(r => r.Host, StringComparer.Ordinal))
            {
                var (first, last) = (group.Min(r => r.At), group.Max(r => r.At));
                hosts[group.Key] = hosts.TryGetValue(group.Key, out var was)
                    ? was with { Refusals = was.Refusals + group.Count(), Batches = was.Batches + 1, First = first < was.First ? first : was.First, Last = last > was.Last ? last : was.Last, LastBatch = batchId }
                    : new LearnedHost(group.Key, group.Count(), 1, first, last, batchId);
            }
            // Approved hosts stay whatever the cap: dropping one would take it off the repository's allowlist.
            var approved = hosts.Values.Where(h => h.Approved is not null).ToList();
            all[repository] = [.. approved.Concat(hosts.Values.Where(h => h.Approved is null).OrderByDescending(h => h.Last).ThenBy(h => h.Host, StringComparer.Ordinal)
                .Take(Math.Max(0, MaxPerRepository - approved.Count))).OrderByDescending(h => h.Last).ThenBy(h => h.Host, StringComparer.Ordinal)];
            try
            {
                Save(all);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Recorded at a batch's teardown: a full disk loses a hint, not the batch's result.
            }
        }
    }

    /// <summary>Approves <paramref name="host"/> for <paramref name="repository"/>; false when the repository's sessions were never refused it (or it is approved already). Only a refused host
    /// can be approved: the view is for reviewing what sessions asked for, not a second allowlist.</summary>
    public bool Approve(string repository, string host, DateTimeOffset at) => Change(repository, host, h => h.Approved is null ? h with { Approved = at } : null);

    /// <summary>Takes the approval back; false when the host was not approved.</summary>
    public bool Revoke(string repository, string host) => Change(repository, host, h => h.Approved is null ? null : h with { Approved = null });

    /// <summary>The hosts approved for <paramref name="repository"/>, for its batch's allowlist.</summary>
    public IReadOnlyList<string> ApprovedFor(string repository)
    {
        lock (_lock)
            return Load().TryGetValue(repository, out var hosts) ? [.. hosts.Where(h => h.Approved is not null).Select(h => h.Host).Order(StringComparer.Ordinal)] : [];
    }

    private bool Change(string repository, string host, Func<LearnedHost, LearnedHost?> change)
    {
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        lock (_lock)
        {
            var all = Load();
            if (!all.TryGetValue(repository, out var hosts) || hosts.FindIndex(h => h.Host == name) is var i && i < 0 || change(hosts[i]) is not { } changed)
                return false;
            hosts[i] = changed;
            Save(all);
            return true;
        }
    }

    /// <summary>Each repository's refused hosts, the most recently refused first.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LearnedHost>> Read()
    {
        lock (_lock)
            return Load().ToDictionary(e => e.Key, e => (IReadOnlyList<LearnedHost>)e.Value, StringComparer.Ordinal);
    }

    private Dictionary<string, List<LearnedHost>> Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), GatewayJsonContext.Default.DictionaryStringListLearnedHost) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, List<LearnedHost>> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(file, all, GatewayJsonContext.Default.DictionaryStringListLearnedHost);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
