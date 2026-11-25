using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Modules.Pool.Api;

namespace Chargehand.Automation;

/// <summary>A wheelhouse board item as the producer reads it.</summary>
/// <param name="Repository"><c>source.repo</c> (<c>owner/name</c>); empty when the item has none.</param>
public sealed record BoardItem(string Id, string Title, string Ask, string State, string Work, string Repository);

/// <summary>Where the producer reads the board.</summary>
public interface IBoardSource
{
    Task<IReadOnlyList<BoardItem>> ListAsync(string kind, CancellationToken ct);
}

/// <summary>Whether the night-lane window is open now (after the cut-off, before Yehor's wake time).</summary>
public interface INightWindow
{
    bool IsOpen(DateTimeOffset now);
}

/// <summary>Reads the board with <c>wh board ls --work KIND --state planned --json</c>.</summary>
public sealed class WhBoardSource(string wh, string kindsFile) : IBoardSource
{
    public async Task<IReadOnlyList<BoardItem>> ListAsync(string kind, CancellationToken ct)
    {
        var info = new ProcessStartInfo("node") { RedirectStandardOutput = true };
        foreach (var a in new[] { wh, "board", "ls", "--work", kind, "--state", "planned", "--json" })
            info.ArgumentList.Add(a);
        info.Environment["WHEELHOUSE_WORK_KINDS_FILE"] = kindsFile;
        using var process = Process.Start(info) ?? throw new IntakeException("wh did not start");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new IntakeException($"wh board ls exited {process.ExitCode.ToString(CultureInfo.InvariantCulture)}");
        using var doc = JsonDocument.Parse(output);
        return [.. doc.RootElement.EnumerateArray().Select(Item)];
    }

    private static BoardItem Item(JsonElement e)
    {
        string S(JsonElement parent, string name) => parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var ask = e.TryGetProperty("ask", out var a) && a.ValueKind == JsonValueKind.Object ? S(a, "text") : "";
        var repo = e.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object ? S(s, "repo") : "";
        return new BoardItem(S(e, "id"), S(e, "title"), ask, S(e, "state"), S(e, "work"), repo);
    }
}

/// <summary>Turns each planned board item of a kind with <c>runner = "chargehand"</c> into a pool item (A7 of the work-kinds spec). It only enqueues: it never claims or starts an item and never writes to the board.
/// While the night window is open a kind with <c>night = false</c> waits for the day.</summary>
public sealed class BoardProducer(IReadOnlyDictionary<string, WorkKind> kinds, IBoardSource board, TaskPool pool, INightWindow night, TimeProvider time, Action<string> warn)
{
    public const string Source = "board";

    /// <returns>How many pool items it added.</returns>
    public async Task<int> ProduceAsync(CancellationToken ct)
    {
        var added = 0;
        var nightNow = night.IsOpen(time.GetUtcNow());
        foreach (var kind in kinds.Values.Where(k => k.Runner == "chargehand" && !(nightNow && !k.Night)))
        {
            IReadOnlyList<BoardItem> items;
            try
            {
                items = await board.ListAsync(kind.Name, ct);
            }
            catch (IntakeException e)
            {
                warn($"board: {kind.Name}: not read: {e.Message}");
                continue;
            }
            foreach (var item in items.Where(i => i.State == "planned" && i.Work == kind.Name && i.Repository.Length > 0))
            {
                var area = $"board-{item.Id}";
                if ((await pool.ItemsAsync(new PoolScope(item.Repository, area), ct)).Count > 0)
                    continue;
                var result = await pool.EnqueueAsync(new EnqueueRequest(item.Repository, area, item.Title, Body(kind, item), 0, null, Source), ct);
                if (result.Refusal is { } refusal)
                    warn($"board: {item.Id}: not enqueued: {refusal.Message}");
                else
                    added++;
            }
        }
        return added;
    }

    private static string Body(WorkKind kind, BoardItem item) => new StringBuilder()
        .Append(CultureInfo.InvariantCulture, $"Board item: {item.Id}\nKind: {kind.Name}\nModel class: {kind.ModelClass}\nGates: {string.Join(", ", kind.Gates)}\n")
        .Append(CultureInfo.InvariantCulture, $"Budget: ${kind.BudgetUsd}\nMax minutes: {kind.MaxMinutes}\n\n{item.Ask}")
        .ToString();
}
