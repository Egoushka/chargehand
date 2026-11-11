using System.Text.Json;
using Chargehand.Modules.Events.Api;

namespace Chargehand.Modules.Warmer.Api;

/// <summary>Restores a checkout's packages ahead of the agent that will use it, when the event log says the checkout exists (ADR 0090). Each job is logged back as <see cref="WarmedType"/>.</summary>
public sealed class CacheWarmer(IEventLog log, string checkoutsRoot, IStepRunner runner, TimeProvider? clock = null)
{
    public const string CreatedType = "chargehand.worktree.created";
    public const string QueuedType = "chargehand.branch.queued";
    public const string WarmedType = "chargehand.cache.warmed";
    public const string Source = "chargehand/warmer";

    private static readonly EventGrant Grant = new("warmer", Source, ["*"], ["*"]);
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    /// <summary>Handles events that arrive from now on, one at a time, until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var cursor = log.Signal.Latest;
        while (true)
        {
            await log.Signal.WaitAsync(cursor, ct).ConfigureAwait(false);
            foreach (var e in await log.ReadAsync(new EventFilter(After: cursor, Limit: 200), ct).ConfigureAwait(false))
            {
                cursor = e.Seq;
                try
                {
                    await WarmAsync(e, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad event must not stop the warmer.
                    Console.Error.WriteLine($"warmer: event {e.Seq}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Warms the checkout a trigger event names. False when the event is not a job: another type, or a checkout that is not under the root.</summary>
    public async Task<bool> WarmAsync(StoredEvent trigger, CancellationToken ct)
    {
        if (trigger.Type is not (CreatedType or QueuedType) || Name(trigger) is not { } name || Resolve(name) is not { } dir)
            return false;
        var started = time.GetTimestamp();
        var steps = new List<(string Name, string Result, long Ms, string? Error)>();
        if (Directory.Exists(dir))
        {
            foreach (var step in WarmPlan.For(dir))
                steps.Add(await Run(step, ct).ConfigureAwait(false));
        }
        var outcome = !Directory.Exists(dir) ? "skipped" : steps.Any(s => s.Result == "failed") ? "failed" : steps.Any(s => s.Result == "miss") ? "miss" : "hit";
        var ms = (long)time.GetElapsedTime(started).TotalMilliseconds;
        var (stored, refusal) = Envelope.Check(Build(trigger, name, outcome, ms, steps), Grant);
        if (stored is null)
            throw new InvalidOperationException(refusal?.Message ?? "the warm result was refused");
        await log.AppendAsync([stored], ct).ConfigureAwait(false);
        return true;
    }

    private async Task<(string Name, string Result, long Ms, string? Error)> Run(WarmStep step, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        string result;
        string? error = null;
        if (step.Done())
            result = "hit";
        else if (step.File is null)
            result = "miss";
        else
        {
            error = await Execute(step, ct).ConfigureAwait(false);
            result = error is null ? "miss" : "failed";
        }
        return (step.Name, result, (long)time.GetElapsedTime(started).TotalMilliseconds, error);
    }

    private async Task<string?> Execute(WarmStep step, CancellationToken ct)
    {
        try
        {
            var code = await runner.RunAsync(step, ct).ConfigureAwait(false);
            return code == 0 ? null : $"exit code {code}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"warmer: {step.Name}: {ex.Message}");
            return ex.Message;
        }
    }

    private static string? Name(StoredEvent trigger)
    {
        using var doc = JsonDocument.Parse(trigger.Envelope);
        return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("checkout", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
    }

    private string? Resolve(string name)
    {
        if (name.Length == 0 || Path.IsPathRooted(name) || name.Split('/', '\\').Contains("..") || name.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            return null;
        var root = Path.GetFullPath(checkoutsRoot);
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        var walk = root;
        foreach (var part in name.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            walk = Path.Combine(walk, part);
            if (new DirectoryInfo(walk).LinkTarget is not null)
                return null;
        }
        return full;
    }

    private JsonElement Build(StoredEvent trigger, string checkout, string outcome, long ms, List<(string Name, string Result, long Ms, string? Error)> steps)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("specversion", "1.0");
            w.WriteString("id", $"warm-{trigger.Seq}");
            w.WriteString("source", Source);
            w.WriteString("type", WarmedType);
            w.WriteString("time", time.GetUtcNow().ToString(Envelope.Format, System.Globalization.CultureInfo.InvariantCulture));
            w.WriteString("scope", trigger.Scope);
            w.WriteStartObject("data");
            w.WriteString("checkout", checkout);
            w.WriteString("outcome", outcome);
            w.WriteNumber("ms", ms);
            w.WriteStartArray("steps");
            foreach (var (name, result, stepMs, error) in steps)
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteString("result", result);
                w.WriteNumber("ms", stepMs);
                if (error is not null)
                    w.WriteString("error", error);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }
}
