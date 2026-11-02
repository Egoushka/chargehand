using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Runtimes.ClaudeCode.Api;

/// <summary>The owner's explicit, time-boxed override of a budget. There are two, and they never share state: <see cref="Subscription"/> lifts the subscription's usage hold (new batches start
/// without it) and <see cref="Api"/> lifts the fallback's daily cap and its refusal (and sends the fallback on the override key, when the profile names one). Each lives in its own file beside the
/// fallback ledger, so the server, the gateway's batches and the CLI see the same answer; every set, clear and first use is appended to <c>budget-ignore.jsonl</c>.</summary>
public sealed class BudgetIgnore(string directory, TimeProvider? time = null)
{
    public const string Subscription = "subscription";
    public const string Api = "api";

    /// <summary>How long an ignore lasts. Fixed: an ignore that never ends is a removed budget.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    private readonly object _lock = new();
    private readonly HashSet<(string Kind, DateTimeOffset Created)> _used = [];

    public static bool IsKind(string kind) => kind is Subscription or Api;

    /// <summary>The ignore of <paramref name="kind"/> in force now, or null.</summary>
    public IgnoreRecord? Active(string kind)
    {
        var found = Read(kind);
        return found is { } r && r.Until > Now ? r : null;
    }

    public IReadOnlyList<IgnoreRecord> Status() => [.. new[] { Subscription, Api }.Select(Active).OfType<IgnoreRecord>()];

    /// <summary>Starts (or restarts) the ignore of <paramref name="kind"/> for <see cref="Lifetime"/>. <paramref name="reason"/> is required.</summary>
    public IgnoreRecord Set(string kind, string reason, string by)
    {
        if (!IsKind(kind))
            throw new ArgumentException($"unknown budget '{kind}': use '{Subscription}' or '{Api}'", nameof(kind));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("an ignore needs a reason", nameof(reason));
        var record = new IgnoreRecord(kind, Now, Now + Lifetime, reason.Trim(), by);
        lock (_lock)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(PathOf(kind), JsonSerializer.Serialize(record, BudgetIgnoreJsonContext.Default.IgnoreRecord));
            Audit(new IgnoreAudit(Now, kind, "set", by, record.Reason, record.Until));
        }
        return record;
    }

    /// <summary>Ends the ignore of <paramref name="kind"/> now. False when none was in force.</summary>
    public bool Clear(string kind, string by)
    {
        if (!IsKind(kind))
            throw new ArgumentException($"unknown budget '{kind}': use '{Subscription}' or '{Api}'", nameof(kind));
        lock (_lock)
        {
            if (Active(kind) is null)
                return false;
            File.Delete(PathOf(kind));
            Audit(new IgnoreAudit(Now, kind, "clear", by, null, null));
            return true;
        }
    }

    /// <summary>Records, once per ignore and process, that a limit was passed because of it.</summary>
    public void Used(IgnoreRecord record, string what)
    {
        lock (_lock)
        {
            if (_used.Add((record.Kind, record.Created)))
                Audit(new IgnoreAudit(Now, record.Kind, "used", record.By, what, record.Until));
        }
    }

    private DateTimeOffset Now => (time ?? TimeProvider.System).GetUtcNow();

    private string PathOf(string kind) => Path.Combine(directory, $"ignore-{kind}.json");

    private IgnoreRecord? Read(string kind)
    {
        try
        {
            var file = PathOf(kind);
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), BudgetIgnoreJsonContext.Default.IgnoreRecord) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A failed audit write must not decide whether a limit holds, so it is swallowed; the ignore file itself is the state.
    private void Audit(IgnoreAudit line)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "budget-ignore.jsonl"), JsonSerializer.Serialize(line, BudgetIgnoreJsonContext.Default.IgnoreAudit) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <param name="Kind"><c>subscription</c> or <c>api</c>.</param>
/// <param name="By">Who set it (the CLI's user name).</param>
public sealed record IgnoreRecord(string Kind, DateTimeOffset Created, DateTimeOffset Until, string Reason, string By);

internal sealed record IgnoreAudit(DateTimeOffset At, string Kind, string Event, string By, string? Detail, DateTimeOffset? Until);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IgnoreRecord))]
[JsonSerializable(typeof(IgnoreAudit))]
internal sealed partial class BudgetIgnoreJsonContext : JsonSerializerContext;
