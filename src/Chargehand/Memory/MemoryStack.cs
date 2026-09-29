using System.Globalization;
using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Contracts;

namespace Chargehand.Memory;

/// <summary>
/// What one memory source may cost a run: facts, characters in all and characters per fact recalled (after whitespace is
/// collapsed), and the time one recall or retain call may take. Defaults are starting values, not measurements
/// (spec, decisions 6 and 17).
/// </summary>
public sealed record MemoryLimits(int MaxFacts = 10, int MaxChars = 4000, TimeSpan? Timeout = null, int MaxFactChars = 600)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
}

/// <summary>One memory the stack asks: a named provider, where in it to look, its limits, and whether a run writes to it.</summary>
/// <param name="RetainTags">Tags a retained item carries in this source; null keeps the item's own.</param>
public sealed record MemorySource(string Name, IMemoryProvider Provider, MemoryScope Scope, MemoryLimits Limits, bool Retain = false, IReadOnlyList<string>? RetainTags = null);

/// <param name="Items">What passed the source's limits, as one line each and cut at the fact cap, before facts another source already returned are dropped.</param>
/// <param name="SkippedReason">Why the source contributed nothing (failed, timed out); null when it answered.</param>
public sealed record SourceRecall(string Source, IReadOnlyList<RecalledMemory> Items, string? SkippedReason);

/// <param name="Prompt">The text appended to the task, or empty when no fact survived.</param>
/// <param name="Blocks">One runtime chain block per source that contributed a fact of its own.</param>
public sealed record RecallOutcome(IReadOnlyList<SourceRecall> Sources, string Prompt, IReadOnlyList<ChainBlock> Blocks);

public sealed record RetainReport(string Source, int Claims, string? SkippedReason);

/// <summary>
/// Several memory providers behind one recall and one retain (goal 0.6, ADR 0026: fan-out, labelled by source).
/// Memory is optional context, so it fails open (ADR 0008, ADR 0013): a provider that throws anything, hangs or
/// answers garbage is skipped with a reason and the others still contribute. Only the caller's own cancellation
/// propagates.
/// </summary>
public sealed partial class MemoryStack(IReadOnlyList<MemorySource> sources)
{
    private const string Header = "Facts from long-term memory (unverified; check them in the repository and cite files, never these). "
        + "The name in brackets is the memory each came from:";

    private const int MaxReasonLength = 200;

    public IReadOnlyList<MemorySource> Sources => sources;

    /// <summary>
    /// The profile's single <c>memory</c> object as a stack of one source named <c>hindsight</c>, until the object form
    /// goes. Its timeout is the 30 s the CLI gives the HTTP client, so a slow service is waited for as before.
    /// </summary>
    public static MemoryStack ForObjectForm(MemorySettings settings, IMemoryProvider provider) =>
        new([new MemorySource("hindsight", provider, new MemoryScope(settings.Backend, settings.Namespace), new MemoryLimits(Timeout: TimeSpan.FromSeconds(30)),
            settings.Retain, ["chargehand"])]);

    /// <summary>Asks every source at once, then merges in list order: one line per fact, labelled with the names of the sources that returned it.</summary>
    public async Task<RecallOutcome> RecallAsync(string query, CancellationToken ct)
    {
        var answers = await Task.WhenAll(sources.Select(s => RecallFrom(s, query, ct)));
        var lines = new List<Line>();
        var byText = new Dictionary<string, Line>(StringComparer.OrdinalIgnoreCase);
        var recalls = new List<SourceRecall>();
        for (var i = 0; i < sources.Count; i++)
        {
            var (items, skipped) = answers[i];
            var limits = sources[i].Limits;
            var kept = new List<RecalledMemory>();
            var chars = 0;
            foreach (var item in items)
            {
                var text = Cut(OneLine(item.Text), limits.MaxFactChars);
                if (text.Length == 0)
                    continue;
                if (kept.Count >= limits.MaxFacts || chars + text.Length > limits.MaxChars)
                    break;
                kept.Add(item with { Text = text });
                chars += text.Length;
                if (!byText.TryGetValue(text, out var line))
                    lines.Add(byText[text] = line = new Line(text, i));
                if (!line.Names.Contains(sources[i].Name))
                    line.Names.Add(sources[i].Name);
            }
            recalls.Add(new SourceRecall(sources[i].Name, kept, skipped));
        }

        var blocks = lines.GroupBy(l => l.Owner)
            .Select(g => new ChainBlock($"memory/recall/{sources[g.Key].Name}", "1", PromptBlock.Hash(string.Join("\n", g.Select(l => l.Render()))), BlockSource.Runtime));
        return new RecallOutcome(recalls, lines.Count == 0 ? "" : $"\n{Header}\n{string.Join("\n", lines.Select(l => l.Render()))}\n", [.. blocks]);
    }

