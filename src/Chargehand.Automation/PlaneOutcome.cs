using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Automation;

/// <summary>How a task of a driven batch ended, read from the batch result's <c>driven-batch</c> artifact (the same rows <see cref="GitHubIntake"/> reads).</summary>
internal sealed record PlaneOutcome(string Status, string? RunId, string? PrUrl, string? Branch, string? Detail)
{
    public static PlaneOutcome? Of(ResultContract result, string taskId)
    {
        if (result.Artifacts.FirstOrDefault(a => a.Kind == "driven-batch")?.Content is not { } content)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(content);
            foreach (var row in doc.RootElement.GetProperty("tasks").EnumerateArray())
                if (Text(row, "id") == taskId)
                    return new PlaneOutcome(Text(row, "status") ?? "failed", Text(row, "run_id"), Text(row, "pr_url"), Text(row, "branch"), Text(row, "detail"));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A result this reader cannot read is reported as a run that never started the task.
        }
        return null;
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
