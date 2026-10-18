using System.Text.Json.Serialization;

namespace Chargehand.Modules.Forge.Api;

/// <summary>The bodies the forge clients send, without reflection (ADR 0060), written byte for byte as the anonymous types before them: snake_case, in declaration order.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AppClaims))]
[JsonSerializable(typeof(DraftPullRequest))]
[JsonSerializable(typeof(TitleAndBody))]
[JsonSerializable(typeof(BranchUpdate))]
[JsonSerializable(typeof(SquashMerge))]
[JsonSerializable(typeof(CommentBody))]
[JsonSerializable(typeof(GraphQlRequest))]
[JsonSerializable(typeof(PullRequestVariables))]
[JsonSerializable(typeof(NodeVariables))]
[JsonSerializable(typeof(AutoMergeVariables))]
[JsonSerializable(typeof(MergeRequestDraft))]
[JsonSerializable(typeof(TitleAndDescription))]
[JsonSerializable(typeof(TitleOnly))]
[JsonSerializable(typeof(NoFields))]
[JsonSerializable(typeof(AutoMergeRequest))]
[JsonSerializable(typeof(GitLabMerge))]
internal sealed partial class ForgeJsonContext : JsonSerializerContext;

/// <summary>The GitHub App JWT's claims.</summary>
internal sealed record AppClaims(long Iat, long Exp, string Iss);

internal sealed record DraftPullRequest(string Title, string Head, string Base, string Body, bool Draft, bool MaintainerCanModify);

internal sealed record TitleAndBody(string Title, string Body);

internal sealed record BranchUpdate(string ExpectedHeadSha);

/// <param name="CommitMessage">The body of the squash commit; left out, GitHub builds its own (ADR 0070 puts the run trailers in it).</param>
internal sealed record SquashMerge(string MergeMethod, string Sha, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommitMessage = null);

internal sealed record CommentBody(string Body);

/// <summary>A GraphQL call; <see cref="Variables"/> is one of the variables records of this context.</summary>
internal sealed record GraphQlRequest(string Query, object Variables);

internal sealed record PullRequestVariables(string Owner, string Name, int Number);

internal sealed record NodeVariables(string Id);

internal sealed record AutoMergeVariables(string Id, string Oid, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Body = null);

internal sealed record MergeRequestDraft(string SourceBranch, string TargetBranch, string Title, string Description);

internal sealed record TitleAndDescription(string Title, string Description);

internal sealed record TitleOnly(string Title);

/// <summary>An empty object, <c>{}</c>.</summary>
internal sealed record NoFields;

internal sealed record AutoMergeRequest(bool MergeWhenPipelineSucceeds, string Sha, bool Squash);

internal sealed record GitLabMerge(string Sha, bool Squash);
