using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <summary>Sends a notice to an approvals server as a decision item (docs/guide/phone-answers.md): <c>PUT {url}/items/{id}</c> with the source's bearer token, the questions in the body, one editable
/// answer field and the actions of <see cref="AnswerActions"/>. The person's decision comes back signed to the callback (<see cref="ApprovalDecision"/>). A withdrawn notice is <c>DELETE</c>d.</summary>
/// <param name="token">Read at each call, so a token that is not in the secret store yet fails that call and not the server's start.</param>
public sealed class ApprovalsSink(NotifyApprovals settings, Func<string> token, string? consoleUrl, HttpClient? client = null, TimeProvider? clock = null) : IQuestionSink
{
    public const int MaxTitle = 200;
    public const int MaxSummary = 300;
    public const int MaxBody = 30_000;

    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private readonly TimeProvider _time = clock ?? TimeProvider.System;

    public string Name => "approvals";

    /// <summary>The item id for a notice: the source, the kind (and the round after a dot, from the second), the batch and the task. <see cref="ApprovalDecision.Parse(string, string)"/> reads it back.</summary>
    public static string ItemId(string source, QuestionNotice notice) =>
        $"{source}:{notice.Kind}{(notice.Round > 0 ? $".{notice.Round}" : "")}:{notice.BatchRunId}:{notice.TaskId}";

    public async Task SendAsync(QuestionNotice notice, CancellationToken ct)
    {
        var status = await SendAsync(HttpMethod.Put, notice, Item(notice).ToJsonString(), ct);
        // 409: the item was decided already (a restart lost the state that said it was sent).
        if (status is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Conflict))
            throw new IntakeException($"the approvals server answered {(int)status} to the item", (int)status);
    }

    public async Task WithdrawAsync(QuestionNotice notice, CancellationToken ct)
    {
        var status = await SendAsync(HttpMethod.Delete, notice, null, ct);
        // 404: never stored or already cleaned up; 409: a decision is being sent for it right now, and that answer resolves it anyway.
        if (status is not (HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound or HttpStatusCode.Conflict))
            throw new IntakeException($"the approvals server answered {(int)status} to the withdrawal", (int)status);
    }

    public JsonObject Item(QuestionNotice notice)
    {
        var question = notice.Kind == QuestionKinds.Question;
        var body = new JsonObject
        {
            ["repository"] = notice.Repository ?? "",
            ["task"] = notice.TaskId,
            ["questions"] = Cut(question ? string.Join("\n", notice.Questions.Select((q, i) => $"{i + 1}. {q}")) : "The session stopped before it finished; its work so far is kept.", MaxBody),
            ["answer"] = "",
        };
        return new JsonObject
        {
            ["source"] = settings.Source,
            ["id"] = ItemId(settings.Source, notice),
            ["kind"] = "decision",
            ["priority"] = 4,
            ["title"] = Cut($"{(question ? "Question" : "Stopped early")}: {notice.Title}", MaxTitle),
            ["summary"] = Cut($"{notice.Repository ?? "no repository"} · task {notice.TaskId}", MaxSummary),
            ["body"] = body,
            ["editable"] = new JsonArray("body.answer"),
            ["links"] = Links(notice),
            ["actions"] = new JsonArray([.. AnswerActions.For(notice.Kind).Select(Action)]),
            // A partial's kept work, and the questions' session state, are swept after this.
            ["expires_at"] = (notice.At + ContinuationSource.KeptFor > _time.GetUtcNow() ? notice.At + ContinuationSource.KeptFor : _time.GetUtcNow().AddDays(1)).UtcDateTime.ToString("O"),
            ["callback"] = settings.CallbackUrl,
        };
    }

    private JsonArray Links(QuestionNotice notice)
    {
        var links = new JsonArray();
        if (consoleUrl is not null)
            links.Add(new JsonObject { ["label"] = "Task", ["url"] = TaskUrl(consoleUrl, notice) });
        if (notice.PullRequest is { } pr && Uri.TryCreate(pr, UriKind.Absolute, out var url) && url.Scheme == "https")
            links.Add(new JsonObject { ["label"] = "Pull request", ["url"] = pr });
        return links;
    }

    public static string TaskUrl(string consoleUrl, QuestionNotice notice) =>
        $"{consoleUrl.TrimEnd('/')}/runs/{Uri.EscapeDataString(notice.BatchRunId)}/tasks/{Uri.EscapeDataString(notice.TaskId)}";

    private static JsonObject Action(string id) => id switch
    {
        AnswerActions.Answer => new JsonObject { ["id"] = id, ["label"] = "Send answer", ["style"] = "primary" },
        AnswerActions.Decide => new JsonObject { ["id"] = id, ["label"] = "Your call" },
        AnswerActions.Continue => new JsonObject { ["id"] = id, ["label"] = "Continue", ["style"] = "primary" },
        _ => new JsonObject { ["id"] = id, ["label"] = "Drop", ["style"] = "danger", ["confirm"] = true },
    };

    private async Task<HttpStatusCode> SendAsync(HttpMethod method, QuestionNotice notice, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"{settings.Url!.TrimEnd('/')}/items/{Uri.EscapeDataString(ItemId(settings.Source, notice))}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token());
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            using var response = await _client.SendAsync(request, ct);
            return response.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new IntakeException($"the approvals server was not reached ({e.GetType().Name})");
        }
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : $"{text[..(max - 1)]}…";
}
