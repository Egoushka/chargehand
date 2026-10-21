using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Chargehand.Automation;

/// <summary>The Plane implementation of <see cref="IPlaneHost"/>: the public REST API v1 with the key in <c>X-API-Key</c>, redacted from every message. Plane renamed issues to work items, so the host
/// asks for <c>work-items</c> once and falls back to <c>issues</c> when the instance answers 404; the answer is kept. A list is read up to <see cref="MaxPages"/> pages of 100.</summary>
/// <param name="http">Its <c>BaseAddress</c> is the instance's URL and ends in a slash.</param>
/// <param name="apiKey">Read when a call is made, so a key that is not in the secret store yet fails that call and not the server's start.</param>
public sealed class PlaneHost(HttpClient http, string workspace, Func<string> apiKey) : IPlaneHost
{
    public const int MaxPages = 20;

    private string? _segment;

    public async Task<string> ProjectIdentifierAsync(string project, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, $"{ProjectPath(project)}/", null, "the project", ct));
        return Parse(() => Text(doc.RootElement, "identifier") ?? throw new KeyNotFoundException());
    }

    public async Task<IReadOnlyList<PlaneWorkflowState>> StatesAsync(string project, CancellationToken ct) =>
        [.. (await PagesAsync($"{ProjectPath(project)}/states/", "the states", ct)).Select(s => Parse(() => new PlaneWorkflowState(Text(s, "id") ?? throw new KeyNotFoundException(), Text(s, "name") ?? "")))];

    public async Task<IReadOnlyList<PlaneItem>> ItemsInStateAsync(string project, string stateId, CancellationToken ct) =>
        [.. (await PagesAsync($"{await ItemsPathAsync(project, ct)}/", "the work items", ct)).Where(i => Text(i, "state") == stateId).Select(Item)];

    public async Task<PlaneItem> GetAsync(string project, string itemId, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, $"{await ItemsPathAsync(project, ct)}/{Uri.EscapeDataString(itemId)}/", null, "the work item", ct));
        return Item(doc.RootElement);
    }

    public async Task CommentAsync(string project, string itemId, string text, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, $"{await ItemsPathAsync(project, ct)}/{Uri.EscapeDataString(itemId)}/comments/", new { comment_html = PlaneText.ToHtml(text) }, "the comment", ct);

    public async Task SetStateAsync(string project, string itemId, string stateId, CancellationToken ct) =>
        await SendAsync(HttpMethod.Patch, $"{await ItemsPathAsync(project, ct)}/{Uri.EscapeDataString(itemId)}/", new { state = stateId }, "the state change", ct);

    private string ProjectPath(string project) => $"api/v1/workspaces/{Uri.EscapeDataString(workspace)}/projects/{Uri.EscapeDataString(project)}";

    private async Task<string> ItemsPathAsync(string project, CancellationToken ct)
    {
        if (_segment is null)
        {
            try
            {
                await SendAsync(HttpMethod.Get, $"{ProjectPath(project)}/work-items/?per_page=1", null, "the work items", ct);
                _segment = "work-items";
            }
            catch (IntakeException e) when (e.Status == 404)
            {
                _segment = "issues";
            }
        }
        return $"{ProjectPath(project)}/{_segment}";
    }

    /// <summary>A list is an array or Plane's cursor page (<c>results</c>, <c>next_page_results</c>, <c>next_cursor</c>).</summary>
    private async Task<IReadOnlyList<JsonElement>> PagesAsync(string path, string what, CancellationToken ct)
    {
        List<JsonElement> all = [];
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{path}?per_page=100{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, url, null, what, ct));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return [.. root.EnumerateArray().Select(e => e.Clone())];
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new IntakeException($"Plane answered {what} with a body that is not a list");
            all.AddRange(results.EnumerateArray().Select(e => e.Clone()));
            cursor = Text(root, "next_cursor");
            if (cursor is null || !(root.TryGetProperty("next_page_results", out var more) && more.ValueKind == JsonValueKind.True))
                break;
        }
        return all;
    }

    private static PlaneItem Item(JsonElement e) => Parse(() => new PlaneItem(Text(e, "id") ?? throw new KeyNotFoundException(), Text(e, "name") ?? "",
        PlaneText.FromHtml(Text(e, "description_html")), Text(e, "state") ?? "", e.GetProperty("sequence_id").GetInt32(), e.GetProperty("updated_at").GetDateTimeOffset()));

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static T Parse<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or JsonException)
        {
            throw new IntakeException("Plane answered with a body that is not what intake reads");
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, string what, CancellationToken ct)
    {
        string key;
        try
        {
            key = apiKey();
        }
        catch (InvalidOperationException e)
        {
            throw new IntakeException(e.Message);
        }
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", key);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new IntakeException(Redact($"Plane answered {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)} to {what}: {Message(text)}", key), (int)response.StatusCode);
            return text;
        }
        catch (HttpRequestException e)
        {
            throw new IntakeException(Redact($"{what} could not be reached: {e.Message}", key));
        }
    }

    private static string Message(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var message = Text(doc.RootElement, "error") ?? Text(doc.RootElement, "detail") ?? Text(doc.RootElement, "message") ?? "no message";
            return message.Length > 300 ? message[..300] : message;
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }

    private static string Redact(string text, string key) => key.Length >= 4 ? text.Replace(key, "[redacted]", StringComparison.Ordinal) : text;
}