    /// <summary>Writes the item to every source that retains. A source that fails or hangs is reported, never thrown.</summary>
    public async Task<IReadOnlyList<RetainReport>> RetainAsync(MemoryItem item, CancellationToken ct) =>
        await Task.WhenAll(sources.Where(s => s.Retain).Select(s => RetainTo(s, item, ct)));

    private static async Task<(IReadOnlyList<RecalledMemory> Items, string? Skipped)> RecallFrom(MemorySource source, string query, CancellationToken ct)
    {
        try
        {
            return (await Bounded(t => source.Provider.RecallAsync(query, source.Scope, t), source.Limits, ct), null);
        }
        catch (Exception e)
        {
            // Whatever the provider threw, it is a memory failure; only the caller's own cancellation is not.
            ct.ThrowIfCancellationRequested();
            return ([], Reason(e));
        }
    }

    private static async Task<RetainReport> RetainTo(MemorySource source, MemoryItem item, CancellationToken ct)
    {
        var own = source.RetainTags is { } tags ? item with { Tags = tags } : item;
        try
        {
            await Bounded(async t =>
            {
                await source.Provider.RetainAsync(own, source.Scope, t);
                return true;
            }, source.Limits, ct);
            return new RetainReport(source.Name, 1, null);
        }
        catch (Exception e)
        {
            ct.ThrowIfCancellationRequested();
            return new RetainReport(source.Name, 0, Reason(e));
        }
    }

    /// <summary>
    /// Runs the call under the source's timeout. The provider gets a token that fires at the timeout, and the wait
    /// ends then even if the provider ignores it (a hung transport must not hold the run), or blocks before its
    /// first await (hence Task.Run, so the other sources start regardless).
    /// </summary>
    private static async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> call, MemoryLimits limits, CancellationToken ct)
    {
        var timeout = limits.Timeout ?? MemoryLimits.DefaultTimeout;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        var task = Task.Run(() => call(timer.Token), CancellationToken.None);
        try
        {
            return await task.WaitAsync(timer.Token);
        }
        catch (Exception e)
        {
            if (!task.IsCompleted)
                _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (e is OperationCanceledException && timer.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new TimeoutException($"timed out after {timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s");
            throw;
        }
    }

    private static string Reason(Exception e)
    {
        var reason = OneLine(ChargehandException.Scrub(string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message));
        return reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength];
    }

    /// <summary>At most <paramref name="max"/> characters, the last of them an ellipsis when the text was longer; never through a surrogate pair.</summary>
    private static string Cut(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var keep = Math.Max(max - 1, 0);
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
            keep--;
        return text[..keep] + "…";
    }

    /// <summary>A fact is one line, so no fact can start a labelled line of its own.</summary>
    private static string OneLine(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed class Line(string text, int owner)
    {
        /// <summary>The source that returned it first; the chain block hashes its lines.</summary>
        public int Owner => owner;

        public List<string> Names { get; } = [];

        public string Render() => $"- [{string.Join(", ", Names)}] {text}";
    }
}
