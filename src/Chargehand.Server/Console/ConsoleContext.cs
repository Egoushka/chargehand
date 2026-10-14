using System.Globalization;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.RunLog;

namespace Chargehand.Server;

/// <summary>What the operator console's read routes draw on (CHARGEHAND-209): the run log, the learning records, the saved state of unfinished batches, the server's own runs and, for the owner,
/// the profile. Every route reads what the server already keeps; nothing here writes, and nothing here is a database.</summary>
internal sealed class ConsoleContext(ServerSettings settings, RunService runs, IRunLog log, DrivenRun? driven, SecretPresence secrets)
{
    public ServerSettings Settings => settings;

    public Profile? Profile => settings.Profile;

    public RunService Runs => runs;

    public DrivenRun? Driven => driven;

    public IRunLog Log => log;

    public DateTimeOffset Now => runs.Now;

    /// <summary>The run log, whole. A log that cannot list its records reads as empty.</summary>
    // ponytail: reads the whole log per call, as GET /v1/runs does; index it if the console is ever polled often over a large log.
    public async Task<RunIndex> IndexAsync(CancellationToken ct)
    {
        try
        {
            return new RunIndex(await log.ReadAllAsync(ct));
        }
        catch (NotSupportedException)
        {
            return new RunIndex(new([], [], [], []));
        }
    }

    /// <summary>A batch's events: those kept on disk when there are any (they outlive a restart), else those this process holds. <c>Source</c> says which.</summary>
    public (IReadOnlyList<RunStatus>? Events, string? Source) EventsOf(string runId)
    {
        if (driven?.Records?.ReadEvents(runId) is { } kept)
            return (kept, "records");
        return runs.Events(runId) is { } held ? (held, "memory") : (null, null);
    }

    /// <summary>The sessions, governor logs, checks and CI fix attempts kept for a batch's tasks (CHARGEHAND-214); empty when none are.</summary>
    public BatchTaskRecords TaskRecordsOf(string runId) => driven?.Records?.ReadTasks(runId) ?? BatchTaskRecords.Empty;

    public async Task<IReadOnlyList<LearningEntry>> LearningAsync(CancellationToken ct) =>
        driven?.Learning is { } store ? await store.ReadAsync(ct) : [];

    /// <summary>The integrator's policy for a remote repository; off when the profile has none.</summary>
    public IntegrationMode IntegrationOf(RemoteRepository? remote) =>
        remote is not null && Profile?.Driven?.Integrator is { } integrator ? new IntegratorPolicy(integrator).Resolve(remote) : IntegrationMode.Off;

    /// <summary>Whether the profile's secret item resolves, remembered for a minute (a command source can be slow). Null: the server was not given a way to tell.</summary>
    public bool? SecretPresent(string item) => secrets.Present(item, Now);

    public static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>The records of the run log by run id: the latest start and the latest run record of each.</summary>
internal sealed class RunIndex
{
    public RunIndex(RunLogData data)
    {
        Starts = data.Starts.GroupBy(s => s.RunId).ToDictionary(g => g.Key, g => g.Last());
        Finished = data.Runs.GroupBy(r => r.RunId).ToDictionary(g => g.Key, g => g.Last());
        Data = data;
    }

    public RunLogData Data { get; }

    public IReadOnlyDictionary<string, StartRecord> Starts { get; }

    public IReadOnlyDictionary<string, RunRecord> Finished { get; }

    public IEnumerable<StartRecord> Batches => Starts.Values.Where(s => s.Request.Driven is not null);

    public RunRecord? RunOf(string runId) => Finished.GetValueOrDefault(runId);

    public RunSummary Summary(StartRecord start) => JsonlRunLog.SummaryOf(start, RunOf(start.RunId));

    /// <summary>The starts of batches that continue <paramref name="batch"/>'s <paramref name="task"/> (ADR 0045): they carry its answers.</summary>
    public IEnumerable<(StartRecord Start, DrivenTask Task)> ContinuationsOf(string batch, string task) =>
        Batches.SelectMany(s => s.Request.Driven!.Tasks.Where(t => t.ContinueFrom is { } from && from.RunId == batch && from.TaskId == task).Select(t => (s, t)));
}

/// <summary>Reading the inline JSON artifacts of a result.</summary>
internal static class ResultArtifacts
{
    public static JsonElement? Content(ResultContract? result, string kind)
    {
        var content = result?.Artifacts.FirstOrDefault(a => a.Kind == kind)?.Content;
        if (content is null)
            return null;
        try
        {
            return JsonDocument.Parse(content).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? String(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    public static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>Remembers, for a minute, whether a secret item resolved. One per server, so a console that polls does not run a command source on every request.</summary>
internal sealed class SecretPresence(Func<string, bool>? probe)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, bool Present)> _known = new();

    public bool? Present(string item, DateTimeOffset now)
    {
        if (probe is null)
            return null;
        if (_known.TryGetValue(item, out var known) && now - known.At < TimeSpan.FromMinutes(1))
            return known.Present;
        var present = probe(item);
        _known[item] = (now, present);
        return present;
    }
}
