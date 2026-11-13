using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Chargehand.Contracts;
using Chargehand.Modules.Events.Api;

namespace Chargehand.Server;

/// <summary>Writes a run's lifecycle into the event log (ADR 0090) as <c>chargehand.run.started</c>, <c>chargehand.run.status_changed</c> and <c>chargehand.run.finished</c>,
/// so a subscriber follows a run without polling. One writer keeps a run's events in order; a log that cannot take an event never stops a run.</summary>
public sealed class RunEventEmitter : IAsyncDisposable
{
    public const string Source = "chargehand/orchestrator";
    public const string StartedType = "chargehand.run.started";
    public const string StatusChangedType = "chargehand.run.status_changed";
    public const string FinishedType = "chargehand.run.finished";

    private const int Attempts = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    private static readonly EventGrant Grant = new("orchestrator", Source, [], ["*"]);

    private readonly IEventLog _log;
    private readonly Channel<(RunStatus Event, string? Repo)> _queue = Channel.CreateUnbounded<(RunStatus, string?)>(new() { SingleReader = true });
    private readonly Dictionary<string, Seen> _runs = [];
    private readonly string _boot = Guid.NewGuid().ToString("N")[..8];
    private readonly Task _writer;

    private sealed class Seen
    {
        public RunState Status;
        public int Count;
        public string? Branch;
        public string? PrUrl;
    }

    public RunEventEmitter(IEventLog log)
    {
        _log = log;
        _writer = Task.Run(WriteAsync);
    }

    /// <summary>Queues one published status; <paramref name="repo"/> is the repository's folder name, never its path.</summary>
    public void Offer(RunStatus e, string? repo)
    {
        if (_queue.Writer.TryWrite((e, repo)))
            return;
        // Offered after dispose (a run that ended during shutdown): the writer is gone, so write it here rather than lose the end.
        _writer.GetAwaiter().GetResult();
        WriteOneAsync(e, repo).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private async Task WriteAsync()
    {
        await foreach (var (e, repo) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            await WriteOneAsync(e, repo).ConfigureAwait(false);
    }

    private async Task WriteOneAsync(RunStatus e, string? repo)
    {
        try
        {
            if (Decide(e) is not var (type, seen, id))
                return;
            // The id is fixed before the first try and the log keeps the first of a repeated (source, id), so a retry cannot double an event; a lost end would leave a run open for ever.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await AppendAsync(type, id, seen, e, repo).ConfigureAwait(false);
                    return;
                }
                catch (Exception) when (attempt < Attempts)
                {
                    await Task.Delay(RetryDelay * attempt).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The log is a view of the run, not part of it.
            Console.Error.WriteLine($"run events: {e.RunId}: {ex.GetType().Name}");
        }
    }

    private (string Type, Seen State, string Id)? Decide(RunStatus e)
    {
        if (!_runs.TryGetValue(e.RunId, out var seen))
            _runs[e.RunId] = seen = new Seen { Status = e.Status };
        seen.Branch = e.Branch ?? seen.Branch;
        seen.PrUrl = e.PrUrl ?? seen.PrUrl;
        var previous = seen.Status;
        seen.Status = e.Status;
        var type = e.Event == RunEventKind.Started ? StartedType
            : e.Event == RunEventKind.RunFinished ? FinishedType
            : e.Status != previous ? StatusChangedType : null;
        if (type is null)
            return null;
        // Counted per run and keyed by this process, so a status that comes back, or a batch a restart resumed, is a new event and not a repeat of the log's key.
        var id = $"run-{e.RunId}-{_boot}-{seen.Count++}";
        if (type == FinishedType)
            _runs.Remove(e.RunId);
        return (type, seen, id);
    }

    private async Task AppendAsync(string type, string id, Seen seen, RunStatus e, string? repo)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("specversion", "1.0");
            w.WriteString("id", id);
            w.WriteString("source", Source);
            w.WriteString("type", type);
            w.WriteString("subject", $"run/{e.RunId}");
            w.WriteString("time", (e.At ?? DateTimeOffset.UtcNow).UtcDateTime.ToString(Envelope.Format, CultureInfo.InvariantCulture));
            w.WriteString("scope", "global");
            w.WriteStartObject("data");
            w.WriteString("run_id", e.RunId);
            w.WriteString("status", e.Status.ToString().ToLowerInvariant());
            if (repo is not null)
                w.WriteString("repo", repo);
            if (seen.Branch is not null)
                w.WriteString("branch", seen.Branch);
            if (seen.PrUrl is not null)
            {
                w.WriteString("pr_url", seen.PrUrl);
                if (PrNumber(seen.PrUrl) is { } n)
                    w.WriteNumber("pr_number", n);
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        var (stored, refusal) = Envelope.Check(doc.RootElement, Grant);
        if (stored is null)
            throw new InvalidOperationException($"envelope refused: {refusal?.Code}");
        await _log.AppendAsync([stored], CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The number at the end of a pull request URL; null when it has none.</summary>
    internal static int? PrNumber(string url) =>
        int.TryParse(url.TrimEnd('/')[(url.TrimEnd('/').LastIndexOf('/') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}
