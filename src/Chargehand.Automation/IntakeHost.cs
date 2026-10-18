using Chargehand.Contracts;

namespace Chargehand.Automation;

/// <param name="Status">The HTTP status GitHub answered with, when it did.</param>
public sealed class IntakeException(string message, int? status = null) : Exception(message)
{
    public int? Status => status;
}

/// <param name="Author">The login of the person who wrote the issue.</param>
/// <param name="IsPullRequest">GitHub lists a pull request among the issues; it has a head branch.</param>
public sealed record IntakeIssue(int Number, string Title, string Body, string Author, bool IsPullRequest, IReadOnlyList<string> Labels, DateTimeOffset UpdatedAt);

/// <param name="Author">The login of the comment's author.</param>
public sealed record IntakeComment(long Id, int IssueNumber, string Author, string Body, DateTimeOffset UpdatedAt);

/// <param name="HeadBranch">The pull request's head branch.</param>
/// <param name="HeadRepository">The repository the head branch is in (<c>owner/repo</c>); null when it is gone (a deleted fork).</param>
/// <param name="BaseRepository">The repository the pull request is against.</param>
public sealed record IntakePull(int Number, string HeadBranch, string? HeadRepository, string BaseRepository, bool Open);

/// <summary>An open pull request as the review loop sees it (ADR 0051).</summary>
/// <param name="Body">The description; empty when there is none.</param>
/// <param name="HeadSha">The commit its head branch points at now.</param>
public sealed record ReviewablePull(int Number, string Title, string Body, string Author, string HeadSha, bool Draft);

/// <summary>An inline comment of a review, on a line of a file of a pull request.</summary>
/// <param name="Line">The line it is on in the head commit, else the line it was made on; null when the review comment is on the file as a whole.</param>
/// <param name="IsReply">It answers another comment of a thread.</param>
public sealed record ReviewComment(long Id, int PullNumber, string Author, string Body, string Path, int? Line, bool IsReply, DateTimeOffset UpdatedAt);

/// <param name="Id">The event's id: a label applied again is a new event, so it can trigger again.</param>
/// <param name="Actor">The login of the person who applied the label.</param>
public sealed record LabelEvent(long Id, string Actor);

/// <summary>What intake does on GitHub (ADR 0051), each call as the GitHub App. It reads issues and comments and writes comments, nothing else.</summary>
public interface IIntakeHost
{
    /// <summary>The open issues and pull requests that carry <paramref name="label"/>.</summary>
    Task<IReadOnlyList<IntakeIssue>> LabeledAsync(string repository, string label, CancellationToken ct);

    /// <summary>The latest event that applied <paramref name="label"/> to the issue; null when GitHub no longer lists one.</summary>
    Task<LabelEvent?> LastLabelEventAsync(string repository, int number, string label, CancellationToken ct);

    Task<IntakeIssue> GetAsync(string repository, int number, CancellationToken ct);

    /// <summary>The issue and pull request comments of a repository updated at or after <paramref name="since"/>, oldest first.</summary>
    Task<IReadOnlyList<IntakeComment>> CommentsSinceAsync(string repository, DateTimeOffset since, CancellationToken ct);

    Task<IntakePull> GetPullAsync(string repository, int number, CancellationToken ct);

    /// <summary>The inline review comments of a repository's pull requests updated at or after <paramref name="since"/>, oldest first.</summary>
    Task<IReadOnlyList<ReviewComment>> ReviewCommentsSinceAsync(string repository, DateTimeOffset since, CancellationToken ct);

    /// <summary>The bodies of an issue's or pull request's conversation comments, oldest first: where the hidden markers of fix attempts and review rounds are.</summary>
    Task<IReadOnlyList<string>> CommentBodiesAsync(string repository, int number, CancellationToken ct);

    /// <summary>The open pull requests of a repository, drafts included.</summary>
    Task<IReadOnlyList<ReviewablePull>> OpenPullsAsync(string repository, CancellationToken ct);

    /// <summary>The pull request's whole diff as GitHub renders it.</summary>
    /// <exception cref="IntakeException">GitHub answered 406 or 422 when the diff is too large to render.</exception>
    Task<string> PullDiffAsync(string repository, int number, CancellationToken ct);

    /// <returns>The new comment's id.</returns>
    Task<long> CommentAsync(string repository, int number, string body, CancellationToken ct);
}

/// <summary>The id of a batch that started, or why it did not.</summary>
public sealed record BatchStart(string? RunId, string? Refusal = null);

/// <summary>Starts a driven batch on this server; the server implements it over its run service.</summary>
public interface IBatchStarter
{
    Task<BatchStart> StartAsync(RunRequest request, CancellationToken ct);
}

/// <summary>The commit a triggered batch works from.</summary>
public interface IRepositoryHeads
{
    /// <summary>Fetches the checkout at <paramref name="path"/> and returns the commit its remote's default branch points at.</summary>
    /// <exception cref="IntakeException">The checkout could not be fetched or has no default branch.</exception>
    Task<string> CurrentCommitAsync(string path, CancellationToken ct);

    /// <summary>Fetches the head of pull request <paramref name="number"/> (<c>refs/pull/N/head</c>) into the checkout at <paramref name="path"/>, keeps it under <c>refs/chargehand/pull/N</c>, and returns its commit.</summary>
    /// <exception cref="IntakeException">The head could not be fetched.</exception>
    Task<string> FetchPullHeadAsync(string path, int number, CancellationToken ct);
}
