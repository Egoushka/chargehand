namespace Chargehand.Modules.Conversations.Api;

/// <summary>What <c>POST /v1/conversations/{id}/messages</c> takes. <paramref name="Text"/> is checked by the service, so a missing one is a <c>400</c> with the other errors.</summary>
public sealed record MessageBody(string? Text);

/// <summary>What <c>POST /v1/conversations/{id}/approvals/{call}</c> takes: <c>allow_once</c> or <c>reject</c>.</summary>
public sealed record AnswerBody(string? Decision);
