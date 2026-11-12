using System.Text;
using System.Text.RegularExpressions;
using Chargehand.Kernel;

namespace Chargehand.Modules.Pool.Api;

/// <summary>What a producer sends to put one item into the pool (<c>schemas/enqueue/v1</c>).</summary>
/// <param name="Repository"><c>owner/name</c>.</param>
/// <param name="Area">The scope label the producer chooses; 1–64 characters.</param>
/// <param name="Title">1–200 characters.</param>
/// <param name="Body">Up to <see cref="MaxBodyBytes"/> of UTF-8.</param>
/// <param name="Priority">0 normal, higher first; 0–100.</param>
/// <param name="PullRequest">The open pull request this is work on, when it is one.</param>
/// <param name="Source">Who enqueues: <c>enqueue</c> for the tool.</param>
public sealed record EnqueueRequest(
    string? Repository,
    string? Area,
    string? Title,
    string? Body,
    int Priority = 0,
    int? PullRequest = null,
    string Source = "enqueue")
{
    /// <summary>A body larger than this is refused with <c>413 too_large</c>.</summary>
    public const int MaxBodyBytes = 16 * 1024;

    /// <summary>The refusal, or null when the request can be written. Messages name the field, never its value.</summary>
    public PoolRefusal? Validate()
    {
        if (string.IsNullOrWhiteSpace(Repository) || Repository.Length > 100 || !PoolPatterns.Repository().IsMatch(Repository))
            return Invalid("repository must be owner/name");
        if (string.IsNullOrWhiteSpace(Area) || Area.Length > 64 || Area.Trim() != Area)
            return Invalid("area must be 1 to 64 characters without surrounding spaces");
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 200)
            return Invalid("title must be 1 to 200 characters");
        if (Body is null)
            return Invalid("body is required");
        if (Priority is < 0 or > 100)
            return Invalid("priority must be 0 to 100");
        if (PullRequest is < 1)
            return Invalid("pull_request must be a positive number");
        if (Encoding.UTF8.GetByteCount(Body) > MaxBodyBytes)
            return new PoolRefusal(413, "too_large", $"body must be at most {MaxBodyBytes} bytes of UTF-8");
        if (DiffScan.ScanText(Title + "\n" + Body, []) is { Count: > 0 } kinds)
            return new PoolRefusal(422, "secret", $"title or body holds a credential-shaped value ({string.Join(", ", kinds)}); remove it");
        return null;
    }

    private static PoolRefusal Invalid(string message) => new(400, "invalid", message);
}

internal static partial class PoolPatterns
{
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    public static partial Regex Repository();
}
