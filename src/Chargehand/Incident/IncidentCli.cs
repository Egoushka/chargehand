using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Verification;

namespace Chargehand.Incident;

/// <summary><c>chargehand incident &lt;repository path&gt; [--commit &lt;ref&gt;]</c>: reads an alert on stdin, an Alertmanager or Grafana webhook payload or plain text,
/// and prints a <c>request/v1</c> for the <c>incident</c> preset, to pipe into <c>chargehand run</c> or post to <c>POST /v1/runs</c> (CHARGEHAND-192). It needs no
/// profile and makes no network call. Exit codes: 0 printed, 1 the alert or the commit is unusable, 2 usage error.</summary>
public static partial class IncidentCli
{
    public const string Usage = "usage: chargehand incident <repository path> [--commit <ref, default HEAD>] < alert (webhook JSON or text)";

    /// <summary>Alerts listed in the input; a payload with more firing alerts says how many it left out.</summary>
    public const int MaxAlerts = 10;

    private const int MaxValueLength = 500;
    private const int MaxTextLength = 8000;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, string workingDirectory, TextReader input, TextWriter output, TextWriter error, DateTimeOffset now, CancellationToken ct)
    {
        string? repository = null, commit = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--commit" when i + 1 < args.Count:
                    commit = args[++i];
                    break;
                case var positional when !positional.StartsWith('-') && repository is null:
                    repository = positional;
                    break;
                default:
                    error.WriteLine(Usage);
                    return 2;
            }
        }
        if (repository is null)
        {
            error.WriteLine(Usage);
            return 2;
        }
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository, workingDirectory));
        if (!Directory.Exists(path))
        {
            error.WriteLine($"{path} is not a directory");
            return 2;
        }
        var (request, problem) = await BuildAsync(path, commit ?? "HEAD", await input.ReadToEndAsync(ct), now, ct);
        if (request is null)
        {
            error.WriteLine(problem);
            return 1;
        }
        output.WriteLine(JsonSerializer.Serialize(request, ContractJson.Options));
        return 0;
    }

    public static async Task<(RunRequest? Request, string? Problem)> BuildAsync(string repository, string commitRef, string payload, DateTimeOffset now, CancellationToken ct)
    {
        if (commitRef.StartsWith('-'))
            return (null, "a ref cannot start with '-'");
        var (alert, title, problem) = Describe(payload, now);
        if (alert is null)
            return (null, problem);
        var commit = (await GitEvidenceResolver.Git(repository, ct, "rev-parse", "--verify", "--quiet", $"{commitRef}^{{commit}}"))?.Trim();
        if (string.IsNullOrEmpty(commit))
            return (null, $"{commitRef} is not a commit of {repository}");
        return (new RunRequest("request/v1", $"Diagnose this alert: {title}", new RequestContext(false, "incident", Repository: new RepositoryRef(repository, commit)),
            [new CallerInput("alert", "alert", alert)]), null);
    }

    /// <summary>The alert as text for the worker, and a one-line title. Webhook JSON lists its firing alerts (the payload of Alertmanager's
    /// webhook receiver and of Grafana's webhook contact point share the <c>alerts</c> array); anything else is the text itself.</summary>
    internal static (string? Alert, string Title, string? Problem) Describe(string payload, DateTimeOffset now)
    {
        var text = payload.Trim();
        if (text.Length == 0)
            return (null, "", "the alert is empty: pipe the webhook payload or the alert text to stdin");
        var received = $"Received: {now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}";
        if (!text.StartsWith('{'))
            return ($"{received}\n{Cut(text, MaxTextLength)}", Cut(Collapse(text.Split('\n')[0]), 120), null);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return ($"{received}\n{Cut(text, MaxTextLength)}", Cut(Collapse(text.Split('\n')[0]), 120), null);
        }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("alerts", out var alerts) || alerts.ValueKind != JsonValueKind.Array)
                return (null, "", "the JSON has no alerts array; pass an Alertmanager or Grafana webhook payload, or the alert as plain text");
            var firing = alerts.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object && Text(a, "status") is not "resolved").ToList();
            if (firing.Count == 0)
                return (null, "", "the payload has no firing alert");

            var sb = new StringBuilder(received);
            if (Text(doc.RootElement, "status") is { } status)
                sb.Append("\nGroup status: ").Append(status);
            foreach (var (a, n) in firing.Take(MaxAlerts).Select((a, n) => (a, n + 1)))
            {
                sb.Append("\n\n[").Append(n).Append("] ").Append(Text(a, "status") ?? "firing").Append(": ").Append(Label(a, "alertname") ?? "alert");
                if (Text(a, "startsAt") is { } started)
                    sb.Append(", started ").Append(started);
                sb.Append("\nlabels: ").Append(Pairs(a, "labels", "="));
                foreach (var (key, value) in Entries(a, "annotations"))
                    sb.Append('\n').Append(key).Append(": ").Append(Cut(value, MaxValueLength));
                if (Text(a, "valueString") is { } values)
                    sb.Append("\nvalues: ").Append(Cut(values, MaxValueLength));
            }
            if (firing.Count > MaxAlerts)
                sb.Append("\n\n").Append(firing.Count - MaxAlerts).Append(" more firing alerts are not listed.");
            if (alerts.GetArrayLength() > firing.Count)
                sb.Append("\n\n").Append("Resolved alerts left out: ").Append(alerts.GetArrayLength() - firing.Count).Append('.');

            var title = Label(firing[0], "alertname") ?? "alert";
            return (Cut(sb.ToString(), MaxTextLength), firing.Count > 1 ? $"{title} and {firing.Count - 1} more" : title, null);
        }
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? Collapse(s) : null;

    private static string? Label(JsonElement alert, string name) =>
        alert.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Object ? Text(l, name) : null;

    private static IEnumerable<(string Key, string Value)> Entries(JsonElement alert, string property) =>
        alert.TryGetProperty(property, out var o) && o.ValueKind == JsonValueKind.Object
            ? o.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Trim().Length > 0)
                .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (Collapse(p.Name), Collapse(p.Value.GetString()!)))
            : [];

    private static string Pairs(JsonElement alert, string property, string separator) =>
        string.Join(", ", Entries(alert, property).Select(e => $"{e.Key}{separator}{Cut(e.Value, MaxValueLength)}"));

    private static string Collapse(string s) => Whitespace().Replace(s, " ").Trim();

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
